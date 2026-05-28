using FluentMigrator;

namespace YobaBox.Core.Data.Migrations;

// Metadata about per-project user-data SQLite databases. The actual data lives
// in `data/db/{ProjectKey}/{Name}.db` files; this table tracks which DBs exist,
// who created them, and the per-DB page-count quota (~1GB default at 4KB pages).
//
// Note: the older `DataTables` table (M005, currently disabled feature) is left
// in place — its data is empty and it will be dropped in a future cosmetic
// migration. Removing it together with this one risks breaking unrelated tests
// that still touch the YobaBoxDb mapping.
[Migration(13, "Create DataDbs metadata table for Data module")]
public sealed class M013_DataDbs : Migration
{
	public override void Up()
	{
		Create.Table("DataDbs")
			.WithColumn("ProjectKey").AsString(100).NotNullable().PrimaryKey("PK_DataDbs")
			.WithColumn("Name").AsString(100).NotNullable().PrimaryKey("PK_DataDbs")
			.WithColumn("Description").AsString(1000).Nullable()
			.WithColumn("MaxPageCount").AsInt64().NotNullable()
			.WithColumn("CreatedAt").AsDateTime().NotNullable()
			.WithColumn("UpdatedAt").AsDateTime().NotNullable();
	}

	public override void Down() => Delete.Table("DataDbs");
}
