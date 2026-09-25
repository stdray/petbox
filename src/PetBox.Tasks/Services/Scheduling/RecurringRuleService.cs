using LinqToDB;
using PetBox.Tasks.Contract;
using PetBox.Tasks.Data;

namespace PetBox.Tasks.Services.Scheduling;

// Rules of repetition (spec recurring-card-rule / recurring-card-no-pileup). CRUD over the
// `recurring_rules` table plus the firing half of the daily pass (FireDueAsync), which
// ScheduledWakeService calls — the only caller, so the hosted job and the manual trigger share it.
//
// A fired card is created through ITasksService.UpsertAsync, the ordinary write door: the rule gets
// no privilege a hand-written card would not have, so a template the board's methodology refuses
// (a wrong type, a tag outside the declared axes, a missing required link) is refused here too —
// recorded on the rule as LastError instead of being forced in.
public sealed class RecurringRuleService : IRecurringRuleService
{
	// Who the system's own comments on a card are signed by.
	public const string Author = "petbox-scheduler";

	readonly ITaskBoardStore _boards;
	readonly ITasksService _tasks;
	readonly ICommentService _comments;
	readonly TimeProvider _time;

	public RecurringRuleService(ITaskBoardStore boards, ITasksService tasks, ICommentService comments, TimeProvider? time = null)
	{
		_boards = boards;
		_tasks = tasks;
		_comments = comments;
		_time = time ?? TimeProvider.System;
	}

	DateTime Now => NodeSnooze.Normalize(_time.GetUtcNow().UtcDateTime);

	public async Task<RecurringRuleView> UpsertAsync(string projectKey, RecurringRuleInput input, CancellationToken ct = default)
	{
		var id = TaskSlug.Validate(input.Id);
		var board = (input.Board ?? "").Trim();
		if (!await _boards.ExistsAsync(projectKey, board, ct))
			throw new ArgumentException($"board '{board}' does not exist in project '{projectKey}' — a rule can only fire onto an existing board");
		var title = (input.Title ?? "").Trim();
		if (title.Length == 0)
			throw new ArgumentException("'title' is required — it becomes the title of every card the rule creates");
		var period = (input.Period ?? "").Trim().ToLowerInvariant();
		if (!RecurringPeriod.IsValid(period))
			throw new ArgumentException($"'period' must be '{RecurringPeriod.Day}', '{RecurringPeriod.Week}' or '{RecurringPeriod.Month}' (got '{input.Period}')");
		var wakeTo = NodeSnooze.NormalizeWakeTo(input.WakeTo);
		if (!NodeSnooze.IsValidWakeTo(wakeTo))
			throw new ArgumentException($"'wakeTo' must be '{NodeSnooze.Agent}' (default) or '{NodeSnooze.Owner}' (got '{input.WakeTo}')");

		var now = Now;
		using var db = _boards.NewEnsuredConnection(projectKey);
		var existing = await db.RecurringRules.FirstOrDefaultAsync(r => r.Id == id, ct);
		var row = new RecurringRule
		{
			Id = id,
			Board = board,
			Type = (input.Type ?? "").Trim().ToLowerInvariant(),
			Title = title,
			Body = input.Body ?? "",
			Tags = JoinTags(input.Tags),
			Period = period,
			WakeTo = wakeTo,
			// A new rule is due at once unless told otherwise; a rewrite keeps its schedule unless
			// told otherwise. Firing state (last fire, open card, misses) always survives a rewrite.
			NextDueAt = input.NextDueAt is { } due ? NodeSnooze.Normalize(due) : existing?.NextDueAt ?? now,
			LastFiredAt = existing?.LastFiredAt,
			OpenNodeId = existing?.OpenNodeId,
			MissedCount = existing?.MissedCount ?? 0,
			LastError = existing?.LastError,
			CreatedAt = existing?.CreatedAt ?? now,
			UpdatedAt = now,
		};
		if (existing is null) await db.InsertAsync(row, token: ct);
		else await db.UpdateAsync(row, token: ct);
		return View(row);
	}

	public async Task<IReadOnlyList<RecurringRuleView>> ListAsync(string projectKey, CancellationToken ct = default)
	{
		using var db = _boards.NewEnsuredConnection(projectKey);
		var rows = await db.RecurringRules.OrderBy(r => r.Id).ToListAsync(ct);
		return rows.Select(View).ToList();
	}

	public async Task<bool> DeleteAsync(string projectKey, string id, CancellationToken ct = default)
	{
		using var db = _boards.NewEnsuredConnection(projectKey);
		return await db.RecurringRules.Where(r => r.Id == id).DeleteAsync(ct) > 0;
	}

