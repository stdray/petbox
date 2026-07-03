using System.Text;
using System.Text.Json;
using PetBox.Core.Data;
using PetBox.LlmRouter.Contract;
using PetBox.Memory.Contract;
using PetBox.Sessions.Contract;
using PetBox.Sessions.Data;

namespace PetBox.Web.Search;

// Distills every session into a compact facts digest stored in the project's
// `session-digests` memory store — the always-on DISCOVERY tier of session search
// (spec: session-discovery-digest). The digest entry is also the cursor: its metadata
// records the last distilled message ordinal, so a pass touches only sessions whose
// header Version moved past it and feeds ISessionService.DeltaAsync just the increment.
// Distillation is asynchronous enrichment off the write path (spec:
// write-never-blocks-on-enrich); no chat capability → the pass is a no-op and the
// un-advanced cursor backfills when chat recovers (spec: durable-backfill).
public sealed class SessionDigestJob : IVectorizationJob
{
	public const string Store = "session-digests";
	public const string Tag = "session-digest";

	// An actively-pushed session grows every turn; distilling on each 60s tick would burn
	// a chat call per turn. Only sessions quiet for this long get distilled — the cursor
	// keeps the delta intact until then.
	public static readonly TimeSpan DefaultQuietPeriod = TimeSpan.FromMinutes(3);

	// A transcript can be megabytes; the chat context is not. Each message is capped and
	// the delta is distilled in sequential merge batches until it is EMPTY or the pass
	// budget runs out (DrainClock) — the cursor parks at the last distilled ordinal, so
	// a partial drain resumes next pass.
	internal const int MessageCharCap = 4000;
	internal const int BatchCharCap = 48_000;

	// A CONSERVATIVE pre-filter: a settled session whose new delta carries less than this much
	// meaningful (trimmed) text is almost certainly empty (a heartbeat, "ok", tool-call noise)
	// — the kind the model answers "no content to digest". We skip it BEFORE the LLM: no chat
	// spent, no noise digest minted, and (when a digest already exists) its cursor is advanced
	// past the trivial tail so it is not re-examined. Kept low on purpose — the reliable
	// content judge is the LLM, whose refusal the post-call guard catches; this floor only
	// spares the obvious empties (spec: session-search discovery hygiene).
	internal const int MinDistillChars = 20;

	// A merged answer this short (after trim) is empty/degenerate, not a digest — a real
	// digest is a title line plus fact lines. Kept low so it only catches near-empty output;
	// phrased refusals ("no content to digest") are caught by RefusalMarkers regardless of
	// length, and the MinDistillChars input floor is the primary empty-session defense. Same
	// test detects the junk digests older passes minted, so a pass self-cleans them.
	internal const int MinDigestChars = 12;

	// Stock "there is nothing here to digest" phrasings a model emits for an empty session.
	// Matched only inside a SHORT answer (a long legit digest may mention such words in a fact).
	static readonly string[] RefusalMarkers =
	{
		"no content to digest", "nothing to digest", "no meaningful content",
		"no substantial content", "no relevant content", "there is no content",
		"nothing to summarize", "no content to summarize",
	};

	const string SystemPrompt =
		"""
		You maintain the discovery digest of one AI-agent work session. You are given the
		current digest (possibly empty) and new transcript messages; return the complete
		updated digest, merging new facts into the existing ones (update, don't duplicate).
		Format: the FIRST line is one short sentence naming what the session is about, in
		the session's own language. Then up to 30 lines, each "- " + one concrete fact:
		goals, decisions and their why, outcomes, errors and fixes, and the entities
		touched (files, modules, branches, commands, tools, URLs, ids). Keep distinctive
		terms VERBATIM (identifiers, slugs, error texts) — the digest is matched by
		full-text search. No commentary, no markdown headers; output the digest text only.
		""";

