using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PetBox.Core.Data;
using PetBox.Core.Search;
using PetBox.LlmRouter.Contract;
using PetBox.Memory.Contract;
using PetBox.Memory.Data;

namespace PetBox.Web.Search;

// Cross-session behavior-pattern mining (spec: autocapture-behavior-patterns): a recurring
// procedure is invisible inside one session's delta — it is proven by REPETITION across
// sources. This pass consolidates the accumulated Feedback distillates (plus session-digest
// summaries) into pattern entries: type=Feedback, tag behavior:pattern, and
// metadata.sources = every contributing sessionId — the lift's true skill/hook candidates.
//
// The observation accumulator IS the memory store: the cursor is the store's temporal
// version (memory.DeltaAsync), mining fires only when ≥MinNewFeedback fresh Feedback
// entries accumulated, and consolidation happens in ONE chat call that sees the existing
// patterns (with keys) and answers create-or-update — no separate judge round. Runs after
// SessionFactsJob on the same enrichment tick, same quarantine, same degradation
// disciplines (chat down → no-op, cursor holds, backfills on recovery).
public sealed class BehaviorPatternJob : IBackgroundIndexJob
{
	public const string PatternTag = "behavior:pattern";
	const string QuarantineStore = SessionFactsJob.Store;
	const string CuratedStore = "notes";
	const string CursorIndex = "behavior-mining";
	// PERIODIC durable marker for the pre-existing-duplicate collapse: it stores the UNIX
	// SECONDS of the last sweep (co-located with the store, like every other cursor). 0 = never
	// swept. The sweep re-runs once the marker is older than RecollapseIntervalDays — machine
	// writes keep accruing consolidation debt, so a single lifetime sweep isn't enough.
	const string DedupSweepIndex = "autocapture-dedup-sweep";

	// A pattern is REPETITION — it needs ≥2 distinct sources. Mining on a single fresh
	// Feedback burns a chat call that can only answer [] (one observation can't repeat),
	// unless that entry already carries ≥2 accumulated sources (seenIn) — but then it is
	// not "fresh" in isolation either. Wait for ≥2 fresh observations before firing.
	internal const int MinNewFeedback = 2;
	internal const int MaxInputEntries = 60;
	internal const int MaxPatternsPerPass = 5;
	internal const int BodyClip = 300;

	const string MinePrompt =
		"""
		You consolidate recurring BEHAVIOR PATTERNS from an agent's memory. You are given
		FEEDBACK facts (each with its source sessionId), EXISTING PATTERNS (each with its
		key and known sources), and session digest summaries for context. Identify
		procedures that repeat across at least TWO distinct sources. Output STRICT JSON
		only — an array (possibly empty) of:
		  {"key":"<existing pattern key to update, omit for a new pattern>","description":"когда X — делай Y (one line)","body":"<2-4 lines of detail>","sources":["<sessionId>", ...]}
		`sources` lists EVERY distinct contributing sessionId you saw (for an update —
		merged with the known ones). Never invent sources; never output a pattern with
		fewer than two distinct sources; [] when nothing repeats.
		""";

	readonly IScopedDbFactory<MemoryDb> _factory;
	readonly IMemoryService _memory;
	readonly ILlmClient? _llm;
	readonly ILogger<BehaviorPatternJob>? _logger;
	readonly TimeSpan _budget;
	readonly AutocaptureDedupOptions _dedup;

	// Round-robin start position across passes; passes run strictly sequentially.
	static int _rotation;

	public BehaviorPatternJob(IScopedDbFactory<MemoryDb> factory, IMemoryService memory,
		ILlmClient? llm = null, ILogger<BehaviorPatternJob>? logger = null, TimeSpan? budget = null,
		IOptions<AutocaptureDedupOptions>? dedup = null)
	{
		_factory = factory;
		_memory = memory;
		_llm = llm;
		_logger = logger;
		_budget = budget ?? DrainPacing.DefaultBudget;
		_dedup = dedup?.Value ?? new AutocaptureDedupOptions();
	}