	// The firing half of the daily pass. For every rule whose NextDueAt has come:
	//   * previous card still OPEN → create nothing (spec recurring-card-no-pileup); count the missed
	//     periods on the rule and say so in a comment on that open card, with the running total;
	//   * otherwise → create the card from the template, remember it as the open card.
	// Either way NextDueAt moves past `now` by whole periods, so a pass run again the same day finds
	// nothing due, and a job that was down for a week fires ONCE rather than seven times.
	// A template the board refuses leaves NextDueAt alone (tomorrow's pass retries) and records why.
	public async Task<RecurringFireResult> FireDueAsync(string projectKey, DateTime now, CancellationToken ct = default)
	{
		var fired = new Dictionary<string, string>(StringComparer.Ordinal);
		var skipped = new Dictionary<string, long>(StringComparer.Ordinal);
		var failed = new Dictionary<string, string>(StringComparer.Ordinal);

		List<RecurringRule> due;
		using (var db = _boards.NewEnsuredConnection(projectKey))
			due = await db.RecurringRules.Where(r => r.NextDueAt <= now).OrderBy(r => r.Id).ToListAsync(ct);

		foreach (var rule in due)
		{
			var (next, periods) = AdvancePast(rule.NextDueAt, rule.Period, now);
			if (rule.OpenNodeId is { } openId && await OpenCardAsync(projectKey, openId, ct) is { } open)
			{
				var missed = rule.MissedCount + periods;
				await _comments.AddAsync(projectKey, open.Board, openId, null, Author,
					$"Recurring rule `{rule.Id}` came due again (period: {rule.Period}) while this card is still open, "
					+ $"so no new card was created. Missed periods so far: {missed}.",
					tags: null, ct);
				await SaveAsync(projectKey, rule with { NextDueAt = next, MissedCount = missed, UpdatedAt = now }, ct);
				skipped[rule.Id] = missed;
				continue;
			}

			var created = await CreateCardAsync(projectKey, rule, now, ct);
			if (created.Error is { } error)
			{
				await SaveAsync(projectKey, rule with { LastError = error, UpdatedAt = now }, ct);
				failed[rule.Id] = error;
				continue;
			}
			await SaveAsync(projectKey, rule with
			{
				NextDueAt = next,
				LastFiredAt = now,
				OpenNodeId = created.NodeId,
				MissedCount = 0,
				LastError = null,
				UpdatedAt = now,
			}, ct);
			fired[rule.Id] = created.Key!;
		}
		return new RecurringFireResult(fired, skipped, failed);
	}

	// The card from the previous firing, when it still exists AND is open on its own board's FSM.
	// A deleted card, or one in any terminal status, frees the rule to fire again.
	async Task<NodeDetailView?> OpenCardAsync(string projectKey, string nodeId, CancellationToken ct)
	{
		var node = await _tasks.GetNodeAsync(projectKey, nodeId, ct);
		if (node is null) return null;
		var meta = await _boards.FindAsync(projectKey, node.Board, ct);
		var runtime = await _tasks.GetRuntimeForBoardAsync(projectKey, node.Board, ct);
		return runtime.IsTerminalStatus(meta?.Kind, node.Node.Status) ? null : node;
	}

	async Task<(string? Key, string? NodeId, string? Error)> CreateCardAsync(
		string projectKey, RecurringRule rule, DateTime now, CancellationToken ct)
	{
		var key = await FreeKeyAsync(projectKey, rule.Board, $"{rule.Id}-{now:yyyy-MM-dd}", ct);
		var patch = new NodePatch
		{
			Key = key,
			Version = 0,
			Type = rule.Type.Length == 0 ? null : rule.Type,
			Title = rule.Title,
			// spec recurring-card-rule: "the created card MUST reference its rule". The rule is not a
			// node, so there is no relation to draw; the reference is written where every reader of
			// the card sees it.
			Body = (rule.Body.Length == 0 ? "" : rule.Body.TrimEnd() + "\n\n")
				+ $"---\nCreated by recurring rule `{rule.Id}` (period: {rule.Period}). Rules: `tasks_recurring_list`.",
			Tags = SplitTags(rule.Tags) is { Length: > 0 } tags ? tags : null,
			// Only an owner-addressed rule puts its card in the owner's queue (idea
			// recurring-run-scheduler, the owner's fork: recurring cards carry no flag by default).
			DecisionPending = rule.WakeTo == NodeSnooze.Owner ? true : null,
		};
		try
		{
			var outcome = await _tasks.UpsertAsync(projectKey, rule.Board, [patch], ct: ct);
			if (!outcome.Result.Applied || outcome.Result.Added.Count == 0)
				return (null, null, string.Join("; ", outcome.Result.Conflicts.Select(c => c.Reason ?? c.Kind.ToString()))
					is { Length: > 0 } reason ? reason : "the write did not apply");
			var added = outcome.Result.Added[0];
			return (added.Key, added.NodeId, null);
		}
		catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
		{
			return (null, null, ex.Message);
		}
	}

	// The card key: `<rule>-<date>`, suffixed when that slug is already taken on the board (a rule
	// whose schedule was reset by hand can come due twice on one day).
	async Task<string> FreeKeyAsync(string projectKey, string board, string baseKey, CancellationToken ct)
	{
		var key = baseKey;
		for (var i = 2; await _tasks.ResolveNodeRefOrNullAsync(projectKey, key, board, ct) is not null; i++)
			key = $"{baseKey}-{i}";
		return key;
	}

	async Task SaveAsync(string projectKey, RecurringRule row, CancellationToken ct)
	{
		using var db = _boards.NewEnsuredConnection(projectKey);
		await db.UpdateAsync(row, token: ct);
	}

	// Move `due` forward by whole periods until it is past `now`; the count is how many periods
	// matured (at least one — the rule is due, that is why we are here).
	public static (DateTime Next, long Periods) AdvancePast(DateTime due, string period, DateTime now)
	{
		var next = due;
		long periods = 0;
		while (next <= now)
		{
			next = RecurringPeriod.Advance(next, period);
			periods++;
		}
		return (next, Math.Max(1, periods));
	}

	static RecurringRuleView View(RecurringRule r) => new(
		r.Id, r.Board, r.Type, r.Title, r.Body, SplitTags(r.Tags), r.Period, r.WakeTo, r.NextDueAt,
		r.LastFiredAt, r.OpenNodeId, r.MissedCount, r.LastError, r.CreatedAt, r.UpdatedAt);

	static string JoinTags(IReadOnlyList<string>? tags) =>
		string.Join('\n', (tags ?? []).Select(t => t.Trim()).Where(t => t.Length > 0).Distinct(StringComparer.Ordinal));

	static string[] SplitTags(string tags) =>
		tags.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
