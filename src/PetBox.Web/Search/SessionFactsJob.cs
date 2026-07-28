using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using PetBox.Core.Data;
using PetBox.Core.Search;
using PetBox.LlmRouter.Contract;
using PetBox.Memory.Contract;
using PetBox.Sessions.Contract;
using PetBox.Sessions.Data;

namespace PetBox.Web.Search;

// Autocapture: distills DURABLE typed facts out of settled sessions into the project's
// `autocaptured` memory store (spec: memory-autocapture) — the machine counterpart of the
// agent's explicit memory_remember. Three promises shape the flow:
//   dedup      — every candidate is checked against existing memory (hybrid search gives
//                the top neighbors, an LLM judge decides add/update/skip over THEM only —
//                never over the whole store) so repeats don't pile up;
//   quarantine — machine writes land ONLY in `autocaptured` (cascading recall still
//                surfaces them); curated stores are never modified;
//   provenance — every fact carries {sessionId, message range}: the bridge back to the
//                verbatim source via session_get.
// Same enrichment-tick worker pattern as SessionDigestJob: per-session cursor (in the
// session store's cursor table), quiet period, chat-down → no-op pass that backfills on
// recovery (spec: write-never-blocks-on-enrich / durable-backfill).
public sealed class SessionFactsJob : IBackgroundIndexJob
{
	public const string Store = "autocaptured";
	public const string Tag = "autocaptured";
	const string CuratedStore = "notes";

	// The candidate/neighbour facts are serialized INTO the LLM judge prompt. PetBox content is
	// largely Cyrillic; the default encoder would escape it to \uXXXX and hand the model gibberish.
	// The shared relaxed encoder passes Cyrillic through so the judge reads the actual fact text.
	static readonly JsonSerializerOptions PromptJson = new() { Encoder = PetBox.Core.Json.PetBoxJsonEncoder.Relaxed };

	public static readonly TimeSpan DefaultQuietPeriod = TimeSpan.FromMinutes(3);

	internal const int MaxCandidatesPerSession = 8;
	internal const int MessageCharCap = 4000;
	internal const int BatchCharCap = 48_000;
	internal const int NeighborK = 3;

	const string ExtractPrompt =
		"""
		You extract DURABLE facts from a fragment of an AI-agent work-session transcript.
		Output STRICT JSON only. Your response MUST START with the array's opening `[` as its
		very first character — no preamble, no explanation, no <reasoning>/<thinking> block, no
		markdown fence. Stop the instant the closing `]` is written; nothing follows it, not even
		a newline of commentary. The array (possibly empty) holds objects:
		  {"type":"User|Feedback|Project|Reference","description":"<one line>","body":"<2-5 lines of detail>","tags":"<csv, optional>"}
		Qualifying facts ONLY: a decision plus its why; a fixed bug's root cause; a discovered
		convention or gotcha; a stated user preference; a durable project fact or constraint;
		a recurring PROCEDURE — an action sequence the session repeats or prescribes ("before
		merging, re-check git status"). For a procedure: type=Feedback, phrase the description
		as «когда X — делай Y», and include "behavior:pattern" in tags.

		DURABILITY TEST — before emitting anything, ask: "would a FUTURE agent, a month from
		now, need this — and is it NOT already derivable from the code, git history, or the
		task/spec boards?" If it is derivable there, it is NOT a fact. Keep distinctive
		identifiers/slugs/error texts VERBATIM.

		Do NOT extract (these are the common false positives):
		  • narration of work done — "реализовано/задеплоено/добавлен пакет X/wave N готова",
		    "смок прошёл", ci numbers, commit hashes, "переименовал файл", executed plans;
		  • progress/status reports and per-session bookkeeping (what was tried this session,
		    which tests ran, transient TODOs);
		  • anything a `git log`, the code, or a board query would already answer.
		A record of THIS session's activity is not durable knowledge — the session transcript
		already holds it.

		Emit at most 1–3 candidates for this fragment; most fragments yield zero. WHEN IN
		DOUBT, output []. When nothing qualifies, output [].
		""";

