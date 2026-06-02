using FluentMigrator;

namespace PetBox.Core.Data.Migrations;

// For a work board: the spec board its tasks link into (task_spec). Makes the
// work->spec relationship explicit so specRef targets can be validated and an agent
// need not guess among several spec boards. Null = unset.
[Migration(27, "Add SpecBoard to TaskBoards (work->spec board link)")]
public sealed class M027_TaskBoardSpecBoard : Migration
{
	public override void Up() =>
		Create.Column("SpecBoard").OnTable("TaskBoards").AsString().Nullable();

	public override void Down() => Delete.Column("SpecBoard").FromTable("TaskBoards");
}
