using System.ComponentModel;
using ModelContextProtocol.Server;
using PetBox.Core.Auth;
using PetBox.Core.Features;
using PetBox.Tasks.Contract;

namespace PetBox.Web.Mcp;

// The agent-facing door onto the daily alarm (idea recurring-run-scheduler): rules of repetition
// (spec recurring-card-rule / recurring-card-no-pileup) and the manual trigger of the same pass the
// hosted ScheduledWakeJob runs once a day. Snoozing a node is NOT here — it is a field of the node,
// written through tasks_upsert's `snooze`. A separate class from TasksTools only to keep that file
// from growing; the tenant posture is the same, type-level TenantFrom(projectKey).
[McpServerToolType]
[TenantFrom(TenantSource.Argument, "projectKey")]
public static class TasksScheduleTools
{
	[McpServerTool(Name = "tasks_recurring_upsert", Title = "Create or replace a rule of repetition", UseStructuredContent = true, OutputSchemaType = typeof(RecurringRuleView))]
	[Description("""
		Create or REPLACE (same `id`) a rule of repetition: a card template (board, type, title, body, tags)
		plus a `period` (day|week|month). When the period comes, the daily pass creates the card through the
		ordinary tasks_upsert path (the board's methodology still applies), with a footer naming the rule. While
		the card from the previous firing is still OPEN no second card is made — the missed period is counted
		on the rule (`missedCount`) and noted in a comment on the open card. `wakeTo`: "agent" (default, no
		flag) or "owner" (the card is created with decisionPending:true). `nextDueAt`: when the next card is
		due — omitted = now for a new rule (the next pass fires it), unchanged for an existing one. A rewrite
		keeps the rule's firing state. A template the board refuses shows up as `lastError` on
		tasks_recurring_list. No FSM, no UI. Requires tasks:write.
		""")]
	public static async Task<RecurringRuleView> RecurringUpsertAsync(
		IHttpContextAccessor http, FeatureFlags features, IRecurringRuleService rules,
		string projectKey,
		[Description("The rule's slug (same rules as a node key). Reusing an id replaces that rule's template.")] string id,
		[Description("The board the cards are created on. Must exist.")] string board,
		[Description("Card title.")] string title,
		[Description("day | week | month.")] string period,
		[Description("Card type (e.g. chore on a work board). Omit for a board kind with a single type.")] string? type = null,
		[Description("Card body (GFM markdown). A footer naming the rule is appended.")] string? body = null,
		[Description("Card tags (\"namespace:value\").")] IReadOnlyList<string>? tags = null,
		[Description("\"agent\" (default) or \"owner\" — only \"owner\" puts the card in the owner's decision queue.")] string? wakeTo = null,
		[Description("When the next card is due (ISO-8601, UTC when no offset). Omit: now for a new rule, unchanged for an existing one.")] DateTime? nextDueAt = null,
		CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Tasks);
		ModuleMcp.AssertScope(http, ApiKeyScopes.TasksWrite);
		return await rules.UpsertAsync(projectKey, new RecurringRuleInput
		{
			Id = id,
			Board = board,
			Title = title,
			Period = period,
			Type = type,
			Body = body,
			Tags = tags,
			WakeTo = wakeTo,
			NextDueAt = nextDueAt,
		}, ct);
	}

	[McpServerTool(Name = "tasks_recurring_list", Title = "List rules of repetition", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(RecurringRuleListResult))]
	[Description("List the project's rules of repetition with their firing state: nextDueAt, lastFiredAt, openNodeId (the card from the last firing), missedCount (periods skipped because that card was still open), lastError (why the last firing could not create its card). Requires tasks:read.")]
	public static async Task<RecurringRuleListResult> RecurringListAsync(
		IHttpContextAccessor http, FeatureFlags features, IRecurringRuleService rules,
		string projectKey,
		CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Tasks);
		ModuleMcp.AssertScope(http, ApiKeyScopes.TasksRead);
		return new RecurringRuleListResult(await rules.ListAsync(projectKey, ct));
	}

	[McpServerTool(Name = "tasks_recurring_delete", Title = "Delete a rule of repetition", Destructive = true, UseStructuredContent = true, OutputSchemaType = typeof(RecurringRuleDeletedResult))]
	[Description("Delete a rule of repetition by id. The cards it already created are NOT touched. `deleted:false` = there was no such rule. Requires tasks:write.")]
	public static async Task<RecurringRuleDeletedResult> RecurringDeleteAsync(
		IHttpContextAccessor http, FeatureFlags features, IRecurringRuleService rules,
		string projectKey,
		[Description("The rule's id.")] string id,
		CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Tasks);
		ModuleMcp.AssertScope(http, ApiKeyScopes.TasksWrite);
		return new RecurringRuleDeletedResult(await rules.DeleteAsync(projectKey, id, ct), id);
	}

	[McpServerTool(Name = "tasks_schedule_run", Title = "Run the daily alarm pass now", UseStructuredContent = true, OutputSchemaType = typeof(ScheduledPassReport))]
	[Description("""
		Run, for THIS project and right now, the same pass the server runs once a day: wake every open node
		whose snooze date has come (status untouched; `snooze.wokeAt` stamped; decisionPending set only when
		the snooze named the owner), drop the snooze of nodes that closed before their date, then fire every
		due rule of repetition. Idempotent: a second run the same day changes nothing. Returns what it did.
		Requires tasks:write.
		""")]
	public static async Task<ScheduledPassReport> ScheduleRunAsync(
		IHttpContextAccessor http, FeatureFlags features, IScheduledWakeService schedule,
		string projectKey,
		CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Tasks);
		ModuleMcp.AssertScope(http, ApiKeyScopes.TasksWrite);
		return await schedule.RunProjectAsync(projectKey, ct);
	}
}

public sealed record RecurringRuleListResult(IReadOnlyList<RecurringRuleView> Rules);

public sealed record RecurringRuleDeletedResult(bool Deleted, string Id);
