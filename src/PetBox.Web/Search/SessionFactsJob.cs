using System.Text;
using System.Text.Json;
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
		Output STRICT JSON only — an array (possibly empty) of objects:
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
		EXISTING entries (each with store, key, description, body), answer with STRICT JSON only:
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
			var raw = await ChatAsync(project, ExtractPrompt, RenderTranscript(batch), ct);
			var candidates = ParseCandidates(raw);
			if (candidates is null)
			{
				// Unparseable output: advancing past the batch is deliberate — holding the
				// cursor would re-spend a chat call on the same bad input every tick. Dead-letter
				// the raw body so a lost extraction is findable in the self-log (log_query), not
				// a silent drop.
				_logger?.LogWarning("facts extraction returned unparseable output for {Project}/{Session}; batch skipped. raw={Raw}",
					project, sessionId, Clip(raw, 2000));
			}
			else
			{
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
		var verdict = await JudgeAsync(project, candidate, neighbors, ct);
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
				await _memory.UpsertAsync(project, Store, [], [new MemoryDelete(stale.Key, stale.Version)], ct);
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
				}], [], ct);
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
		}], [], ct);
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

	async Task<JudgeVerdict?> JudgeAsync(string project, FactCandidate candidate,
		IReadOnlyList<Neighbor> neighbors, CancellationToken ct)
	{
		var sb = new StringBuilder();
		sb.AppendLine("CANDIDATE:");
		sb.AppendLine(JsonSerializer.Serialize(candidate, PromptJson));
		sb.AppendLine();
		sb.AppendLine("EXISTING:");
		sb.AppendLine(JsonSerializer.Serialize(neighbors, PromptJson));
		var raw = await ChatAsync(project, JudgePrompt, sb.ToString(), ct);
		try
		{
			var verdict = JsonSerializer.Deserialize<JudgeVerdict>(StripFences(raw), JsonOpts);
			if (verdict is null || verdict.Action is not ("add" or "update" or "skip" or "drop" or "delete"))
				return Fallback();
			return verdict;
		}
		catch (JsonException)
		{
			return Fallback();
		}

		// A broken judge answer defaults to SKIP, not ADD: quarantine quality first — a
		// lost fact stays recoverable in the episodic tier, junk would multiply.
		JudgeVerdict? Fallback()
		{
			_logger?.LogWarning("facts judge returned unparseable verdict; candidate skipped");
			return null;
		}
	}

	async Task<string> ChatAsync(string project, string system, string user, CancellationToken ct)
	{
		var res = await _llm!.ChatAsync(project, new ChatRequest(
			[new ChatMessage("system", system), new ChatMessage("user", user)],
			Temperature: 0.1,
			MaxTokens: 2000), ct);
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

	internal static IReadOnlyList<FactCandidate>? ParseCandidates(string raw)
	{
		try
		{
			return JsonSerializer.Deserialize<List<FactCandidate>>(StripFences(raw), JsonOpts);
		}
		catch (JsonException)
		{
			return null;
		}
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
