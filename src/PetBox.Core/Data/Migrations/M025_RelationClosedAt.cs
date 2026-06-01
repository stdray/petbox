using FluentMigrator;

namespace PetBox.Core.Data.Migrations;

// Makes relations interval-temporal: ClosedAt null = active, set = retired.
// relations.delete and the unblock/auto-close effects soft-close (keep history)
// instead of hard-deleting.
[Migration(25, "Add ClosedAt to Relation (interval-temporal soft-close)")]
public sealed class M025_RelationClosedAt : Migration
{
	public override void Up() =>
		Create.Column("ClosedAt").OnTable("Relation").AsDateTime().Nullable();

	public override void Down() => Delete.Column("ClosedAt").FromTable("Relation");
}
