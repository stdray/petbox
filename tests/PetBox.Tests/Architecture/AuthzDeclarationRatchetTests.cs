using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using PetBox.Core.Auth;
using PetBox.Web;
using PetBox.Web.Mcp;
using Xunit;

namespace PetBox.Tests.Architecture;

// A booted host, once, for the whole ratchet: REST + Razor surfaces exist only after Program.Configure
// has run (see the header of AuthzSurfaces for why a static sweep cannot see them).
public sealed class AuthzSurfaceHost : IAsyncLifetime
{
	public WebApplicationFactory<Program> Factory { get; }

	public IReadOnlyList<AuthzSurface> Endpoints { get; private set; } = [];
	public IReadOnlyList<AuthzSurface> McpTools { get; private set; } = [];
	public IReadOnlyList<AuthzSurface> All => [.. Endpoints.Concat(McpTools)];

	public AuthzSurfaceHost() =>
		Factory = new WebApplicationFactory<Program>()
			.WithWebHostBuilder(b =>
			{
				b.UseEnvironment("Testing");
				b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
				{
					["ConnectionStrings:PetBox"] = TestSchema.NewTempConnectionString(),
					["Host:BackgroundServices"] = "false",

					// EVERY module on. The inventory is only complete if every feature-gated block in
					// Program.Configure ran — a host with Data off simply has no /api/data/** endpoints,
					// and the ratchet would then report them as REMOVED (a stale allowlist entry) rather
					// than as surfaces. These are also the appsettings.json defaults, restated here so a
					// change to that file cannot quietly shrink what this guard sees.
					["Features:Config"] = "true",
					["Features:Logging"] = "true",
					["Features:Data"] = "true",
					["Features:Dashboard"] = "true",
					["Features:Tasks"] = "true",
					["Features:Memory"] = "true",
					["Features:LlmRouter"] = "true",
					["Features:Deploy"] = "true",

					// …and every OPTIONAL endpoint on, for the same reason and one sharper one. The Seq
					// self-log receiver (POST /api/events/raw) is gated by a config value that defaults to
					// false — but another test in the suite turns it on through an ENVIRONMENT VARIABLE,
					// which is process-global and leaks into every host built afterwards. So this surface
					// appeared in the full-suite run and NOT in a filtered one: the ratchet was red or green
					// depending on which other tests happened to run first. A security guard whose inventory
					// depends on test ordering is worse than no guard, so the value is pinned here (an
					// in-memory source added last wins over the environment) and the surface is enumerated
					// unconditionally — the maximal surface is the honest one to count.
					["Seq:SelfLog:Enabled"] = "true",
				}));
			});

	public async Task InitializeAsync()
	{
		// CreateClient is what actually builds the pipeline; one request makes sure routing has
		// materialized before the data source is read.
		using var client = Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
		using var _ = await client.GetAsync("/health");

		Endpoints = AuthzSurfaces.FromEndpoints(
			Factory.Services.GetRequiredService<EndpointDataSource>().Endpoints);
		McpTools = AuthzSurfaces.FromMcpTools(typeof(Program).Assembly);
	}

	public async Task DisposeAsync() => await Factory.DisposeAsync();
}

// THE RATCHET for spec `authz-scope-declaration` (work `authz-default-deny-delivery`, step 1).
//
// WHAT IT ASSERTS: every externally reachable surface either DECLARES where its tenant comes from,
// or is on the allowlist (TenantEnforcementAllowlist, in product code). A NEW surface is on neither,
// so it fails — which is the whole mechanism. Acceptance criterion 2 of the work card, in one
// sentence: a new surface without a declaration breaks the build without anyone remembering that
// this class of defect exists.
//
// WHAT IT DOES NOT ASSERT, deliberately: that anything is REFUSED. That is the PEPs' job and is
// covered by TenantEnforcementTests — this guard only ever asks whether a surface DECLARES. The
// separation mattered most while the two were out of step: when this was written the PEPs were live
// but passed every surface on the allowlist, and the allowlist held all 217, so a declaration changed
// no behaviour anywhere. That ordering was the point — the enumeration and the ratchet came FIRST, so
// the surface could be counted before it was argued about, and the rollout was then "cross a line out
// of the allowlist" rather than a flag. The rollout is COMPLETE: the allowlist is empty, all 217
// surfaces declare or name an exemption class, and both PEPs now reach the declaration path on every
// call. This guard is unchanged by that — it is what keeps a NEW surface from arriving undeclared.
//
// THE ALLOWLIST STARTS FULL — every surface in the tree at the moment this guard was written — and
// only ever SHRINKS: a stale entry fails StaleAllowlistEntries_AreDeleted, so the list cannot
// silently re-grant an exemption to whoever edits that surface next. It is not a list of things that
// are FINE; it is the debt this work item exists to pay off, made countable.
//
// Modelled on DbLayerGuardTests, including its guard-the-guard: every assertion here is an
// "is empty", and an inventory that silently swept NOTHING would make all of them pass by vacuity.
[Collection("WebAppFactory")]
public sealed class AuthzDeclarationRatchetTests : IClassFixture<AuthzSurfaceHost>
{
	readonly AuthzSurfaceHost _host;

