using FluentMigrator;

namespace PetBox.Core.Data.Migrations;

// Metadata about per-project named memory stores. Entries live in
// `data/memory/{ProjectKey}/{Name}.db` temporal files; this table tracks which
// stores exist. Mirrors Logs (M016). v1 project-scoped only.
[Migration(22, "Create MemoryStores metadata table for the Memory module")]
public sealed class M022_MemoryStores : Migration
{
	public override void Up()
	{
		Create.Table("MemoryStores")
			.WithColumn("ProjectKey").AsString(100).NotNullable().PrimaryKey("PK_MemoryStores")
			.WithColumn("Name").AsString(100).NotNullable().PrimaryKey("PK_MemoryStores")
			.WithColumn("Description").AsString(1000).Nullable()
			.WithColumn("CreatedAt").AsDateTime().NotNullable()
			.WithColumn("UpdatedAt").AsDateTime().NotNullable();
	}

	public override void Down() => Delete.Table("MemoryStores");
}
