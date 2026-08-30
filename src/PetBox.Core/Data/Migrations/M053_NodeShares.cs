using FluentMigrator;

namespace PetBox.Core.Data.Migrations;

// Public links onto a task node (spec `node-share`). See PetBox.Core.Models.NodeShare for the
// column semantics and PetBox.Core.Data.NodeShareDirectory for the door.
//
// ITS OWN TABLE, not a widened ShareLinks: that row's LogName/Kql/ColumnsJson/ModesJson are NOT
// NULL and describe a log export. Making them nullable to fit a second kind of grant would weaken
// the schema of a shipped feature for the benefit of a new one.
//
// ExpiresAt is NULLABLE, and that is the point rather than an oversight (spec
// `node-share-lifetime`): NULL means the link never expires. The index is what the retention sweep
// (RetentionService) uses, and SQLite does not index NULLs for a range predicate anyway — so the
// never-expiring rows are exactly the rows retention cannot reach. The two facts line up on
// purpose.
[Migration(53, "Create node_shares — public links onto a task node")]
public sealed class M053_NodeShares : Migration
{
	public override void Up()
	{
		Create.Table("node_shares")
			.WithColumn("Id").AsString(40).PrimaryKey().NotNullable()
			.WithColumn("ProjectKey").AsString(100).NotNullable()
			.WithColumn("Board").AsString(100).NotNullable()
			.WithColumn("NodeId").AsString(32).NotNullable()
			.WithColumn("CommentId").AsString(32).Nullable()
			.WithColumn("Scope").AsString(16).NotNullable().WithDefaultValue("body")
			.WithColumn("CreatedAt").AsDateTime().NotNullable()
			.WithColumn("CreatedBy").AsString(100).NotNullable().WithDefaultValue("system")
			.WithColumn("ExpiresAt").AsDateTime().Nullable();

		Create.Index("IX_node_shares_ExpiresAt").OnTable("node_shares").OnColumn("ExpiresAt").Ascending();
	}

	public override void Down() => Delete.Table("node_shares");
}
