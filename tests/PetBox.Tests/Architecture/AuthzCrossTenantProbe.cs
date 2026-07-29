using System.Net;
using System.Text;
using System.Text.Json;
using LinqToDB;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using PetBox.Core.Auth;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Core.Settings;

namespace PetBox.Tests.Architecture;

// THE CROSS-TENANT PROBE — work `authz-default-deny-delivery`, step 4.
//
// It takes THE SAME enumeration the ratchet guards (AuthzSurfaces, 217 surfaces) and, for every one
// of them, performs one real call from a principal belonging to ANOTHER tenant. The question asked
// of each surface is the one the spec answers with default-deny: *what happens on this surface when
// the caller is not entitled to the tenant it names?*
//
// WHY GARBAGE ARGUMENTS ARE ENOUGH — this is the whole economy of the step. A default-deny decision
// happens BEFORE argument binding, so a correct surface cannot tell a well-formed foreign request
// from a malformed one: both are refused for the same reason. That makes the assertion expressible
// without knowing a single signature:
//
//     foreign tenant + garbage everywhere else  =>  an AUTHORIZATION denial,
//                                                   never an argument error, never a success.
//
// The contrapositive is what makes it worth running: a surface that answers "400 — field `name` is
// required" has proven it bound the body BEFORE it decided, and a surface that answers 200 has
// proven it never decided at all.
//
// THE ATTACKER CARRIES EVERY SCOPE. The scope axis (ApiKeyScopes / ScopeRequirement) is already
// centralised and already works; if the probe key were missing scopes, most of the surface would
// deny on THAT axis and the run would be a field of false greens that says nothing about tenancy.
// So the key holds all 26 scopes — including `admin:provision`. The provisioning verbs consequently
// ALLOW the foreign tenant, which is not a bug but the `Provisioning` exemption class of spec
// `authz-scope-declaration` showing up as a number instead of a footnote.
//
// The probe RECORDS; it does not judge. Verdicts are assigned by observation only, and the
// judgement (which verdicts are acceptable, which are documented debt) lives in
// AuthzCrossTenantTests next to the two explicit lists.

public enum CrossTenantVerdict
{
	// The surface refused on the AUTHORIZATION axis: 401/403, a redirect to /AccessDenied or /Login,
	// or an MCP UnauthorizedAccessException envelope. This is the only PASS.
	Denied,

	// 2xx / a non-error MCP result. The foreign tenant got served. THIS IS A HOLE.
	Allowed,

	// 404 — an existence answer where an authorization answer was due. Not a hole by itself (the
	// tenant's data is not disclosed), but it is a different denial FORM, and on a surface where the
	// object genuinely exists it is the difference between "you may not" and "there is nothing here".
	NotFound,

	// 4xx that is not 401/403/404, or an MCP envelope whose exception type is an argument/validation
	// failure. The surface bound the request BEFORE it decided who was asking.
	ArgumentError,

	// 5xx, or an MCP envelope of some non-authorization runtime failure.
	ServerError,

	// The call did not come back inside the probe budget — long-poll, server-sent events, an
	// outbound dependency. Counted, named, and never silently dropped.
	Timeout,
}

// One surface + the one call the probe made against it + what came back.
//
// `Addressed` is kept SEPARATE from the verdict on purpose. It answers a question about the surface,
// not about the response: was there anywhere in this call to WRITE the victim's tenant? A route with
// no {projectKey}/{workspaceKey} and a tool whose schema has neither takes its tenant (if it has one)
// from the caller's own claim, so "call it as somebody else" has no meaning — nothing crossed, and a
// 200 there is the attacker being served their OWN tenant. Folding that into the verdict would have
// let ~40 surfaces read as passes for a reason that has nothing to do with authorization.
public sealed record CrossTenantProbe(
	AuthzSurface Surface,
	string Call,
	bool Addressed,
	CrossTenantVerdict Verdict,
	string Observed)
{
	public string Key => Surface.Key;
}

// A booted host with TWO tenants and one attacker principal per transport, probed once for the whole
// class. Boot configuration is deliberately identical to AuthzSurfaceHost's — every module on, every
// optional endpoint pinned on — because the probe must see the SAME 217 surfaces the ratchet counts;
// a host with one module off would report its endpoints as "not probed" and quietly shrink the run.
public sealed class AuthzCrossTenantHost : IAsyncLifetime
{
	// The victim: a real workspace with a real project inside it. Both exist, so a 404 from a probe
	// is a statement about the SURFACE, never about missing seed data.
	const string VictimWorkspace = "victimws";
	public const string VictimProject = "victimproj";

	// The attacker: an equally real tenant next door. The API key is scoped to `attackerproj`; the
	// browser user is a Member of `attackerws` and of nothing else (never a sysadmin — the sysadmin
	// free pass is a different axis and would mask every page result).
	const string AttackerWorkspace = "attackerws";
	public const string AttackerProject = "attackerproj";

	const string AttackerUser = "crosstenant-attacker";
	const string Password = "test123";
	const string PasswordHashValue = "pbkdf2$100000$h1twJi/he3s8S7jSM9pkGQ==$efnLBffww5Gprn6BjpNgZkTcG+1zNu2L6z3TZ7YvD/o=";

	// One call gets this long. Everything past it is a Timeout verdict rather than a hung suite: a
	// couple of these surfaces are a long-poll and a server-sent-event stream that never complete by
	// design.
	static readonly TimeSpan CallBudget = TimeSpan.FromSeconds(10);

	readonly string _baseDir;

	WebApplicationFactory<Program> Factory { get; }
	string AttackerApiKey { get; } = $"yb_key_{Guid.NewGuid():N}";