	const string JudgePrompt =
		"""
		You are the write-gate for an agent's long-term memory. Given a CANDIDATE fact and
		EXISTING entries (each with store, key, description, body), answer with STRICT JSON only.
		Your response MUST START with the object's opening `{` as its very first character — no
		preamble, no explanation, no <reasoning>/<thinking> block, no markdown fence. Stop the
		instant the closing `}` is written; nothing follows it, not even a newline of commentary:
		  {"action":"add|update|skip|drop|delete","key":"<existing key for update/delete>","description":"<merged>","body":"<merged>"}
		Decide TWO things in this one answer:
		1. Is it worth storing AT ALL? If the candidate is narration of work done, a
		   progress/status report, session-specific bookkeeping, or anything derivable from
		   code, git history, or the task/spec boards → "drop". When unsure whether it is
		   durable knowledge → "drop".
		2. If worth keeping, deduplicate against EXISTING:
		   • an existing entry already covers the candidate's knowledge → "skip";
		   • an AUTOCAPTURED entry covers it but the candidate adds material new detail →
		     "update" that entry (key + merged description/body);
		   • an AUTOCAPTURED entry is now STALE or contradicted by the candidate → "delete"
		     (key = that entry) to invalidate it;
		   • otherwise → "add".
		Never pick an entry from the notes store for update or delete — those are human-curated
		and off-limits to the machine.
		""";

	// The corrective retry turn (ChatParseWithRetryAsync): replayed as a user message after the
	// model's own unparseable answer, so the correction reads as feedback on THAT answer.
	const string ExtractCorrection = "верни ТОЛЬКО JSON-массив, ничего кроме";
	const string JudgeCorrection = "верни ТОЛЬКО JSON-объект, ничего кроме";

	readonly IScopedDbFactory<SessionsDb> _factory;
	readonly IProjectCatalog _catalog;
	readonly ISessionService _sessions;
	readonly IMemoryService _memory;
	readonly ILlmClient? _llm;
	readonly ILogger<SessionFactsJob>? _logger;
	readonly TimeSpan _quietPeriod;
	readonly TimeSpan _budget;
	readonly AutocaptureDedupOptions _dedup;

	// Round-robin start position across passes; passes run strictly sequentially.
	static int _rotation;

	public SessionFactsJob(IScopedDbFactory<SessionsDb> factory, IProjectCatalog catalog,
		ISessionService sessions, IMemoryService memory, ILlmClient? llm = null,
		ILogger<SessionFactsJob>? logger = null, TimeSpan? quietPeriod = null, TimeSpan? budget = null,
		IOptions<AutocaptureDedupOptions>? dedup = null)
	{
		_factory = factory;
		_catalog = catalog;
		_sessions = sessions;
		_memory = memory;
		_llm = llm;
		_logger = logger;
		_quietPeriod = quietPeriod ?? DefaultQuietPeriod;
		_budget = budget ?? DrainPacing.DefaultBudget;
		_dedup = dedup?.Value ?? new AutocaptureDedupOptions();
	}

	static string CursorName(string sessionId) => "session-facts:" + sessionId;

