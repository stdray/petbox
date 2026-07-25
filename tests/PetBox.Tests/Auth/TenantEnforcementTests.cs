using System.Net;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PetBox.Core.Auth;
using PetBox.Core.Data;
using PetBox.Web.Mcp;

namespace PetBox.Tests.Auth;

// THE TWO ENFORCEMENT POINTS ACTUALLY REFUSE (work `authz-default-deny-delivery`, step 3).
//
// This is the guard the rest of the suite cannot give. Every one of the 217 live surfaces is on
// TenantEnforcementAllowlist, so on the real host both PEPs pass everything — a middleware that had
// been wired up wrong, or a filter whose body was `return`, would look exactly as green. So the
// behaviour is proven on surfaces that are NOT allowlisted: a throwaway app with its own routes for
// the endpoint plane, and a fabricated declaration map for the MCP plane.
//
// What is asserted, on both planes, is the same four-way shape:
//   allowlisted        -> through, without even looking at a declaration;
//   undeclared         -> REFUSED (default-deny is the zero case, not a pass);
//   exempt             -> through;
//   declared source    -> through iff ITenantAuthorizer says Allowed.
public sealed class TenantEnforcementTests
{
	const string WsA = "wsa";
	const string ProjA = "proja";
	const string ProjB = "projb";

	// wsa owns proja; projb belongs to somebody else.
	static readonly TenantAuthorizer Authorizer = new(new FakeCatalog(
		workspaceOf: new Dictionary<string, string>(StringComparer.Ordinal) { [ProjA] = WsA, [ProjB] = "wsb" },
		sandboxProjects: []));

	// ── PEP #1: the endpoint plane ───────────────────────────────────────────────────────────────
	//
	// A real pipeline (UseRouting → the middleware → the endpoint), so what is exercised is the actual
	// metadata reading, not a re-statement of it. The routes are chosen to be absent from the
	// allowlist — `rest:GET /probe/...` is not a PetBox surface — except where the test is about being
	// allowlisted, which borrows a REAL entry.

	[Fact]
	public async Task Endpoint_Undeclared_IsRefused()
	{
		var (status, body) = await GetAsync(app => app.MapGet("/probe/undeclared", () => "handler ran"), ApiKey(ProjA));

		status.Should().Be(HttpStatusCode.Forbidden, "a surface nobody declared is the default-deny case");
		body.Should().NotContain("handler ran", "the refusal happens BEFORE the handler, not after it")
			.And.Contain("declares no target tenant", "the refusal says what is missing");
	}

	[Fact]
	public async Task Endpoint_Exempt_PassesThrough() =>
		(await GetAsync(
			app => app.MapGet("/probe/exempt", () => "handler ran")
				.DeclaresTenantExempt(TenantExemption.Identity, "probe"),
			ApiKey(ProjA)))
			.Should().Be((HttpStatusCode.OK, "handler ran"));

	// An allowlisted surface is passed through BEFORE its declaration is read — which is the property
	// that makes today's rollout a no-op. Proven on a route that IS undeclared and WOULD be refused.
	[Fact]
	public async Task Endpoint_Allowlisted_PassesThrough_EvenUndeclared()
	{
		TenantEnforcementAllowlist.Contains("rest:GET|HEAD /version").Should().BeTrue("fixture assumption");

		// GET *and* HEAD, because the key carries the sorted method list — mapping only GET would
		// produce "rest:GET /version", which is a DIFFERENT surface and is not allowlisted.
		(await GetAsync(app => app.MapMethods("/version", ["GET", "HEAD"], () => "handler ran"), ApiKey(ProjA), "/version"))
			.Should().Be((HttpStatusCode.OK, "handler ran"));
	}

	// The route value is read from the REQUEST, and the answer comes from ITenantAuthorizer — the same
	// decision point everything else uses.
	[Theory]
	[InlineData(ProjA, HttpStatusCode.OK)]
	[InlineData(ProjB, HttpStatusCode.Forbidden)]
	public async Task Endpoint_TenantFromRoute_AnswersOnTheDecisionPoint(string routeProject, HttpStatusCode expected)
	{
		var (status, _) = await GetAsync(
			app => app.MapGet("/probe/route/{projectKey}", () => "handler ran")
				.DeclaresTenant(TenantSource.Route, "projectKey"),
			ApiKey(ProjA),
			$"/probe/route/{routeProject}");

		status.Should().Be(expected);
	}

