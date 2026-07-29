using PetBox.Core.Auth;
using PetBox.Core.Data;
using PetBox.Memory.Contract;
using PetBox.Web.Auth;

namespace PetBox.Web.Memory;

// Non-MCP read surface for the agent memory canon (spec agent-wiring, memory-canon-storage).
// The wiring hooks pull the curated canon index at session start over REST (a shell command
// can't easily speak MCP), the same way the Stop hook pushes sessions via SessionApi. One
// endpoint, project-scoped, returns BOTH the project's canon and the caller's workspace
// canon so a single call arms an agent's context.
//   GET /api/memory/{projectKey}/canon
// Auth mirrors SessionApi: RequireAuthorization("ApiKey"), then assert memory:read and that the
// key's project claim authorizes {projectKey}.
//
// Card canon-invisible-and-unfed: an EMPTY canon used to mean a null part — 200, silently
// carrying nothing, so an agent on day 0 never learned canon exists at all (the store/key/
// budget convention lived ONLY in MemoryService.cs's comments). A queried-but-empty scope now
// answers with Version 0 (still 200, still a real CanonPart) instead of null — the signal both
// consumers of this route (the wiring hook's injected block AND a human hitting the endpoint
// directly) use to notice "this leg exists and is empty". null is reserved for a leg this
// CALLER cannot see at all (SandboxContainment suppression, or a project with no workspace) —
// never for "asked, and it's empty"; see CanonAsync below.
//
// Card canon-banner-empty-notice-unlabelled: an empty leg's Body used to carry a fixed
// human-readable nudge string (EmptyCanonMarker) — prose baked into the WIRE response. The kit
// (canon.ts's buildBlock) glued that prose onto the end of the assembled banner with no
// section heading of its own, so a populated project section immediately followed by an
// unheaded "canon is empty" line read as a claim about the WHOLE canon, not just the empty
// workspace leg. Fixed at the CORRECT level per that card: the server now returns emptiness AS
// emptiness — Body is "" (Version 0 is still the discriminator; nothing about the shape
// changed) — and turning that into human prose, attributed to the SPECIFIC leg it describes, is
// the renderer's job (canon.ts's EMPTY_CANON_TEXT / buildBlock), not this endpoint's. Consumers
// checked before this change: canon.ts (updated to classify by Version before looking at Body,
// and to no longer read the nudge text from the wire), MemoryCanonApiTests.cs (updated
// assertions), doctor-banner-budget.test.ts (already used Version 5 / content legs — untouched),
// status.ts's formatCanonLeg (already classified via CanonLegState.kind, never the body text —
// untouched).
public static class MemoryApi
{
	// The canon convention: store `canon`, entry `index` — the same in every container. The
	// project canon is `index` in the project container; the shared cross-project canon is
	// `index` in the project's workspace container (WorkspaceMemory.ContainerKeyFor — "$workspace"
	// for $system, "$ws-{wsKey}" otherwise). Two containers, one key: the scope is the
	// container, not a key suffix.
	const string CanonStore = "canon";
	const string CanonKey = "index";

	public static void MapMemoryEndpoints(this IEndpointRouteBuilder app)
	{
		app.MapGet("/api/memory/{projectKey}/canon", CanonAsync)
			.Produces<CanonResponse>()
			.RequireAuthorization("ApiKey");
	}

	// The handler opens no database: the canon comes from IMemoryService and the project's workspace
	// from IProjectDirectory (the catalog of projects — see AGENTS.md, "the database is visible only
	// in the service layer"; an endpoint lambda asks a service, it does not call .Open() itself).
	// TENANT: the PROJECT in the route, and only that. The workspace leg of the response is not a second
	// target a caller can aim — it is derived from the project's own row (IProjectDirectory). Declaring
	// `ArgumentOrContainer` here would be wrong for the same reason: this route takes a real project
	// key, never a `$workspace`/`$ws-<key>` container, and the container it reads is the one the
	// project belongs to.
	//
	// "NOT AIMABLE" IS NOT "AUTHORIZED", AND THIS COMMENT USED TO SAY IT WAS. It read "authorizing the
	// project authorizes it" — true for an ordinary key, FALSE for a sandboxOnly one, and that
	// sentence was the whole hole. Being underivable by an attacker says nothing about whether the
	// CALLER may have it: a sandboxOnly key aimed at its own perfectly legitimate sandbox project is
	// allowed by the PEP, and then this handler derived a container the PEP never judged. Measured on
	// production 2026-07-26 with the real smoke key: `GET /api/memory/smoke/canon` -> 200 carrying the
	// `$system` workspace canon (1309 bytes of owner facts) as the ENTIRE body, `project` being null,
	// while `GET /api/memory/$system/canon` and `/api/memory/kpvotes/canon` both -> 403. The gate
	// refused the key everywhere it was aimed and handed over the container anyway.
	[TenantFrom(TenantSource.Route, "projectKey")]
	static async Task<IResult> CanonAsync(
		HttpContext ctx, string projectKey, IMemoryService memory, IProjectDirectory projects,
		IProjectCatalog catalog, CancellationToken ct)
	{
		var scopes = ctx.User.Claims.FirstOrDefault(c => c.Type == "scopes")?.Value ?? "";
		if (!scopes.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries).Contains(ApiKeyScopes.MemoryRead))
			return TypedResults.Forbid();

