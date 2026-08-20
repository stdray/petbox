using PetBox.Core.Data;
using PetBox.LlmRouter.Contract;

namespace PetBox.LlmRouter.Registry;

// The admin/MCP surface's way into the LEVELLED registry. It answers exactly one question the
// levelled admin refuses to answer — "which level does THIS project write to?" — and then delegates
// to ILlmRegistryLevelAdmin, which still takes an explicit (Scope, ScopeKey) and still cannot
// cascade. The derivation happens HERE, once, from the project's workspace, and never from a row
// that was read: nothing the caller saw can steer where the write lands.
//
//   workspace $system  ->  level System:$        (the reserved built-in workspace IS the system
//                                                 level — that is where the imported registry lives
//                                                 and what every inheriting workspace is served
//                                                 from, so the owner's page must edit THAT, not a
//                                                 shadow level that would silently mask it)
//   any other ws       ->  level Workspace:{ws}
//
// A workspace that declares nothing of its own is INHERITING, and here it is READ-ONLY. Not out of
// caution: a level is resolved WHOLE (first level with a route wins, levels never merge), so
// "just add one endpoint here" would create a workspace level of one row that instantly SHADOWS the
// entire inherited registry — routes gone, keys gone. The safe move is to copy the level whole
// (override / copy-on-write), and that is deliberately not built yet (llm-l5 item 4: whether an
// override copies $system's key ciphertext is the owner's call, not ours).
public sealed class LlmRegistryEditor : ILlmRegistryEditor
{
	// This editor and the LevelAdmin it delegates to used to share ONE connection (both scoped, one
	// scope). They now open their own, which makes the ORDER OF OPERATIONS load-bearing:
	//
	//   NEVER call another core-db service while holding an open core transaction.
	//
	// core.db runs with Cache=Shared, where a second connection reading a table another connection
	// has locked gets SQLITE_LOCKED — and the busy handler does NOT retry LOCKED, so it is an
	// instant hard failure, not a wait. Every method below therefore resolves the level FIRST
	// (OwnLevelAsync opens a connection, reads, and disposes it — the `using` ends with the method),
	// and only THEN calls _admin, which opens its own connection and its own transaction. The read
	// is finished and the connection returned before any transaction exists. Keep it that way: if a
	// future edit needs a lookup inside the write, hand the value in — do not reach back out.
	readonly ICoreDbFactory _factory;
	readonly ILlmRegistryLevelAdmin _admin;
	readonly ILlmRegistryLevelResolver _resolver;

	public LlmRegistryEditor(ICoreDbFactory factory, ILlmRegistryLevelAdmin admin, ILlmRegistryLevelResolver resolver)
	{
		_factory = factory;
		_admin = admin;
		_resolver = resolver;
	}

	public async Task<LlmRegistry> GetAsync(string projectKey, CancellationToken ct = default)
	{
		var level = await OwnLevelAsync(projectKey, ct);
		return await _admin.GetAsync(level.Scope, level.ScopeKey, ct);
	}

	public async Task<LlmRegistryDeclaration> GetDeclaredAsync(string projectKey, CancellationToken ct = default)
	{
		var level = await OwnLevelAsync(projectKey, ct);
		var registry = await _admin.GetAsync(level.Scope, level.ScopeKey, ct);
		var version = await _admin.GetVersionAsync(level.Scope, level.ScopeKey, ct);

		// The level DECLARES something -> it is its own, and there is nothing being inherited past it.
		if (registry.Endpoints.Count > 0 || registry.Routes.Count > 0)
			return new LlmRegistryDeclaration(registry, version, level.ToString());

		// Nothing of its own. An empty read used to be indistinguishable from "this project has no
		// registry at all", which is the reading that makes an inheriting level look like free space —
		// and declaring one row here would SHADOW the inherited level whole. Name what actually serves
		// it (the resolver's answer, so this cannot disagree with the router), exactly as ViewAsync does
		// for the admin page. Sequential reads, each on its own connection, no transaction held — see
		// the class comment.
		var resolved = await _resolver.ResolveAsync(projectKey, ct);
		var servedBy = resolved.Level is { } from && from != level ? from.ToString() : null;
		return new LlmRegistryDeclaration(registry, version, level.ToString(), servedBy);
	}

