using System.Security.Cryptography;
using System.Text;

namespace PetBox.Tasks.Engine.Tests;

// The in-memory stand-in for everything TasksService prefetches into MethodologyEngineContext.
// No store, no mock, no host: the context IS data, so the "fixture" is a builder over records.
//
// Two runtime shapes matter and both appear below, because the engine answers differently for
// them: PRESETS-ONLY (no methodology definition — a bare board) and QUARTET-DEFINED (what a real
// provisioned project has: RenderPresetDefinition materializes the preset kinds VERBATIM into the
// stored definition, so `work`/`spec` are DEFINED kind slugs there, not preset ones). That
// distinction is the presetkind-spec-blind-spot trap; tests that only used the preset shape are
// precisely how the blocker invariant once shipped never firing on a real board.
static class EngineFixture
{
	public const string SpecBoardName = "spec";
	public const string WorkBoardName = "work";
	public const string IdeasBoardName = "ideas";
	public const string Instance = "inst-1";

	// A real quartet project's runtime: every quartet kind is a DEFINED kind slug.
	public static readonly MethodologyRuntime Quartet =
		MethodologyRuntime.From(MethodologyPresets.RenderPresetDefinition("quartet"));

	public static readonly MethodologyRuntime Presets = MethodologyRuntime.PresetsOnly;

	// A real-shaped NodeId (32 lowercase hex, like a Guid "N") derived deterministically from a
	// readable seed, so a test can name the id it expects without hardcoding 32 characters.
	// It MUST be genuine hex: GuardEngine.LooksLikeNodeId is what separates "this is a NodeId" from
	// "this is a slug", so a seed-shaped fake id would take the slug branch and quietly test the
	// wrong thing.
	public static string Id(string seed) =>
		Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(seed)))[..32];

	public static NodeIndexEntry Node(string seed, string board, string boardKind, string slug, string status, string type = "") =>
		new(Id(seed), board, boardKind, slug, status, type);

	public static NodeState State(string key, string status, string type = "", string? prevKey = null, string nodeId = "") =>
		new(key, prevKey, nodeId, status, type);

	public static Dictionary<string, string> Refs(params (string Key, string Value)[] pairs) =>
		pairs.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);

	public static readonly Dictionary<string, string> NoRefs = new(StringComparer.Ordinal);

	// The generic links door as the engine reads it: (nodeKey, linkKind, refs) tuples folded into
	// nodeKey -> (kind -> refs). Refs may be a slug or a NodeId; a single value is just one element.
	public static Dictionary<string, IReadOnlyDictionary<string, IReadOnlyList<string>>> Links(
		params (string Key, string Kind, string[] Refs)[] entries)
	{
		var map = new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyList<string>>>(StringComparer.Ordinal);
		foreach (var (key, kind, refs) in entries)
		{
			if (!map.TryGetValue(key, out var byKind))
				map[key] = byKind = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
			((Dictionary<string, IReadOnlyList<string>>)byKind)[kind] = refs;
		}
		return map;
	}

	// One (nodeKey, kind, singleRef) link — the common case.
	public static Dictionary<string, IReadOnlyDictionary<string, IReadOnlyList<string>>> Link(string key, string kind, string @ref) =>
		Links((key, kind, [@ref]));

	public static readonly Dictionary<string, IReadOnlyDictionary<string, IReadOnlyList<string>>> NoLinks = new(StringComparer.Ordinal);

	// Resolved `blocks` edges (blocker→task, writer is the TO end) for the RequireBlockers guard.
	public static List<ResolvedLink> Blocks(params (string Key, string TargetId)[] pairs) =>
		pairs.Select(p => new ResolvedLink("blocks", p.Key, p.TargetId, WriterIsFrom: false)).ToList();

	public static readonly List<ResolvedLink> NoResolvedLinks = [];

	// A context over the quartet's three linked boards, indexed by whatever nodes the test names.
	public static MethodologyEngineContext Ctx(
		MethodologyRuntime? runtime = null,
		string kindSlug = "work",
		string board = WorkBoardName,
		string? specBoard = SpecBoardName,
		string instance = Instance,
		IEnumerable<NodeIndexEntry>? index = null,
		IEnumerable<EngineBoard>? boards = null,
		IReadOnlyDictionary<string, IReadOnlyList<string>>? blockerEdges = null,
		IReadOnlyDictionary<string, IReadOnlyList<string>>? partOfChildren = null,
		IReadOnlyDictionary<string, IReadOnlyList<string>>? commentTags = null) =>
		new(
			runtime ?? Quartet,
			kindSlug,
			board,
			board,
			specBoard,
			instance,
			(index ?? []).ToDictionary(n => n.NodeId, n => n, StringComparer.Ordinal),
			(boards ?? DefaultBoards).ToList(),
			blockerEdges ?? Edges(),
			partOfChildren ?? Edges(),
			commentTags ?? Edges());

	public static readonly EngineBoard[] DefaultBoards =
	[
		new(WorkBoardName, "work", Instance, Closed: false),
		new(SpecBoardName, "spec", Instance, Closed: false),
		new(IdeasBoardName, "ideas", Instance, Closed: false),
	];

	public static Dictionary<string, IReadOnlyList<string>> Edges(params (string NodeId, string[] Targets)[] pairs) =>
		pairs.ToDictionary(p => p.NodeId, p => (IReadOnlyList<string>)p.Targets, StringComparer.Ordinal);

	// An empty prior set — "nothing on this board exists yet". Spelled out rather than `[]` because
	// a collection expression can't build an IReadOnlyDictionary.
	public static readonly Dictionary<string, NodeState> NoPrior = new(StringComparer.Ordinal);

	public static Dictionary<string, NodeState> Prior(params NodeState[] rows) =>
		rows.ToDictionary(r => r.Key, r => r, StringComparer.Ordinal);
}
