using PetBox.Core.Auth;
using Xunit;

namespace PetBox.Tests.Architecture;

// STEP 4 of work `authz-default-deny-delivery`: the cross-tenant test, over THE SAME enumeration the
// ratchet guards (AuthzSurfaces — 215 surfaces: 55 REST, 65 Razor, 95 MCP; was 217/97 MCP before
// batch3 (read-surface-shape-batch-and-dead-delta) removed session_delta and config_binding_delta).
// One test source, one inventory: a second enumeration would drift from the ratchet's and the two
// would quietly stop talking about the same system.
//
// THE ASSERTION, for every surface where a foreign tenant can be NAMED: the call is refused on the
// authorization axis. Never served, never answered with an argument error. See AuthzCrossTenantProbe
// for why garbage arguments are sufficient (and for the one place where they are deliberately
// type-shaped rather than absent).
//
// WHAT THIS TEST IS NOT ALLOWED TO DO: pass by omission. Every one of the 215 surfaces lands in
// exactly one of three places, and the sum is checked:
//
//   * REFUSED               — 143 addressed surfaces that already deny a foreign tenant today.
//   * KnownDeviations       —   9 addressed surfaces that do NOT, each named, with the behaviour that
//                              was actually observed. Same discipline as the ratchet's allowlist: it
//                              only ever shrinks, a fixed entry fails as stale, and the number is
//                              visible. These are step 5's work — they are NOT repaired here and NOT
//                              papered over.
//   * NotAddressable        —  63 surfaces with nowhere to write a foreign tenant: no
//                              {projectKey}/{workspaceKey} in the route, none in the tool schema.
//                              Named one by one and grouped by WHY, because "the rest" is exactly
//                              the sentence this work item exists to stop anyone writing.
//
// 143 + 9 + 63 = 215, and TheAccounting_IsComplete fails if it ever stops adding up.
[Collection("WebAppFactory")]
public sealed class AuthzCrossTenantTests : IClassFixture<AuthzCrossTenantHost>
{
	readonly AuthzCrossTenantHost _host;

	public AuthzCrossTenantTests(AuthzCrossTenantHost host) => _host = host;

