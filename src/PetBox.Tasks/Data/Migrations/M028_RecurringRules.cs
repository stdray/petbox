using FluentMigrator;

namespace PetBox.Tasks.Data.Migrations;

// recurring-card-rule + recurring-card-no-pileup (work recurring-card-rules, idea
// recurring-run-scheduler): a project's rules of repetition — a card template plus a period. A
// plain mutable table keyed by the rule's slug, NOT a temporal node table: a rule has no workflow
// and no history worth versioning (v1 deliberately has no FSM and no UI), closer in shape to
// observation_signal (M023) than to plan_nodes. One row per rule, in the project's tasks file.
//
//   Id          — the rule's slug (TaskSlug rules), unique per project.
//   Board/Type/Title/Body/Tags — the card template. Tags is newline-joined "namespace:value".
//   Period      — day|week|month.
//   WakeTo      — agent|owner (the snooze vocabulary); only owner sets decisionPending on the card.
//   NextDueAt   — when the rule next matures. Advanced by whole periods past `now` on every firing
//                 or skip, so a job that was down for a while fires ONCE, not once per missed day.
//   LastFiredAt — the last time a card was actually created.
//   OpenNodeId  — the card from the last firing; while it is open no new card is created.
//   MissedCount — periods skipped because OpenNodeId was still open (reset when a new card is made).
//   LastError   — why the last firing could not create its card (null = it could). Kept on the row
//                 so tasks_recurring_list shows a broken template instead of it failing in silence.
[Migration(28, "recurring_rules — card templates with a period (recurring-card-rule)")]
public sealed class M028_RecurringRules : Migration
{
	public override void Up()
	{
		Create.Table("recurring_rules")
			.WithColumn("Id").AsString().NotNullable().PrimaryKey()
			.WithColumn("Board").AsString().NotNullable()
			.WithColumn("Type").AsString().NotNullable().WithDefaultValue("")
			.WithColumn("Title").AsString().NotNullable()
			.WithColumn("Body").AsString().NotNullable().WithDefaultValue("")
			.WithColumn("Tags").AsString().NotNullable().WithDefaultValue("")
			.WithColumn("Period").AsString().NotNullable()
			.WithColumn("WakeTo").AsString().NotNullable()
			.WithColumn("NextDueAt").AsString().NotNullable()
			.WithColumn("LastFiredAt").AsString().Nullable()
			.WithColumn("OpenNodeId").AsString().Nullable()
			.WithColumn("MissedCount").AsInt64().NotNullable().WithDefaultValue(0)
			.WithColumn("LastError").AsString().Nullable()
			.WithColumn("CreatedAt").AsString().NotNullable()
			.WithColumn("UpdatedAt").AsString().NotNullable();
	}

	public override void Down() => Delete.Table("recurring_rules");
}