		var project = await ReadCanonAsync(memory, projectKey, CanonKey, ct);

		// Workspace leg = the project's own workspace container — never a hardcoded global. An
		// unknown project has no row, so there is no container to even ask about — the workspace
		// part stays null (still 200): a genuinely absent LEG, distinct from an empty one.
		CanonPart? workspace = null;
		var wsKey = (await projects.GetAsync(projectKey, ct))?.WorkspaceKey;
		if (wsKey is not null)
		{
			var container = WorkspaceMemory.ContainerKeyFor(wsKey);

			// THE DERIVED-STORAGE HOP, asked of the shared predicate (SandboxContainment; the guard test
			// enumerates this site mechanically, no list is maintained by hand). A sandboxOnly key gets
			// the project leg it is entitled
			// to and NO workspace leg — suppression, not a 403, because the whole response is not
			// forbidden: the project canon is legitimately its own. ReadCanonAsync is simply never
			// CALLED for a suppressed leg, so `workspace` stays the plain null it was declared with —
			// distinct from an EMPTY canon (which ReadCanonAsync WOULD have answered with the nudge
			// had it been asked). A null workspace part is already a valid 200 shape, so the wiring
			// hook needs no new contract and simply injects no shared canon.
			if (await SandboxContainment.PermitsAsync(ctx.User, container, catalog, ct))
				workspace = await ReadCanonAsync(memory, container, CanonKey, ct);
		}

		return TypedResults.Ok(new CanonResponse(project, workspace));
	}

	// The active canon entry of a scope, or an empty Body at Version 0 when the store or entry
	// is absent — Version 0 is what turns silence into a visible "queried, empty" signal
	// (canon-invisible-and-unfed); the Body carries no prose (canon-banner-empty-notice-unlabelled
	// — the human-readable nudge is the RENDERER's job, see canon.ts's EMPTY_CANON_TEXT). An
	// unknown project has no store meta row either, so it gets the SAME empty answer as a real,
	// still-empty project: this endpoint deliberately does not disclose which (mirrors the
	// "refusal indistinguishable from absence" posture elsewhere in this file's auth checks).
	// Never returns null — a genuinely absent LEG (workspace suppressed/not applicable) is
	// CanonAsync's decision, made BEFORE this is even called, not this method's.
	static async Task<CanonPart> ReadCanonAsync(IMemoryService memory, string projectKey, string key, CancellationToken ct)
	{
		if (!await memory.StoreExistsAsync(projectKey, CanonStore, ct))
			return new CanonPart("", DateTime.UtcNow, 0);
		var entry = (await memory.ListActiveEntriesAsync(projectKey, CanonStore, ct))
			.FirstOrDefault(e => e.Key == key);
		return entry is null
			? new CanonPart("", DateTime.UtcNow, 0)
			: new CanonPart(entry.Body, entry.Updated, entry.Version);
	}
}

// One scope's canon: the raw index body plus its temporal cursor (updatedAt/version), so the
// hook can cache and detect staleness. A QUERIED scope is never null — an empty one carries an
// empty string as its Body (Version 0) instead of vanishing silently: Version 0 is the
// discriminator, NOT the Body text (card canon-banner-empty-notice-unlabelled) — a consumer
// must classify by Version first, then treat Body as real curated text only when Version > 0.
// null at the CanonResponse level means the leg was never asked (no workspace) or was withheld
// (sandbox containment) — see CanonAsync.
public sealed record CanonPart(string Body, DateTime UpdatedAt, long Version);

// GET /api/memory/{projectKey}/canon — the project's canon and its workspace's shared canon.
// Project is always populated (real content or the empty-canon nudge); Workspace is null only
// when there is no container to ask (no workspace) or the caller's key is sandbox-contained.
public sealed record CanonResponse(CanonPart? Project, CanonPart? Workspace);