	// ── THE DEVIATIONS: surfaces a foreign tenant reaches, or that answer it something other than
	//    "no" ──────────────────────────────────────────────────────────────────────────────────────
	//
	// Each entry records the verdict OBSERVED, so the list is a ratchet in both directions: fixing a
	// surface makes its entry stale (delete the line), and a surface getting WORSE — an argument error
	// turning into a success — fails here rather than sliding through under an old note.
	//
	// The seven `Allowed` entries are all reachable only with a scope the system already documents as
	// root-equivalent (`admin:provision`, and `deploy:write` which ApiKeyScopes calls "NEAR-ROOT,
	// FLEET-WIDE"). That is the `Provisioning` / `FleetWide` exemption of spec `authz-scope-declaration`
	// showing up as a measured number rather than a footnote — acceptance criterion 5. Three of the
	// seven MUTATE the other tenant, which is the part of that number worth staring at.
	//
	// Since the MCP declaration wave (step 5) those exemptions are no longer an interpretation of this
	// list: every one of the eleven MCP entries below is now [TenantExempt(...)] on its own tool type,
	// or declares where its tenant comes from. memory_get was the last one held back on the allowlist;
	// it declares too now ([TenantFrom(ArgumentOrContainer, "projectKey")], with the rest of the memory
	// family), and TenantEnforcementAllowlist is EMPTY — so there is no longer any surface here whose
	// exemption rests on a list. The list did not move a single verdict when enforcement went live
	// across all 97 tools, which is the property step 5 was supposed to have: the families that came
	// out had complete manual coverage already, so the PEP reproduces their allow/deny exactly and only
	// relocates the refusal.
	static readonly IReadOnlyDictionary<string, (CrossTenantVerdict Verdict, string Observed)> KnownDeviations =
		new Dictionary<string, (CrossTenantVerdict, string)>(StringComparer.Ordinal)
		{
			// ── SERVED A FOREIGN TENANT ──────────────────────────────────────────────────────────
			//
			// The three MCP config verbs are the sharpest of these: their REST twin on the same data,
			// POST|DELETE /api/config/{workspaceKey}/bindings, DENIES the identical call — since the REST
			// wave with a 403 from TenantEnforcementMiddleware, on a
			// [TenantFrom(Route, "workspaceKey", TenantKind.Workspace)] declaration rather than the
			// hand-written ConfigApi.AuthorizeWorkspaceAsync it replaced. Same operation, two transports,
			// opposite answers. That asymmetry is a defect rather than an exemption anyone chose, and the
			// REST side is the correct half: it was NOT "brought into line" with `provisioning`, which
			// would have meant opening cross-tenant config writes to close a diff.
			["mcp:config_binding_upsert"] = (CrossTenantVerdict.Allowed,
				"WROTE the victim workspace's config (\"applied\":true) with admin:provision; the REST twin denies"),
			["mcp:config_binding_search"] = (CrossTenantVerdict.Allowed,
				"read the victim workspace's bindings with admin:provision; the REST twin denies"),
			// mcp:config_binding_delta was here — same "REST twin denies" deviation as upsert/search. GONE
			// (batch3, read-surface-shape-batch-and-dead-delta P.3): config_binding_delta itself was
			// removed (no tombstone, so a delta could only ever repeat _search — never called).

			// Provisioning, as declared: admin:provision is de facto root over every tenant
			// (TenantDeclaration.cs says so in the enum). project_create is the one that MUTATES —
			// it created a project inside the victim's workspace.
			["mcp:project_create"] = (CrossTenantVerdict.Allowed,
				"CREATED a project inside the victim's workspace with admin:provision"),
			["mcp:project_list"] = (CrossTenantVerdict.Allowed,
				"listed the victim workspace's projects with admin:provision"),
			["mcp:apikey_list"] = (CrossTenantVerdict.Allowed,
				"listed the victim project's api keys with admin:provision"),

			// Fleet-wide, as declared — and the only one of the seven that does not need
			// admin:provision. deploy:write alone attached a deployment to the victim's PROJECT.
			["mcp:deploy_upsert"] = (CrossTenantVerdict.Allowed,
				"CREATED a deployment carrying \"project\":\"victimproj\" with deploy:write alone"),

			// ── ANSWERED SOMETHING OTHER THAN "NO" ───────────────────────────────────────────────
			//
			// These four decided about the ARGUMENTS of a foreign tenant's request before deciding
			// about the tenant. Two of them are existence oracles: they tell an outsider whether a
			// binding exists in a workspace they may not touch, and one echoes the workspace key back.
			["mcp:config_binding_get"] = (CrossTenantVerdict.ArgumentError,
				"InvalidOperationException \"config binding '0' not found in workspace 'victimws'\" — an existence "
				+ "answer to an outsider, naming the workspace back at them"),
			["mcp:config_binding_delete"] = (CrossTenantVerdict.ArgumentError,
				"InvalidOperationException \"Binding not found\" — an existence answer to an outsider"),
			["mcp:apikey_create"] = (CrossTenantVerdict.ArgumentError,
				"ArgumentException \"Unknown scopes: …\" — validates the scope list before the tenant"),

			// mcp:memory_get was here — "ArgumentException 'key or keys is required', argument validation
			// runs before the tenant check". FIXED by the MCP declaration wave rather than excused: the
			// memory family now declares [TenantFrom(ArgumentOrContainer, "projectKey")] and the PEP
			// refuses ahead of the tool body, so the argument is never reached. The list shrank by one,
			// which is the only direction it is allowed to move.

			// DELETE /api/config/{workspaceKey}/bindings was here — "400 BadRequest before
			// AuthorizeWorkspaceAsync; POST on the SAME route returns 403". FIXED by the REST wave: the
			// 400 came from binding the `path`/`tags` QUERY parameters, which happens inside the endpoint,
			// so moving the decision into the middleware put it genuinely first and both verbs of the
			// route now answer 403. One route, one answer.

			// The two session POST routes were here — "415 UnsupportedMediaType from endpoint metadata,
			// before the handler's ProjectScope check". BOTH GONE, and the diagnosis in that note turned
			// out to be half wrong, which is worth keeping because it is the reason the probe changed.
			//
			// The 415 was never a late check: it comes from MVC's ConsumesMatcherPolicy, an
			// IEndpointSelectorPolicy that runs INSIDE UseRouting and short-circuits to a non-route 415
			// endpoint before authentication, authorization or the tenant PEP see the request. No
			// middleware can be placed in front of it, so declaring a tenant on those routes did not (and
			// could not) change what the old probe measured — it was measuring the probe's own
			// content-type guess. The probe now retries with the content type the ENDPOINT declares
			// (AuthzCrossTenantProbe.RetryWithDeclaredContentTypeAsync), the call reaches the surface, and
			// both routes answer 403 from TenantEnforcementMiddleware.
		};