	// …and a route whose declared value is simply not there resolves to an UNRESOLVED TenantRef, which
	// is a refusal for every principal — including a cross-project one.
	[Fact]
	public async Task Endpoint_DeclaredRouteValueMissing_IsRefused_EvenForAWildcardKey()
	{
		var (status, _) = await GetAsync(
			app => app.MapGet("/probe/missing", () => "handler ran")
				.DeclaresTenant(TenantSource.Route, "projectKey"),
			ApiKey("*"));

		status.Should().Be(HttpStatusCode.Forbidden, "a wildcard claim authorizes any tenant, not the absence of one");
	}

	[Theory]
	[InlineData(ProjA, HttpStatusCode.OK)]
	[InlineData(ProjB, HttpStatusCode.Forbidden)]
	public async Task Endpoint_TenantFromQueryArgument(string project, HttpStatusCode expected)
	{
		var (status, _) = await GetAsync(
			app => app.MapGet("/probe/query", () => "handler ran")
				.DeclaresTenant(TenantSource.Argument, "projectKey"),
			ApiKey(ProjA),
			$"/probe/query?projectKey={project}");

		status.Should().Be(expected);
	}

	// "Цель, пришедшая в теле запроса, проверяется так же строго, как пришедшая из маршрута" — and the
	// handler must still be able to read the body afterwards, which is why the middleware rewinds it.
	[Theory]
	[InlineData(ProjA, HttpStatusCode.OK)]
	[InlineData(ProjB, HttpStatusCode.Forbidden)]
	public async Task Endpoint_TenantFromBodyField(string project, HttpStatusCode expected)
	{
		using var host = Host(app => app.MapPost("/probe/body", async (HttpContext ctx) =>
			{
				using var reader = new StreamReader(ctx.Request.Body);
				return await reader.ReadToEndAsync();
			})
			.DeclaresTenant(TenantSource.BodyField, "projectKey"));

		using var client = host.GetTestClient();
		client.DefaultRequestHeaders.Add(ApiKeyAuthenticationHandler.ApiKeyHeader, ProjA);

		var body = $$"""{"projectKey":"{{project}}"}""";
		using var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
		using var response = await client.PostAsync("/probe/body", content);

		response.StatusCode.Should().Be(expected);
		if (expected == HttpStatusCode.OK)
			(await response.Content.ReadAsStringAsync()).Should().Be(body,
				"the PEP buffers and REWINDS the body — the handler must still see it whole");
	}

	// A body that is not JSON, or that does not carry the declared field, yields no tenant — refused,
	// never passed. Fail-closed is the whole point of putting the check here.
	[Theory]
	[InlineData("{\"other\":\"proja\"}", "application/json")]
	[InlineData("not json at all", "application/json")]
	[InlineData("projectKey=proja", "application/x-www-form-urlencoded")]
	public async Task Endpoint_BodyFieldUnreadable_IsRefused(string body, string contentType)
	{
		using var host = Host(app => app.MapPost("/probe/body", () => "handler ran")
			.DeclaresTenant(TenantSource.BodyField, "projectKey"));

		using var client = host.GetTestClient();
		client.DefaultRequestHeaders.Add(ApiKeyAuthenticationHandler.ApiKeyHeader, ProjA);

		using var content = new StringContent(body, System.Text.Encoding.UTF8, contentType);
		using var response = await client.PostAsync("/probe/body", content);

		response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
	}

	// Two declarations on one surface: refused, not "whichever the reader saw first".
	[Fact]
	public async Task Endpoint_DeclaredTwice_IsRefused()
	{
		var (status, _) = await GetAsync(
			app => app.MapGet("/probe/twice", () => "handler ran")
				.DeclaresTenant(TenantSource.Route, "projectKey")
				.DeclaresTenantExempt(TenantExemption.Public, "probe"),
			ApiKey("*"));

		status.Should().Be(HttpStatusCode.Forbidden);
	}

	// A request that routes nowhere must not be turned into a 403 by an authz middleware — 404 is the
	// framework's answer and stays it.
	[Fact]
	public async Task Endpoint_NoEndpointMatched_IsUntouched()
	{
		var (status, _) = await GetAsync(app => app.MapGet("/probe/exists", () => "ok"), ApiKey(ProjA), "/probe/nothing-here");
		status.Should().Be(HttpStatusCode.NotFound);
	}

	// ── PEP #2: the MCP plane ────────────────────────────────────────────────────────────────────
	//
	// Same four-way shape, on the filter's own decision seam (RequestContext cannot be built outside a
	// live MCP server). The declaration map is fabricated so the tools under test are NOT allowlisted —
	// every real tool is.

