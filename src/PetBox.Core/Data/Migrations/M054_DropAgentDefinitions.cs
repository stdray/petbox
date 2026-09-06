using FluentMigrator;

namespace PetBox.Core.Data.Migrations;

// Drops `agent_definitions` (created by M037) — work agent-defs-server-teardown.
//
// WHY IT GOES. The table held one portable agent-definition document per project, written by the
// admin editor / `agent_def_upsert` / `PUT /api/{p}/agent-defs` and read by the wiring kit at
// wire time. The kit stopped asking (work wire-stops-fetching-definition): a definition is now
// COMPILED FROM FILES on disk — the kit's own baseline, then the user layer, then the project
// layer. That left the stored documents authoritative-looking and read by nothing, which is worse
// than absent: an owner editing a role in the admin UI would see a saved document that changes no
// agent anywhere. Every surface that reached this table is deleted in the same change, so keeping
// the table would leave rows no code can read, write or delete.
//
// DATA LOSS IS DELIBERATE AND WAS MEASURED FIRST. A full export of all 20 projects' documents was
// taken from production on 2026-09-06 before this migration was written; 15 of the 20 differed from
// the git baseline only by carrying the PRE-TRIM orchestrator prose (i.e. they were stale copies of
// an older baseline, not curated divergence), and the remaining 5 matched it. Nothing here is a
// source of truth for anything: the baseline that survives is src/common/default-agents.json, still
// embedded in this assembly and still validated on load (DefaultAgentDefinition).
//
// NOT MODELLED ON M038. That one drops a table only if it exists AND is empty — a guard that fits a
// table nothing ever wrote to. This one is the opposite case: the table is populated in every live
// project, so an "only if empty" drop would silently never fire and the schema would diverge
// between prod and a fresh database forever. Typed `Delete.Table` in the style of M019/M012:
// unconditional, and loud if the table is somehow already gone.
//
// SQLite drops the table's indexes (ux_agent_definitions_active_project_key,
// ix_agent_definitions_project_active) with it. M037 is left in place — its version is applied on
// prod and a migration timeline is history, not the current schema.
//
// Forward-only: Down does not recreate the table (nothing would read it).
[Migration(54, "Drop agent_definitions — the wiring kit builds its definition from files, nothing reads this store")]
public sealed class M054_DropAgentDefinitions : Migration
{
	public override void Up() => Delete.Table("agent_definitions");

	public override void Down() { } // forward-only
}