	// ── THE SURFACES A FOREIGN TENANT CANNOT BE AIMED AT ─────────────────────────────────────────
	//
	// Not "untested" and not "fine": these are surfaces where the probe found NO SLOT to write another
	// tenant's key into — no {projectKey}/{workspaceKey} route parameter, no projectKey/workspaceKey
	// argument in the tool schema. Whatever tenant they act on comes from the caller's own claim, from
	// a body field, from a query string, or from nowhere at all, so "call it as somebody else" has no
	// meaning on them.
	//
	// Every one is named, and each group says WHY. The groups deliberately use the vocabulary of spec
	// `authz-scope-declaration` (TenantExemption / TenantSource), because step 5 has to turn each of
	// these lines into a declaration and this is the shape of that work.
	static readonly IReadOnlyDictionary<string, string> NotAddressable = Group(
		("PUBLIC — the surface has no tenant at all; anonymous by design", [
			"rest:GET /.well-known/oauth-authorization-server",
			"rest:GET /.well-known/oauth-authorization-server/{*rest}",
			"rest:GET /.well-known/oauth-protected-resource",
			"rest:GET /.well-known/oauth-protected-resource/{*rest}",
			"rest:GET /.well-known/openid-configuration",
			"rest:GET /.well-known/openid-configuration/{*rest}",
			"rest:GET /openapi/{documentName}.json",
			"rest:GET|HEAD /health",
			"rest:GET|HEAD /version",
			"page:/Error",
			"page:/Login",
			"page:/Doc/Agent",
			"page:/Doc/Index",
			"page:/Doc/Methodology",
			"page:/Doc/Onboarding",
			"page:/Doc/Overview",
			"page:/Doc/Philosophy",
			"page:/Doc/Wire",
		]),

		("IDENTITY — the tenant IS the caller; there is no second tenant to name", [
			"rest:GET /api/auth/validate",
			"rest:POST /api/auth/logout",
			"mcp:whoami",
			"page:/AccessDenied",
			"page:/Index",
			"page:/Me/Account",
			"page:/Me/Preferences",
			"page:/Me/Security",
			// The cross-tenant fan-out page. It USED to sit in its own "QUERY STRING" group, on the reading
			// that its scope was a query parameter — which was wrong twice over: `q` is a search string and
			// not a tenant, and the extent of the fan-out is the caller's own membership-filtered project
			// enumeration. The Razor wave declared it [TenantExempt(Identity)] and it is grouped with the
			// rest of that class here rather than left describing a tenant slot it never had.
			"page:/Search",
		]),

		("PROVISIONING — creates the TENANT itself, which cannot exist when the request is judged", [
			"page:/Me/NewWorkspace",
		]),

		("CAPABILITY TOKEN — addressed by a share token, which IS the authorization", [
			"rest:GET /api/share/{token}/tsv",
			"page:/Share",
		]),

		("FEEDBACK — writes into the vendor's own project, never the caller's tenant", [
			"mcp:petbox_report_issue",
		]),

		("FLEET-WIDE — the deploy control plane is addressed by node/deployment id and carries NO tenant "
			+ "slot whatsoever, so every one of these acts on ANY tenant's deployment by id "
			+ "(work `deploy-tools-fleet-wide-undocumented`). The probe confirmed they RUN: "
			+ "deploy_node_upsert created a node, deploy_delete/deploy_node_delete executed and reported "
			+ "deleted:false, deploy_move/start/stop reached \"deployment not found\"", [
			"mcp:deploy_delete",
			"mcp:deploy_list",
			"mcp:deploy_move",
			"mcp:deploy_node_delete",
			"mcp:deploy_node_list",
			"mcp:deploy_node_upsert",
			"mcp:deploy_start",
			"mcp:deploy_stop",
			"rest:GET /agent/poll",
			"rest:POST /agent/heartbeat",
			"rest:POST /api/deploy/nodes",
		]),

		("PROVISIONING — addressed by api-key id, which carries its own tenant; no slot to aim elsewhere", [
			"mcp:apikey_delete",
			"mcp:apikey_update",
		]),

		("SERVER-WIDE ADMIN — the whole deployment rather than one tenant (SysAdmin policy). Every one "
			+ "of these DID deny the attacker (302 /AccessDenied), but on the ROLE axis, not the tenant "
			+ "axis — so the denial is recorded and not counted as a cross-tenant pass. The Razor wave "
			+ "split them across the two classes that actually apply — fleet-wide for the three that "
			+ "READ the installation (/Admin/Index, /Admin/SysDefaults, /Admin/Deploy), provisioning for "
			+ "the three that hand out access across tenants (/Admin/AgentKeys re-scopes any key, "
			+ "/Admin/Users sets workspace allowances, /Admin/Workspaces creates tenants) — but they stay "
			+ "grouped here because what this list answers is 'why is there no tenant SLOT', and that is "
			+ "the same sentence for all six", [
			"page:/Admin/AgentKeys",
			"page:/Admin/Deploy",
			"page:/Admin/Index",
			"page:/Admin/SysDefaults",
			"page:/Admin/Users",
			"page:/Admin/Workspaces",
		]),

		("CALLER-DEFAULT — the tenant comes from the key's own project claim; the route names none", [
			"rest:GET /v1/conf",
			"rest:POST /api/events/raw",
			"rest:POST /v1/logs",
			"rest:POST /v1/metrics",
			"rest:POST /v1/traces",
		]),

		("NO TENANT IN THE ROUTE — the tenant, where there is one, comes out of the request BODY or off the "
			+ "caller's own claim, so FillRoute has nowhere to write the victim's key and `Addressed` is "
			+ "decided false by construction. These six USED to be the probe's one real blind spot: it aimed "
			+ "the victim's projectKey/workspaceKey at every body it sent but could not know whether any "
			+ "handler read the field, so no verdict was asserted. The REST wave closed that by DECLARING "
			+ "each one — [TenantFrom(BodyField, …)] for /api/health (`tags.project`), /api/share "
			+ "(`projectKey`) and the two /api/ui switches (`ws`, a form field), [TenantFrom(CallerDefault)] "
			+ "for /v1/chat/completions, [TenantExempt(Identity)] for the per-user board preference — so the "
			+ "mechanism now reads exactly the field the handler binds and the verdicts recorded in "
			+ ".tmp/authz-cross-tenant-report.txt are real. They are still listed here rather than asserted "
			+ "because `Addressed` answers a question about the ROUTE, and that has not changed; the probe "
			+ "measured two of them (/api/ui/project, /api/ui/workspace) SERVING the attacker before the "
			+ "declarations went in, which is the reason the blind spot was worth closing", [
			"rest:POST /api/health",
			"rest:POST /api/share",
			"rest:POST /api/ui/board-filter-prefs",
			"rest:POST /api/ui/project",
			"rest:POST /api/ui/workspace",
			"rest:POST /v1/chat/completions",
		]),

		("QUERY STRING — the tenant is a query ARGUMENT, so there is nothing to write into the route and "
			+ "`Addressed` is false by construction. /Nav/Tree is the one page in the tree shaped this way, "
			+ "and the Razor wave declared it [TenantFrom(Argument, \"project\")]: it USED to be listed "
			+ "under IDENTITY on the reading that its tenant came from the caller, which was simply wrong — "
			+ "the sidebar passes an explicit `?project=`, and CanAccessProjectAsync used to check it by "
			+ "hand. THIS LIST IS NOT THE PROOF FOR IT: the GET/POST route sweeps cannot reach a query "
			+ "argument, so the surface is covered by NavTreeAndDataViewTests' own refused/served pair "
			+ "instead, and is named here only to say why the route sweep skipped it", [
			"page:/Nav/Tree",
		]),

		("TOOL METADATA — describes a tool, touches no tenant's data", [
			"mcp:tool_describe",
		]));

