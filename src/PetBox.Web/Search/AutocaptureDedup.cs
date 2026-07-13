using System.Text.Json;
using PetBox.Core.Search;
using PetBox.LlmRouter.Contract;
using PetBox.Memory.Contract;

namespace PetBox.Web.Search;

// Tunables for the deterministic dedup guard (spec: memoverhaul / autocapture-dedup). Bound
// from configuration section "AutocaptureDedup"; the defaults reproduce the pre-config values.
public sealed class AutocaptureDedupOptions
{
	// Two entries must be near-identical in embedding space to collapse — conservative so
	// genuinely different facts are never merged.
	public double SemanticThreshold { get; set; } = AutocaptureDedup.DefaultSemanticThreshold;

	// Above this many entries the O(n²) semantic clustering is skipped for the sweep and it
	// falls back to text-only. The embed-once-per-pass cache removes the repeated-embedding
	// cost, so the ceiling now guards only the cheap in-memory cosine loop (n² dot products,
	// sub-second even at a few thousand entries, run at most once every RecollapseIntervalDays)
	// — safe to keep generous.
	public int MaxSemanticCluster { get; set; } = AutocaptureDedup.DefaultMaxSemanticCluster;

	// The pre-existing-twin sweep is PERIODIC, not one-shot: re-run once the last sweep is
	// older than this many days. New machine writes keep accruing consolidation debt, so a
	// single lifetime sweep would let twins pile up again — this is also the "background
	// consolidation" promise (W3) folded into the mining tick.
	public int RecollapseIntervalDays { get; set; } = 7;
}

// Deterministic dedup guard for machine memory writes (spec: autocapture-dedup). The LLM
// judge (SessionFactsJob) and the miner (BehaviorPatternJob) are SOFT filters: a
// hallucinated "add", a paraphrase the neighbor search never surfaced, or an existing
// pattern the miner failed to reference by key each births a duplicate row — the observed
// prod gap (four bp-… twins on $system). This guard sits BEHIND the LLM on the write path
// and is not fooled by rephrasing:
//   (a) cheap  — normalized text equality of the identity line (lowercase, punctuation →
//                space, whitespace collapsed);
//   (b) semantic — cosine over on-the-fly embeddings (reuses the SAME ILlmClient embed
//                route + VectorMath the vector index rides), thresholded conservatively so
//                genuinely different facts are never merged.
// Embedding failure/absence degrades to text-only — the same discipline as the chat-down
// no-op the jobs already keep. A companion CollapseAsync folds pre-existing twins, run
// periodically off the mining tick. An EmbeddingCache passed by the caller embeds each
// distinct text at most once per DrainAllAsync pass (the store used to be re-embedded on
// every candidate).
internal static class AutocaptureDedup
{
	// Defaults for AutocaptureDedupOptions; the single source of truth for the magic numbers.
	internal const double DefaultSemanticThreshold = 0.92;
	internal const int DefaultMaxSemanticCluster = 2000;

	// Identity key for the cheap pass: lowercase, every non-alphanumeric run → a single
	// space, trimmed. Casing/punctuation drift no longer hides an exact duplicate.
	internal static string Normalize(string? s)
	{
		if (string.IsNullOrWhiteSpace(s)) return "";
		var sb = new System.Text.StringBuilder(s.Length);
		var pendingSpace = false;
		foreach (var ch in s.Trim())
		{
			if (char.IsLetterOrDigit(ch))
			{
				if (pendingSpace && sb.Length > 0) sb.Append(' ');
				pendingSpace = false;
				sb.Append(char.ToLowerInvariant(ch));
			}
			else pendingSpace = true;
		}
		return sb.ToString();
	}

	// Returns the key of an existing entry that duplicates `candidateText`, or null. The
	// cheap normalized-equality pass runs first; the semantic pass runs only when it found
	// nothing and an embedder is available. `cache` (per-pass) embeds the store once.
	internal static async Task<string?> FindDuplicateKeyAsync(string project, string candidateText,
		IReadOnlyList<(string Key, string Text)> existing, ILlmClient? llm, CancellationToken ct,
		double semanticThreshold = DefaultSemanticThreshold, EmbeddingCache? cache = null)
	{
		if (existing.Count == 0 || string.IsNullOrWhiteSpace(candidateText)) return null;

		var norm = Normalize(candidateText);
		if (norm.Length > 0)
			foreach (var e in existing)
				if (Normalize(e.Text) == norm) return e.Key;

		var texts = new List<string> { candidateText };
		texts.AddRange(existing.Select(e => e.Text));
		var vectors = await EmbedAsync(project, texts, llm, cache, ct);
		if (vectors is null) return null;

		var query = vectors[0];
		var bestScore = -1.0;
		string? bestKey = null;
		for (var i = 0; i < existing.Count; i++)
		{
			var sim = VectorMath.Cosine(query, vectors[i + 1]);
			if (sim > bestScore) { bestScore = sim; bestKey = existing[i].Key; }
		}
		return bestScore >= semanticThreshold ? bestKey : null;
	}

