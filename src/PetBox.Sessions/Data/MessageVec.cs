using LinqToDB.Mapping;

namespace PetBox.Sessions.Data;

// One cached message embedding (see M004): the episodic index reuses a row when the
// ordinal's content hash and the embedder identity (Model + truncated Dim) still match,
// and re-embeds otherwise. Pure cache — losing rows only costs a re-embed.
[Table("message_vec")]
public sealed record MessageVec
{
	[Column, PrimaryKey, NotNull] public string SessionId { get; init; } = string.Empty;
	[Column, PrimaryKey] public long Version { get; init; }
	[Column, NotNull] public string Hash { get; init; } = string.Empty;
	[Column, NotNull] public string Model { get; init; } = string.Empty;
	[Column] public int Dim { get; init; }
	[Column, NotNull] public byte[] Vec { get; init; } = [];
}
