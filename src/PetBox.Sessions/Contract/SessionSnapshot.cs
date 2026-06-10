using System.Linq;

namespace PetBox.Sessions.Contract;

// The latest snapshot of a session: every message, newest state (no per-revision history).
// Version == the last message's Version. Content renders the conversation as readable text
// (what the UI shows and session.get returns) — a single-message snapshot renders verbatim,
// multi-message renders with `### role` headers (mirrors the old hook output).
public sealed record SessionSnapshot(
	string SessionId, string Agent, IReadOnlyList<SessionMessage> Messages, long Version, DateTime Updated)
{
	public string Content => Messages.Count == 1
		? Messages[0].Content
		: string.Join("\n\n", Messages.Select(m => $"### {m.Role}\n\n{m.Content}"));

	public int Length => Content.Length;
}

// A lightweight list entry — no message bodies, so listing never decompresses a blob.
public sealed record SessionHeader(string SessionId, string Agent, long Version, DateTime Updated);