	readonly IScopedDbFactory<SessionsDb> _factory;
	readonly ISessionService _sessions;
	readonly IMemoryService _memory;
	readonly ILlmClient? _llm;
	readonly ILogger<SessionDigestJob>? _logger;
	readonly TimeSpan _quietPeriod;
	readonly TimeSpan _budget;

	// Round-robin start position across passes; passes run strictly sequentially.
	static int _rotation;

	public SessionDigestJob(IScopedDbFactory<SessionsDb> factory, ISessionService sessions,
		IMemoryService memory, ILlmClient? llm = null, ILogger<SessionDigestJob>? logger = null,
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

	public async Task<int> DrainAllAsync(CancellationToken ct)
	{
		if (_llm is null) return 0;

		var distilled = 0;
		var clock = new DrainClock(_budget);
		// Session DBs are flat {baseDir}/{project}.db files (Scope.Project, no sub-name),
		// so the scope keys are the file names at the base dir itself.
		foreach (var project in DrainPacing.Rotate(ScopedDbFiles.ListNames(_factory.BaseDir, string.Empty), ref _rotation))
		{
			ct.ThrowIfCancellationRequested();
			if (clock.Exhausted) break;
			clock.StartProject();
			try
			{
				if (!await _llm.IsAvailableAsync(project, LlmCapability.Chat, ct)) continue;

				// The drain reads through ISessionService, whose store may open raw
				// connections; GetDb first runs the migrations so a file last opened
				// before a schema change is current (reference: NewConnection ≠ migrations).
				_factory.GetDb(project);

				// Self-heal: purge junk digests older passes minted (empty / "no content to
				// digest" / super-short) before distilling — a pass owns this machine store.
				await CleanupJunkDigestsAsync(project, ct);

				var headers = await _sessions.ListAsync(project, ct);
				if (headers.Count == 0) continue;

				var states = await LoadDigestStatesAsync(project, ct);
				var cutoff = DateTime.UtcNow - _quietPeriod;
				foreach (var header in headers)
				{
					ct.ThrowIfCancellationRequested();
					if (clock.ProjectExhausted) break;
					states.TryGetValue(header.SessionId, out var state);
					if (header.Version <= (state?.Cursor ?? 0)) continue;
					if (header.Updated > cutoff) continue; // still hot — let the turn settle
					try
					{
						if (await DistillAsync(project, header, state, clock, ct))
							distilled++;
					}
					catch (Exception ex) when (ex is not OperationCanceledException)
					{
						// One failing session must not block the rest; its cursor is
						// unchanged, so it retries next tick.
						_logger?.LogError(ex, "session digest failed for {Project}/{Session}; skipped",
							project, header.SessionId);
					}
				}
			}
			catch (Exception ex) when (ex is not OperationCanceledException)
			{
				_logger?.LogError(ex, "session digest pass failed for project {Project}; skipped", project);
			}
		}
		return distilled;
	}

	sealed record DigestState(long Cursor, long EntryVersion, string Body, string Description);

	async Task<Dictionary<string, DigestState>> LoadDigestStatesAsync(string project, CancellationToken ct)
	{
		var states = new Dictionary<string, DigestState>(StringComparer.Ordinal);
		if (!await _memory.StoreExistsAsync(project, Store, ct)) return states;
		foreach (var entry in await _memory.ListAsync(project, Store, type: null, ct))
		{
			var cursor = ReadCursor(entry.Metadata);
			if (cursor >= 0)
				states[entry.Key] = new DigestState(cursor, entry.Version, entry.Body, entry.Description);
		}
		return states;
	}

	static long ReadCursor(string metadata)
	{
		if (string.IsNullOrWhiteSpace(metadata)) return 0;
		try
		{
			using var doc = JsonDocument.Parse(metadata);
			return doc.RootElement.TryGetProperty("cursor", out var c) && c.TryGetInt64(out var v) ? v : 0;
		}
		catch (JsonException)
		{
			return 0;
		}
	}

	async Task<bool> DistillAsync(string project, SessionHeader header, DigestState? state,
		DrainClock clock, CancellationToken ct)
	{
		var cursor = state?.Cursor ?? 0;
		var delta = await _sessions.DeltaAsync(project, header.SessionId, cursor, ct);
		if (delta.Count == 0) return false;

		// #1 Skip before the LLM: an insubstantial delta (empty/heartbeat/tool-noise) is not
		// distilled at all — no chat call, no digest minted. If a digest already exists, its
		// cursor is advanced past this trivial tail so it is not re-examined; a brand-new
		// empty session anchors no cursor (nothing to write it on) and is re-skipped cheaply.
		if (!IsSubstantial(delta))
		{
			if (state is not null)
				await AdvanceCursorAsync(project, header, state, delta[^1].Version, ct);
			return false;
		}

		var digest = Compose(state?.Description, state?.Body);
		var lastVersion = cursor;
		foreach (var batch in Batches(delta))
		{
			if (clock.ProjectExhausted) break; // park at lastVersion — resumes next pass
			var updated = await ChatDistillAsync(project, digest, batch, ct);
			if (string.IsNullOrWhiteSpace(updated)) return false; // hold the cursor, retry next tick
			digest = updated.Trim();
			lastVersion = batch[^1].Version;
			clock.Unit();
		}
		if (lastVersion == cursor) return false; // budget hit before the first batch

		// #2 Guard after the LLM: a refusal / empty / super-short answer is NOT written as a
		// digest. The cursor still advances (over an existing entry) so the same trailing
		// noise is not re-distilled next tick; a session with no anchor entry mints nothing.
		if (IsRefusal(digest))
		{
			_logger?.LogWarning(
				"session digest skipped for {Project}/{Session}: model returned no usable digest ({Len} chars); cursor advanced, nothing written",
				project, header.SessionId, digest.Trim().Length);
			if (state is not null)
				await AdvanceCursorAsync(project, header, state, lastVersion, ct);
			return false;
		}

		var (description, body) = Split(digest);
		var metadata = BuildMetadata(header, lastVersion);
		var outcome = await _memory.UpsertAsync(project, Store, [new MemoryEntryInput
		{
			Key = header.SessionId,
			Version = state?.EntryVersion ?? 0,
			Type = "Reference",
			Description = description,
			Body = body,
			Tags = [Tag],
			Metadata = metadata,
		}], [], ct);
		if (outcome.Result.Conflicts.Count > 0)
		{
			// Someone edited the digest entry concurrently; the held cursor re-distills
			// against the fresh baseline next tick.
			_logger?.LogWarning("session digest write conflicted for {Project}/{Session}; retrying next tick",
				project, header.SessionId);
			return false;
		}
		return true;
	}

	// The stored entry splits the digest into description (first line) + body; the merge
	// prompt wants the digest whole again.
	static string Compose(string? description, string? body)
	{
		if (string.IsNullOrWhiteSpace(description)) return body ?? "";
		return string.IsNullOrWhiteSpace(body) ? description : description + "\n" + body;
	}

	static (string Description, string Body) Split(string digest)
	{
		var nl = digest.IndexOf('\n');
		if (nl < 0) return (digest, "");
		return (digest[..nl].Trim(), digest[(nl + 1)..].Trim());
	}

	// A delta is worth a chat call only if its messages carry at least MinDistillChars of
	// meaningful (trimmed) text combined. Cheap short-circuit as soon as the floor is met.
	static bool IsSubstantial(IReadOnlyList<SessionMessage> delta)
	{
		var chars = 0;
		foreach (var message in delta)
		{
			chars += (message.Content ?? "").Trim().Length;
			if (chars >= MinDistillChars) return true;
		}
		return false;
	}

	// A model answer that is empty, super-short, or a stock "nothing to digest" refusal is
	// not a digest. Markers are matched only inside a SHORT answer so a long legit digest
	// mentioning such words in a fact is not misjudged.
	static bool IsRefusal(string digest)
	{
		var text = (digest ?? "").Trim();
		if (text.Length < MinDigestChars) return true;
		if (text.Length <= 200)
		{
			var lower = text.ToLowerInvariant();
			foreach (var marker in RefusalMarkers)
				if (lower.Contains(marker)) return true;
		}
		return false;
	}

	static string BuildMetadata(SessionHeader header, long cursor) =>
		JsonSerializer.Serialize(new { sessionId = header.SessionId, agent = header.Agent, cursor });

	// Move an EXISTING digest's cursor forward without re-distilling or changing its body —
	// used when a delta is insubstantial or the model refused, so the trailing noise is not
	// reconsidered next tick. A conflict just means someone rewrote the entry; the held
	// cursor re-converges next pass.
	async Task AdvanceCursorAsync(string project, SessionHeader header, DigestState state, long cursor, CancellationToken ct)
	{
		if (cursor <= state.Cursor) return; // nothing to advance past
		var outcome = await _memory.UpsertAsync(project, Store, [new MemoryEntryInput
		{
			Key = header.SessionId,
			Version = state.EntryVersion,
			Type = "Reference",
			Description = state.Description,
			Body = state.Body,
			Tags = [Tag],
			Metadata = BuildMetadata(header, cursor),
		}], [], ct);
		if (outcome.Result.Conflicts.Count > 0)
			_logger?.LogWarning("session digest cursor-advance conflicted for {Project}/{Session}; retrying next tick",
				project, header.SessionId);
	}

	// Self-heal the machine store: soft-delete any active digest whose text is empty, a stock
	// refusal, or super-short — the shape older passes minted for empty sessions and the
	// semantic leg of session_search then surfaced as weak-match noise. Idempotent: a
	// soft-deleted entry drops out of the active listing, so it is not re-deleted next pass.
	async Task CleanupJunkDigestsAsync(string project, CancellationToken ct)
	{
		if (!await _memory.StoreExistsAsync(project, Store, ct)) return;
		var entries = await _memory.ListAsync(project, Store, type: null, ct);
		var deletes = entries
			.Where(e => IsRefusal(Compose(e.Description, e.Body)))
			.Select(e => new MemoryDelete(e.Key, e.Version))
			.ToList();
		if (deletes.Count == 0) return;
		await _memory.UpsertAsync(project, Store, [], deletes, ct);
		_logger?.LogWarning("session digest cleanup: removed {Count} empty/no-content digest(s) in {Project}",
			deletes.Count, project);
	}

	static IEnumerable<IReadOnlyList<SessionMessage>> Batches(IReadOnlyList<SessionMessage> delta)
	{
		var batch = new List<SessionMessage>();
		var chars = 0;
		foreach (var message in delta)
		{
			var len = Math.Min(message.Content.Length, MessageCharCap);
			if (batch.Count > 0 && chars + len > BatchCharCap)
			{
				yield return batch;
				batch = [];
				chars = 0;
			}
			batch.Add(message);
			chars += len;
		}
		if (batch.Count > 0)
			yield return batch;
	}

	async Task<string> ChatDistillAsync(string project, string currentDigest,
		IReadOnlyList<SessionMessage> batch, CancellationToken ct)
	{
		var sb = new StringBuilder();
		sb.AppendLine("CURRENT DIGEST:");
		sb.AppendLine(string.IsNullOrWhiteSpace(currentDigest) ? "(empty)" : currentDigest);
		sb.AppendLine();
		sb.AppendLine("NEW MESSAGES:");
		foreach (var message in batch)
		{
			var content = message.Content.Length > MessageCharCap
				? message.Content[..MessageCharCap] + " …[truncated]"
				: message.Content;
			sb.AppendLine($"[{message.Role}]");
			sb.AppendLine(content);
		}

		var result = await _llm!.ChatAsync(project, new ChatRequest(
			[new ChatMessage("system", SystemPrompt), new ChatMessage("user", sb.ToString())],
			Temperature: 0.2,
			MaxTokens: 2000), ct);
		return result.Text;
	}
}