	public AuthzDeclarationRatchetTests(AuthzSurfaceHost host) => _host = host;

	// ── THE ALLOWLIST ────────────────────────────────────────────────────────────────────────────
	//
	// It is NOT here. It lives in PRODUCT code (PetBox.Core.Auth.TenantEnforcementAllowlist), because
	// the work card requires ONE list to drive both this ratchet and the two enforcement points
	// (TenantEnforcementMiddleware on the endpoint plane, McpTenantEnforcementFilter on the MCP one) —
	// "Один список управляет и тестом, и энфорсом — разойтись не могут". Read the reasoning there; the
	// alias below exists only so the assertions read the way they did before the move.
	static IReadOnlySet<string> Allowlist => TenantEnforcementAllowlist.Keys;

	[Fact]
	public void EverySurface_DeclaresItsTenant_OrIsOnTheAllowlist()
	{
		var undeclared = _host.All
			.Where(s => s.Declarations.Count == 0)
			.Where(s => !Allowlist.Contains(s.Key))
			.OrderBy(s => s.Key, StringComparer.Ordinal)
			.Select(s => $"  {s.Key}   [{s.Owner}]")
			.ToList();

		undeclared.Should().BeEmpty(
			"every surface declares in ONE machine-readable place where its target tenant comes from — a "
			+ "route value, a call argument, a body field, or the caller's own claim — or which of the six "
			+ "closed exemption classes it belongs to (spec `authz-scope-declaration`). Put a "
			+ "[TenantFrom(...)] or [TenantExempt(...)] on the handler method, the PageModel class or the "
			+ "[McpServerTool] method (or call .DeclaresTenant(...) on the endpoint builder). Do NOT add a "
			+ "line to TenantEnforcementAllowlist: it holds the surfaces that predate the rule and it only ever "
			+ "shrinks. If the surface fits no source and no exemption class, STOP — that is a spec change "
			+ "(a new idea), not a line in a PR. Undeclared:\n" + string.Join("\n", undeclared));
	}

	[Fact]
	public void StaleAllowlistEntries_AreDeleted()
	{
		var live = _host.All.ToDictionary(s => s.Key, StringComparer.Ordinal);

		var stale = Allowlist
			.Where(key => !live.TryGetValue(key, out var surface) || surface.Declarations.Count > 0)
			.Order(StringComparer.Ordinal)
			.ToList();

		stale.Should().BeEmpty(
			"these allowlist entries name a surface that is now DECLARED, or that no longer exists. Delete "
			+ "the line — the allowlist only ever shrinks, and a stale entry both hides work that is already "
			+ "done and silently re-grants the exemption to whoever edits that surface next. Stale:\n  "
			+ string.Join("\n  ", stale));
	}

	// A surface with TWO declarations is not "extra safe" — it is a surface whose tenant answer depends
	// on which reader wins, which is the ambiguity the spec's "ровно одно" exists to forbid.
	[Fact]
	public void NoSurface_DeclaresTwice()
	{
		var contradictory = _host.All
			.Where(s => s.Declarations.Count > 1)
			.OrderBy(s => s.Key, StringComparer.Ordinal)
			.Select(s => $"  {s.Key} -> {s.Describe()}")
			.ToList();

		contradictory.Should().BeEmpty(
			"a surface declares EXACTLY ONE thing: a tenant source, or an exemption class. Two declarations "
			+ "leave the answer to whichever reader looks first.\n" + string.Join("\n", contradictory));
	}

