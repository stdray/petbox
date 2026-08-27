using LinqToDB.Mapping;

namespace PetBox.Tasks.Data;

// One node delivered to one caller by one tool call (see M022): the COST it spent
// (DeliveredChars/BodyChars/RowChars) and the FIT it had (Rank/ScoreRaw/KRel), kept as raw
// components — never collapsed into a single scalar. Append-only telemetry, never load-bearing.
[Table("node_delivery_events")]
public sealed record NodeDeliveryEvent
{
	[Column, Identity] public long Id { get; init; }
	[Column, NotNull] public DateTime Ts { get; init; }
	// The MCP session the delivery went to; null on a stateless transport (no session id).
	[Column] public string? SessionId { get; init; }
	// search | get | listing.
	[Column, NotNull] public string Tool { get; init; } = string.Empty;
	[Column, NotNull] public string Board { get; init; } = string.Empty;
	// Stable identity (survives a re-key); Key is the slug AS DELIVERED, kept for readability.
	[Column, NotNull] public string NodeId { get; init; } = string.Empty;
	[Column, NotNull] public string Key { get; init; } = string.Empty;
	// The owning board's declared role AT DELIVERY TIME (index|corpus). Denormalized on purpose:
	// the whole point of the declaration is that a cost/fit number cannot be read without it, and
	// a column that must be JOINed to be seen is a column that gets left out of the query.
	[Column, NotNull] public string DeclaredRole { get; init; } = string.Empty;
	// Body chars actually SENT in this row (the bodyLen contract already applied).
	[Column] public long DeliveredChars { get; init; }
	// The node's FULL body length — DeliveredChars/BodyChars is how much of it survived.
	[Column] public long BodyChars { get; init; }
	// The row's whole serialized wire cost — the honest context price of this delivery.
	[Column] public long RowChars { get; init; }
	// 1-based position in the delivered answer.
	[Column] public long Rank { get; init; }
	// The fused relevance score; null in a listing / node_get (no relevance leg ran).
	[Column] public double? ScoreRaw { get; init; }
	// ScoreRaw normalized by the request's top-1 → a within-request [0,1] fit. 1 for a
	// tasks_node_get (an explicit open is a perfect fit); null in a listing.
	[Column] public double? KRel { get; init; }
	// deliberate | machine — the same honest split node_usage.DeliberateCount records.
	[Column, NotNull] public string UsageSource { get; init; } = string.Empty;
}