	const string Undeclared = "probe_undeclared";
	const string Exempt = "probe_exempt";
	const string FromArgument = "probe_from_argument";
	const string FromCallerDefault = "probe_from_caller_default";
	const string FromRoute = "probe_from_route";

	const string FromArgumentOrContainer = "probe_from_argument_or_container";

	static readonly Dictionary<string, IReadOnlyList<TenantDeclarationAttribute>> ProbeDeclarations = new(StringComparer.Ordinal)
	{
		[Undeclared] = [],
		[Exempt] = [new TenantExemptAttribute(TenantExemption.Identity, "probe")],
		[FromArgument] = [new TenantFromAttribute(TenantSource.Argument, "projectKey")],
		[FromArgumentOrContainer] = [new TenantFromAttribute(TenantSource.ArgumentOrContainer, "projectKey")],
		[FromCallerDefault] = [new TenantFromAttribute(TenantSource.CallerDefault)],
		[FromRoute] = [new TenantFromAttribute(TenantSource.Route, "projectKey")],
	};

	[Fact]
	public async Task Mcp_Undeclared_IsRefused() =>
		(await McpAsync(Undeclared, ApiKey(ProjA))).Allowed.Should().BeFalse();

	// A tool the map has never heard of is undeclared too — not "unknown, therefore fine".
	[Fact]
	public async Task Mcp_UnknownTool_IsRefused() =>
		(await McpAsync("probe_not_in_the_map", ApiKey("*"))).Allowed.Should().BeFalse();

	[Fact]
	public async Task Mcp_Exempt_PassesThrough() =>
		(await McpAsync(Exempt, ApiKey(ProjA))).Allowed.Should().BeTrue();

	// This used to be Mcp_Allowlisted_PassesThrough_WithoutADeclaration — the allowlist-before-
	// declaration ORDER, proven against a real entry (mcp:tasks_upsert, then mcp:memory_upsert). The
	// declaration wave took every MCP tool out of the allowlist, so there is no longer a real MCP
	// surface to observe that order ON, and pinning it against a made-up key would prove only that the
	// test can invent a string.
	//
	// What replaces it is the stronger statement the wave actually earned, and it is a ratchet: the
	// MCP plane carries NO allowlist entries, so every tool call reaches the declaration path. If an
	// `mcp:` entry ever comes back, this fails — and whoever adds it should restore the order test
	// above it, because an allowlisted surface is exactly where the order matters.
	//
	// The order property itself is not lost: Endpoint_Allowlisted_PassesThrough_EvenUndeclared proves
	// it on the endpoint plane, where 120 real entries remain, and both PEPs ask the same list the
	// same way.
	[Fact]
	public void Mcp_IsFullyEnforced_NothingLeftOnTheAllowlist() =>
		TenantEnforcementAllowlist.Keys.Where(k => k.StartsWith(AuthzSurfaceKey.McpPrefix, StringComparison.Ordinal))
			.Should().BeEmpty(
				"all 97 MCP tools declare a tenant source or an exemption class and are enforced by "
				+ "McpTenantEnforcementFilter. An entry here would be a tool exempted from the rule by a list "
				+ "instead of by a declaration — which is the opt-in hole this work item removed");

	[Theory]
	[InlineData(ProjA, true)]
	[InlineData(ProjB, false)]
	public async Task Mcp_TenantFromArgument_AnswersOnTheDecisionPoint(string argument, bool allowed) =>
		(await McpAsync(FromArgument, ApiKey(ProjA), Args(("projectKey", argument)))).Allowed.Should().Be(allowed);

	// ── TenantSource.ArgumentOrContainer: one argument, two encodings ────────────────────────────
	//
	// The memory family's shape. `$ws-<key>` / `$workspace` is a WORKSPACE wearing a projectKey's
	// clothes, and a key of any project in that workspace may reach it; anything else is an ordinary
	// project reference and is judged exactly as plain Argument judges it.
	[Theory]
	// A container of the caller's OWN workspace: reachable. wsa owns proja, so proja's key reaches it.
	[InlineData("$ws-wsa", true)]
	// A container of SOMEBODY ELSE's workspace: refused. This is the case the decode must not widen.
	[InlineData("$ws-wsb", false)]
	// Plain project keys keep the ordinary answer — the decode changes nothing for them.
	[InlineData(ProjA, true)]
	[InlineData(ProjB, false)]
	public async Task Mcp_TenantFromArgumentOrContainer_ResolvesTheKindFromTheValue(string argument, bool allowed) =>
		(await McpAsync(FromArgumentOrContainer, ApiKey(ProjA), Args(("projectKey", argument)))).Allowed
			.Should().Be(allowed);

