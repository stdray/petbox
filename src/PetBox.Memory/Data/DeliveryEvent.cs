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
	// The MCP `Mcp-Session-Id` transport header, verbatim, when the client sent one — NOT an
	// agent/transcript session identifier (that space is SessionRow.SessionId, a disjoint id the
	// MCP handler never sees). PetBox's MCP transport is stateless (Program.cs, .WithHttpTransport
	// (o => o.Stateless = true)), so this header is NEVER sent by any real client and this column
	// is ALWAYS null in practice (renamed from SessionId 2026-08-28 — see M014 — precisely because
	// that name promised a link to the agent session that cannot exist on this transport).
	[Column] public string? TransportSessionId { get; init; }
	// search | get | listing | canon.
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
