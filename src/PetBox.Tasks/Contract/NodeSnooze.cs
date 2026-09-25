using PetBox.Tasks.Data;

namespace PetBox.Tasks.Contract;

// The snooze vocabulary (spec node-snooze-until, snooze-wake-addressee), in ONE place so the
// upsert validation, the wake job and the digest can never spell the addressee two ways.
public static class NodeSnooze
{
	// The addressee of a wake. AGENT is the default by the owner's decision of 2026-09-25 (idea
	// recurring-run-scheduler): the owner's decision queue is already the bottleneck, so automation
	// only adds to it when a snooze names the owner explicitly.
	public const string Agent = "agent";
	public const string Owner = "owner";

	public static bool IsValidWakeTo(string value) => value is Agent or Owner;

	// Normalizes a caller's addressee: null/blank = the default (agent); anything else is returned
	// lower-cased for IsValidWakeTo to judge.
	public static string NormalizeWakeTo(string? value) =>
		string.IsNullOrWhiteSpace(value) ? Agent : value.Trim().ToLowerInvariant();

	// Every snooze/recurrence instant as it is STORED: UTC, truncated to the millisecond. plan_nodes
	// keeps timestamps as TEXT at millisecond precision, so an untruncated value would read back
	// different from what was written — and SamePayload would then see a resubmit of the very same
	// snooze as a change and mint a phantom revision every time. A date without a zone is UTC.
	public static DateTime Normalize(DateTime t)
	{
		var utc = t.Kind switch
		{
			DateTimeKind.Utc => t,
			DateTimeKind.Local => t.ToUniversalTime(),
			_ => DateTime.SpecifyKind(t, DateTimeKind.Utc),
		};
		return new DateTime(utc.Ticks - utc.Ticks % TimeSpan.TicksPerMillisecond, DateTimeKind.Utc);
	}

	// The read-side view: null when the node carries neither a snooze nor a wake mark.
	public static NodeSnoozeView? View(TaskNode n) =>
		n.SnoozeUntil is null && n.WokeAt is null
			? null
			: new NodeSnoozeView(n.SnoozeUntil, n.SnoozeReason, n.SnoozeWakeTo, n.WokeAt);
}

// A snooze edit as submitted on NodePatch.Snooze. Exactly one of two shapes:
//   * SET   — Until required (a snooze without a date would never surface: the machine does not
//             evaluate the condition text); Reason optional; WakeTo optional (default agent).
//             Setting a snooze also clears any earlier wake mark: the node is asleep again.
//   * CLEAR — Clear=true and nothing else: removes the snooze AND the wake mark. This is also how
//             a woken node is acknowledged.
// Anything else (no Until and no Clear, Clear together with Until, an unknown WakeTo, a snooze on a
// node whose resulting status is terminal) is REFUSED through conflicts[] — never a partial apply.
public sealed record NodeSnoozeEdit
{
	public DateTime? Until { get; init; }
	public string? Reason { get; init; }
	public string? WakeTo { get; init; }
	public bool Clear { get; init; }
}

// What a node's snooze looks like on a read. `Until` null + `WokeAt` set = woken (the snooze was
// lifted by the daily job); `Until` set = still asleep. Reason/WakeTo survive the wake on purpose.
public sealed record NodeSnoozeView(DateTime? Until, string Reason, string WakeTo, DateTime? WokeAt);
