using PetBox.Core.Data;
using PetBox.Core.Settings;
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
	readonly PetBoxDb _db;
	readonly ILlmRegistryLevelAdmin _admin;
	readonly ILlmRegistryLevelResolver _resolver;

	public LlmRegistryEditor(PetBoxDb db, ILlmRegistryLevelAdmin admin, ILlmRegistryLevelResolver resolver)
	{
		_db = db;
		_admin = admin;
		_resolver = resolver;
	}

	public async Task<LlmRegistry> GetAsync(string projectKey, CancellationToken ct = default)
	{
		var level = await OwnLevelAsync(projectKey, ct);
		return await _admin.GetAsync(level.Scope, level.ScopeKey, ct);
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
		CancellationToken ct = default)
	{
		var level = await OwnLevelAsync(projectKey, ct);
		await _admin.SetSnapshotAsync(level.Scope, level.ScopeKey, endpoints, routes, apiKeys, ct: ct);
	}

	async Task<RegistryLevel> OwnLevelAsync(string projectKey, CancellationToken ct)
	{
		var workspaceKey = await _db.Projects
			.Where(p => p.Key == projectKey)
			.Select(p => p.WorkspaceKey)
			.FirstOrDefaultAsync(ct)
			?? throw new InvalidOperationException($"unknown project '{projectKey}'");

		return workspaceKey == WorkspaceMemory.SystemWorkspace
			? RegistryLevel.System
			: RegistryLevel.Workspace(workspaceKey);
	}
}
