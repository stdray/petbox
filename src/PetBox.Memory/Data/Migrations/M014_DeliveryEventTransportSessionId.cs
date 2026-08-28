using FluentMigrator;

namespace PetBox.Memory.Data.Migrations;

// delivery_events.SessionId renamed to TransportSessionId (owner decision 2026-08-27, card
// delivery-event-transport-session-id): the name promised a link to the AGENT/transcript session
// (SessionRow.SessionId — a disjoint id space, populated by push-session.ts from the Stop-hook
// input) that this column never carries and never will. What it actually holds is the MCP
// `Mcp-Session-Id` TRANSPORT header, verbatim, when a client sends one — and PetBox's MCP
// transport is stateless (Program.cs, .WithHttpTransport(o => o.Stateless = true)), so no real
// client ever sends it: 3750 MCP calls sampled over 30 days, every slot empty. Neither the MCP
// path nor the canon-delivery path (canon injects unconditionally, so an id there would only
// repeat what is already known — see memory m-6362f8894f29476997cc4b399336dc41) will ever produce
// an agent session id here, so this rename is a final answer, not a placeholder.
//
// A plain column rename on a non-PK, non-virtual table IS expressible with FluentMigrator's typed
// API (`Rename.Column`), so per SqliteDdl.cs's rule it is written with the typed API, not the
// create/copy/DROP/RENAME table-rebuild idiom M009/M012/M013 needed for a PK change or an FTS5
// virtual table — neither applies here. SQLite has supported `ALTER TABLE ... RENAME COLUMN`
// natively since 3.25.0 (2018); existing rows (all null, or a transport value from the rare
// client that sent the header) are carried over unchanged — this is a name-only change, no data
// is touched or lost.
[Migration(14, "delivery_events: rename SessionId to TransportSessionId (name matches what it holds)")]
public sealed class M014_DeliveryEventTransportSessionId : Migration
{
	public override void Up() =>
		Rename.Column("SessionId").OnTable("delivery_events").To("TransportSessionId");

	public override void Down() =>
		Rename.Column("TransportSessionId").OnTable("delivery_events").To("SessionId");
}