	public async Task<int> DrainAllAsync(CancellationToken ct)
	{
		if (_llm is null) return 0;

		var mined = 0;
		var clock = new DrainClock(_budget);
		foreach (var project in DrainPacing.Rotate(ScopedDbFiles.ListScopeKeys(_factory.BaseDir), ref _rotation))
		{
			ct.ThrowIfCancellationRequested();
			if (clock.Exhausted) break; // mining = one chat call per project; rotation serves the rest next pass
			try
			{
				if (!await _memory.StoreExistsAsync(project, QuarantineStore, ct)) continue;
				if (!await _llm.IsAvailableAsync(project, LlmCapability.Chat, ct)) continue;

				// GetDb runs the store migrations before the cursor's raw connections
				// (reference: NewConnection ≠ migrations).
				_factory.GetDb(project, QuarantineStore);
				var cursors = new SqliteIndexCursorStore(() => _factory.NewConnection(project, QuarantineStore));

				// One embedding memo per project per pass, shared by the sweep and the miner's
				// dedup guard (both compare against the whole store).
				var embedCache = new EmbeddingCache();

				// PERIODIC cleanup of twins on disk (spec: autocapture-dedup; also the W3
				// "background consolidation" promise). Runs before the fresh-observation gate so
				// a store with duplicates but no new feedback is still healed; the durable marker
				// holds the last sweep time and re-runs once it is older than the configured
				// interval.
				var lastSweep = await cursors.GetCursorAsync(DedupSweepIndex, ct);
				var nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
				var intervalSeconds = (long)TimeSpan.FromDays(Math.Max(0, _dedup.RecollapseIntervalDays)).TotalSeconds;
				if (lastSweep == 0 || nowUnix - lastSweep >= intervalSeconds)
				{
					await AutocaptureDedup.CollapseAsync(_memory, project, QuarantineStore, _llm, _logger, ct,
						_dedup.SemanticThreshold, _dedup.MaxSemanticCluster, embedCache);
					await cursors.SetCursorAsync(DedupSweepIndex, nowUnix, ct);
				}

				var cursor = await cursors.GetCursorAsync(CursorIndex, ct);

				var delta = (await _memory.DeltaAsync(project, QuarantineStore, cursor, ct)).Result;
				var fresh = delta.Added.Concat(delta.Updated).Count(e => e.Type == MemoryType.Feedback);
				if (fresh < MinNewFeedback) continue; // cursor holds — wait for more observations

				mined += await MineAsync(project, embedCache, ct);
				clock.Unit();

				// Re-read the version AFTER our own writes so the patterns we just wrote
				// don't count as fresh observations and self-trigger the next pass.
				var after = (await _memory.DeltaAsync(project, QuarantineStore, cursor, ct)).Result;
				await cursors.SetCursorAsync(CursorIndex, after.CurrentVersion, ct);
			}
			catch (Exception ex) when (ex is not OperationCanceledException)
			{
				_logger?.LogError(ex, "behavior-pattern mining failed for project {Project}; skipped", project);
			}
		}
		return mined;
	}