	public async Task<int> DrainAllAsync(CancellationToken ct)
	{
		if (_llm is null) return 0;

		var captured = 0;
		var clock = new DrainClock(_budget);
		// Catalog, not file scan (spec: catalog-is-source-of-truth) — see SessionDigestJob for the
		// full argument. Sessions have no per-entity catalog, so the PROJECT catalog is the answer:
		// a project with no sessions file yet is now visible (its file is created + migrated behind
		// the LLM gate below), and a deleted project's ghost file is not autocaptured into memory
		// that the orphan sweeper has already reclaimed.
		foreach (var project in DrainPacing.Rotate(await _catalog.ListProjectKeysAsync(ct), ref _rotation))
		{
			ct.ThrowIfCancellationRequested();
			if (clock.Exhausted) break;
			clock.StartProject();
			try
			{
				if (!await _llm.IsAvailableAsync(project, LlmCapability.Chat, ct)) continue;

				// No DDL here: NewEnsuredConnection runs the sessions-tier migrations
				// (SessionsSchema.Ensure), and M007 owns search_cursor/search_deadletter.
				var cursors = new SqliteIndexCursorStore(() => _factory.NewEnsuredConnection(project));

				var headers = await _sessions.ListAsync(project, ct);
				var cutoff = DateTime.UtcNow - _quietPeriod;
				// One embedding memo per project per pass: the dedup guard compares every
				// candidate against the whole quarantined store, so the store's texts are
				// embedded once here instead of once per candidate.
				var embedCache = new EmbeddingCache();
				foreach (var header in headers)
				{
					ct.ThrowIfCancellationRequested();
					if (clock.ProjectExhausted) break;
					if (header.Updated > cutoff) continue; // still hot — let the turn settle
					var cursor = await cursors.GetCursorAsync(CursorName(header.SessionId), ct);
					if (header.Version <= cursor) continue;
					try
					{
						captured += await DistillAsync(project, header.SessionId, cursor, cursors, clock, embedCache, ct);
					}
					catch (Exception ex) when (ex is not OperationCanceledException)
					{
						_logger?.LogError(ex, "facts distillation failed for {Project}/{Session}; skipped",
							project, header.SessionId);
					}
				}
			}
			catch (Exception ex) when (ex is not OperationCanceledException)
			{
				_logger?.LogError(ex, "facts distillation pass failed for project {Project}; skipped", project);
			}
		}
		return captured;
	}

	async Task<int> DistillAsync(string project, string sessionId, long cursor,
		SqliteIndexCursorStore cursors, DrainClock clock, EmbeddingCache embedCache, CancellationToken ct)
	{
		var remaining = await _sessions.DeltaAsync(project, sessionId, cursor, ct);
		var captured = 0;
		// Honest per-session cap: the counter spans EVERY batch of this DistillAsync pass, so a
		// long session can't mint MaxCandidatesPerSession facts per batch (it used to). Once the
		// cap is hit we stop spending chat calls; the cursor parks and a later pass resumes.
		var taken = 0;
		// Bounded batches until the delta is EMPTY, the pass budget runs out, or the per-session
		// cap is reached; the cursor advances per batch, so a partial drain resumes where it parked.
		while (remaining.Count > 0 && !clock.ProjectExhausted && taken < MaxCandidatesPerSession)
		{
			ct.ThrowIfCancellationRequested();
			var (batch, lastVersion) = TakeBatch(remaining);
			var (candidates, recovered, raw) = await ChatParseWithRetryAsync<List<FactCandidate>>(
				project, ExtractPrompt, RenderTranscript(batch), ExtractCorrection, ct);
			if (candidates is null)
			{
				// Genuinely unparseable after a corrective retry: advancing past the batch is
				// deliberate — holding the cursor would re-spend chat calls on the same bad input
				// every tick. Dead-letter the raw body so a lost extraction is findable in the
				// self-log (log_query), not a silent drop.
				_logger?.LogWarning("facts extraction returned unparseable output for {Project}/{Session}; batch skipped after retry. raw={Raw}",
					project, sessionId, Clip(raw, 2000));
			}
			else
			{
				// The dominant "loss" turned out to be the model answering correctly ([]) and
				// then padding with prose/reasoning — recovered here, NOT a warning-worthy loss.
				// Kept at Information (not Debug) and phrased distinctly so log_query can tell
				// the two apart and measure how often the tolerant parser is doing real work.
				if (recovered && _logger is not null && _logger.IsEnabled(LogLevel.Information))
					_logger.LogInformation("facts extraction RECOVERED candidates from non-strict output for {Project}/{Session}; count={Count}",
						project, sessionId, candidates.Count);
				foreach (var candidate in candidates)
				{
					ct.ThrowIfCancellationRequested();
					if (taken >= MaxCandidatesPerSession) break;
					if (string.IsNullOrWhiteSpace(candidate.Description) && string.IsNullOrWhiteSpace(candidate.Body))
						continue;
					taken++;
					if (await ApplyAsync(project, sessionId, batch[0].Version, lastVersion, candidate, embedCache, ct))
						captured++;
				}
			}
			await cursors.SetCursorAsync(CursorName(sessionId), lastVersion, ct);
			clock.Unit();
			remaining = remaining.Where(m => m.Version > lastVersion).ToList();
		}
		return captured;
	}