	public IReadOnlyList<AuthzSurface> Surfaces { get; private set; } = [];
	public IReadOnlyList<CrossTenantProbe> Probes { get; private set; } = [];

	// THE RAZOR BLIND SPOT, CLOSED — one probe per (page, POST handler) pair, kept in its OWN list.
	//
	// `Probes` above is one call per SURFACE, and a Razor page is one surface however many handlers it
	// has: that is what makes the 217 accounting add up, so a page's eight `?handler=` mutations cannot
	// go in there without redefining what a surface is. They are a real question all the same, and until
	// this list existed nobody had asked it — the page sweep only ever sent GET, so every OnPost*Async in
	// the tree (82 of them across 31 pages) was unmeasured. A mutation through another tenant's project
	// is strictly worse than a read of one, so the untested half was the dangerous half.
	//
	// This is NOT folded into the accounting pins in AuthzCrossTenantTests. It is asserted on its own, in
	// AuthzCrossTenantPostHandlerTests, so the 217/144 numbers keep meaning exactly what they meant.
	public IReadOnlyList<CrossTenantProbe> PagePostProbes { get; private set; } = [];

	string AttackerCookie { get; set; } = "";
	public IReadOnlyList<string> ToolsVisibleToAttacker { get; private set; } = [];

	// The server's OWN account of who the probe key is — whoami, untruncated. The guard-the-guard
	// reads it: the deviation list blames two root-equivalent scopes for the surfaces a foreign tenant
	// reaches, and this is the evidence that the probe actually held them.
	public string AttackerWhoAmI { get; private set; } = "";

	// THE CONTROL GROUP. Every assertion in AuthzCrossTenantTests is "this was DENIED", and a probe
	// whose attacker had simply stopped working — a key that never authenticated, a cookie that
	// expired, a host that refuses everything — would make all of them pass while proving nothing.
	// These three calls are the same attacker aiming at its OWN tenant on all three transports; they
	// must be SERVED. If they are not, the run is void, not green.
	public IReadOnlyList<(string Name, bool Served, string Observed)> SelfControls { get; private set; } = [];

