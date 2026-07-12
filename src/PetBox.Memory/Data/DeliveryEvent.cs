using LinqToDB.Mapping;

namespace PetBox.Memory.Data;

// One entry delivered to one caller by one tool call (see M011): the COST it spent
// (DeliveredChars/BodyChars/RowChars) and the FIT it had (Rank/ScoreRaw/KRel), kept as raw
// components — never collapsed into a single scalar. Append-only telemetry, never load-bearing.
[Table("delivery_events")]
public sealed record DeliveryEvent
{
	[Column, Identity] public long Id { get; init; }
	[Column, NotNull] public DateTime Ts { get; init; }
	// The MCP session the delivery went to; null on a stateless transport (no session id).
	[Column] public string? SessionId { get; init; }
	// search | get | listing.
	[Column, NotNull] public string Tool { get; init; } = string.Empty;
	// project | workspace — how the container was reached (the row lives in the container's file).
	[Column, NotNull] public string Scope { get; init; } = string.Empty;
	[Column, NotNull] public string Store { get; init; } = string.Empty;
	[Column, NotNull] public string Key { get; init; } = string.Empty;
	// Body chars actually SENT in this row (the bodyLen contract already applied).
	[Column] public long DeliveredChars { get; init; }
	// The entry's FULL body length — DeliveredChars/BodyChars is how much of it survived.
	[Column] public long BodyChars { get; init; }
	// The row's whole serialized wire cost — the honest context price of this delivery.
	[Column] public long RowChars { get; init; }
	// 1-based position in the delivered answer (MMR reorders rows without changing ScoreRaw).
	[Column] public long Rank { get; init; }
	// Fused RRF score BEFORE recency decay; null in a listing / memory_get (no relevance leg ran).
	[Column] public double? ScoreRaw { get; init; }
	// ScoreRaw normalized by the request's top-1 ScoreRaw → a within-request [0,1] fit.
	// 1 for memory_get (an explicit open is a perfect fit); null in a listing.
	[Column] public double? KRel { get; init; }
	// deliberate | machine — the same honest split entry_usage.DeliberateCount records.
	[Column, NotNull] public string UsageSource { get; init; } = string.Empty;
}