	// Idempotent collapse of duplicate clusters in a quarantined store (requirement: fold the
	// twins already on disk without hand-SQL). Clusters active entries by normalized-text ∪
	// semantic similarity, keeps the richest-provenance canonical, merges every cluster
	// member's provenance into it, and soft-deletes the rest. Returns the number of entries
	// removed. Callers gate it behind a durable cursor so it runs at most once per
	// RecollapseIntervalDays and survives restart.
	internal static async Task<int> CollapseAsync(IMemoryService memory, string project, string store,
		ILlmClient? llm, ILogger? logger, CancellationToken ct,
		double semanticThreshold = DefaultSemanticThreshold, int maxSemanticCluster = DefaultMaxSemanticCluster,
		EmbeddingCache? cache = null)
	{
		var entries = await memory.ListAsync(project, store, type: null, ct);
		if (entries.Count < 2) return 0;

		var clusters = await ClusterAsync(project, entries, llm, semanticThreshold, maxSemanticCluster, cache, ct);
		var removed = 0;
		foreach (var cluster in clusters)
		{
			ct.ThrowIfCancellationRequested();
			if (cluster.Count < 2) continue;
			var canonical = PickCanonical(cluster);
			var deletes = cluster.Where(e => e.Key != canonical.Key)
				.Select(e => new MemoryDelete(e.Key, e.Version)).ToList();
			await memory.UpsertAsync(project, store, [new MemoryEntryInput
			{
				Key = canonical.Key,
				Version = canonical.Version,
				Type = canonical.Type,
				Description = canonical.Description,
				Body = canonical.Body,
				Tags = canonical.Tags,
				Metadata = MergeMetadata(cluster, canonical),
			}], deletes, ct: ct);
			removed += deletes.Count;
		}
		if (removed > 0 && logger is not null && logger.IsEnabled(LogLevel.Information))
			logger.LogInformation("autocapture dedup sweep collapsed {Removed} duplicate entries in {Project}/{Store}",
				removed, project, store);
		return removed;
	}

	static async Task<List<List<MemoryEntryView>>> ClusterAsync(string project,
		IReadOnlyList<MemoryEntryView> entries, ILlmClient? llm, double semanticThreshold,
		int maxSemanticCluster, EmbeddingCache? cache, CancellationToken ct)
	{
		var n = entries.Count;
		var parent = Enumerable.Range(0, n).ToArray();
		int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
		void Union(int a, int b) { parent[Find(a)] = Find(b); }

		// Cheap pass: identical normalized identity line.
		var norms = entries.Select(e => Normalize(e.Description)).ToArray();
		for (var i = 0; i < n; i++)
			for (var j = i + 1; j < n; j++)
				if (norms[i].Length > 0 && norms[i] == norms[j]) Union(i, j);

		// Semantic pass: cosine ≥ threshold folds rephrasings the cheap pass misses.
		if (n <= maxSemanticCluster)
		{
			var vectors = await EmbedAsync(project, entries.Select(e => e.Description).ToList(), llm, cache, ct);
			if (vectors is not null)
				for (var i = 0; i < n; i++)
					for (var j = i + 1; j < n; j++)
						if (Find(i) != Find(j) && VectorMath.Cosine(vectors[i], vectors[j]) >= semanticThreshold)
							Union(i, j);
		}

		var groups = new Dictionary<int, List<MemoryEntryView>>();
		for (var i = 0; i < n; i++)
		{
			var root = Find(i);
			if (!groups.TryGetValue(root, out var list)) groups[root] = list = [];
			list.Add(entries[i]);
		}
		return groups.Values.ToList();
	}

	// The keeper is the entry that already proves the most repetition (widest sources ∪
	// seenIn ∪ own sessionId); ties break to the oldest revision so the stable original key
	// survives and recall history points somewhere durable.
	static MemoryEntryView PickCanonical(IReadOnlyList<MemoryEntryView> cluster) =>
		cluster.OrderByDescending(ProvenanceWidth).ThenBy(e => e.Version).First();

	static int ProvenanceWidth(MemoryEntryView e)
	{
		var ids = new HashSet<string>(MetaArray(e.Metadata, "sources"));
		foreach (var s in MetaArray(e.Metadata, "seenIn")) ids.Add(s);
		var sid = MetaString(e.Metadata, "sessionId");
		if (sid is not null) ids.Add(sid);
		return ids.Count;
	}