	// ── THE TWO ENFORCEMENT POINTS READ THE SAME THING THE RATCHET DOES ──────────────────────────
	//
	// One list, two readers (work card, step 3: "Один список управляет и тестом, и энфорсом").
	// The list itself is shared by construction — both sides literally reference
	// TenantEnforcementAllowlist — so what is left to prove is that both sides look the same surface
	// UP in it, and find the same declaration on it. That is what these two tests do.

	// THE RAZOR FORK, mechanized. The inventory's unit is a PAGE (carrier = the PageModel class); the
	// endpoint PEP's unit is a REQUEST, i.e. an ENDPOINT. A page with several routes is several
	// endpoints and one inventory row, so the two units only converge if every endpoint of a page maps
	// to the same surface key AND carries the same declaration. Both halves are asserted here rather
	// than argued: if a framework change ever put a page's declaration on only one of its endpoints,
	// crossing that page out of the allowlist would enforce on one route and not the other — a hole
	// with a green ratchet above it.
	[Fact]
	public void EveryEndpointOfAPage_CarriesTheSameDeclaration()
	{
		var byPage = _host.Factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
			.Where(e => e.Metadata.GetMetadata<PageActionDescriptor>() is not null)
			.GroupBy(e => e.Metadata.GetMetadata<PageActionDescriptor>()!.ViewEnginePath, StringComparer.Ordinal)
			.ToList();

		// NOT hypothetical, and not vacuous: 6 of the 65 pages are mapped by two endpoints each
		// (/Index, /Config/{Editor,History,Index,Preview}, /ProjectHome/TaskBoardNode). If this ever
		// dropped to zero the two halves below would pass by saying nothing.
		byPage.Count(p => p.Count() > 1).Should().BeGreaterThan(0,
			"pages with several routes are what makes the per-page/per-endpoint question real");

		// Half 1 — one key per page, for every endpoint of it. AuthzSurfaceKey.OfEndpoint is the PEP's
		// own function, so this is the PEP's answer, not a restatement of the sweep's.
		foreach (var page in byPage)
			page.Select(AuthzSurfaceKey.OfEndpoint).Distinct(StringComparer.Ordinal).Should().Equal(
				[AuthzSurfaceKey.PagePrefix + page.Key],
				"every endpoint of a page must resolve to the ONE surface key the inventory lists it under — "
				+ "otherwise the per-request PEP looks up a key the per-page allowlist does not have");

		// Half 2 — and the same declaration on each of them.
		var split = byPage
			.Where(page => page
				.Select(e => e.Metadata.OfType<TenantDeclarationAttribute>().OrderBy(d => d.Describe(), StringComparer.Ordinal).ToList())
				.Select(d => string.Join(" + ", d.Select(x => x.Describe())))
				.Distinct(StringComparer.Ordinal)
				.Count() > 1)
			.Select(page => $"  {page.Key} ({page.Count()} endpoints)")
			.ToList();

		split.Should().BeEmpty(
			"a PageModel class attribute reaches EVERY endpoint of its page, so the per-endpoint PEP and the "
			+ "per-page inventory agree. A page whose endpoints disagree would be enforced on some of its "
			+ "routes and not others:\n" + string.Join("\n", split));
	}

