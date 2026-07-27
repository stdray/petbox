using System.Linq;

namespace PetBox.Sessions.Contract;

// The latest snapshot of a session: every message, newest state (no per-revision history).
// Version == the last message's Version. Content renders the conversation as readable text
// (what the UI shows and session_get returns) — a single-message snapshot renders verbatim,
// multi-message renders with `### role` headers (mirrors the old hook output).
public sealed record SessionSnapshot(
	string SessionId, string Agent, IReadOnlyList<SessionMessage> Messages, long Version, DateTime Updated,
	string? MetaJson = null, DateTime Created = default)
{
	public string Content => Render(Messages);

	public int Length => Content.Length;

	// The ONE "what is new after ordinal X" predicate. SessionService.DeltaAsync and
	// session_get's `fromOrdinal` window both read it, so the incremental-read semantics
	// cannot drift into two slightly different answers. Ordinals are dense 1..N and
	// immutable, so this slice is stable: a cursor into it can never go stale.
	public IReadOnlyList<SessionMessage> Since(long sinceVersion) =>
		sinceVersion <= 0 ? Messages : Messages.Where(m => m.Version > sinceVersion).ToList();

	// The transcript rendered from `fromOrdinal` (1-based) onward — the incremental read
	// (card session-get-from-ordinal). The window is EXACTLY a suffix of Content: the header
	// mode is decided by the FULL message count, not the slice's, so a one-message tail of a
	// multi-message session still carries its `### role` header instead of silently switching
	// to the verbatim single-message rendering. `fromOrdinal` past the last ordinal renders
	// the empty string — the still-a-suffix answer to "nothing new yet", not an error.
	public string ContentFrom(long fromOrdinal) =>
		fromOrdinal <= 1 ? Content : Render(Since(fromOrdinal - 1), Messages.Count);

	// `total` is the message count the header mode is decided by; it defaults to the slice's
	// own count so the whole-transcript render is unchanged.
	static string Render(IReadOnlyList<SessionMessage> slice, int? total = null) =>
		(total ?? slice.Count) == 1
			? (slice.Count == 0 ? "" : slice[0].Content)
			: string.Join("\n\n", slice.Select(m => $"### {m.Role}\n\n{m.Content}"));
}

// A lightweight list entry — no message bodies, so listing never decompresses a blob.
// MetaJson is the optional observed client stamp (cheap TEXT; not a ContentZ blob). Created is
// the row's first-seen timestamp (M008; default(DateTime) on any pre-migration caller that
// doesn't supply it — SessionStore's own listings always do).
public sealed record SessionHeader(string SessionId, string Agent, long Version, DateTime Updated, string? MetaJson = null, DateTime Created = default);

// One KEYSET-paged slice of a project's session headers (spec listing-tail-reachable):
// the page rows and an opaque NextCursor (PetBox.Core.Contract.KeysetCursor) — null means
// this page is the tail. Replaces the former (HasNext, Total) offset shape: an offset
// silently re-serves or swallows a row under concurrent writes (a row inserted/deleted before
// the boundary shifts what "skip N" points at); a cursor instead names the last row actually
// emitted, so "after this row" is stable no matter what else changed. No Total: a keyset walk
// has no cheap notion of "how many pages" — the presence of NextCursor is the only "more
// exists" signal a caller needs (mirrors tasks_search's listing mode).
public sealed record SessionHeaderPage(IReadOnlyList<SessionHeader> Headers, string? NextCursor);

// Server-side sort axes for the session listing (card ui-search-sessions-hybrid — human parity
// with the agent surface's filter/sort set, spec search-one-engine-for-human-and-agent). Length
// has no maintained char-count column; Version (the last message's ordinal == the message
// count) is the cheap, already-selected proxy — monotonic with transcript growth, no
// decompression needed. Used by both the plain listing (SessionStore.ListPageAsync, a real
// SQL ORDER BY) and the search-results reorder (SessionsModel, over the already-selected
// candidate pool) so the two paths can't drift onto different semantics for the same word.
public enum SessionSortField
{
	Updated,
	Created,
	Length,
}