	static IReadOnlyDictionary<string, string> Group(params (string Reason, string[] Keys)[] groups) =>
		groups.SelectMany(g => g.Keys.Select(k => (Key: k, g.Reason)))
			.ToDictionary(x => x.Key, x => x.Reason, StringComparer.Ordinal);

	// ── THE ASSERTION ────────────────────────────────────────────────────────────────────────────

	[Fact]
	public void EveryAddressedSurface_RefusesAForeignTenant()
	{
		var served = _host.Probes
			.Where(p => p.Addressed && p.Verdict != CrossTenantVerdict.Denied)
			.Where(p => !KnownDeviations.ContainsKey(p.Key))
			.OrderBy(p => p.Verdict)
			.ThenBy(p => p.Key, StringComparer.Ordinal)
			.Select(p => $"  [{p.Verdict}] {p.Key}\n      call:     {p.Call}\n      answered: {p.Observed}")
			.ToList();

		served.Should().BeEmpty(
			"a call that names ANOTHER tenant must be refused on the authorization axis before anything else "
			+ "is decided about it — never served, and never answered with an argument error (an argument "
			+ "error proves the request was BOUND before it was judged, so the refusal is unreachable "
			+ "without valid arguments and a surface that forgot its check has nothing in front of it). "
			+ "If this surface is genuinely meant to cross tenants, it belongs to one of the six exemption "
			+ "classes of spec `authz-scope-declaration` and gets a [TenantExempt] — not a line in "
			+ "KnownDeviations, which only ever shrinks.\n" + string.Join("\n", served));
	}

