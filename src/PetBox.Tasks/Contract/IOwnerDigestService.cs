namespace PetBox.Tasks.Contract;

// THE OWNER-AWAY DIGEST (spec `owner-away-digest`, work `owner-away-digest-delivery`) — "what
// happened while I was gone", assembled ONCE here and rendered by BOTH doors (the MCP verb
// `tasks_owner_digest` and the Razor page /ui/{ws}/{project}/digest/{board}). The page does NOT
// build a digest of its own: a second assembly is a second definition of "waiting on me", and the
// first thing it would do is disagree with the agent-facing one.
//
// THE SECTION ORDER IS THE PRODUCT, NOT A PRESENTATION DETAIL, and it is fixed by the owner's own
// decision of 2026-08-27: (1) waiting on your decision → (2) what closed → (3) new cohorts by
// theme → (4) chronology, and only on request. It is NOT chronological, deliberately: a
// chronological feed is pleasant for a one-day absence and useless for a two-week one, where it is
// 200 events nobody reads. The record's field order below IS that order — do not "sort it more
// logically".
//
// WHAT IS A WINDOW AND WHAT IS A STATE, because the two sections read differently:
//   * (2), (3) and (4) are CHANGE — they answer "since when", off the version cursor (or, with no
//     cursor, the last `days` days).
//   * (1) is STATE — the owner's whole open decision queue on this board, NOT clipped to the
//     window. A decision that has been waiting LONGER than the absence is more urgent, not less,
//     and a digest that hid it because it was flagged before the trip would be actively harmful.
//     This is the one place the digest deliberately ignores its own period.
public interface IOwnerDigestService
{
	// Assemble the digest for one board. `urlPrefix` (null = no links) is the same absolute-permalink
	// prefix the tasks read verbs take, so both doors can hand the owner a clickable node.
	Task<OwnerDigestView> DigestAsync(
		string projectKey, OwnerDigestRequest request, string? urlPrefix = null, CancellationToken ct = default);
}

// The ask. Two cursors, not one, because the change feeds this digest reads live in two INDEPENDENT
// version spaces (task nodes per board, comments per board) — a single scalar cannot address both,
// and folding them would silently re-show or skip half the timeline. Both are optional; omitting
// them selects the time window instead.
public sealed record OwnerDigestRequest
{
	public string Board { get; init; } = string.Empty;

	// The task-node cursor (a `currentVersion` from a previous digest / tasks_delta). null = no
	// cursor: the window is then the last `Days` days, measured on each node's own Updated stamp.
	public long? SinceVersion { get; init; }

	// The comment cursor, same contract, in the comments' own version space. Only read when
	// IncludeTimeline is set.
	public long? SinceCommentVersion { get; init; }

	// The cursor-less window, in days (default 7). Ignored when SinceVersion is given.
	public int Days { get; init; } = DefaultDays;

	// Section (4). Off by default — see the class header for why chronology is opt-in.
	public bool IncludeTimeline { get; init; }

	// Rows per section (default 20). Every section also reports its own untruncated total, so a
	// clipped section says so with a number rather than by quietly ending.
	public int SectionLimit { get; init; } = DefaultSectionLimit;

	public const int DefaultDays = 7;
	public const int DefaultSectionLimit = 20;
}

// One node as the digest shows it. `StatusKind` is the RESOLVED terminal classification
// (open|terminalok|terminalcancel) from the board's own workflow — never a guess off the status
// spelling.
public sealed record OwnerDigestItem(
	string Key, string NodeId, string Title, string Status, string StatusKind, string Type,
	IReadOnlyList<string> Tags, DateTime CreatedAt, DateTime UpdatedAt, bool DecisionPending,
	string? Url = null);

// Section (3): the new nodes of the window grouped on the `area` tag axis — the axis the quartet's
// instance rules already declare, so the grouping is the project's own vocabulary rather than one
// invented here. Semantic clustering is explicitly NOT this card. `Area` is the bare tag VALUE
// ("tasks", "search", …); nodes carrying no area tag land in a single group keyed by
// OwnerDigestCohort.NoArea.
public sealed record OwnerDigestCohort(string Area, int Total, IReadOnlyList<OwnerDigestItem> Items)
{
	public const string NoArea = "(no area)";
}

// One row of section (4). `Kind` is "node" (a node revision landed in the window) or "comment".
public sealed record OwnerDigestEvent(
	string Kind, DateTime At, string NodeKey, string NodeId, string Title, string? Author, string? Excerpt);

// The assembled digest. FIELD ORDER IS THE SECTION ORDER — see the interface header.
public sealed record OwnerDigestView(
	string Board,
	string Kind,
	// Cursors: what was asked, and what to pass next time. CurrentVersion/CurrentCommentVersion are
	// the cursors a follow-up digest should carry.
	long SinceVersion,
	long CurrentVersion,
	long SinceCommentVersion,
	long CurrentCommentVersion,
	// The instant the time window opened — null when the caller passed a version cursor, because
	// then there IS no instant: the period is "since revision N", and printing a fabricated date
	// for it would be the same dishonesty the closure caveat below exists to prevent.
	DateTime? WindowStart,
	// (1) waiting on your decision — STATE, not clipped to the window (see the interface header).
	IReadOnlyList<OwnerDigestItem> AwaitingDecision,
	int AwaitingDecisionTotal,
	// (2) what closed — nodes in the window whose CURRENT status is terminal.
	IReadOnlyList<OwnerDigestItem> Closed,
	int ClosedTotal,
	// (3) new cohorts by theme.
	IReadOnlyList<OwnerDigestCohort> NewCohorts,
	int NewTotal,
	// (4) chronology — null unless the caller asked for it, so "absent" and "empty" stay different
	// answers.
	IReadOnlyList<OwnerDigestEvent>? Timeline,
	int? TimelineTotal,
	// Nodes that disappeared in the window (deleted, not closed) — keys only; there is no row left
	// to enrich.
	IReadOnlyList<string> RemovedKeys,
	// THE KNOWN LIMITATION, carried in the payload so every door has to show it and none can round
	// it off: the server does not store the moment a status changed. See OwnerDigestService.Caveat.
	string ClosureDatingCaveat);
