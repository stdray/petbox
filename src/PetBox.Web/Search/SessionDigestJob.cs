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
	// the delta is distilled in sequential merge batches; a pass folds at most
	// MaxBatchesPerSession batches into the digest and parks the cursor at the last
	// distilled ordinal, so an oversized backlog drains across ticks instead of stalling.
	internal const int MessageCharCap = 4000;
	internal const int BatchCharCap = 48_000;
	internal const int MaxBatchesPerSession = 6;

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

	public SessionDigestJob(IScopedDbFactory<SessionsDb> factory, ISessionService sessions,
		IMemoryService memory, ILlmClient? llm = null, ILogger<SessionDigestJob>? logger = null,
		TimeSpan? quietPeriod = null)
	{
		_factory = factory;
		_sessions = sessions;
		_memory = memory;
		_llm = llm;
		_logger = logger;
		_quietPeriod = quietPeriod ?? DefaultQuietPeriod;
	}

	public async Task<int> DrainAllAsync(CancellationToken ct)
	{
		if (_llm is null) return 0;

		var distilled = 0;
		// Session DBs are flat {baseDir}/{project}.db files (Scope.Project, no sub-name),
		// so the scope keys are the file names at the base dir itself.
		foreach (var project in ScopedDbFiles.ListNames(_factory.BaseDir, string.Empty))
		{
			ct.ThrowIfCancellationRequested();
			try
			{
				if (!await _llm.IsAvailableAsync(project, LlmCapability.Chat, ct)) continue;

				// The drain reads through ISessionService, whose store may open raw
				// connections; GetDb first runs the migrations so a file last opened
				// before a schema change is current (reference: NewConnection ≠ migrations).
				_factory.GetDb(project);

				var headers = await _sessions.ListAsync(project, ct);
				if (headers.Count == 0) continue;

				var states = await LoadDigestStatesAsync(project, ct);
				var cutoff = DateTime.UtcNow - _quietPeriod;
				foreach (var header in headers)
				{
					ct.ThrowIfCancellationRequested();
					states.TryGetValue(header.SessionId, out var state);
					if (header.Version <= (state?.Cursor ?? 0)) continue;
					if (header.Updated > cutoff) continue; // still hot — let the turn settle
					try
					{
						if (await DistillAsync(project, header, state, ct))
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

	async Task<bool> DistillAsync(string project, SessionHeader header, DigestState? state, CancellationToken ct)
	{
		var cursor = state?.Cursor ?? 0;
		var delta = await _sessions.DeltaAsync(project, header.SessionId, cursor, ct);
		if (delta.Count == 0) return false;

		var digest = Compose(state?.Description, state?.Body);
		var lastVersion = cursor;
		foreach (var batch in Batches(delta).Take(MaxBatchesPerSession))
		{
			var updated = await ChatDistillAsync(project, digest, batch, ct);
			if (string.IsNullOrWhiteSpace(updated)) return false; // hold the cursor, retry next tick
			digest = updated.Trim();
			lastVersion = batch[^1].Version;
		}

		var (description, body) = Split(digest);
		var metadata = JsonSerializer.Serialize(new
		{
			sessionId = header.SessionId,
			agent = header.Agent,
			cursor = lastVersion,
		});
		var outcome = await _memory.UpsertAsync(project, Store, [new MemoryEntryInput
		{
			Key = header.SessionId,
			Version = state?.EntryVersion ?? 0,
			Type = "Reference",
			Description = description,
			Body = body,
			Tags = Tag,
			Metadata = metadata,
		}], [], 0, ct);
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
