using FluentMigrator;

namespace PetBox.Core.Data.Migrations;

// Board → methodology instance membership (methodology-instance-core / methodology-board-
// membership). Null = legacy unassigned (pre-backfill / pre-instance projects); once a
// project has any instance, board_create requires an explicit instance name. Exactly one
// instance per board when set; adopt/move rewrites this column.
[Migration(36, "Add MethodologyInstance to TaskBoards (board membership in a methodology instance)")]
public sealed class M036_TaskBoardMethodologyInstance : Migration
{
	public override void Up() =>
		Create.Column("MethodologyInstance").OnTable("TaskBoards").AsString().Nullable();

	public override void Down() => Delete.Column("MethodologyInstance").FromTable("TaskBoards");
}