	// The hole class, on its own, so it can never be read past in a long failure message: a surface
	// that SERVED another tenant is a different kind of news from one that answered 400.
	[Fact]
	public void NoUnlistedSurface_ServesAForeignTenant()
	{
		var breached = _host.Probes
			.Where(p => p.Addressed && p.Verdict == CrossTenantVerdict.Allowed)
			.Where(p => !KnownDeviations.ContainsKey(p.Key))
			.Select(p => $"  {p.Key}\n      call:     {p.Call}\n      answered: {p.Observed}")
			.ToList();

		breached.Should().BeEmpty(
			"these surfaces SERVED a caller from another tenant. This is not a form-of-denial question and "
			+ "not debt to schedule — it is one tenant reading or writing another's data.\n"
			+ string.Join("\n", breached));
	}

	[Fact]
	public void KnownDeviations_OnlyShrink()
	{
		var probes = _host.Probes.ToDictionary(p => p.Key, StringComparer.Ordinal);

		var stale = KnownDeviations
			.Where(entry => !probes.TryGetValue(entry.Key, out var probe)
				|| !probe.Addressed
				|| probe.Verdict == CrossTenantVerdict.Denied
				|| probe.Verdict != entry.Value.Verdict)
			.Select(entry => probes.TryGetValue(entry.Key, out var probe)
				? $"  {entry.Key}: listed as {entry.Value.Verdict}, now {(probe.Addressed ? probe.Verdict.ToString() : "not addressable")} ({probe.Observed})"
				: $"  {entry.Key}: no such surface any more")
			.Order(StringComparer.Ordinal)
			.ToList();

		stale.Should().BeEmpty(
			"a KnownDeviations entry that now DENIES is fixed — delete the line, the list only ever shrinks "
			+ "and a stale entry re-grants the exemption to whoever edits that surface next. An entry whose "
			+ "verdict CHANGED is worse: the note describes behaviour that no longer happens, and if it moved "
			+ "toward Allowed the surface got weaker under cover of an old comment.\n"
			+ string.Join("\n", stale));
	}

	[Fact]
	public void NotAddressable_AreNamedOneByOne()
	{
		var probes = _host.Probes.ToDictionary(p => p.Key, StringComparer.Ordinal);

		var wrong = NotAddressable
			.Where(entry => !probes.TryGetValue(entry.Key, out var probe) || probe.Addressed)
			.Select(entry => probes.ContainsKey(entry.Key)
				? $"  {entry.Key}: now HAS a tenant slot — it is addressable, so it must be asserted, not excused"
				: $"  {entry.Key}: no such surface any more")
			.Order(StringComparer.Ordinal)
			.ToList();

		wrong.Should().BeEmpty(
			"this list holds surfaces with nowhere to write another tenant's key. One that acquires a "
			+ "{projectKey}/{workspaceKey} route value or tool argument has left the list — delete the line "
			+ "and let EveryAddressedSurface_RefusesAForeignTenant judge it.\n" + string.Join("\n", wrong));

		var unexcused = _host.Probes
			.Where(p => !p.Addressed && !NotAddressable.ContainsKey(p.Key))
			.Select(p => $"  {p.Key}   [{p.Surface.Owner}]  answered: {p.Observed}")
			.Order(StringComparer.Ordinal)
			.ToList();

		unexcused.Should().BeEmpty(
			"a surface the probe could not aim at another tenant must be NAMED here with the reason it has no "
			+ "tenant slot — silently skipping it is precisely the failure mode this step exists to prevent "
			+ "(work card step 4: \"поверхности, которые нельзя дёрнуть автоматически, перечисляются явно и "
			+ "числом — не молча\").\n" + string.Join("\n", unexcused));
	}