	public AuthzCrossTenantHost()
	{
		_baseDir = Path.Combine(Path.GetTempPath(), "petbox-xtenant-" + Guid.NewGuid().ToString("N"));
		Factory = new WebApplicationFactory<Program>()
			.WithWebHostBuilder(b =>
			{
				b.UseEnvironment("Testing");
				b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
				{
					["ConnectionStrings:PetBox"] = TestSchema.NewTempConnectionString("petbox-xtenant"),
					["Host:BackgroundServices"] = "false",

					// Same module/endpoint pinning as AuthzSurfaceHost — see the long note there. The
					// probe and the ratchet MUST see one and the same inventory, so this block is a
					// copy on purpose: a divergence would show up as surfaces the probe never called.
					["Features:Config"] = "true",
					["Features:Logging"] = "true",
					["Features:Data"] = "true",
					["Features:Dashboard"] = "true",
					["Features:Tasks"] = "true",
					["Features:Memory"] = "true",
					["Features:LlmRouter"] = "true",
					["Features:Deploy"] = "true",
					["Seq:SelfLog:Enabled"] = "true",

					// Needed for the cookie plane: without a bootstrap admin the Login page has no
					// credential store to authenticate the attacker user against.
					["Admin:Username"] = "admin",
					["Admin:PasswordHash"] = PasswordHashValue,
				}));

				// Keep module storage inside this host's own temp dir (the ModuleViewsFixture idiom).
				b.ConfigureServices(svc =>
				{
					Replace<PetBox.Tasks.Data.TasksDb>(svc, "tasks",
						c => new PetBox.Tasks.Data.TasksDb(PetBox.Tasks.Data.TasksDb.CreateOptions(c)),
						TestSchema.Tasks);
					Replace<PetBox.Memory.Data.MemoryDb>(svc, "memory",
						c => new PetBox.Memory.Data.MemoryDb(PetBox.Memory.Data.MemoryDb.CreateOptions(c)),
						TestSchema.Memory);
					Replace<PetBox.Sessions.Data.SessionsDb>(svc, "sessions",
						c => new PetBox.Sessions.Data.SessionsDb(PetBox.Sessions.Data.SessionsDb.CreateOptions(c)),
						TestSchema.Sessions);
				});
			});
	}

	void Replace<TDb>(IServiceCollection svc, string sub, Func<string, TDb> create, Action<string> ensure)
		where TDb : LinqToDB.Data.DataConnection
	{
		var existing = svc.SingleOrDefault(d => d.ServiceType == typeof(IScopedDbFactory<TDb>));
		if (existing is not null) svc.Remove(existing);
		svc.AddSingleton<IScopedDbFactory<TDb>>(_ => new ScopedDbFactory<TDb>(
			Path.Combine(_baseDir, sub), Scope.Project, create, ensure));
	}

	public async ValueTask InitializeAsync()
	{
		var cs = Factory.Services.GetRequiredService<IConfiguration>().GetConnectionString("PetBox")!;
		TestSchema.Core(cs);

		using var client = Factory.CreateClient(new WebApplicationFactoryClientOptions
		{
			AllowAutoRedirect = false,
			HandleCookies = false,
		});
		using (var _ = await client.GetAsync("/health")) { }

		await SeedTenantsAsync();

		var endpoints = Factory.Services.GetRequiredService<EndpointDataSource>().Endpoints;
		var routed = RoutesBySurfaceKey(endpoints);
		var endpointSurfaces = AuthzSurfaces.FromEndpoints(endpoints);
		var mcpSurfaces = AuthzSurfaces.FromMcpTools(typeof(Program).Assembly);
		Surfaces = [.. endpointSurfaces.Concat(mcpSurfaces)];

		AttackerCookie = await LoginAsync(client, AttackerUser);

		var probes = new List<CrossTenantProbe>();
		foreach (var surface in endpointSurfaces)
			probes.Add(await ProbeEndpointAsync(client, surface, routed));

		probes.AddRange(await ProbeMcpAsync(mcpSurfaces));

		Probes = [.. probes.OrderBy(p => p.Key, StringComparer.Ordinal)];
		PagePostProbes = await ProbePagePostHandlersAsync(client, endpoints);
		SelfControls = await SelfControlsAsync(client);
	}

	// ── THE RAZOR POST SWEEP ─────────────────────────────────────────────────────────────────────

	// Every `?handler=` MUTATION on every page that has somewhere to write the victim's tenant, aimed at
	// the victim, from the attacker's browser session.
	//
	// WHY IT NEEDS AN ANTIFORGERY TOKEN AT ALL, given that the tenant PEP is middleware and runs long
	// before MVC's antiforgery filter: because without one every answer would be a 400 and the sweep
	// would be measuring ITSELF, which is the exact mistake the old 415 note in AuthzCrossTenantTests
	// records ("the probe now retries with the content type the ENDPOINT declares … it was measuring the
	// probe's own content-type guess"). A token makes the 400 case meaningful instead of universal, and
	// the assertion side treats any 400 as INCONCLUSIVE and fails on it rather than scoring it a denial.
	//
	// The token is harvested from a page the attacker CAN reach (/ui/me/preferences — `identity`-exempt,
	// so it renders for anybody signed in). Antiforgery tokens bind to the IDENTITY, not to the URL, so a
	// pair minted there is valid on any POST this session makes — which is precisely why CSRF protection
	// is not, and must not be mistaken for, a tenant boundary.
	async Task<IReadOnlyList<CrossTenantProbe>> ProbePagePostHandlersAsync(
		HttpClient client, IReadOnlyList<Endpoint> endpoints)
	{
		var (token, antiforgeryCookie) = await AntiforgeryPairAsync(client);
		var probes = new List<CrossTenantProbe>();

		foreach (var endpoint in endpoints.OfType<RouteEndpoint>())
		{
			if (endpoint.Metadata.GetMetadata<PageActionDescriptor>() is not CompiledPageActionDescriptor page)
				continue;

			// Only pages with a tenant slot in the ROUTE — the same `Addressed` question the GET sweep
			// asks. A page with nowhere to write the victim's key cannot be aimed at them by POST either.
			var (url, hasTenant) = FillRoute(endpoint.RoutePattern);
			if (!hasTenant) continue;

			foreach (var handler in page.HandlerMethods)
			{
				if (!string.Equals(handler.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase)) continue;

				var target = string.IsNullOrEmpty(handler.Name)
					? url
					: url + (url.Contains('?', StringComparison.Ordinal) ? "&" : "?") + "handler=" + handler.Name;

				// The ROUTE TEMPLATE is part of the id, not just the page and the handler — because a page
				// mapped twice must be probed twice. That is the whole Config family (one workspace-scoped
				// template, one project-scoped) plus TaskBoardNode, i.e. exactly the pages whose single
				// class-level declaration has to be answerable on BOTH of their routes. Keying by page alone
				// would have collapsed each pair into one probe and dropped the template that carries fewer
				// tenant slots — which is the one a mis-declaration breaks.
				probes.Add(await ProbePagePostAsync(
					client,
					new AuthzSurface(
						AuthzTransport.Razor,
						$"{page.ViewEnginePath}?handler={(handler.Name is { Length: > 0 } n ? n : "(default)")}"
							+ $" [{endpoint.RoutePattern.RawText}]",
						page.ModelTypeInfo?.FullName ?? page.RelativePath,
						[.. endpoint.Metadata.OfType<TenantDeclarationAttribute>()]),
					target, hasTenant, token, antiforgeryCookie));
			}
		}

		return [.. probes.OrderBy(p => p.Key, StringComparer.Ordinal)];
	}

	async Task<CrossTenantProbe> ProbePagePostAsync(
		HttpClient client, AuthzSurface surface, string url, bool hasTenant, string token, string antiforgeryCookie)
	{
		// The victim's keys in every field name a form on this plane might bind them under, plus the
		// token. Garbage for everything else, exactly as the GET sweep does it: a page that decides before
		// it binds cannot notice, and one that binds first says 400 and tells us so.
		using var req = new HttpRequestMessage(HttpMethod.Post, url)
		{
			Content = new FormUrlEncodedContent(new Dictionary<string, string>
			{
				["__RequestVerificationToken"] = token,
				["projectKey"] = VictimProject,
				["workspaceKey"] = VictimWorkspace,
				["ws"] = VictimWorkspace,
				["key"] = VictimProject,
				["petbox_cross_tenant_probe"] = "true",
			}),
		};
		req.Headers.Add("Cookie", $"{AttackerCookie}; {antiforgeryCookie}");

		var call = $"POST {url}";
		using var cts = new CancellationTokenSource(CallBudget);
		try
		{
			using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
			var location = resp.Headers.Location?.ToString();
			return new CrossTenantProbe(surface, call, hasTenant, VerdictOf(resp),
				$"{(int)resp.StatusCode} {resp.StatusCode}" + (location is null ? "" : $" -> {location}"));
		}
		catch (OperationCanceledException)
		{
			return new CrossTenantProbe(surface, call, hasTenant, CrossTenantVerdict.Timeout,
				$"no response within {CallBudget.TotalSeconds:0}s");
		}
	}

	// A token + cookie pair valid for the attacker's own authenticated session.
	async Task<(string Token, string Cookie)> AntiforgeryPairAsync(HttpClient client)
	{
		const string formPage = "/ui/me/preferences";

		using var req = new HttpRequestMessage(HttpMethod.Get, formPage);
		req.Headers.Add("Cookie", AttackerCookie);
		using var resp = await client.SendAsync(req);
		if (resp.StatusCode != HttpStatusCode.OK)
			throw new InvalidOperationException(
				$"the POST sweep could not reach {formPage} as the attacker ({(int)resp.StatusCode}) to mint an "
				+ "antiforgery pair. Without one every POST below answers 400 and the sweep measures itself.");

		var html = await resp.Content.ReadAsStringAsync();
		var tokenStart = html.IndexOf("__RequestVerificationToken", StringComparison.Ordinal);
		if (tokenStart < 0)
			throw new InvalidOperationException(
				$"{formPage} rendered no __RequestVerificationToken; the POST sweep has no token to send.");
		var valueStart = html.IndexOf("value=\"", tokenStart, StringComparison.Ordinal) + 7;
		var token = html[valueStart..html.IndexOf('"', valueStart)];

		var cookie = resp.Headers.TryGetValues("Set-Cookie", out var setCookies)
			? setCookies.FirstOrDefault(c => c.Contains("Antiforgery", StringComparison.OrdinalIgnoreCase))?.Split(';')[0]
			: null;

		return (token, cookie ?? "");
	}

	// The attacker, aimed at itself. Anything other than "served" here voids the whole run.
	async Task<List<(string, bool, string)>> SelfControlsAsync(HttpClient client)
	{
		var controls = new List<(string, bool, string)>();

		using (var rest = new HttpRequestMessage(HttpMethod.Get, $"/api/sessions/{AttackerProject}"))
		{
			rest.Headers.Add(ApiKeyAuthenticationHandler.ApiKeyHeader, AttackerApiKey);
			using var resp = await client.SendAsync(rest);
			controls.Add(($"rest GET /api/sessions/{AttackerProject}",
				resp.IsSuccessStatusCode, $"{(int)resp.StatusCode} {resp.StatusCode}"));
		}

		using (var page = new HttpRequestMessage(HttpMethod.Get, $"/ui/{AttackerWorkspace}/{AttackerProject}"))
		{
			page.Headers.Add("Cookie", AttackerCookie);
			using var resp = await client.SendAsync(page);
			controls.Add(($"page GET /ui/{AttackerWorkspace}/{AttackerProject}",
				resp.IsSuccessStatusCode, $"{(int)resp.StatusCode} {resp.StatusCode}"));
		}

		using var http = Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
		http.DefaultRequestHeaders.Add(ApiKeyAuthenticationHandler.ApiKeyHeader, AttackerApiKey);
		await using var mcp = await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
		{
			Endpoint = new Uri(http.BaseAddress!, "/mcp"),
			AdditionalHeaders = new Dictionary<string, string>
			{
				[ApiKeyAuthenticationHandler.ApiKeyHeader] = AttackerApiKey,
			},
		}, http), cancellationToken: default);

		var result = await mcp.CallToolAsync(
			"tasks_board_list", new Dictionary<string, object?> { ["projectKey"] = AttackerProject });
		controls.Add(($"mcp tasks_board_list(projectKey={AttackerProject})", result.IsError != true,
			Short(string.Join(" ", result.Content.OfType<TextContentBlock>().Select(c => c.Text)))));

		return controls;
	}

	public async ValueTask DisposeAsync()
	{
		await Factory.DisposeAsync();
		TestDirs.CleanupOrDefer(_baseDir);
	}

	// ── SEEDING ──────────────────────────────────────────────────────────────────────────────────

	async Task SeedTenantsAsync()
	{
		using var scope = Factory.Services.CreateScope();
		using var db = scope.ServiceProvider.GetRequiredService<ICoreDbFactory>().Open();

		var now = DateTime.UtcNow;
		await db.InsertAsync(new Workspace { Key = VictimWorkspace, Name = "Victim", Description = "", CreatedAt = now });
		await db.InsertAsync(new Workspace { Key = AttackerWorkspace, Name = "Attacker", Description = "", CreatedAt = now });
		await db.InsertAsync(new Project { Key = VictimProject, WorkspaceKey = VictimWorkspace, Name = "Victim", Description = "" });
		await db.InsertAsync(new Project { Key = AttackerProject, WorkspaceKey = AttackerWorkspace, Name = "Attacker", Description = "" });

		// EVERY scope, on purpose — see the header. A missing scope would deny on the scope axis and
		// tell us nothing about the tenant axis.
		await db.InsertAsync(new ApiKey
		{
			Key = AttackerApiKey,
			ProjectKey = AttackerProject,
			Scopes = string.Join(",", ApiKeyScopes.All.Select(s => s.Value)),
			Name = "cross-tenant probe",
			CreatedAt = now,
		});

		var userId = await db.InsertWithInt64IdentityAsync(new User
		{
			Username = AttackerUser,
			PasswordHash = PasswordHashValue,
			CreatedAt = now,
		});
		await db.Factory().SeedMemberAsync(userId, AttackerWorkspace, WorkspaceRole.Member);
	}

	static async Task<string> LoginAsync(HttpClient client, string username)
	{
		using var loginPage = await client.GetAsync("/Login");
		var html = await loginPage.Content.ReadAsStringAsync();
		var tokenStart = html.IndexOf("__RequestVerificationToken", StringComparison.Ordinal);
		var valueStart = html.IndexOf("value=\"", tokenStart, StringComparison.Ordinal) + 7;
		var token = html[valueStart..html.IndexOf('"', valueStart)];
		var afCookie = loginPage.Headers.GetValues("Set-Cookie")
			.First(c => c.Contains("Antiforgery", StringComparison.OrdinalIgnoreCase)).Split(';')[0];

		using var req = new HttpRequestMessage(HttpMethod.Post, "/Login")
		{
			Content = new FormUrlEncodedContent(new Dictionary<string, string>
			{
				["username"] = username,
				["password"] = Password,
				["__RequestVerificationToken"] = token,
			}),
		};
		req.Headers.Add("Cookie", afCookie);
		using var resp = await client.SendAsync(req);
		if (resp.StatusCode != HttpStatusCode.Redirect)
			throw new InvalidOperationException(
				$"the cross-tenant probe could not sign in as '{username}' ({(int)resp.StatusCode}); every Razor "
				+ "surface would then answer 302 /Login and the whole page plane would be a false green.");

		return resp.Headers.GetValues("Set-Cookie")
			.First(c => c.StartsWith(".AspNetCore.Cookies", StringComparison.OrdinalIgnoreCase)).Split(';')[0];
	}

	// ── THE ENDPOINT PLANE ───────────────────────────────────────────────────────────────────────

	// surface key -> every endpoint that produced it. The key is obtained by running the SAME reader
	// the ratchet uses over one endpoint at a time, so the probe can never address a surface by a key
	// AuthzSurfaces would not have minted (and /mcp stays excluded by that reader's own construction).
	static Dictionary<string, List<RouteEndpoint>> RoutesBySurfaceKey(IEnumerable<Endpoint> endpoints)
	{
		var map = new Dictionary<string, List<RouteEndpoint>>(StringComparer.Ordinal);
		foreach (var endpoint in endpoints.OfType<RouteEndpoint>())
		{
			if (AuthzSurfaces.FromEndpoints([endpoint]).SingleOrDefault() is not { } surface) continue;
			if (!map.TryGetValue(surface.Key, out var list)) map[surface.Key] = list = [];
			list.Add(endpoint);
		}

		return map;
	}

	async Task<CrossTenantProbe> ProbeEndpointAsync(
		HttpClient client, AuthzSurface surface, Dictionary<string, List<RouteEndpoint>> routed)
	{
		if (!routed.TryGetValue(surface.Key, out var candidates) || candidates.Count == 0)
			return new CrossTenantProbe(surface, "(no route)", false, CrossTenantVerdict.Timeout,
				"the surface has no RouteEndpoint — the probe could not address it");

		// A page mapped several ways (with and without its route template) is ONE surface; take the
		// mapping that names the most tenants, because that is the one an attacker would type.
		var endpoint = candidates
			.OrderByDescending(e => TenantSlots(e.RoutePattern))
			.ThenByDescending(e => e.RoutePattern.RawText?.Length ?? 0)
			.First();

		var (url, hasTenant) = FillRoute(endpoint.RoutePattern);
		var method = MethodOf(endpoint);

		using var req = new HttpRequestMessage(method, url);
		if (surface.Transport == AuthzTransport.Razor)
			req.Headers.Add("Cookie", AttackerCookie);
		else
			req.Headers.Add(ApiKeyAuthenticationHandler.ApiKeyHeader, AttackerApiKey);

		// GARBAGE, deliberately — with the victim's tenant written into it as well. The garbage is the
		// point (a surface that decides before it binds cannot notice; one that binds first answers
		// 400 and says so); the tenant fields ride along so that a handler taking its tenant from a
		// BODY FIELD (TenantSource.BodyField) is aimed at the victim rather than at nothing. Whether
		// any given handler reads them is not knowable from here, which is why `Addressed` below is
		// still decided by the ROUTE alone — see the not-addressable list in AuthzCrossTenantTests.
		if (method != HttpMethod.Get && method != HttpMethod.Head)
			req.Content = new StringContent(
				$$"""{"projectKey":"{{VictimProject}}","workspaceKey":"{{VictimWorkspace}}","petbox_cross_tenant_probe":true}""",
				Encoding.UTF8, "application/json");

		var call = $"{method.Method} {url}";
		using var cts = new CancellationTokenSource(CallBudget);
		try
		{
			using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);

			// 415 is the probe's own fault, not the surface's, and it is decided ABOVE every middleware:
			// MVC's ConsumesMatcherPolicy is an IEndpointSelectorPolicy, so it runs inside UseRouting and
			// short-circuits to a non-route 415 endpoint before authentication, authorization or the
			// tenant PEP get a request at all. No placement of a middleware can answer in front of it —
			// which means a 415 here measures the probe's content-type guess and NOTHING about the
			// surface's authorization. So retry with the content type the ENDPOINT ITSELF declares
			// (AcceptsMetadata — `.Accepts<T>(...)` or an inferred [FromForm] binding), falling back to
			// text/plain when it declares none.
			if (resp.StatusCode == HttpStatusCode.UnsupportedMediaType)
				return await RetryWithDeclaredContentTypeAsync(client, surface, endpoint, method, url, call, hasTenant);

			var location = resp.Headers.Location?.ToString();
			var observed = $"{(int)resp.StatusCode} {resp.StatusCode}"
				+ (location is null ? "" : $" -> {location}");
			return new CrossTenantProbe(surface, call, hasTenant, VerdictOf(resp), observed);
		}
		catch (OperationCanceledException)
		{
			return new CrossTenantProbe(surface, call, hasTenant, CrossTenantVerdict.Timeout,
				$"no response within {CallBudget.TotalSeconds:0}s");
		}
		catch (Exception ex)
		{
			return new CrossTenantProbe(surface, call, hasTenant, CrossTenantVerdict.ServerError,
				$"{ex.GetType().Name}: {Short(ex.Message)}");
		}
	}

	// The retry that gets PAST routing's content-type gate — see the call site for why a 415 is never a
	// statement about the surface. The victim's tenant still rides in the body wherever the encoding can
	// carry it (a form post gets form fields, so a [FromForm] tenant is aimed at the victim exactly as
	// the JSON attempt aims a BodyField one).
	async Task<CrossTenantProbe> RetryWithDeclaredContentTypeAsync(
		HttpClient client, AuthzSurface surface, RouteEndpoint endpoint,
		HttpMethod method, string url, string call, bool hasTenant)
	{
		var declared = endpoint.Metadata.GetMetadata<IAcceptsMetadata>()?.ContentTypes;
		var contentType = declared is { Count: > 0 } ? declared[0] : "text/plain";

		using var req = new HttpRequestMessage(method, url)
		{
			Content = BodyFor(contentType),
		};
		if (surface.Transport == AuthzTransport.Razor)
			req.Headers.Add("Cookie", AttackerCookie);
		else
			req.Headers.Add(ApiKeyAuthenticationHandler.ApiKeyHeader, AttackerApiKey);

		var retried = $"{call} [{contentType}]";
		using var cts = new CancellationTokenSource(CallBudget);
		try
		{
			using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
			var location = resp.Headers.Location?.ToString();
			return new CrossTenantProbe(surface, retried, hasTenant, VerdictOf(resp),
				$"{(int)resp.StatusCode} {resp.StatusCode}" + (location is null ? "" : $" -> {location}"));
		}
		catch (OperationCanceledException)
		{
			return new CrossTenantProbe(surface, retried, hasTenant, CrossTenantVerdict.Timeout,
				$"no response within {CallBudget.TotalSeconds:0}s");
		}
	}

	// Garbage in the encoding the endpoint asked for, with the victim's tenant written into it wherever
	// the encoding has a place for a named field.
	static HttpContent BodyFor(string contentType) => contentType switch
	{
		// A form-binding endpoint declares BOTH form encodings; urlencoded is the one that is trivial to
		// build correctly, and an endpoint that accepts multipart accepts it too.
		"application/x-www-form-urlencoded" or "multipart/form-data" => new FormUrlEncodedContent(new Dictionary<string, string>
		{
			["projectKey"] = VictimProject,
			["workspaceKey"] = VictimWorkspace,
			["ws"] = VictimWorkspace,
			["key"] = VictimProject,
			["petbox_cross_tenant_probe"] = "true",
		}),
		var json when json.Contains("json", StringComparison.OrdinalIgnoreCase) => new StringContent(
			$$"""{"projectKey":"{{VictimProject}}","workspaceKey":"{{VictimWorkspace}}","petbox_cross_tenant_probe":true}""",
			Encoding.UTF8, json),
		_ => new StringContent("petbox cross tenant probe", Encoding.UTF8, contentType),
	};

	static CrossTenantVerdict VerdictOf(HttpResponseMessage resp)
	{
		var code = (int)resp.StatusCode;

		if (code is 401 or 403) return CrossTenantVerdict.Denied;

		// A redirect to the sign-in or access-denied page is the cookie plane's denial form.
		if (code is >= 300 and < 400)
		{
			var to = resp.Headers.Location?.ToString() ?? "";
			return to.Contains("/AccessDenied", StringComparison.OrdinalIgnoreCase)
				|| to.Contains("/Login", StringComparison.OrdinalIgnoreCase)
					? CrossTenantVerdict.Denied
					: CrossTenantVerdict.Allowed;
		}

		if (code == 404) return CrossTenantVerdict.NotFound;
		if (code >= 500) return CrossTenantVerdict.ServerError;
		if (code >= 400) return CrossTenantVerdict.ArgumentError;
		return CrossTenantVerdict.Allowed;
	}

	static HttpMethod MethodOf(RouteEndpoint endpoint)
	{
		var methods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? [];
		if (methods.Count == 0) return HttpMethod.Get;
		if (methods.Contains("GET")) return HttpMethod.Get;
		return new HttpMethod(methods.Order(StringComparer.Ordinal).First());
	}

	static int TenantSlots(RoutePattern pattern) =>
		pattern.Parameters.Count(p => TenantValueFor(p.Name, pattern) is not null);

	// The two encodings of a tenant that appear in a ROUTE. `{key}` under /workspaces/ is the third
	// spelling of a workspace key (the sysadmin workspace-detail page) and is named here rather than
	// missed — missing it would have scored that page NoTenantSlot, i.e. silently unprobed.
	static string? TenantValueFor(string? name, RoutePattern pattern) => name switch
	{
		"projectKey" or "project" => VictimProject,
		"workspaceKey" or "workspace" => VictimWorkspace,
		"key" when (pattern.RawText ?? "").Contains("/workspaces/", StringComparison.Ordinal) => VictimWorkspace,
		_ => null,
	};

	// Builds a concrete URL from a route template: tenant slots get the VICTIM's keys, every other
	// slot gets a value chosen only to make the route MATCH (an int constraint gets "1", everything
	// else gets a nonsense string). Matching is the point — a route that does not match answers 404
	// for a routing reason and tells us nothing about authorization.
	static (string Url, bool HasTenant) FillRoute(RoutePattern pattern)
	{
		var sb = new StringBuilder();
		var hasTenant = false;

		foreach (var segment in pattern.PathSegments)
		{
			var text = new StringBuilder();
			foreach (var part in segment.Parts)
			{
				switch (part)
				{
					case RoutePatternLiteralPart literal:
						text.Append(literal.Content);
						break;
					case RoutePatternSeparatorPart separator:
						text.Append(separator.Content);
						break;
					case RoutePatternParameterPart parameter:
						if (TenantValueFor(parameter.Name, pattern) is { } tenant)
						{
							hasTenant = true;
							text.Append(tenant);
						}
						else
						{
							text.Append(FillerFor(parameter));
						}

						break;
				}
			}

			if (text.Length > 0) sb.Append('/').Append(text);
		}

		return (sb.Length == 0 ? "/" : sb.ToString(), hasTenant);
	}

	static string FillerFor(RoutePatternParameterPart parameter)
	{
		var numeric = parameter.ParameterPolicies.Any(p =>
			p.Content is "long" or "int" or "guid" or "decimal" or "double" or "float"
			|| (p.Content ?? "").StartsWith("min(", StringComparison.Ordinal)
			|| (p.Content ?? "").StartsWith("range(", StringComparison.Ordinal));
		if (numeric) return parameter.ParameterPolicies.Any(p => p.Content == "guid")
			? Guid.Empty.ToString()
			: "1";

		return parameter.IsCatchAll ? "petbox-probe" : "petbox-probe";
	}

	// ── THE MCP PLANE ────────────────────────────────────────────────────────────────────────────

	async Task<List<CrossTenantProbe>> ProbeMcpAsync(IReadOnlyList<AuthzSurface> surfaces)
	{
		// The tool's own generated input schema says whether it HAS a tenant slot and what it is
		// called. Reading the schema (rather than a hand list) is the same source McpProjectDefaultFilter
		// uses at runtime, so the probe aims at exactly the argument the server binds.
		var schemas = Factory.Services.GetServices<McpServerTool>()
			.ToDictionary(t => t.ProtocolTool.Name, t => t.ProtocolTool.InputSchema, StringComparer.Ordinal);

		using var http = Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
		http.DefaultRequestHeaders.Add(ApiKeyAuthenticationHandler.ApiKeyHeader, AttackerApiKey);
		var transport = new HttpClientTransport(new HttpClientTransportOptions
		{
			Endpoint = new Uri(http.BaseAddress!, "/mcp"),
			AdditionalHeaders = new Dictionary<string, string>
			{
				[ApiKeyAuthenticationHandler.ApiKeyHeader] = AttackerApiKey,
			},
		}, http);

		await using var mcp = await McpClient.CreateAsync(transport, cancellationToken: default);
		ToolsVisibleToAttacker = [.. (await mcp.ListToolsAsync()).Select(t => t.Name).Order(StringComparer.Ordinal)];
		AttackerWhoAmI = string.Join(" ",
			(await mcp.CallToolAsync("whoami", new Dictionary<string, object?>()))
				.Content.OfType<TextContentBlock>().Select(c => c.Text));

		var probes = new List<CrossTenantProbe>();
		foreach (var surface in surfaces)
		{
			var (args, tenantArgs) = ArgumentsFor(schemas.GetValueOrDefault(surface.Id));

			// Every tool is CALLED, including the ones with no tenant slot: what a fleet-wide verb
			// answers a foreign tenant is a fact worth having on the record even though there is no
			// tenant in it to cross.
			var addressed = tenantArgs.Count > 0;
			var filler = args.Count - tenantArgs.Count;
			var call = $"{surface.Id}({string.Join(", ", tenantArgs.Select(a => $"{a}={args[a]}"))}"
				+ (filler > 0 ? $"{(addressed ? ", " : "")}+{filler} garbage" : "") + ")";

			using var cts = new CancellationTokenSource(CallBudget);
			try
			{
				var result = await mcp.CallToolAsync(surface.Id, args, cancellationToken: cts.Token);
				probes.Add(Classify(surface, call, addressed, result));
			}
			catch (OperationCanceledException)
			{
				probes.Add(new CrossTenantProbe(surface, call, addressed, CrossTenantVerdict.Timeout,
					$"no response within {CallBudget.TotalSeconds:0}s"));
			}
			catch (Exception ex)
			{
				// A protocol-level failure (unknown tool, client-side argument validation) never reaches
				// the tool body — so whatever it is, it is not an authorization decision.
				probes.Add(new CrossTenantProbe(surface, call, addressed, CrossTenantVerdict.ArgumentError,
					$"{ex.GetType().Name}: {Short(ex.Message)}"));
			}
		}

		return probes;
	}

	// The argument set for one tool call: the VICTIM's keys in whichever tenant slots the schema has,
	// and a type-shaped piece of NONSENSE in every other REQUIRED slot.
	//
	// The nonsense is what earns the step its answer. A tenant-only call is refused by the SDK's own
	// argument binder ("missing a value for the required parameter 'board'") before the tool body ever
	// runs — 62 of the 97 tools answered exactly that — and a binder error is not evidence about
	// authorization either way: it cannot tell "the check is late" from "there is no check". Filling
	// the required slots with garbage carries the call PAST binding and into the handler, where the
	// answer is either the denial (the check exists, merely downstream of binding) or a success (there
	// is no check at all, and that is a hole). Still no VALID arguments anywhere — nothing here knows
	// what a real board id or a real store name looks like, which is the economy the work card is
	// after.
	static (Dictionary<string, object?> Args, List<string> TenantArgs) ArgumentsFor(JsonElement? schema)
	{
		var args = new Dictionary<string, object?>(StringComparer.Ordinal);
		var tenantArgs = new List<string>();
		if (schema is not { ValueKind: JsonValueKind.Object } input) return (args, tenantArgs);

		var required = input.TryGetProperty("required", out var req) && req.ValueKind == JsonValueKind.Array
			? req.EnumerateArray().Select(e => e.GetString()).Where(s => s is not null).ToHashSet(StringComparer.Ordinal)!
			: new HashSet<string?>(StringComparer.Ordinal);

		if (!input.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object)
			return (args, tenantArgs);

		foreach (var property in properties.EnumerateObject())
		{
			switch (property.Name)
			{
				case "projectKey":
					args[property.Name] = VictimProject;
					tenantArgs.Add(property.Name);
					continue;
				case "workspaceKey":
					args[property.Name] = VictimWorkspace;
					tenantArgs.Add(property.Name);
					continue;
			}

			if (required.Contains(property.Name)) args[property.Name] = GarbageFor(property.Value);
		}

		return (args, tenantArgs);
	}

	static object GarbageFor(JsonElement property)
	{
		var type = property.TryGetProperty("type", out var t) ? TypeName(t) : null;
		return type switch
		{
			"integer" or "number" => 0,
			"boolean" => false,
			"array" => Array.Empty<object>(),
			"object" => new Dictionary<string, object?>(StringComparer.Ordinal),
			_ => "petbox-probe",
		};
	}

	// `type` is a string on most schemas and ["string","null"] on a nullable one.
	static string? TypeName(JsonElement type) => type.ValueKind switch
	{
		JsonValueKind.String => type.GetString(),
		JsonValueKind.Array => type.EnumerateArray()
			.Select(e => e.GetString())
			.FirstOrDefault(s => s is not null and not "null"),
		_ => null,
	};

	// Every MCP failure arrives as McpErrorEnvelopeFilter's {"error":{"type":…,"message":…}} on an
	// IsError result — so the CLR exception TYPE is the wire fact this reads, and the authorization
	// denial is exactly UnauthorizedAccessException (ModuleMcp / MemoryTools throw nothing else for it).
	static CrossTenantProbe Classify(AuthzSurface surface, string call, bool addressed, CallToolResult result)
	{
		var text = string.Join(" ", result.Content.OfType<TextContentBlock>().Select(c => c.Text));

		if (result.IsError != true)
			return new CrossTenantProbe(surface, call, addressed, CrossTenantVerdict.Allowed, "ok: " + Short(text));

		var (type, message) = ErrorOf(text);
		var observed = $"{type}: {Short(message)}";

		var verdict = type switch
		{
			"UnauthorizedAccessException" => CrossTenantVerdict.Denied,

			// McpProjectExistsFilter's "no such project" — an existence answer, not an authorization one.
			"InvalidOperationException" when message.Contains("project", StringComparison.OrdinalIgnoreCase)
				&& (message.Contains("not found", StringComparison.OrdinalIgnoreCase)
					|| message.Contains("does not exist", StringComparison.OrdinalIgnoreCase)
					|| message.Contains("unknown", StringComparison.OrdinalIgnoreCase))
				=> CrossTenantVerdict.NotFound,

			"ArgumentException" or "ArgumentNullException" or "ArgumentOutOfRangeException"
				or "JsonException" or "FormatException" or "InvalidOperationException" or "McpException"
				or "NotSupportedException" or "KeyNotFoundException"
				=> CrossTenantVerdict.ArgumentError,

			_ => CrossTenantVerdict.ServerError,
		};

		return new CrossTenantProbe(surface, call, addressed, verdict, observed);
	}

	static (string Type, string Message) ErrorOf(string text)
	{
		try
		{
			using var doc = JsonDocument.Parse(text);
			if (doc.RootElement.TryGetProperty("error", out var error))
				return (error.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "",
					error.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "");
		}
		catch (JsonException)
		{
			// Not the envelope — fall through and report the raw text.
		}

		return ("(unparsed)", text);
	}

	static string Short(string? s)
	{
		s = (s ?? "").ReplaceLineEndings(" ").Trim();
		return s.Length <= 160 ? s : s[..157] + "...";
	}

	// ── THE MACHINE REPORT ───────────────────────────────────────────────────────────────────────

	public static string Render(IEnumerable<CrossTenantProbe> probes)
	{
		var all = probes
			.OrderByDescending(p => p.Addressed)
			.ThenBy(p => p.Verdict)
			.ThenBy(p => p.Key, StringComparer.Ordinal)
			.ToList();
		var addressed = all.Where(p => p.Addressed).ToList();

		var lines = new List<string>
		{
			$"# PetBox cross-tenant probe — {all.Count} surfaces",
			$"# attacker: api key on project '{AttackerProject}' (workspace '{AttackerWorkspace}') with EVERY scope;",
			$"#           browser user '{AttackerUser}', Member of '{AttackerWorkspace}' only",
			$"# target:   project '{VictimProject}' / workspace '{VictimWorkspace}'",
			"#",
			$"# ADDRESSED (the call named the victim tenant in a route value or a tool argument): {addressed.Count}",
		};
		lines.AddRange(addressed.GroupBy(p => p.Verdict).OrderBy(g => g.Key)
			.Select(g => $"#     {g.Key}: {g.Count()}"));
		lines.Add($"# NOT ADDRESSABLE (no tenant slot in the route or the tool schema): {all.Count - addressed.Count}");
		lines.AddRange(all.Where(p => !p.Addressed).GroupBy(p => p.Verdict).OrderBy(g => g.Key)
			.Select(g => $"#     {g.Key}: {g.Count()}"));
		lines.Add("#");
		lines.AddRange(all.Select(p =>
			$"{(p.Addressed ? "addressed" : "no-slot")}\t{p.Verdict}\t{p.Key}\t{p.Call}\t{p.Observed}"));
		return string.Join(Environment.NewLine, lines) + Environment.NewLine;
	}

	public static string RepoRoot()
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