	// A merge ACCUMULATES provenance, never drops it: union every member's sources and every
	// member's seenIn/sessionId, and keep the canonical's own sessionId+messages so the
	// verbatim bridge (session_get) still resolves.
	static string MergeMetadata(IReadOnlyList<MemoryEntryView> cluster, MemoryEntryView canonical)
	{
		var sources = new HashSet<string>();
		var seenIn = new HashSet<string>();
		foreach (var e in cluster)
		{
			foreach (var s in MetaArray(e.Metadata, "sources")) sources.Add(s);
			foreach (var s in MetaArray(e.Metadata, "seenIn")) seenIn.Add(s);
			var sid = MetaString(e.Metadata, "sessionId");
			if (sid is not null) seenIn.Add(sid);
		}

		var dict = new Dictionary<string, object?>();
		var canonSid = MetaString(canonical.Metadata, "sessionId");
		if (canonSid is not null) dict["sessionId"] = canonSid;
		var messages = MetaRaw(canonical.Metadata, "messages");
		if (messages is not null) dict["messages"] = messages;
		if (seenIn.Count > 0) dict["seenIn"] = seenIn.ToList();
		if (sources.Count > 0) dict["sources"] = sources.ToList();
		return JsonSerializer.Serialize(dict);
	}

	// Embed with the optional per-pass cache; falls back to a direct batch embed when no cache
	// is supplied (a match keeps the pre-cache behavior for callers that don't pass one).
	static Task<IReadOnlyList<float[]>?> EmbedAsync(string project, List<string> inputs,
		ILlmClient? llm, EmbeddingCache? cache, CancellationToken ct) =>
		cache is not null ? cache.EmbedAsync(project, inputs, llm, ct) : TryEmbedAsync(project, inputs, llm, ct);

	internal static async Task<IReadOnlyList<float[]>?> TryEmbedAsync(string project, IReadOnlyList<string> inputs,
		ILlmClient? llm, CancellationToken ct)
	{
		if (llm is null || inputs.Count == 0) return null;
		try
		{
			if (!await llm.IsAvailableAsync(project, LlmCapability.Embed, ct)) return null;
			var res = await llm.EmbedAsync(project, new EmbedRequest(inputs), ct);
			// Dim/count mismatch or a stub that can't embed → degrade, don't guess.
			if (res.Vectors.Count != inputs.Count) return null;
			return res.Vectors;
		}
		catch (OperationCanceledException) { throw; }
		catch { return null; } // embed down → text-only, same discipline as the chat-down no-op
	}

	static List<string> MetaArray(string metadata, string prop)
	{
		if (string.IsNullOrWhiteSpace(metadata)) return [];
		try
		{
			using var doc = JsonDocument.Parse(metadata);
			if (!doc.RootElement.TryGetProperty(prop, out var arr) || arr.ValueKind != JsonValueKind.Array) return [];
			return arr.EnumerateArray().Select(x => x.GetString())
				.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!).ToList();
		}
		catch (JsonException) { return []; }
	}

	static string? MetaString(string metadata, string prop)
	{
		if (string.IsNullOrWhiteSpace(metadata)) return null;
		try
		{
			using var doc = JsonDocument.Parse(metadata);
			return doc.RootElement.TryGetProperty(prop, out var v) && !string.IsNullOrWhiteSpace(v.GetString())
				? v.GetString() : null;
		}
		catch (JsonException) { return null; }
	}

	// Raw JSON of a property (e.g. the [from,to] message range), cloned so it outlives the doc.
	static JsonElement? MetaRaw(string metadata, string prop)
	{
		if (string.IsNullOrWhiteSpace(metadata)) return null;
		try
		{
			using var doc = JsonDocument.Parse(metadata);
			return doc.RootElement.TryGetProperty(prop, out var v) ? v.Clone() : null;
		}
		catch (JsonException) { return null; }
	}
}

// Per-pass embedding memo: within one DrainAllAsync pass a candidate is compared against the
// whole quarantined store, so the store's texts would be re-embedded on every candidate. This
// caches vectors by exact text and batches only the misses, degrading to text-only (null) the
// moment an embed fails — the same discipline as the chat-down no-op. Not thread-safe: a pass
// runs sequentially on one worker.
internal sealed class EmbeddingCache
{
	readonly Dictionary<string, float[]> _byText = new(StringComparer.Ordinal);

	public async Task<IReadOnlyList<float[]>?> EmbedAsync(string project, IReadOnlyList<string> inputs,
		ILlmClient? llm, CancellationToken ct)
	{
		if (inputs.Count == 0) return null;

		var missing = new List<string>();
		foreach (var t in inputs)
			if (!_byText.ContainsKey(t) && !missing.Contains(t)) missing.Add(t);

		if (missing.Count > 0)
		{
			var vectors = await AutocaptureDedup.TryEmbedAsync(project, missing, llm, ct);
			if (vectors is null) return null; // embed down this attempt → text-only, don't poison the cache
			for (var i = 0; i < missing.Count; i++) _byText[missing[i]] = vectors[i];
		}

		var result = new List<float[]>(inputs.Count);
		foreach (var t in inputs) result.Add(_byText[t]);
		return result;
	}
}
