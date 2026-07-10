using System.Linq;

namespace PetBox.Sessions.Contract;

// The latest snapshot of a session: every message, newest state (no per-revision history).
// Version == the last message's Version. Content renders the conversation as readable text
// (what the UI shows and session_get returns) — a single-message snapshot renders verbatim,
// multi-message renders with `### role` headers (mirrors the old hook output).
public sealed record SessionSnapshot(
	string SessionId, string Agent, IReadOnlyList<SessionMessage> Messages, long Version, DateTime Updated,
	string? MetaJson = null)
{
	public string Content => Messages.Count == 1
		? Messages[0].Content
		: string.Join("\n\n", Messages.Select(m => $"### {m.Role}\n\n{m.Content}"));

	public int Length => Content.Length;
}

// A lightweight list entry — no message bodies, so listing never decompresses a blob.
// MetaJson is the optional observed client stamp (cheap TEXT; not a ContentZ blob).
public sealed record SessionHeader(string SessionId, string Agent, long Version, DateTime Updated, string? MetaJson = null);

// One server-paged slice of a project's session headers: the page rows, whether a further
// page exists (probe row), and the total matching the current search. Keeps the UI's OFFSET/
// LIMIT paging off the full set (spec ui-list-pagination).
public sealed record SessionHeaderPage(IReadOnlyList<SessionHeader> Headers, bool HasNext, int Total);
