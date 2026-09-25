using LinqToDB.Mapping;

namespace PetBox.Tasks.Data;

// Row shape for `recurring_rules` (M028_RecurringRules) — see that migration's header for what each
// column means and why this is a plain table rather than a temporal one.
[Table("recurring_rules")]
public sealed record RecurringRule
{
	[Column, PrimaryKey, NotNull] public string Id { get; init; } = string.Empty;
	[Column, NotNull] public string Board { get; init; } = string.Empty;
	[Column, NotNull] public string Type { get; init; } = string.Empty;
	[Column, NotNull] public string Title { get; init; } = string.Empty;
	[Column, NotNull] public string Body { get; init; } = string.Empty;
	// Newline-joined "namespace:value" tags; see RecurringRuleService.SplitTags / JoinTags.
	[Column, NotNull] public string Tags { get; init; } = string.Empty;
	[Column, NotNull] public string Period { get; init; } = string.Empty;
	[Column, NotNull] public string WakeTo { get; init; } = string.Empty;
	[Column, NotNull] public DateTime NextDueAt { get; init; }
	[Column, Nullable] public DateTime? LastFiredAt { get; init; }
	[Column, Nullable] public string? OpenNodeId { get; init; }
	[Column, NotNull] public long MissedCount { get; init; }
	[Column, Nullable] public string? LastError { get; init; }
	[Column, NotNull] public DateTime CreatedAt { get; init; }
	[Column, NotNull] public DateTime UpdatedAt { get; init; }
}