	// THE STEP-5 TRIPWIRE for the same fork, from the other side. A page declares ONCE for all of its
	// routes, so a [TenantFrom(Route, "x")] on a PageModel is only answerable if EVERY route of that
	// page actually carries an "x". That is not automatic here:
	//
	//     /Config/Preview   ui/{workspaceKey}/config/preview
	//                       ui/{workspaceKey}/{projectKey}/config/preview
	//
	// — the same page, once workspace-scoped and once project-scoped. `[TenantFrom(Route,
	// "projectKey")]` on it would resolve to nothing on the first route, and an unresolved tenant is a
	// REFUSAL: the workspace-level config pages would start 403-ing the moment /Config/* left the
	// allowlist. The declaration those four pages want is the route value both of their routes have —
	// `[TenantFrom(Route, "workspaceKey", Tenant = TenantKind.Workspace)]` — which is also the tenant
	// ConfigApi.AuthorizeWorkspaceAsync already checks by hand, and a project-claimed key still reaches
	// it because ITenantAuthorizer resolves "a project claim authorizes its workspace" centrally.
	//
	// Written as a tripwire for step 5 BEFORE it could bite — at the time it was vacuous, because no
	// page declared yet and all 65 were allowlisted. It is no longer vacuous: the Razor wave landed, 42
	// pages declare a route tenant (the /Config/* four among them, naming `workspaceKey` for exactly
	// the reason above), and this now guards live declarations. It was verified to actually fire.
	[Fact]
	public void APageDeclaringARouteTenant_NamesARouteValueAllOfItsRoutesCarry()
	{
		var broken = _host.Factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
			.OfType<RouteEndpoint>()
			.Where(e => e.Metadata.GetMetadata<PageActionDescriptor>() is not null)
			.GroupBy(e => e.Metadata.GetMetadata<PageActionDescriptor>()!.ViewEnginePath, StringComparer.Ordinal)
			.SelectMany(page => page
				.SelectMany(e => e.Metadata.OfType<TenantFromAttribute>())
				.Where(d => d.Source == TenantSource.Route)
				.Select(d => d.Name)
				.Distinct(StringComparer.Ordinal)
				.Where(name => page.Any(e => !e.RoutePattern.Parameters.Any(p => string.Equals(p.Name, name, StringComparison.Ordinal))))
				.Select(name => $"  page:{page.Key} declares route value '{name}', missing from: "
					+ string.Join(" | ", page
						.Where(e => !e.RoutePattern.Parameters.Any(p => string.Equals(p.Name, name, StringComparison.Ordinal)))
						.Select(e => e.RoutePattern.RawText))))
			.ToList();

		broken.Should().BeEmpty(
			"a PageModel declares ONCE for every route of its page, so the route value it names must exist on "
			+ "all of them — on a route that lacks it the tenant resolves to nothing and the request is "
			+ "REFUSED. Declare the value the page's routes share (often workspaceKey + "
			+ "Tenant = TenantKind.Workspace), or split the page:\n" + string.Join("\n", broken));
	}

	// The MCP PEP re-reads the tool attributes by reflection (it has a tool NAME, not a MethodInfo, so
	// it cannot borrow the sweep's result). Two readers of the same attributes are two chances to
	// diverge — and a divergence here is silent in the worst direction: the ratchet would demand a
	// declaration the PEP does not see, or honour one the PEP misses.
	[Fact]
	public void TheEnforcementLookup_AgreesWithTheInventory()
	{
		var enforcement = McpTenantEnforcementFilter.Scan(typeof(Program).Assembly);

		enforcement.Keys.Should().BeEquivalentTo(_host.McpTools.Select(s => s.Id),
			"the PEP and the inventory sweep the same assembly for the same [McpServerTool] methods");

		// Compared by Describe(), not by instance: reflection hands out a FRESH attribute object per read,
		// so the two sides can never be reference-equal — what has to match is what they SAY.
		var disagreements = _host.McpTools
			.Where(s => !enforcement[s.Id].Select(d => d.Describe())
				.SequenceEqual(s.Declarations.Select(d => d.Describe()), StringComparer.Ordinal))
			.Select(s => $"  {s.Key}: inventory={s.Describe()} vs PEP="
				+ string.Join(" + ", enforcement[s.Id].Select(d => d.Describe())))
			.ToList();

		disagreements.Should().BeEmpty(
			"the method-wins-then-type lookup order must be identical on both sides:\n"
			+ string.Join("\n", disagreements));
	}

	// ── THE MACHINE INVENTORY ────────────────────────────────────────────────────────────────────

	// Point 4 of the work card: the enumeration must be able to EMIT the whole surface. Three
	// independent human counts of the MCP tools came back 92 / 100 / 122, so every number this work
	// item reports has to be reproducible by running this test and reading the file it writes.
	[Fact]
	public void TheInventory_IsMachineReadable()
	{
		var rendered = AuthzSurfaces.Render(_host.All);

		var dir = Path.Combine(RepoRoot(), ".tmp");
		Directory.CreateDirectory(dir);
		File.WriteAllText(Path.Combine(dir, "authz-surface-inventory.txt"), rendered);

		rendered.Split('\n').Length.Should().BeGreaterThan(_host.All.Count,
			"the rendered inventory carries a line per surface plus its header");
		_host.All.Select(s => s.Key).Should().OnlyHaveUniqueItems(
			"a surface key is an IDENTITY — two surfaces sharing one key means the allowlist cannot "
			+ "distinguish them, and crossing one out would exempt both");
	}