	// The arithmetic, out loud. Every surface is refused, listed as a deviation, or listed as
	// not-addressable — and no surface is in two of those at once.
	[Fact]
	public void TheAccounting_IsComplete()
	{
		var refused = _host.Probes.Count(p => p.Addressed && p.Verdict == CrossTenantVerdict.Denied);
		var deviations = _host.Probes.Count(p => p.Addressed && p.Verdict != CrossTenantVerdict.Denied);
		var notAddressable = _host.Probes.Count(p => !p.Addressed);

		(refused + deviations + notAddressable).Should().Be(_host.Surfaces.Count,
			"every surface lands in exactly one bucket");
		_host.Surfaces.Should().HaveCount(215,
			"the inventory this test is driven by is the ratchet's (AuthzSurfaces): 55 REST + 65 Razor + 95 MCP "
			+ "(was 97 MCP / 217 before batch3 removed session_delta and config_binding_delta). "
			+ "If that number moved, a surface was added or removed and this test must be re-read, not "
			+ "re-baselined");

		deviations.Should().Be(KnownDeviations.Count,
			"KnownDeviations names every addressed surface that does not deny — no more and no fewer");
		notAddressable.Should().Be(NotAddressable.Count,
			"NotAddressable names every surface with no tenant slot — no more and no fewer");

		KnownDeviations.Keys.Should().NotIntersectWith(NotAddressable.Keys,
			"a surface is either aimable at another tenant or it is not; being on both lists means one of "
			+ "them is describing something that is not there");

		refused.Should().Be(143,
			"the count of surfaces that already refuse a foreign tenant. It is asserted rather than merely "
			+ "reported so that this test cannot go green while quietly protecting less than it did — the "
			+ "number may rise (fix a deviation) but never fall without someone deleting this line on purpose. "
			+ "140 -> 141 in the MCP declaration wave (memory_get stopped answering an argument error and now "
			+ "denies, because the PEP decides ahead of the tool body); 141 -> 143 in the REST wave, when the "
			+ "two session POST routes were reached for the first time (see the KnownDeviations note on why "
			+ "the old 415 measured the probe rather than the surface); 143 -> 144 with DELETE "
			+ "/api/config/{{workspaceKey}}/bindings, whose 400 came from binding its query parameters inside "
			+ "the endpoint and is now a 403 decided above it; 144 -> 143 when batch3 "
			+ "(read-surface-shape-batch-and-dead-delta) removed session_delta, a surface that was itself "
			+ "part of this Denied count. "
			+ "THE RAZOR WAVE MOVED IT BY ZERO, and that is the result rather than an absence of one: all 65 "
			+ "pages left the allowlist, 41 of them addressed, and every one of those 41 answered Denied "
			+ "BEFORE and after. The families that came out had complete manual coverage already, so the PEP "
			+ "reproduces their allow/deny exactly and only relocates the refusal — the same property the MCP "
			+ "and REST waves had, and the reason a wave that changed 65 surfaces is allowed to leave this "
			+ "line alone");
	}

	// ── GUARD THE GUARD ──────────────────────────────────────────────────────────────────────────
	//
	// Every assertion above is satisfied by a DENIAL, so an attacker that had silently stopped being a
	// valid caller — an unauthenticated key, a dead cookie, a host refusing everything — would make the
	// whole file green while testing nothing at all. That failure mode is the reason DbLayerGuardTests
	// and AuthzDeclarationRatchetTests both carry this section, and it is not hypothetical.

	[Fact]
	public void TheAttacker_IsServedByItsOwnTenant()
	{
		_host.SelfControls.Should().HaveCount(3, "one control per transport");
		_host.SelfControls.Where(c => !c.Served).Should().BeEmpty(
			"the attacker principal must be a WORKING caller in its own tenant. If it is not, every 'denied' "
			+ "above is a denial of a broken caller and this whole file proves nothing:\n  "
			+ string.Join("\n  ", _host.SelfControls.Select(c => $"{c.Name} -> {c.Observed}")));
	}

