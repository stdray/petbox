using LinqToDB.Mapping;
using PetBox.Core.Data.Temporal;

namespace PetBox.Sessions.Data;

// An agent working-session plan blob, stored as a temporal (SCD type-2) row.
// Identity (Key) is the agent-supplied sessionId. Payload: Agent + Content
// (markdown — what claude-code writes to ~/.claude/plans/*.md).
[Table("sessions")]
public sealed record SessionRow : TemporalRow
{
	[Column, NotNull] public string Agent { get; init; } = string.Empty;
	[Column, NotNull] public string Content { get; init; } = string.Empty;

	public override bool SamePayload(TemporalRow other) =>
		other is SessionRow s && s.Agent == Agent && s.Content == Content;

	public override TemporalRow AsRevision(long version, DateTime created, DateTime updated) =>
		this with { Version = version, ActiveFrom = version, ActiveTo = null, Created = created, Updated = updated };
}
