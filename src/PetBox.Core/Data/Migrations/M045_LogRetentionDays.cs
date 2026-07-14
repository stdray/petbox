using FluentMigrator;

namespace PetBox.Core.Data.Migrations;

// spec log-retention-cascade (amended): retention cascades log -> project -> workspace ->
// system, and a named log MAY carry its own window. NULL is the default for every row this
// migration touches — every log that exists today keeps being swept by the project/system
// cascade exactly as before (RetentionService.cs), which is what makes this a true no-op for
// existing logs.
[Migration(45, "Add Logs.RetentionDays (nullable per-log retention override)")]
public sealed class M045_LogRetentionDays : Migration
{
	public override void Up() =>
		Create.Column("RetentionDays").OnTable("Logs").AsInt32().Nullable();

	public override void Down() => Delete.Column("RetentionDays").FromTable("Logs");
}