	[Fact]
	public void TheAttacker_CarriesEveryScope()
	{
		// The scope axis is already centralised and already works. If the probe key were short a scope,
		// the surfaces guarded by it would deny for the WRONG reason and read as passes.
		_host.ToolsVisibleToAttacker.Should().HaveCount(95,
			"McpToolScopeFilter trims tools/list to what the key's scopes allow. A key missing a scope sees "
			+ "fewer than the full 95 verbs (was 97 before batch3 removed session_delta and "
			+ "config_binding_delta), and every tool it cannot see would deny on the scope axis — a "
			+ "field of false greens. Seeing all 95 is the proof that every MCP denial above is about the "
			+ "TENANT");

		// The two scopes the deviation list BLAMES for the seven surfaces a foreign tenant reaches. If
		// the probe key did not actually carry them, those seven entries would be describing a denial
		// that never happened for a reason that was never tested.
		var whoami = _host.AttackerWhoAmI;
		whoami.Should().Contain(ApiKeyScopes.AdminProvision).And.Contain(ApiKeyScopes.DeployWrite,
			"the probe key is minted from ApiKeyScopes.All and whoami is the server's own account of what it "
			+ "sees; these are the two root-equivalent scopes the KnownDeviations entries name");
		whoami.Should().Contain($"\"project\":\"{AuthzCrossTenantHost.AttackerProject}\"",
			"the server must see the probe as the ATTACKER's tenant — if the claim were the victim's, or "
			+ "the wildcard '*', every denial above would be measuring something else entirely");
	}

	[Fact]
	public void TheProbe_ActuallyReachedTheSurface()
	{
		var byKey = _host.Probes.ToDictionary(p => p.Key, StringComparer.Ordinal);

		_host.Probes.Should().HaveCount(_host.Surfaces.Count, "one probe per surface, none skipped");
		_host.Probes.Select(p => p.Key).Should().OnlyHaveUniqueItems();

		// Anchors: one real denial per transport and per denial FORM, so a sweep that stopped reaching a
		// whole plane fails here instead of reporting an inventory of untouched surfaces.
		byKey["rest:GET /api/data/{projectKey}/dbs"].Verdict.Should().Be(CrossTenantVerdict.Denied,
			"the REST plane: a project-scoped endpoint must 403 a foreign key");
		byKey["page:/ProjectHome/Index"].Verdict.Should().Be(CrossTenantVerdict.Denied,
			"the Razor plane: a project page must send a foreign member to /AccessDenied");
		byKey["mcp:tasks_search"].Verdict.Should().Be(CrossTenantVerdict.Denied,
			"the MCP plane: a tool must raise UnauthorizedAccessException for a foreign projectKey");

		// The garbage-argument trick has to actually carry calls PAST the SDK's argument binder,
		// otherwise the MCP plane degenerates into 60-odd binder errors that say nothing either way.
		var binderErrors = _host.Probes.Count(p =>
			p.Surface.Transport == AuthzTransport.Mcp
			&& p.Observed.Contains("missing a value for the required parameter", StringComparison.Ordinal));
		binderErrors.Should().Be(0,
			"every MCP call must reach the tool body. A binder error means AuthzCrossTenantProbe.ArgumentsFor "
			+ "stopped filling the required arguments — and 62 tools would then report an argument error that "
			+ "cannot distinguish 'the check is late' from 'there is no check'");

		// The victim must be a REAL tenant. If it were not, every denial could be an existence answer
		// wearing an authorization costume, and the strongest result in the file would be an artefact.
		byKey["mcp:project_list"].Observed.Should().Contain(AuthzCrossTenantHost.VictimProject,
			"the victim project must genuinely exist in the victim workspace — otherwise 'denied' could just "
			+ "mean 'there is nothing there'");
	}

	// ── THE MACHINE REPORT ───────────────────────────────────────────────────────────────────────

	// Same discipline as the ratchet's inventory: every number this step reports has to be reproducible
	// by running the test and reading the file it writes.
	[Fact]
	public void TheProbe_IsMachineReadable()
	{
		var rendered = AuthzCrossTenantHost.Render(_host.Probes);
		var dir = Path.Combine(AuthzCrossTenantHost.RepoRoot(), ".tmp");
		Directory.CreateDirectory(dir);
		File.WriteAllText(Path.Combine(dir, "authz-cross-tenant-report.txt"), rendered);

		rendered.Split('\n').Length.Should().BeGreaterThan(_host.Probes.Count,
			"a line per surface plus the header");
		_host.Probes.Should().OnlyContain(p => !string.IsNullOrWhiteSpace(p.Observed),
			"a probe with nothing observed is a probe that never ran");
	}
}
