using LinqToDB;
using LinqToDB.Async;
using PetBox.Core.Data;
using PetBox.Tasks.Workflow;

namespace PetBox.Tasks.Data;

// The single door to enforced node tags (spec-flat-tags). Tags are "namespace:value"
// from a controlled set of namespaces; a node's tags are an SCD-2 set bound to its
// stable NodeId (so they survive renames). Lives in the per-project tasks file via
// IScopedDbFactory<TasksDb> (same file as plan_nodes — the FK needs same-file).
public interface ITagStore
{
	// Replace a node's active tag set with `tags` (normalized "ns:value"): soft-close
	// removed, vocab-ensure + insert added. With enforceNamespaces an unknown namespace
	// throws; with it false any namespace is allowed and a bare word is filed under the
	// default `tag:` namespace. The service resolves BOTH knobs from the board kind's tag
	// axes (MethodologyRuntime.TagAxes — no axes = free-form) and passes `namespaces`
	// explicitly; null falls back to the builtin preset axes (direct store callers).
	Task SetAsync(string projectKey, string board, string nodeId, IReadOnlyList<string> tags, bool enforceNamespaces = true, IReadOnlyList<string>? namespaces = null, CancellationToken ct = default);
	// Active tags for one node, sorted.
	Task<IReadOnlyList<string>> ActiveTagsAsync(string projectKey, string nodeId, CancellationToken ct = default);
	// Active (nodeId -> tag) for a whole board, for group-by projections.
	Task<ILookup<string, string>> BoardTagsAsync(string projectKey, string board, CancellationToken ct = default);
}

public sealed class TagStore : ITagStore
{
	// The default allowlist mirrors the preset quartet axes (MethodologyPresets.BuiltinAxes
	// — the pair is DATA there now); the service passes the resolved axes explicitly, so
	// this default only serves direct store callers.
	static readonly string[] DefaultNamespaces =
		MethodologyPresets.BuiltinAxes.Select(a => a.Namespace).ToArray();

	readonly IScopedDbFactory<TasksDb> _factory;
	public TagStore(IScopedDbFactory<TasksDb> factory) => _factory = factory;

	public async Task SetAsync(string projectKey, string board, string nodeId, IReadOnlyList<string> tags, bool enforceNamespaces = true, IReadOnlyList<string>? namespaces = null, CancellationToken ct = default)
	{
		if (string.IsNullOrWhiteSpace(nodeId)) throw new ArgumentException("nodeId is required");
		var desired = Normalize(tags, enforceNamespaces, namespaces);
		var ctx = _factory.GetDb(projectKey);

		var active = ctx.GetTable<NodeTag>().Where(t => t.NodeId == nodeId && t.ValidTo == null).ToList();
		var activeTags = active.Select(t => t.Tag).ToHashSet(StringComparer.Ordinal);
		var now = DateTime.UtcNow;

		// Soft-close tags no longer desired.
		foreach (var a in active.Where(a => !desired.Contains(a.Tag)))
			await ctx.GetTable<NodeTag>()
				.Where(t => t.NodeId == nodeId && t.Tag == a.Tag && t.ValidTo == null)
				.Set(t => t.ValidTo, _ => now)
				.UpdateAsync(ct);

		// Vocab-ensure + insert newly desired tags.
		foreach (var tag in desired.Where(d => !activeTags.Contains(d)))
		{
			await EnsureVocabAsync(ctx, tag, now, ct);
			await ctx.InsertAsync(new NodeTag { NodeId = nodeId, Board = board, Tag = tag, ValidFrom = now }, token: ct);
		}
	}

	public async Task<IReadOnlyList<string>> ActiveTagsAsync(string projectKey, string nodeId, CancellationToken ct = default)
	{
		var ctx = _factory.GetDb(projectKey);
		return (await ctx.GetTable<NodeTag>().Where(t => t.NodeId == nodeId && t.ValidTo == null).Select(t => t.Tag).ToListAsync(ct))
			.OrderBy(t => t, StringComparer.Ordinal).ToList();
	}

	public async Task<ILookup<string, string>> BoardTagsAsync(string projectKey, string board, CancellationToken ct = default)
	{
		var ctx = _factory.GetDb(projectKey);
		var rows = await ctx.GetTable<NodeTag>().Where(t => t.Board == board && t.ValidTo == null).Select(t => new { t.NodeId, t.Tag }).ToListAsync(ct);
		return rows.ToLookup(r => r.NodeId, r => r.Tag, StringComparer.Ordinal);
	}

	static async Task EnsureVocabAsync(TasksDb ctx, string tag, DateTime now, CancellationToken ct)
	{
		if (await ctx.GetTable<TagVocab>().AnyAsync(v => v.Tag == tag, ct)) return;
		var ns = tag[..tag.IndexOf(':')];
		await ctx.InsertAsync(new TagVocab { Tag = tag, Namespace = ns, CreatedAt = now }, token: ct);
	}

	// Lowercase, trim, de-dup. With enforceNamespaces: require "ns:value" with ns in the
	// allowlist — `namespaces` when given (the kind's tag axes), else the builtin preset
	// pair. Without it (axis-less kinds — simple boards and axis-less definitions): any
	// namespace is allowed and a bare word is filed under the default `tag:` namespace
	// (free-form, low ceremony). Empty in → empty set.
	public static IReadOnlySet<string> Normalize(IReadOnlyList<string>? tags, bool enforceNamespaces = true, IReadOnlyList<string>? namespaces = null)
	{
		var allowed = namespaces ?? DefaultNamespaces;
		var set = new HashSet<string>(StringComparer.Ordinal);
		if (tags is null) return set;
		foreach (var raw in tags)
		{
			if (string.IsNullOrWhiteSpace(raw)) continue;
			var tag = raw.Trim().ToLowerInvariant();
			var colon = tag.IndexOf(':');
			if (colon <= 0 || colon == tag.Length - 1)
			{
				// No usable "ns:value" shape.
				if (enforceNamespaces)
					throw new ArgumentException($"tag '{raw}' must be 'namespace:value' (namespaces: {string.Join("|", allowed)})");
				var word = tag.Trim(':');
				if (word.Length > 0) set.Add("tag:" + word); // bare word → default namespace
				continue;
			}
			var ns = tag[..colon];
			if (enforceNamespaces && !allowed.Contains(ns))
				throw new ArgumentException($"unknown tag namespace '{ns}' (allowed: {string.Join("|", allowed)})");
			set.Add(tag);
		}
		return set;
	}
}
