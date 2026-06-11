using System.Text;
using System.Text.Json;
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
//                verbatim source via session.get.
// Same enrichment-tick worker pattern as SessionDigestJob: per-session cursor (in the
// session store's cursor table), quiet period, chat-down → no-op pass that backfills on
// recovery (spec: write-never-blocks-on-enrich / durable-backfill).
public sealed class SessionFactsJob : IVectorizationJob
{
	public const string Store = "autocaptured";
	public const string Tag = "autocaptured";
	const string CuratedStore = "notes";

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
		Keep distinctive identifiers/slugs/error texts VERBATIM. Do NOT extract narration,
		progress reports, or anything derivable from code or git history. When nothing
		qualifies, output [].
		""";

	const string JudgePrompt =
		"""
		You deduplicate facts for an agent memory. Given a CANDIDATE fact and EXISTING
		entries (each with store, key, description, body), answer with STRICT JSON only:
		  {"action":"add|update|skip","key":"<existing key when action=update>","description":"<merged>","body":"<merged>"}
		Rules: if an existing entry already covers the candidate's knowledge → "skip".
		If an AUTOCAPTURED entry covers it but the candidate adds material new detail →
		"update" that entry with the merged content. Otherwise → "add". Never pick an
		entry from the notes store for update.
		""";

	readonly IScopedDbFactory<SessionsDb> _factory;
	readonly ISessionService _sessions;
	readonly IMemoryService _memory;
	readonly ILlmClient? _llm;
	readonly ILogger<SessionFactsJob>? _logger;
	readonly TimeSpan _quietPeriod;
	readonly TimeSpan _budget;

	// Round-robin start position across passes; passes run strictly sequentially.
	static int _rotation;

	public SessionFactsJob(IScopedDbFactory<SessionsDb> factory, ISessionService sessions,
		IMemoryService memory, ILlmClient? llm = null, ILogger<SessionFactsJob>? logger = null,
		TimeSpan? quietPeriod = null, TimeSpan? budget = null)
	{
		_factory = factory;
		_sessions = sessions;
		_memory = memory;
		_llm = llm;
		_logger = logger;
		_quietPeriod = quietPeriod ?? DefaultQuietPeriod;
		_budget = budget ?? DrainPacing.DefaultBudget;
	}

	static string CursorName(string sessionId) => "session-facts:" + sessionId;

	public async Task<int> DrainAllAsync(CancellationToken ct)
	{
		if (_llm is null) return 0;

		var captured = 0;
		var clock = new DrainClock(_budget);
		foreach (var project in DrainPacing.Rotate(ScopedDbFiles.ListNames(_factory.BaseDir, string.Empty), ref _rotation))
		{
			ct.ThrowIfCancellationRequested();
			if (clock.Exhausted) break;
			clock.StartProject();
			try
			{
				if (!await _llm.IsAvailableAsync(project, LlmCapability.Chat, ct)) continue;

				// GetDb runs the session migrations; the cursor tables live in the same
				// file and are ensured here (idempotent DDL) before any raw connection.
				var db = _factory.GetDb(project);
				SqliteIndexCursorStore.EnsureSchema(db);
				var cursors = new SqliteIndexCursorStore(() => _factory.NewConnection(project));

				var headers = await _sessions.ListAsync(project, ct);
				var cutoff = DateTime.UtcNow - _quietPeriod;
				foreach (var header in headers)
				{
					ct.ThrowIfCancellationRequested();
					if (clock.ProjectExhausted) break;
					if (header.Updated > cutoff) continue; // still hot — let the turn settle
					var cursor = await cursors.GetCursorAsync(CursorName(header.SessionId), ct);
					if (header.Version <= cursor) continue;
					try
					{
						captured += await DistillAsync(project, header.SessionId, cursor, cursors, clock, ct);
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
		SqliteIndexCursorStore cursors, DrainClock clock, CancellationToken ct)
	{
		var remaining = await _sessions.DeltaAsync(project, sessionId, cursor, ct);
		var captured = 0;
		// Bounded batches until the delta is EMPTY or the pass budget runs out; the cursor
		// advances per batch, so a partial drain resumes exactly where it parked.
		while (remaining.Count > 0 && !clock.ProjectExhausted)
		{
			ct.ThrowIfCancellationRequested();
			var (batch, lastVersion) = TakeBatch(remaining);
			var raw = await ChatAsync(project, ExtractPrompt, RenderTranscript(batch), ct);
			var candidates = ParseCandidates(raw);
			if (candidates is null)
			{
				// Unparseable output: advancing past the batch is deliberate — holding the
				// cursor would re-spend a chat call on the same bad input every tick.
				_logger?.LogWarning("facts extraction returned unparseable output for {Project}/{Session}; batch skipped",
					project, sessionId);
			}
			else
			{
				foreach (var candidate in candidates.Take(MaxCandidatesPerSession))
				{
					ct.ThrowIfCancellationRequested();
					if (string.IsNullOrWhiteSpace(candidate.Description) && string.IsNullOrWhiteSpace(candidate.Body))
						continue;
					if (await ApplyAsync(project, sessionId, batch[0].Version, lastVersion, candidate, ct))
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
		FactCandidate candidate, CancellationToken ct)
	{
		var neighbors = await CollectNeighborsAsync(project, candidate, ct);
		var verdict = neighbors.Count == 0
			? new JudgeVerdict("add", null, null, null)
			: await JudgeAsync(project, candidate, neighbors, ct);
		if (verdict is null || verdict.Action == "skip") return false;

		var metadata = JsonSerializer.Serialize(new
		{
			sessionId,
			messages = new[] { fromVersion, toVersion },
		});
		var type = candidate.Type is "User" or "Feedback" or "Project" or "Reference" ? candidate.Type : "Project";
		var tags = string.IsNullOrWhiteSpace(candidate.Tags) ? Tag : Tag + "," + candidate.Tags;

		if (verdict.Action == "update" && verdict.Key is not null)
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
				}], [], 0, ct);
				return true;
			}
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
		}], [], 0, ct);
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
		sb.AppendLine(JsonSerializer.Serialize(candidate));
		sb.AppendLine();
		sb.AppendLine("EXISTING:");
		sb.AppendLine(JsonSerializer.Serialize(neighbors));
		var raw = await ChatAsync(project, JudgePrompt, sb.ToString(), ct);
		try
		{
			var verdict = JsonSerializer.Deserialize<JudgeVerdict>(StripFences(raw), JsonOpts);
			if (verdict is null || verdict.Action is not ("add" or "update" or "skip"))
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