	// THE SCOPING PROPERTY, and the reason the decode is attached to the DECLARATION rather than done
	// in the resolver for every argument. A surface that declares plain Argument must keep treating a
	// container as an ordinary project key — i.e. as a claim MISMATCH — or enforcement would hand a
	// project-scoped key access to `$ws-x`-addressed storage in tasks/sessions/data, which have never
	// accepted a container. Same caller, same value, two declarations, two answers: that difference IS
	// the security boundary, so it is asserted rather than described.
	[Fact]
	public async Task Mcp_PlainArgument_DoesNotDecodeAContainer()
	{
		var container = Args(("projectKey", "$ws-wsa"));

		(await McpAsync(FromArgumentOrContainer, ApiKey(ProjA), container)).Allowed.Should().BeTrue(
			"the memory family declared the container encoding, so its own workspace's container resolves");

		(await McpAsync(FromArgument, ApiKey(ProjA), container)).Allowed.Should().BeFalse(
			"a surface that did NOT declare the container encoding must still read '$ws-wsa' as a project "
			+ "key, which proja's claim does not authorize. If this ever passes, the decode has leaked out "
			+ "of the declaration and every Argument surface just gained reach into shared-memory storage");
	}

	// An omitted argument falls back to the caller's own default — the SAME fallback the tool body
	// takes (ModuleMcp.ResolveProject) and McpProjectExistsFilter validates. Authorizing an absent
	// argument as "no tenant" while the tool goes on to act on the default would check the wrong thing.
	[Fact]
	public async Task Mcp_ArgumentOmitted_FallsBackToTheCallersDefault()
	{
		(await McpAsync(FromArgument, ApiKey(ProjA))).Allowed.Should().BeTrue("proja IS this key's own tenant");

		// …and a key with nothing to fall back to resolves nothing, which is a refusal.
		(await McpAsync(FromArgument, ApiKey("*"))).Allowed.Should().BeFalse();
	}

	[Fact]
	public async Task Mcp_CallerDefault_ResolvesTheKeysOwnTenant()
	{
		(await McpAsync(FromCallerDefault, ApiKey(ProjA))).Allowed.Should().BeTrue();
		(await McpAsync(FromCallerDefault, ApiKey("*", defaultProject: ProjB))).Allowed.Should().BeTrue(
			"a cross-project key's default is its own tenant, and its claim authorizes it");
		(await McpAsync(FromCallerDefault, ApiKey("*"))).Allowed.Should().BeFalse("nothing resolves");
	}

	// A source that does not exist on this transport is a MIS-DECLARATION, and it fails closed.
	[Fact]
	public async Task Mcp_RouteSource_IsRefused_ThereAreNoRoutesInAToolCall() =>
		(await McpAsync(FromRoute, ApiKey("*"), Args(("projectKey", ProjA)))).Allowed.Should().BeFalse();

	// ── helpers ──────────────────────────────────────────────────────────────────────────────────

	static ValueTask<TenantGateResult> McpAsync(
		string tool,
		ClaimsPrincipal user,
		IDictionary<string, JsonElement>? arguments = null,
		IReadOnlyDictionary<string, IReadOnlyList<TenantDeclarationAttribute>>? declarations = null) =>
		McpTenantEnforcementFilter.DecideAsync(
			Authorizer, user, tool, arguments, declarations ?? ProbeDeclarations);

	static Dictionary<string, JsonElement> Args(params (string Name, string Value)[] pairs) =>
		pairs.ToDictionary(p => p.Name, p => JsonSerializer.SerializeToElement(p.Value), StringComparer.Ordinal);

	// A minimal host: routing, a stub authentication scheme whose principal is the api-key header's
	// project claim, then the PEP, then the endpoints.
	static IHost Host(Action<WebApplication> map)
	{
		var builder = WebApplication.CreateSlimBuilder();
		builder.WebHost.UseTestServer();
		builder.Services.AddRouting();
		builder.Services.AddSingleton<ITenantAuthorizer>(Authorizer);
		builder.Services.AddAuthentication(StubAuthHandler.SchemeName)
			.AddScheme<AuthenticationSchemeOptions, StubAuthHandler>(StubAuthHandler.SchemeName, _ => { });

		var app = builder.Build();
		app.UseRouting();
		app.UseAuthentication();
		app.UseMiddleware<TenantEnforcementMiddleware>();
		map(app);

		app.Start();
		return app;
	}