	async Task<bool> ApplyAsync(string project, string sessionId, long fromVersion, long toVersion,
		FactCandidate candidate, EmbeddingCache embedCache, CancellationToken ct)
	{
		// The judge is ALWAYS consulted now — it is the worth-gate as well as the dedup gate,
		// so even a candidate with no retrieved neighbors must clear "is this worth storing?".
		var neighbors = await CollectNeighborsAsync(project, candidate, ct);
		var verdict = await JudgeAsync(project, sessionId, candidate, neighbors, ct);
		if (verdict is null || verdict.Action == "skip") return false;

		// Worth-gate: the judge ruled this not durable (narration / session bookkeeping /
		// derivable from code or boards). Dead-letter the body so the drop is findable.
		if (verdict.Action == "drop")
		{
			_logger?.LogWarning("facts judge DROPPED a candidate as not worth storing for {Project}/{Session}. description={Description} body={Body}",
				project, sessionId, Clip(candidate.Description ?? "", 500), Clip(candidate.Body ?? "", 1000));
			return false;
		}

		// Invalidation: the candidate makes an existing entry stale. Quarantine invariant —
		// the machine may delete ONLY from its own store; a key that resolves anywhere else
		// (or nowhere) is ignored with a warning, never acted on.
		if (verdict.Action == "delete")
		{
			if (verdict.Key is not null && await _memory.StoreExistsAsync(project, Store, ct)
				&& await _memory.GetAsync(project, Store, verdict.Key, ct) is { } stale)
			{
				await _memory.UpsertAsync(project, Store, [], [new MemoryDelete(stale.Key, stale.Version)], ct: ct);
				return true;
			}
			_logger?.LogWarning("facts judge DELETE pointed at a non-quarantine or missing key for {Project}; ignored. key={Key}",
				project, verdict.Key);
			return false;
		}

		var metadata = JsonSerializer.Serialize(new
		{
			sessionId,
			messages = new[] { fromVersion, toVersion },
		});
		var type = candidate.Type is "User" or "Feedback" or "Project" or "Reference" ? candidate.Type : "Project";
		// The LLM contract emits tags as CSV; the memory surface speaks arrays — split here.
		string[] tags = string.IsNullOrWhiteSpace(candidate.Tags)
			? [Tag]
			: [Tag, .. candidate.Tags!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

		if (verdict.Action == "update" && verdict.Key is not null && await _memory.StoreExistsAsync(project, Store, ct))
		{
			// Quarantine invariant: the machine may merge ONLY into its own store. A judge
			// pointing anywhere else degrades to add — knowledge is kept, curation isn't touched.
			var existing = await _memory.GetAsync(project, Store, verdict.Key, ct);
			if (existing is not null)
			{
				await _memory.UpsertAsync(project, Store, [new MemoryEntryInput
				{
					Key = existing.Key,
					Version = existing.Version,
					Type = existing.Type,
					Description = verdict.Description ?? existing.Description,
					Body = verdict.Body ?? existing.Body,
					Tags = existing.Tags,
					// A merge must ACCUMULATE provenance, not replace it: `seenIn` keeps
					// every prior sessionId — the repetition evidence pattern mining feeds on.
					Metadata = JsonSerializer.Serialize(new
					{
						sessionId,
						messages = new[] { fromVersion, toVersion },
						seenIn = SeenIn(existing.Metadata, sessionId),
					}),
				}], [], ct: ct);
				return true;
			}
		}

		// Deterministic dedup safety net BEHIND the judge (spec: autocapture-dedup): a
		// hallucinated "add" or a repeat the neighbor search never surfaced (lexical AND-
		// narrowing, a paraphrase ranked below K) still lands here. Compare the candidate to
		// the WHOLE quarantined store by text/semantics before minting a new row — a match =
		// skip, so repetition can't pile up even when the judge lets it through.
		if (await _memory.StoreExistsAsync(project, Store, ct))
		{
			var existing = await _memory.ListAsync(project, Store, type: null, ct);
			var dupKey = await AutocaptureDedup.FindDuplicateKeyAsync(project,
				string.IsNullOrWhiteSpace(candidate.Description) ? candidate.Body ?? "" : candidate.Description,
				existing.Select(e => (e.Key, Text: string.IsNullOrWhiteSpace(e.Description) ? e.Body : e.Description)).ToList(),
				_llm, ct, _dedup.SemanticThreshold, embedCache);
			if (dupKey is not null) return false;
		}

		await _memory.UpsertAsync(project, Store, [new MemoryEntryInput
		{
			Key = "ac-" + Guid.NewGuid().ToString("N")[..12],
			Version = 0,
			Type = type,
			Description = candidate.Description ?? "",
			Body = candidate.Body ?? "",
			Tags = tags,
			Metadata = metadata,
		}], [], ct: ct);
		return true;
	}

	async Task<IReadOnlyList<Neighbor>> CollectNeighborsAsync(string project, FactCandidate candidate, CancellationToken ct)
	{
		// The lexical leg ANDs query tokens, so the neighbor probe must be the candidate's
		// topical core (the one-line description), not description+body — every extra body
		// token would narrow the match and hide a true duplicate.
		var query = string.IsNullOrWhiteSpace(candidate.Description) ? (candidate.Body ?? "") : candidate.Description;
		query = query.Trim();
		if (query.Length > 200) query = query[..200];
		var neighbors = new List<Neighbor>();
		foreach (var store in new[] { CuratedStore, Store })
		{
			if (!await _memory.StoreExistsAsync(project, store, ct)) continue;
			var res = await _memory.SearchAsync(project, store, query, type: null, ct: ct);
			neighbors.AddRange(res.Hits.Take(NeighborK).Select(h => new Neighbor(store, h.Key, h.Description, Clip(h.Body, 400))));
		}
		return neighbors;
	}

	async Task<JudgeVerdict?> JudgeAsync(string project, string sessionId, FactCandidate candidate,
		IReadOnlyList<Neighbor> neighbors, CancellationToken ct)
	{
		var sb = new StringBuilder();
		sb.AppendLine("CANDIDATE:");
		sb.AppendLine(JsonSerializer.Serialize(candidate, PromptJson));
		sb.AppendLine();
		sb.AppendLine("EXISTING:");
		sb.AppendLine(JsonSerializer.Serialize(neighbors, PromptJson));
		var (verdict, recovered, raw) = await ChatParseWithRetryAsync<JudgeVerdict>(
			project, JudgePrompt, sb.ToString(), JudgeCorrection, ct);
		if (verdict is null || verdict.Action is not ("add" or "update" or "skip" or "drop" or "delete"))
		{
			// A broken judge answer defaults to SKIP, not ADD: quarantine quality first — a
			// lost fact stays recoverable in the episodic tier, junk would multiply. Same
			// transport disease as extraction — same dead-letter treatment, findable via log_query.
			_logger?.LogWarning("facts judge returned unparseable verdict for {Project}/{Session} after retry; candidate skipped. raw={Raw}",
				project, sessionId, Clip(raw, 1000));
			return null;
		}
		if (recovered && _logger is not null && _logger.IsEnabled(LogLevel.Information))
			_logger.LogInformation("facts judge RECOVERED verdict from non-strict output for {Project}/{Session}; action={Action}",
				project, sessionId, verdict.Action);
		return verdict;
	}

	// One corrective retry on genuine parse failure — not a chat-burn loop. `T` fails to
	// materialize only when EVERY tolerant-parse stage in ResilientJson misses; then the bad
	// assistant answer plus a blunt correction is replayed as the whole retry turn. Success on
	// either attempt is reported as `Recovered` whenever the FIRST attempt needed anything past
	// strict parsing (including the retry itself) — callers use that to log recovery distinctly
	// from a genuine, still-warning-worthy failure.
	async Task<(T? Value, bool Recovered, string LastRaw)> ChatParseWithRetryAsync<T>(
		string project, string system, string user, string correction, CancellationToken ct) where T : class
	{
		var raw = await ChatAsync(project, system, user, ct);
		var (value, recovered) = ResilientJson.TryParse<T>(raw, JsonOpts);
		if (value is not null) return (value, recovered, raw);

		var retryRaw = await ChatAsync(project, system, user, ct,
			extra: [new ChatMessage("assistant", raw), new ChatMessage("user", correction)]);
		var (retryValue, _) = ResilientJson.TryParse<T>(retryRaw, JsonOpts);
		return retryValue is not null ? (retryValue, true, retryRaw) : (null, false, retryRaw);
	}

	// Preamble/reasoning ahead of the JSON (the retry's corrective turn, or a model that ignores
	// the "JSON first" instruction) eats into the token budget that used to be spent entirely on
	// the JSON payload; 2000 left too little headroom and risked truncating the JSON itself
	// before its closing bracket. Raised, not removed — the destination store's caps
	// (MaxCandidatesPerSession, per-field lengths) still bound what a well-formed answer can cost.
	const int ChatMaxTokens = 3000;

	async Task<string> ChatAsync(string project, string system, string user, CancellationToken ct,
		IReadOnlyList<ChatMessage>? extra = null)
	{
		List<ChatMessage> messages = [new("system", system), new("user", user)];
		if (extra is { Count: > 0 }) messages.AddRange(extra);
		var res = await _llm!.ChatAsync(project, new ChatRequest(
			messages,
			Temperature: 0.1,
			MaxTokens: ChatMaxTokens), ct);
		return res.Text;
	}

	static (IReadOnlyList<SessionMessage> Batch, long LastVersion) TakeBatch(IReadOnlyList<SessionMessage> delta)
	{
		var batch = new List<SessionMessage>();
		var chars = 0;
		foreach (var message in delta)
		{
			var len = Math.Min(message.Content.Length, MessageCharCap);
			if (batch.Count > 0 && chars + len > BatchCharCap) break;
			batch.Add(message);
			chars += len;
		}
		return (batch, batch[^1].Version);
	}

	static string RenderTranscript(IReadOnlyList<SessionMessage> batch)
	{
		var sb = new StringBuilder();
		foreach (var message in batch)
		{
			var content = message.Content.Length > MessageCharCap
				? message.Content[..MessageCharCap] + " …[truncated]"
				: message.Content;
			sb.AppendLine($"[{message.Role}]");
			sb.AppendLine(content);
		}
		return sb.ToString();
	}

	internal static string StripFences(string raw)
	{
		var s = raw.Trim();
		if (!s.StartsWith("```", StringComparison.Ordinal)) return s;
		var open = s.IndexOf('\n');
		var close = s.LastIndexOf("```", StringComparison.Ordinal);
		return open >= 0 && close > open ? s[(open + 1)..close].Trim() : s;
	}

	static string Clip(string s, int max) => s.Length > max ? s[..max] + "…" : s;

	// The union of every sessionId this entry was observed in: the current one plus the
	// prior sessionId and prior seenIn — a judge-merge accumulates provenance, never drops it.
	static List<string> SeenIn(string priorMetadata, string sessionId)
	{
		var ids = new List<string> { sessionId };
		if (!string.IsNullOrWhiteSpace(priorMetadata))
		{
			try
			{
				using var doc = JsonDocument.Parse(priorMetadata);
				if (doc.RootElement.TryGetProperty("sessionId", out var s) && !string.IsNullOrWhiteSpace(s.GetString()))
					ids.Add(s.GetString()!);
				if (doc.RootElement.TryGetProperty("seenIn", out var seen) && seen.ValueKind == JsonValueKind.Array)
					ids.AddRange(seen.EnumerateArray().Select(x => x.GetString()).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!));
			}
			catch (JsonException) { /* prior metadata unparseable — start fresh */ }
		}
		return ids.Distinct().ToList();
	}

