using FluentMigrator;

namespace PetBox.Core.Data.Migrations;

// The board's DECLARED ROLE in delivery (spec: task-usage-layer-with-declared-role).
// Usage telemetry has two axes — what a delivery COST and how well it FIT — and both are
// read against an expectation that differs by role: a CORPUS surface is where the answer
// itself lives (a dead tail there is waste), an INDEX surface is an entry point that is
// SUPPOSED to be surfaced far more often than it is opened (a dead tail there is coverage,
// not waste). Reading an index by corpus expectations is not a rounding error — it already
// happened once, to the memory store `session-digests`, which read as "worst in the system"
// on `opened: 0%` while being exactly the entry-point surface it was built to be.
//
// The role is DECLARED, never inferred from the board's name or kind: boards are named by
// the user (in another project the same roles carry different names), so any hardcoded list
// mis-measures everything it does not recognize — silently, which is the whole failure mode
// this column exists to end.
//
// Default `corpus` for every existing row: the conservative reading is "this board's nodes
// are supposed to be opened", which is what every board was implicitly judged by until now.
// Nothing is retroactively re-labelled by this migration.
[Migration(51, "Add DeclaredRole to TaskBoards (index|corpus delivery role, default corpus)")]
public sealed class M051_TaskBoardDeclaredRole : Migration
{
	public override void Up() =>
		Create.Column("DeclaredRole").OnTable("TaskBoards").AsString(20).NotNullable().WithDefaultValue("corpus");

	public override void Down() => Delete.Column("DeclaredRole").FromTable("TaskBoards");
}
