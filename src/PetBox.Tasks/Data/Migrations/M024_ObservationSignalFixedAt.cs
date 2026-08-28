using FluentMigrator;

namespace PetBox.Tasks.Data.Migrations;

// FixedAt — the timestamp counterpart of M023's FixedByNodeId (work
// observation-edges-promote-and-nail): the moment a linked obligation's terminal-OK status
// automatically fixed this observation. FixedByNodeId alone names WHO closed it; this
// migration adds WHEN. Both are stamped together (TasksService's automatic obligation-status
// sync) and both stay null on an observation that has never been fixed.
[Migration(24, "observation_signal.FixedAt — timestamp of the automatic fix")]
public sealed class M024_ObservationSignalFixedAt : Migration
{
	public override void Up() =>
		Alter.Table("observation_signal").AddColumn("FixedAt").AsString().Nullable();

	public override void Down() =>
		Delete.Column("FixedAt").FromTable("observation_signal");
}