	static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

	internal sealed record FactCandidate(string? Type, string? Description, string? Body, string? Tags);

	sealed record Neighbor(string Store, string Key, string Description, string Body);

	sealed record JudgeVerdict(string Action, string? Key, string? Description, string? Body);
}

// Tolerant JSON extraction shared by SessionFactsJob's extraction AND judge parsing — both
// hit the SAME transport disease: an LLM that answers correctly but pads the answer with
// prose/reasoning around the JSON (facts-extraction-unparseable-batches). Escalating stages,
// cheapest/most-precise first; the FIRST stage that yields a value wins:
//   a. strict  — the whole (fence-stripped) text must be exactly one JSON value; this is the
//                pre-existing behavior and stays fastest for the common well-formed answer.
//   b. reasoning-stripped — cut <reasoning>/<thinking>...</...> pairs, and an UNCLOSED opening
//                tag truncates the text from that tag to the end; then retry (a).
//   c. fenced-anywhere — a ```json (preferred) or plain ``` block located ANYWHERE in the text
//                (not just a leading fence, unlike StripFences), tried in order of appearance.
//   d. first-value scan — walk `[`/`{` start positions left to right and, at each, hand the
//                REST of the text to a Utf8JsonReader-based Deserialize(ref reader, ...): that
//                overload reads exactly one JSON value and does not choke on trailing content,
//                which is exactly the dominant "[]\n\n**Explanation:**..." shape. A stray
//                bracket from surrounding prose almost never parses into the target T's shape,
//                so it is silently skipped in favor of the next candidate position.
internal static class ResilientJson
{
	public static (T? Value, bool Recovered) TryParse<T>(string raw, JsonSerializerOptions opts) where T : class
	{
		var trimmed = (raw ?? "").Trim();
		if (trimmed.Length == 0) return (null, false);

		if (TryStrict<T>(SessionFactsJob.StripFences(trimmed), opts, out var strict))
			return (strict, false);

		var cleaned = StripReasoningBlocks(trimmed).Trim();
		if (cleaned.Length > 0 && cleaned != trimmed && TryStrict<T>(SessionFactsJob.StripFences(cleaned), opts, out var afterReasoning))
			return (afterReasoning, true);

		foreach (var fenced in FindFencedBlocksAnywhere(cleaned))
			if (TryStrict<T>(fenced, opts, out var fromFence))
				return (fromFence, true);

		if (TryReadFirstJsonValue<T>(cleaned, opts, out var scanned))
			return (scanned, true);

		return (null, false);
	}