	public async Task<LlmRegistryDeclaration> PatchAsync(
		string projectKey,
		IReadOnlyList<LlmEndpoint>? endpoints,
		IReadOnlyList<LlmRoute>? routes,
		IReadOnlyDictionary<string, string> apiKeys,
		long version,
		bool acknowledgeShadow = false,
		CancellationToken ct = default)
	{
		var level = await OwnLevelAsync(projectKey, ct);

		// What the level holds now — the base an omitted part is kept from. Read on its own
		// connection, which is closed before the write opens its transaction (see the class comment).
		var snapshot = await _admin.GetSnapshotAsync(level.Scope, level.ScopeKey, ct);

		// SHADOW CHECK — must run BEFORE the write, not after. `version` 0 is the caller declaring
		// "this level has nothing of its own yet"; that is only true when the snapshot just read
		// really is empty (a stale/wrong 0 against a level that already has rows falls through to
		// the ordinary CAS conflict below, unchanged). A genuinely first declaration does not "add"
		// — it SHADOWS whatever the workspace currently inherits, WHOLE, for every OTHER project of
		// the SAME workspace, not only the one named by `projectKey` (see the class comment). The
		// only way to make that checkable before it happens is to count those other projects here,
		// on their own connection, and refuse before SetSnapshotAsync ever opens its transaction.
		if (version == 0 && !acknowledgeShadow && snapshot.Endpoints.Count == 0 && snapshot.Routes.Count == 0)
		{
			var siblings = await SiblingProjectKeysAsync(projectKey, ct);
			if (siblings.Count > 0)
				throw new InvalidOperationException(ShadowRefusalMessage(level, siblings));
		}

		var mergedEndpoints = endpoints ?? snapshot.Endpoints;
		// Omitted routes keep their ROWS, ids included, so an edit that only touches endpoints does
		// not invalidate the handles the admin page rendered its route rows with. Supplied routes are
		// a whole replacement of that list and get fresh ids, exactly as SetAsync does.
		var mergedRoutes = routes is null
			? snapshot.Routes
			: routes.Select(r => new IdentifiedRoute(string.Empty, r)).ToList();

		var newVersion = await _admin.SetSnapshotAsync(
			level.Scope, level.ScopeKey, mergedEndpoints, mergedRoutes, apiKeys, expectedVersion: version, ct: ct);

		// The level is named in the RESULT as well as in the read. A write that reports only "ok, v2"
		// leaves the caller to re-derive where v2 lives, and re-deriving it from the projectKey is the
		// mistake this whole card is about.
		return new LlmRegistryDeclaration(
			new LlmRegistry(mergedEndpoints, mergedRoutes.Select(r => r.Route).ToList()),
			newVersion,
			level.ToString());
	}

	public async Task SetAsync(
		string projectKey,
		LlmRegistry registry,
		IReadOnlyDictionary<string, string> apiKeys,
		CancellationToken ct = default)
	{
		var level = await OwnLevelAsync(projectKey, ct);
		await _admin.SetAsync(level.Scope, level.ScopeKey, registry, apiKeys, ct: ct);
	}

