using LinqToDB;
using Microsoft.Extensions.Logging;
using PetBox.Core.Data;
using PetBox.Core.Data.Temporal;
using PetBox.Tasks.Contract;
using PetBox.Tasks.Data;

namespace PetBox.Tasks.Services.Scheduling;

// The daily alarm — see IScheduledWakeService. Half one (this file): wake snoozed nodes. Half two:
// RecurringRuleService.FireDueAsync. Snoozes first, then rules, in one pass per project.
//
// WHY THE WAKE WRITES THROUGH TemporalStore DIRECTLY and not through ITasksService.UpsertAsync: the
// wake is a SYSTEM action on fields no caller may set (WokeAt), the same posture as
// TaskTransitionEffects.SetDecisionPendingAsync — and like it, it writes a real revision with the
// node's own version as the baseline, so a human edit that lands in between turns the wake into a
// conflict (retried by the next pass) rather than a lost update. The revision is what makes the
// wake visible on tasks_delta and so in the owner digest.
public sealed partial class ScheduledWakeService : IScheduledWakeService
{
	readonly ICoreDbFactory _core;
	readonly ITaskBoardStore _boards;
	readonly ITasksService _tasks;
	readonly IRecurringRuleService _rules;
	readonly ILogger<ScheduledWakeService> _log;
	readonly TimeProvider _time;

	public ScheduledWakeService(ICoreDbFactory core, ITaskBoardStore boards, ITasksService tasks,
		IRecurringRuleService rules, ILogger<ScheduledWakeService> log, TimeProvider? time = null)
	{
		_core = core;
		_boards = boards;
		_tasks = tasks;
		_rules = rules;
		_log = log;
		_time = time ?? TimeProvider.System;
	}

	public async Task<IReadOnlyList<ScheduledPassReport>> RunAllAsync(CancellationToken ct = default)
	{
		List<string> projects;
		using (var db = _core.Open())
			projects = await db.TaskBoards.Select(b => b.ProjectKey).Distinct().OrderBy(k => k).ToListAsync(ct);

		var reports = new List<ScheduledPassReport>(projects.Count);
		foreach (var project in projects)
		{
			try
			{
				var report = await RunProjectAsync(project, ct);
				reports.Add(report);
				if (report.Woken.Count + report.ClearedOnTerminal.Count + report.Fired.Count + report.Skipped.Count + report.Failed.Count > 0)
					LogProjectPass(_log, project, report.Woken.Count, report.WokenForOwner.Count, report.ClearedOnTerminal.Count,
						report.Fired.Count, report.Skipped.Count, report.Failed.Count);
			}
			catch (Exception ex) when (ex is not OperationCanceledException)
			{
				LogProjectFailed(_log, ex, project);
			}
		}
		return reports;
	}

	public async Task<ScheduledPassReport> RunProjectAsync(string projectKey, CancellationToken ct = default)
	{
		var now = NodeSnooze.Normalize(_time.GetUtcNow().UtcDateTime);
		var (woken, wokenForOwner, cleared) = await WakeDueSnoozesAsync(projectKey, now, ct);
		var fire = await _rules.FireDueAsync(projectKey, now, ct);
		return new ScheduledPassReport(projectKey, now, woken, wokenForOwner, cleared, fire.Fired, fire.Skipped, fire.Failed);
	}

	// spec snooze-wakes-without-a-human + snooze-wake-addressee. Every ACTIVE node still carrying a
	// wake date is looked at (a handful per project — the snoozed set, not the board):
	//   * terminal on its board's own FSM → the snooze is dropped WITHOUT a wake (no WokeAt, no flag);
	//   * open and due → woken: date cleared, WokeAt stamped, status untouched, and decisionPending
	//     set only when the snooze named the owner;
	//   * open and not yet due → left alone.
	async Task<(List<string> Woken, List<string> WokenForOwner, List<string> Cleared)> WakeDueSnoozesAsync(
		string projectKey, DateTime now, CancellationToken ct)
	{
		var woken = new List<string>();
		var wokenForOwner = new List<string>();
		var cleared = new List<string>();

		using var ctx = _boards.NewEnsuredConnection(projectKey);
		var snoozed = await ctx.TaskNodes.Where(n => n.ActiveTo == null && n.SnoozeUntil != null).ToListAsync(ct);
		foreach (var byBoard in snoozed.GroupBy(n => n.Board, StringComparer.Ordinal))
		{
			var board = byBoard.Key;
			var meta = await _boards.FindAsync(projectKey, board, ct);
			var runtime = await _tasks.GetRuntimeForBoardAsync(projectKey, board, ct);
			var writes = new List<TaskNode>();
			foreach (var n in byBoard)
			{
				if (runtime.IsTerminalStatus(meta?.Kind, n.Status))
				{
					writes.Add(n with { SnoozeUntil = null });
					cleared.Add(n.Key);
				}
				else if (n.SnoozeUntil <= now)
				{
					var forOwner = n.SnoozeWakeTo == NodeSnooze.Owner;
					writes.Add(n with { SnoozeUntil = null, WokeAt = now, DecisionPending = n.DecisionPending || forOwner });
					woken.Add(n.Key);
					if (forOwner) wokenForOwner.Add(n.Key);
				}
			}
			if (writes.Count == 0) continue;
			var result = await TemporalStore.UpsertAsync(ctx, writes, partition: n => n.Board == board, ct: ct);
			if (!result.Applied)
			{
				// A concurrent edit moved one of these nodes under us. Nothing was written for this
				// board (the batch is atomic); the next pass sees the fresh rows and tries again.
				LogWakeConflict(_log, projectKey, board, result.Conflicts.Count);
				var lost = writes.Select(w => w.Key).ToHashSet(StringComparer.Ordinal);
				woken.RemoveAll(lost.Contains);
				wokenForOwner.RemoveAll(lost.Contains);
				cleared.RemoveAll(lost.Contains);
				continue;
			}
			await _boards.TouchAsync(projectKey, board, ct);
		}
		return (woken, wokenForOwner, cleared);
	}

	[LoggerMessage(EventId = 350, Level = LogLevel.Information,
		Message = "Scheduled pass in {ProjectKey}: woke {Woken} ({ForOwner} for the owner), cleared {Cleared} on terminal, fired {Fired}, skipped {Skipped}, failed {Failed}")]
	static partial void LogProjectPass(ILogger logger, string projectKey, int woken, int forOwner, int cleared, int fired, int skipped, int failed);

	[LoggerMessage(EventId = 351, Level = LogLevel.Warning, Message = "Scheduled pass failed for project {ProjectKey}")]
	static partial void LogProjectFailed(ILogger logger, Exception ex, string projectKey);

	[LoggerMessage(EventId = 352, Level = LogLevel.Information,
		Message = "Snooze wake on {ProjectKey}/{Board} lost a race with {Conflicts} concurrent edit(s); retried next pass")]
	static partial void LogWakeConflict(ILogger logger, string projectKey, string board, int conflicts);
}
