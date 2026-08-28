using FluentMigrator;

namespace PetBox.Core.Data.Migrations;

// The one-shot body transport (work/write-body-by-reference, spec
// no-retransmission-of-existing-content). See PetBox.Core.Models.BodyRefBlob for the column
// semantics and PetBox.Core.Contract.BodyRefs for why this is a table rather than a volume.
//
// TWO indexes, and neither is speculative:
//   * ExpiresAt — the prune job's only predicate. Without it the sweep is a full scan on every
//     background tick, forever, over a table whose whole point is to be written to constantly.
//   * (Ref, ProjectKey) — the substitution lookup matches on BOTH together (the tenant is part of
//     the ADDRESS, not a courtesy filter — the same confinement ShareLinkDirectory.DeleteAsync
//     uses), so the composite is what that query actually needs. Ref alone is already the PK, so
//     this index exists for the second column, not the first.
[Migration(52, "Create BodyRefBlobs — one-shot uploaded bodies referenced by bodyRef")]
public sealed class M052_BodyRefBlobs : Migration
{
	public override void Up()
	{
		Create.Table("BodyRefBlobs")
			.WithColumn("Ref").AsString(64).PrimaryKey().NotNullable()
			.WithColumn("ProjectKey").AsString(100).NotNullable()
			.WithColumn("Body").AsString(int.MaxValue).NotNullable()
			.WithColumn("Bytes").AsInt64().NotNullable().WithDefaultValue(0)
			.WithColumn("CreatedAt").AsDateTime().NotNullable()
			.WithColumn("ExpiresAt").AsDateTime().NotNullable()
			.WithColumn("CreatedBy").AsString(200).NotNullable().WithDefaultValue("");

		Create.Index("IX_BodyRefBlobs_ExpiresAt").OnTable("BodyRefBlobs").OnColumn("ExpiresAt").Ascending();
		Create.Index("IX_BodyRefBlobs_RefProject").OnTable("BodyRefBlobs")
			.OnColumn("Ref").Ascending()
			.OnColumn("ProjectKey").Ascending();
	}

	public override void Down() => Delete.Table("BodyRefBlobs");
}