	static bool TryStrict<T>(string s, JsonSerializerOptions opts, out T? value) where T : class
	{
		value = null;
		if (string.IsNullOrWhiteSpace(s)) return false;
		try
		{
			value = JsonSerializer.Deserialize<T>(s, opts);
			return value is not null;
		}
		catch (JsonException) { return false; }
	}

	static bool TryReadFirstJsonValue<T>(string text, JsonSerializerOptions opts, out T? value) where T : class
	{
		value = null;
		for (var i = 0; i < text.Length; i++)
		{
			if (text[i] != '[' && text[i] != '{') continue;
			var slice = text[i..].TrimEnd();
			if (slice.Length == 0) continue;
			try
			{
				var bytes = Encoding.UTF8.GetBytes(slice);
				var reader = new Utf8JsonReader(bytes, isFinalBlock: true, state: default);
				// Deserialize(ref reader, ...) reads exactly ONE value and is silent about
				// whatever bytes remain after it — unlike Deserialize(string, ...), which is
				// the strict-stage behavior above and is exactly what trips on trailing prose.
				var parsed = JsonSerializer.Deserialize<T>(ref reader, opts);
				if (parsed is not null)
				{
					value = parsed;
					return true;
				}
			}
			catch (JsonException) { }
			catch (NotSupportedException) { }
		}
		return false;
	}

	static readonly Regex ReasoningPair =
		new(@"<(reasoning|thinking)>.*?</\1>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
	static readonly Regex ReasoningOpen =
		new(@"<(reasoning|thinking)>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

	internal static string StripReasoningBlocks(string raw)
	{
		var s = ReasoningPair.Replace(raw, " ");
		var open = ReasoningOpen.Match(s);
		return open.Success ? s[..open.Index] : s;
	}

	static readonly Regex FencedJson =
		new(@"```json\s*\r?\n?(.*?)```", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
	static readonly Regex FencedAny =
		new(@"```\w*\s*\r?\n?(.*?)```", RegexOptions.Singleline | RegexOptions.Compiled);

	static IEnumerable<string> FindFencedBlocksAnywhere(string text)
	{
		foreach (Match m in FencedJson.Matches(text)) yield return m.Groups[1].Value.Trim();
		foreach (Match m in FencedAny.Matches(text)) yield return m.Groups[1].Value.Trim();
	}
}