	public async Task<LlmRegistryView> ViewAsync(string projectKey, CancellationToken ct = default)
	{
		var own = await OwnLevelAsync(projectKey, ct);
		var snapshot = await _admin.GetSnapshotAsync(own.Scope, own.ScopeKey, ct);

		// The level DECLARES something -> these rows are its own, and editable. Note that a level with
		// endpoints but no route yet does not serve anything (level-atomic), and it is still its own:
		// that is the state you are in halfway through building one.
		if (snapshot.Endpoints.Count > 0 || snapshot.Routes.Count > 0)
			return new LlmRegistryView(own.ToString(), Inherited: false, InheritedFrom: null, snapshot.Endpoints, snapshot.Routes);

		// Nothing of its own: show what actually serves the project (the resolver's answer, so the
		// page cannot disagree with the router), read-only.
		var resolved = await _resolver.ResolveAsync(projectKey, ct);
		if (resolved.Level is { } from && from != own)
		{
			var inherited = await _admin.GetSnapshotAsync(from.Scope, from.ScopeKey, ct);
			return new LlmRegistryView(own.ToString(), Inherited: true, from.ToString(), inherited.Endpoints, inherited.Routes);
		}

		return new LlmRegistryView(own.ToString(), Inherited: false, InheritedFrom: null, [], []);
	}

	public async Task SaveAsync(
		string projectKey,
		IReadOnlyList<LlmEndpoint> endpoints,
		IReadOnlyList<IdentifiedRoute> routes,
		IReadOnlyDictionary<string, string> apiKeys,
		long? expectedVersion = null,
		CancellationToken ct = default)
	{
		var level = await OwnLevelAsync(projectKey, ct);
		await _admin.SetSnapshotAsync(level.Scope, level.ScopeKey, endpoints, routes, apiKeys, expectedVersion: expectedVersion, ct: ct);
	}

	// Opens, reads, and CLOSES — the connection does not outlive this method, so it is never held
	// while _admin runs its replace transaction (see the class comment).
	async Task<RegistryLevel> OwnLevelAsync(string projectKey, CancellationToken ct)
	{
		using var db = _factory.Open();

		var workspaceKey = await db.Projects
			.Where(p => p.Key == projectKey)
			.Select(p => p.WorkspaceKey)
			.FirstOrDefaultAsync(ct)
			?? throw new InvalidOperationException($"unknown project '{projectKey}'");

		return workspaceKey == WorkspaceMemory.SystemWorkspace
			? RegistryLevel.System
			: RegistryLevel.Workspace(workspaceKey);
	}

	// The OTHER projects of `projectKey`'s own workspace — the radius a `version` 0 write would
	// shadow. Deliberately workspace-scoped, not full-cascade: a `System:$` write is also inherited
	// by every OTHER workspace that declares nothing of its own, but folding that in is the general
	// "write radius = shadow radius" model question this card explicitly defers (see the class
	// comment and work llm-config-upsert-shadow-radius-mismatch) — not a per-project override, and
	// not this. Own connection, closed before it returns — same rule as OwnLevelAsync, and this is
	// only ever called before SetSnapshotAsync opens its transaction.
	async Task<IReadOnlyList<string>> SiblingProjectKeysAsync(string projectKey, CancellationToken ct)
	{
		using var db = _factory.Open();

		var workspaceKey = await db.Projects
			.Where(p => p.Key == projectKey)
			.Select(p => p.WorkspaceKey)
			.FirstOrDefaultAsync(ct)
			?? throw new InvalidOperationException($"unknown project '{projectKey}'");

		return await db.Projects
			.Where(p => p.WorkspaceKey == workspaceKey && p.Key != projectKey)
			.Select(p => p.Key)
			.OrderBy(k => k)
			.ToListAsync(ct);
	}

	// Worded like the CAS Conflict message this sits next to (LlmRegistryLevelAdmin.Conflict): name
	// the count AND the projects, not just "some" — a caller staring at a refusal should not have to
	// go query who else is in its workspace to decide whether `acknowledgeShadow: true` is safe.
	static string ShadowRefusalMessage(RegistryLevel level, IReadOnlyList<string> siblings) =>
		$"llm registry level {level} declares nothing yet — writing version 0 here would SHADOW the " +
		$"inherited registry WHOLE for {siblings.Count} other project{(siblings.Count == 1 ? "" : "s")} " +
		$"of this workspace ({string.Join(", ", siblings)}), not just the project you called with. " +
		"Pass acknowledgeShadow: true to confirm you intend that, or write to a level that already " +
		"declares something of its own.";
}
