using LinqToDB.Mapping;

namespace PetBox.Tasks.Data;

// A controlled-vocabulary tag: "namespace:value" (lowercased). The Namespace is the
// prefix before the first ':'. node_tag.Tag has a FK to this, so a tag must be in the
// vocabulary before it can be attached — the DB enforces the namespace.
[Table("tag_vocab")]
public sealed record TagVocab
{
	[Column, PrimaryKey, NotNull] public string Tag { get; init; } = string.Empty;
	[Column, NotNull] public string Namespace { get; init; } = string.Empty;
	[Column, Nullable] public string? Description { get; init; }
	[Column, NotNull] public DateTime CreatedAt { get; init; }
}

// SCD-2 edge attaching a tag to a node's stable NodeId. Active while ValidTo is null;
// removing a tag soft-closes the row (history kept), mirroring Relation. Board is a
// denormalized mirror of the node's partition so a board's tags group-by needs no join.
[Table("node_tag")]
public sealed record NodeTag
{
	[Column, NotNull] public string NodeId { get; init; } = string.Empty;
	[Column, NotNull] public string Board { get; init; } = string.Empty;
	[Column, NotNull] public string Tag { get; init; } = string.Empty;
	[Column, NotNull] public DateTime ValidFrom { get; init; }
	[Column, Nullable] public DateTime? ValidTo { get; init; }
}
