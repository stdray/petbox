using FluentMigrator;

namespace PetBox.Core.Data.Migrations;

// Named config tag-filters (chips on the config page). Workspace-scoped.
[Migration(20, "Create SavedConfigFilters")]
public sealed class M020_SavedConfigFilters : Migration
{
	public override void Up()
	{
		Create.Table("SavedConfigFilters")
			.WithColumn("Id").AsInt64().PrimaryKey().Identity()
			.WithColumn("WorkspaceKey").AsString(100).NotNullable()
			.WithColumn("Name").AsString(200).NotNullable()
			.WithColumn("FilterTags").AsString(2000).NotNullable()
			.WithColumn("CreatedAt").AsDateTime().NotNullable();
		Create.Index("IX_SavedConfigFilters_WorkspaceKey")
			.OnTable("SavedConfigFilters")
			.OnColumn("WorkspaceKey").Ascending();
	}

	public override void Down() => Delete.Table("SavedConfigFilters");
}
