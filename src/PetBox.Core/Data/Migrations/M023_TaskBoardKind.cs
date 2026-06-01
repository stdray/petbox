using FluentMigrator;

namespace PetBox.Core.Data.Migrations;

[Migration(23, "Add Kind column to TaskBoards (board role: free|spec|ideas|intake|work)")]
public sealed class M023_TaskBoardKind : Migration
{
	public override void Up() =>
		Create.Column("Kind").OnTable("TaskBoards").AsString(20).NotNullable().WithDefaultValue("free");

	public override void Down() => Delete.Column("Kind").FromTable("TaskBoards");
}
