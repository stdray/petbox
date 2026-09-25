namespace PetBox.Tasks.Contract;

// THE DAILY ALARM (idea recurring-run-scheduler, spec subtree `scheduled-wake`): one pass that
// (1) wakes snoozed nodes whose date has come and (2) fires the project's rules of repetition. Both
// halves live behind this one door so the hosted daily job (ScheduledWakeJob) and the manual trigger
// (MCP `tasks_schedule_run`) run EXACTLY the same code — the manual trigger is what a live smoke
// uses, and a smoke that exercised a second implementation would prove nothing about the first.
//
// Idempotent by construction: a woken node no longer carries a wake date, and a fired rule's
// NextDueAt has moved past `now`, so a second pass on the same day changes nothing.
public interface IScheduledWakeService
{
	// One project, both halves, at the service's own clock.
	Task<ScheduledPassReport> RunProjectAsync(string projectKey, CancellationToken ct = default);

	// Every project that has task boards. A failure in one project is logged and does not stop the
	// others — the daily job must not let one broken file silence every other project's alarms.
	Task<IReadOnlyList<ScheduledPassReport>> RunAllAsync(CancellationToken ct = default);
}

// What one pass did in one project. Counts, plus the keys, so a manual run can be checked by eye.
public sealed record ScheduledPassReport(
	string ProjectKey,
	DateTime RanAt,
	// Nodes woken (their date had come, they were open). WokenForOwner ⊆ Woken.
	IReadOnlyList<string> Woken,
	IReadOnlyList<string> WokenForOwner,
	// Snoozes dropped WITHOUT a wake because the node had already reached a terminal status.
	IReadOnlyList<string> ClearedOnTerminal,
	// Rules that created a card this pass: rule id -> the new card's key.
	IReadOnlyDictionary<string, string> Fired,
	// Rules that matured while their previous card was still open: rule id -> total missed periods.
	IReadOnlyDictionary<string, long> Skipped,
	// Rules that matured but could not create their card: rule id -> the refusal.
	IReadOnlyDictionary<string, string> Failed);

// Rules of repetition (spec recurring-card-rule / recurring-card-no-pileup). Deliberately minimal
// in v1: CRUD plus the firing pass, no FSM, no UI (the idea's spec_plan rules those out).
public interface IRecurringRuleService
{
	Task<RecurringRuleView> UpsertAsync(string projectKey, RecurringRuleInput input, CancellationToken ct = default);
	Task<IReadOnlyList<RecurringRuleView>> ListAsync(string projectKey, CancellationToken ct = default);
	// False when there was no such rule. Deleting a rule never touches the cards it created.
	Task<bool> DeleteAsync(string projectKey, string id, CancellationToken ct = default);

	// The firing half of the daily pass, at an explicit instant — called by IScheduledWakeService,
	// never by an adapter directly. See RecurringRuleService.FireDueAsync.
	Task<RecurringFireResult> FireDueAsync(string projectKey, DateTime now, CancellationToken ct = default);
}

// What the firing half did: rule id -> new card key / running missed count / refusal.
public sealed record RecurringFireResult(
	IReadOnlyDictionary<string, string> Fired,
	IReadOnlyDictionary<string, long> Skipped,
	IReadOnlyDictionary<string, string> Failed);

public static class RecurringPeriod
{
	public const string Day = "day";
	public const string Week = "week";
	public const string Month = "month";

	public static bool IsValid(string value) => value is Day or Week or Month;

	public static DateTime Advance(DateTime from, string period) => period switch
	{
		Day => from.AddDays(1),
		Week => from.AddDays(7),
		Month => from.AddMonths(1),
		_ => throw new ArgumentException($"unknown period '{period}'"),
	};
}

// A rule as written. Id is the rule's slug (a rewrite of the same id REPLACES the template and
// keeps its firing state). NextDueAt: when the first/next card is due — omitted = now on a new rule
// (the first pass fires it) and unchanged on an existing one.
public sealed record RecurringRuleInput
{
	public required string Id { get; init; }
	public required string Board { get; init; }
	public string? Type { get; init; }
	public required string Title { get; init; }
	public string? Body { get; init; }
	public IReadOnlyList<string>? Tags { get; init; }
	public required string Period { get; init; }
	public string? WakeTo { get; init; }
	public DateTime? NextDueAt { get; init; }
}

public sealed record RecurringRuleView(
	string Id, string Board, string Type, string Title, string Body, IReadOnlyList<string> Tags,
	string Period, string WakeTo, DateTime NextDueAt, DateTime? LastFiredAt, string? OpenNodeId,
	long MissedCount, string? LastError, DateTime CreatedAt, DateTime UpdatedAt);