	async Task<int> MineAsync(string project, EmbeddingCache embedCache, CancellationToken ct)
	{
		var feedback = new List<MemoryEntryView>();
		feedback.AddRange(await _memory.ListAsync(project, QuarantineStore, "Feedback", ct));
		if (await _memory.StoreExistsAsync(project, CuratedStore, ct))
			feedback.AddRange(await _memory.ListAsync(project, CuratedStore, "Feedback", ct));
		feedback = feedback.Take(MaxInputEntries).ToList();

		// An entry is an EXISTING PATTERN only when it already carries merged sources
		// (i.e. this miner consolidated it before). A single-source entry — even one the
		// extraction slice already tagged behavior:pattern — is still an OBSERVATION:
		// it must sit in the facts list so repetition across sessions can prove it.
		var existingPatterns = feedback.Where(e => SourcesOf(e).Count > 0).ToList();

		var digests = await _memory.StoreExistsAsync(project, SessionDigestJob.Store, ct)
			? (await _memory.ListAsync(project, SessionDigestJob.Store, type: null, ct)).Take(30).ToList()
			: [];

		var sb = new StringBuilder();
		sb.AppendLine("FEEDBACK FACTS:");
		foreach (var e in feedback.Where(e => SourcesOf(e).Count == 0))
			sb.AppendLine(JsonSerializer.Serialize(new { e.Key, e.Description, Body = Clip(e.Body), sessionIds = ObservedIn(e) }));
		sb.AppendLine();
		sb.AppendLine("EXISTING PATTERNS:");
		foreach (var e in existingPatterns)
			sb.AppendLine(JsonSerializer.Serialize(new { e.Key, e.Description, Body = Clip(e.Body), sources = SourcesOf(e) }));
		sb.AppendLine();
		sb.AppendLine("SESSION DIGESTS:");
		foreach (var d in digests)
			sb.AppendLine(Clip(d.Description));

		var res = await _llm!.ChatAsync(project, new ChatRequest(
			[new ChatMessage("system", MinePrompt), new ChatMessage("user", sb.ToString())],
			Temperature: 0.1,
			MaxTokens: 2000), ct);

		var candidates = Parse(res.Text);
		if (candidates is null)
		{
			_logger?.LogWarning("behavior-pattern mining returned unparseable output for {Project}; pass skipped", project);
			return 0; // cursor still advances in the caller — no chat-burn loop on bad output
		}

		var written = 0;
		foreach (var candidate in candidates.Take(MaxPatternsPerPass))
		{
			ct.ThrowIfCancellationRequested();
			var sources = (candidate.Sources ?? []).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().ToList();
			if (sources.Count < 2) continue; // a pattern is repetition — one source is a fact
			if (string.IsNullOrWhiteSpace(candidate.Description)) continue;

			var existing = candidate.Key is not null
				? existingPatterns.FirstOrDefault(p => p.Key == candidate.Key)
				: null;
			// Structural dedup guard (spec: autocapture-dedup): the miner may re-derive a
			// pattern it already consolidated and emit it as NEW (key omitted, or a paraphrase)
			// — this is exactly what minted the bp-… twins. Match the candidate to an existing
			// pattern by text/semantics before minting a second row; a hit becomes an UPDATE
			// (sources merged below) instead.
			if (existing is null)
			{
				var dupKey = await AutocaptureDedup.FindDuplicateKeyAsync(project,
					candidate.Description,
					existingPatterns.Select(p => (p.Key, p.Description)).ToList(), _llm, ct,
					_dedup.SemanticThreshold, embedCache);
				if (dupKey is not null) existing = existingPatterns.First(p => p.Key == dupKey);
			}
			var metadata = JsonSerializer.Serialize(new
			{
				sources = existing is null ? sources : sources.Concat(SourcesOf(existing)).Distinct().ToList(),
			});

			await _memory.UpsertAsync(project, QuarantineStore, [new MemoryEntryInput
			{
				Key = existing?.Key ?? "bp-" + Guid.NewGuid().ToString("N")[..12],
				Version = existing?.Version ?? 0,
				Type = "Feedback",
				Description = candidate.Description,
				Body = candidate.Body ?? existing?.Body ?? "",
				Tags = existing?.Tags ?? [SessionFactsJob.Tag, PatternTag],
				Metadata = metadata,
			}], [], ct);
			written++;
		}
		return written;
	}

	// Every sessionId an observation carries: its own plus the `seenIn` accumulation the
	// facts-judge builds when it MERGES a repeat into an existing entry — a single entry
	// can therefore prove repetition on its own.
	static List<string> ObservedIn(MemoryEntryView e)
	{
		if (string.IsNullOrWhiteSpace(e.Metadata)) return [];
		try
		{
			using var doc = JsonDocument.Parse(e.Metadata);
			var ids = new List<string>();
			if (doc.RootElement.TryGetProperty("sessionId", out var s) && !string.IsNullOrWhiteSpace(s.GetString()))
				ids.Add(s.GetString()!);
			if (doc.RootElement.TryGetProperty("seenIn", out var seen) && seen.ValueKind == JsonValueKind.Array)
				ids.AddRange(seen.EnumerateArray().Select(x => x.GetString()).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!));
			return ids.Distinct().ToList();
		}
		catch (JsonException) { return []; }
	}

	static List<string> SourcesOf(MemoryEntryView e)
	{
		if (string.IsNullOrWhiteSpace(e.Metadata)) return [];
		try
		{
			using var doc = JsonDocument.Parse(e.Metadata);
			if (!doc.RootElement.TryGetProperty("sources", out var s) || s.ValueKind != JsonValueKind.Array) return [];
			return s.EnumerateArray().Select(x => x.GetString()).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!).ToList();
		}
		catch (JsonException) { return []; }
	}

	internal static IReadOnlyList<PatternCandidate>? Parse(string raw)
	{
		try
		{
			return JsonSerializer.Deserialize<List<PatternCandidate>>(SessionFactsJob.StripFences(raw), JsonOpts);
		}
		catch (JsonException)
		{
			return null;
		}
	}

	static string Clip(string? s) => string.IsNullOrEmpty(s) ? "" : (s.Length > BodyClip ? s[..BodyClip] + "…" : s);

	static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

	internal sealed record PatternCandidate(string? Key, string? Description, string? Body, string[]? Sources);
}
