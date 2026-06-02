using FluentMigrator;

namespace PetBox.Core.Data.Migrations;

// Lets a board be closed/archived: ClosedAt null = open. Closed boards reject writes
// (so agents don't keep writing by inertia) but stay readable.
[Migration(26, "Add ClosedAt to TaskBoards (close/archive a board)")]
public sealed class M026_TaskBoardClosedAt : Migration
{
	public override void Up() =>
		Create.Column("ClosedAt").OnTable("TaskBoards").AsDateTime().Nullable();

	public override void Down() => Delete.Column("ClosedAt").FromTable("TaskBoards");
}