	// ── GUARD THE GUARD ──────────────────────────────────────────────────────────────────────────
	//
	// Every assertion above is an "is empty". If the sweep ever saw nothing — a host that failed to
	// boot its modules, an EndpointDataSource read before routing materialized, a renamed MCP
	// attribute — all of them would pass while protecting nothing. That failure mode is not
	// hypothetical in this repo (DbLayerGuardTests carries the same section for the same reason), so
	// it is tested rather than assumed.

	[Fact]
	public void TheSweep_ActuallySeesTheSurface()
	{
		var rest = _host.Endpoints.Where(s => s.Transport == AuthzTransport.Rest).ToList();
		var razor = _host.Endpoints.Where(s => s.Transport == AuthzTransport.Razor).ToList();

		rest.Should().HaveCountGreaterThan(40, "the REST surface is dozens of minimal-API endpoints");
		razor.Should().HaveCountGreaterThan(50, "Pages/** is ~100 .cshtml files");
		_host.McpTools.Should().HaveCountGreaterThan(80, "the MCP tool surface is ~100 verbs");

		// Anchors: one surface per KIND of mapping, so a sweep that silently stopped seeing one shape
		// fails here instead of quietly shrinking the inventory.
		var keys = _host.All.Select(s => s.Key).ToHashSet(StringComparer.Ordinal);

		keys.Should().Contain("rest:GET|HEAD /health",
			"an endpoint mapped INLINE in Program.cs — invisible to any sweep over *Api classes");
		keys.Should().Contain(k => k.StartsWith("rest:", StringComparison.Ordinal) && k.Contains("/api/config/", StringComparison.Ordinal),
			"a module's own *Api class must be swept (PetBox.Config)");
		keys.Should().Contain(k => k.StartsWith("rest:", StringComparison.Ordinal) && k.Contains("/api/data/", StringComparison.Ordinal),
			"a feature-gated module's endpoints must be swept (PetBox.Data)");
		keys.Should().Contain("rest:POST /api/events/raw",
			"a CONFIG-GATED endpoint must be swept too — this one is off by default and was flipped on by "
			+ "another test's environment variable, so the inventory used to depend on test ordering; the "
			+ "fixture now pins it on, and this anchor is what keeps it pinned");
		keys.Should().Contain("page:/Login", "a Razor page must be swept by its ViewEnginePath");
		keys.Should().Contain("page:/Admin/Projects", "…including the admin pages this work item is about");
		keys.Should().Contain("mcp:whoami").And.Contain("mcp:tasks_upsert").And.Contain("mcp:apikey_create",
			"the MCP verbs the exemption classes will have to name");

		// The /mcp transport is excluded BY CONSTRUCTION and its tools are swept instead — if that ever
		// inverts, the tool surface would vanish from the inventory behind one green endpoint.
		keys.Should().NotContain(k => k.StartsWith("rest:", StringComparison.Ordinal) && k.Contains(" /mcp", StringComparison.Ordinal),
			"/mcp is a transport, not a tenant surface — its ~100 tools are enumerated individually");

		// Owners must be real: an inventory that cannot say WHO owns an undeclared surface sends the
		// next reader hunting through Program.cs.
		_host.All.Should().OnlyContain(s => !string.IsNullOrWhiteSpace(s.Owner));
	}

	// The MCP plane is reflection over attributes — a second copy of the registration logic. This is
	// what keeps the copy honest: the tools the running SERVER registered, out of its own DI, must be
	// exactly the tools the sweep found. A renamed attribute, a tool type that stopped being
	// [McpServerToolType], or a registration path that changed shape all surface here.
	[Fact]
	public void TheMcpSweep_MatchesTheToolsTheServerActuallyRegisters()
	{
		var registered = _host.Factory.Services.GetServices<McpServerTool>()
			.Select(t => t.ProtocolTool.Name)
			.ToHashSet(StringComparer.Ordinal);

		var swept = _host.McpTools.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);