	static async Task<(HttpStatusCode Status, string Body)> GetAsync(
		Action<WebApplication> map, ClaimsPrincipal principal, string? path = null)
	{
		using var host = Host(map);
		using var client = host.GetTestClient();

		var claim = principal.FindFirst(ApiKeyAuthenticationHandler.ProjectClaim)?.Value;
		if (claim is not null) client.DefaultRequestHeaders.Add(ApiKeyAuthenticationHandler.ApiKeyHeader, claim);
		var fallback = principal.FindFirst(ApiKeyAuthenticationHandler.DefaultProjectClaim)?.Value;
		if (fallback is not null) client.DefaultRequestHeaders.Add(DefaultProjectHeader, fallback);

		using var response = await client.GetAsync(path ?? FirstRoute(host));
		return (response.StatusCode, await response.Content.ReadAsStringAsync());
	}

	// The single probe route the caller mapped, so a test that does not care about the URL need not
	// repeat it.
	static string FirstRoute(IHost host) =>
		"/" + host.Services.GetRequiredService<Microsoft.AspNetCore.Routing.EndpointDataSource>().Endpoints
			.OfType<Microsoft.AspNetCore.Routing.RouteEndpoint>()
			.Select(e => e.RoutePattern.RawText!.TrimStart('/'))
			.First();

	const string DefaultProjectHeader = "X-Test-Default-Project";

	static ClaimsPrincipal ApiKey(string claim, string? defaultProject = null)
	{
		var claims = new List<Claim> { new(ApiKeyAuthenticationHandler.ProjectClaim, claim) };
		if (defaultProject is not null) claims.Add(new Claim(ApiKeyAuthenticationHandler.DefaultProjectClaim, defaultProject));
		return new ClaimsPrincipal(new ClaimsIdentity(claims, ApiKeyAuthenticationHandler.SchemeName));
	}

	// Turns the two headers above into the api-key identity the real handler would produce. The PEP
	// reads a ClaimsPrincipal and nothing else, so a stub scheme is a faithful stand-in and keeps the
	// test off core.db entirely.
	sealed class StubAuthHandler(
		Microsoft.Extensions.Options.IOptionsMonitor<AuthenticationSchemeOptions> options,
		Microsoft.Extensions.Logging.ILoggerFactory logger,
		System.Text.Encodings.Web.UrlEncoder encoder)
		: AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
	{
		public const string SchemeName = "StubApiKey";

		protected override Task<AuthenticateResult> HandleAuthenticateAsync()
		{
			if (!Request.Headers.TryGetValue(ApiKeyAuthenticationHandler.ApiKeyHeader, out var key) || key.Count == 0)
				return Task.FromResult(AuthenticateResult.NoResult());

			var claims = new List<Claim> { new(ApiKeyAuthenticationHandler.ProjectClaim, key.ToString()) };
			if (Request.Headers.TryGetValue(DefaultProjectHeader, out var fallback) && fallback.Count > 0)
				claims.Add(new Claim(ApiKeyAuthenticationHandler.DefaultProjectClaim, fallback.ToString()));

			var identity = new ClaimsIdentity(claims, ApiKeyAuthenticationHandler.SchemeName);
			return Task.FromResult(AuthenticateResult.Success(
				new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
		}
	}

	// wsa/wsb membership only — the decision point's own behaviour is TenantAuthorizerTests' subject;
	// here the catalog exists so the PEPs have a real ITenantAuthorizer to answer through.
	sealed class FakeCatalog(IReadOnlyDictionary<string, string> workspaceOf, HashSet<string> sandboxProjects)
		: IProjectCatalog
	{
		public Task<IReadOnlyList<string>> ListProjectKeysAsync(CancellationToken ct = default) =>
			throw new NotSupportedException();
		public Task<IReadOnlyList<string>> ListWorkspaceKeysAsync(CancellationToken ct = default) =>
			throw new NotSupportedException();
		public Task<IReadOnlyList<string>> ListMemoryProjectKeysAsync(CancellationToken ct = default) =>
			throw new NotSupportedException();
		public Task<IReadOnlyList<string>> ListTaskProjectKeysAsync(CancellationToken ct = default) =>
			throw new NotSupportedException();

		public Task<bool> IsSandboxAsync(string projectKey, CancellationToken ct = default) =>
			Task.FromResult(sandboxProjects.Contains(projectKey));

		public Task<string?> WorkspaceKeyOfAsync(string projectKey, CancellationToken ct = default) =>
			Task.FromResult(workspaceOf.TryGetValue(projectKey, out var ws) ? ws : null);
	}
}
