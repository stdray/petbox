using FluentMigrator;

namespace PetBox.Core.Data.Migrations;

// Share links now point at a specific named log within a project, not the whole
// project. Existing rows default to the conventional `default` log.
[Migration(17, "Add LogName column to ShareLinks")]
public sealed class M017_ShareLinkLogName : Migration
{
	public override void Up()
	{
		Alter.Table("ShareLinks")
			.AddColumn("LogName").AsString(100).NotNullable().WithDefaultValue("default");
	}

	public override void Down() => Delete.Column("LogName").FromTable("ShareLinks");
}