		registered.Should().NotBeEmpty("the host registers its MCP tools at startup");
		swept.Should().BeEquivalentTo(registered,
			"the reflection sweep mirrors McpOutputSchema.WithSchemaHonestToolsFromAssembly. If these "
			+ "diverge, the ratchet is guarding a set of tools that is not the set the server serves — "
			+ "which is exactly how a new verb ships unprotected while the guard stays green");
	}

	// The declaration TYPE is closed by the compiler (a private protected ctor), and the exemption LIST
	// is closed by the spec. Both closures are the reason an exemption is a decision rather than a
	// loophole, so both are pinned here: a seventh class, or a third kind of declaration, fails this
	// test and has to go argue with the spec.
	[Fact]
	public void TheDeclarationType_IsClosed()
	{
		var kinds = typeof(TenantDeclarationAttribute).Assembly.GetTypes()
			.Where(t => typeof(TenantDeclarationAttribute).IsAssignableFrom(t) && t != typeof(TenantDeclarationAttribute))
			.Select(t => t.Name)
			.Order(StringComparer.Ordinal)
			.ToList();

		kinds.Should().Equal(["TenantExemptAttribute", "TenantFromAttribute"],
			"a surface declares a tenant SOURCE or an exemption CLASS — nothing else. The base ctor is "
			+ "`private protected` so no other assembly can add a third kind; this pins the two inside "
			+ "PetBox.Core as well");

		Enum.GetNames<TenantExemption>().Order(StringComparer.Ordinal).Should().Equal(
			["CapabilityToken", "Feedback", "FleetWide", "Identity", "Provisioning", "Public"],
			"the six classes of spec `authz-scope-declaration`, and no seventh. An OPEN list is opt-in "
			+ "under another name: any new verb would declare itself an exception and leave default-deny "
			+ "with nobody noticing. Adding a class is a spec change — the owner's decision, not a PR");

		// The SOURCE list is closed too, but by a different authority, and the distinction is the whole
		// reason this assertion is worth making separately. The spec closes the EXEMPTION classes and
		// says so ("Класс вне закрытого перечня объявить нельзя"); it never enumerates sources at all.
		// So a source may be ADDED as an engineering decision inside a work item — but only by editing
		// the enum and this line together, which is what stops it happening by accident.
		//
		// ArgumentOrContainer was added by the MCP declaration wave for the memory family, whose
		// projectKey carries either a project key or a `$workspace`/`$ws-<key>` container. It is a
		// source rather than a flag because it decides the tenant KIND per CALL, from the value.
		Enum.GetNames<TenantSource>().Order(StringComparer.Ordinal).Should().Equal(
			["Argument", "ArgumentOrContainer", "BodyField", "CallerDefault", "Route"],
			"a body field is as strict as a route value, and a tenant inferred from the caller's own claim "
			+ "is as strict as one named explicitly. Adding a source is allowed and deliberate; adding one "
			+ "WITHOUT touching this line is not, because a source the PEP reads and nobody enumerated is "
			+ "the same invisible surface this ratchet exists to prevent");
	}

	// The CARRIERS must actually carry. A declaration that does not reach Endpoint.Metadata is a
	// comment: the ratchet would keep reporting the surface as undeclared and the future PEP would
	// refuse it. Each of the three carriers is exercised for real below.
	[Fact]
	public async Task EveryCarrier_ReachesTheReader()
	{
		// 1 + 2. Endpoint metadata, both ways a REST endpoint can be declared: an attribute on the
		//        handler METHOD (what a normal *Api endpoint will use) and the .DeclaresTenant()
		//        CONVENTION (for endpoints mapped where no method exists to attribute).
		//
		// A THROWAWAY app rather than the real one, because there is nothing PetBox-specific to prove
		// here — the question is whether ASP.NET carries these two shapes into Endpoint.Metadata at
		// all. (It cannot be done by re-Configure-ing a WebApplicationFactory<Program>: minimal
		// hosting refuses IWebHostBuilder.Configure outright.)
		var probe = WebApplication.CreateSlimBuilder().Build();
		await using var _ = probe;

		probe.MapGet("/probe/by-attribute", DeclaredHandler);
		probe.MapGet("/probe/by-convention", () => "ok")
			.DeclaresTenantExempt(TenantExemption.Public, "probe");

		var declared = AuthzSurfaces
			.FromEndpoints(((IEndpointRouteBuilder)probe).DataSources.SelectMany(d => d.Endpoints))
			.ToDictionary(s => s.Key, StringComparer.Ordinal);

		declared["rest:GET /probe/by-attribute"].Declarations.Should().ContainSingle()
			.Which.Should().BeOfType<TenantFromAttribute>().Which.Name.Should().Be("project",
				"an attribute on a minimal-API handler method must land in Endpoint.Metadata — if it does "
				+ "not, every REST declaration in this codebase is an inert comment");
		declared["rest:GET /probe/by-convention"].Declarations.Should().ContainSingle()
			.Which.Should().BeOfType<TenantExemptAttribute>(
				"the convention form must reach the same reader as the attribute form");

		// 3. The MCP carrier: an attribute on an [McpServerTool] method, and its fallback to the tool
		//    TYPE, read by the same sweep that produces the inventory.
		var probeTools = AuthzSurfaces.FromMcpTools(typeof(AuthzDeclarationRatchetTests).Assembly)
			.ToDictionary(s => s.Id, StringComparer.Ordinal);

		probeTools["probe_tool_declared_on_method"].Declarations.Should().ContainSingle()
			.Which.Should().BeOfType<TenantFromAttribute>().Which.Name.Should().Be("projectKey");
		probeTools["probe_tool_declared_on_type"].Declarations.Should().ContainSingle()
			.Which.Should().BeOfType<TenantExemptAttribute>().Which.Exemption.Should().Be(TenantExemption.Identity,
				"a declaration on the tool TYPE covers every tool in it — the 30 tasks_* verbs read one "
				+ "and the same argument, and making each repeat it is how one ends up different by accident");
	}

	// The Razor carrier, on the REAL application. Razor Pages builds its endpoints through the MVC
	// action-descriptor pipeline, not through RequestDelegateFactory, so "an attribute on the class
	// reaches Endpoint.Metadata" is a genuinely different mechanism from the REST carrier above — and
	// the one most likely to stop working silently after a framework upgrade.
	//
	// It is proven on an attribute that is ALREADY THERE ([AllowAnonymous] on the Login PageModel)
	// rather than on a probe page: a probe page under Pages/** would be a real, routable production
	// surface added for a test's benefit — and, by this very ratchet, one more undeclared surface.
	// What has to hold is the mechanism (class attribute → page endpoint metadata), and Login proves
	// it on the live descriptor pipeline.
	[Fact]
	public void TheRazorCarrier_ReachesTheReader()
	{
		var endpoints = _host.Factory.Services.GetRequiredService<EndpointDataSource>().Endpoints;

		var login = endpoints.SingleOrDefault(e =>
			e.Metadata.GetMetadata<PageActionDescriptor>()?.ViewEnginePath == "/Login");

		login.Should().NotBeNull("the Login page is mapped by MapRazorPages");
		login!.Metadata.OfType<AllowAnonymousAttribute>().Should().NotBeEmpty(
			"[AllowAnonymous] sits on the LoginModel CLASS and nowhere else, so its presence in the page "
			+ "endpoint's metadata is the proof that a class-level attribute reaches the reader. If this "
			+ "ever fails, [TenantFrom]/[TenantExempt] on a PageModel is an inert comment and ~100 pages "
			+ "can never leave the allowlist — the carrier would have to become a page convention instead");
	}

	// The handler the metadata probe above points at. It is a real method (not a lambda) because the
	// carrier under test is "an attribute on the handler METHOD".
	[TenantFrom(TenantSource.Route, "project")]
	static string DeclaredHandler() => "ok";

	// The repo root — where the machine inventory is written (.tmp/, gitignored).
	static string RepoRoot()
	{
		var dir = AppContext.BaseDirectory;
		while (!string.IsNullOrEmpty(dir))
		{
			if (Directory.Exists(Path.Combine(dir, "src", "PetBox.Web"))) return dir;
			dir = Path.GetDirectoryName(dir);
		}

		throw new DirectoryNotFoundException("repo root (with src/PetBox.Web) not found walking up from the test bin.");
	}
}

// Two probe tools, so the MCP carrier is proven on both of its lookup steps. They live in the TEST
// assembly and are swept only by the carrier test above — the production inventory sweeps
// PetBox.Web's assembly, so these can never leak into it.
[McpServerToolType]
[TenantExempt(TenantExemption.Identity, "probe: a declaration on the tool TYPE")]
public static class AuthzProbeTools
{
	[McpServerTool(Name = "probe_tool_declared_on_method")]
	[TenantFrom(TenantSource.Argument, "projectKey")]
	public static string OnMethod() => "ok";

	[McpServerTool(Name = "probe_tool_declared_on_type")]
	public static string OnType() => "ok";
}
