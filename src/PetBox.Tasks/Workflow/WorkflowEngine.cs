namespace PetBox.Tasks.Workflow;

public sealed record WorkflowResult(bool Ok, string? Error)
{
	public static readonly WorkflowResult Success = new(true, null);
	public static WorkflowResult Fail(string error) => new(false, error);
}

// THE single validation point for task status / transitions. Both "unknown status"
// and "no such transition" are decided here so the rule lives in one place.
//
// `enforceApproval` is OFF by default: the approve gate is modelled (transitions
// carry RequiresApproval; TerminalOk = maintainer-only) but NOT enforced in v1 —
// flip it on at the call site once constraints are clear from practice.
public static class WorkflowEngine
{
	// Catalog-resolved convenience (the historical signature): resolve the workflow from
	// the static preset catalog, then validate. Data-resolved callers use the overload
	// below with a runtime-resolved Workflow.
	public static WorkflowResult Validate(
		BoardKind kind, string? type, string? fromSlug, string toSlug,
		bool enforceApproval = false, bool actorCanApprove = false, bool hasReason = true) =>
		Validate(WorkflowCatalog.For(kind, type), kind.ToString().ToLowerInvariant(), WorkflowCatalog.ValidTypes(kind),
			type, fromSlug, toSlug, enforceApproval, actorCanApprove, hasReason);

	// The resolution-agnostic core: `wf` is the already-resolved state machine (catalog or
	// definition — null = the kind needs a known type), `kindName`/`validTypes` only feed
	// the error messages.
	public static WorkflowResult Validate(
		Workflow? wf, string kindName, string validTypes, string? type, string? fromSlug, string toSlug,
		bool enforceApproval = false, bool actorCanApprove = false, bool hasReason = true)
	{
		// Free boards now carry a real preset workflow (free transitions, fixed status vocab) —
		// they flow through the generic path below like any kind. A legacy out-of-vocab status is
		// tolerated on an unchanged-status edit (from==to) and on recovery (unknown from → fresh
		// start), so pre-migration nodes still read/edit; only setting a NEW invalid status is rejected.

		// Unchanged status: don't re-litigate it. Editing a node's other fields must not fail
		// because its (carried-over) status isn't in this kind's workflow — e.g. a legacy/invalid
		// status left by an older creation path. The status only gets validated when it CHANGES.
		if (fromSlug is not null && string.Equals(fromSlug, toSlug, StringComparison.OrdinalIgnoreCase))
			return WorkflowResult.Success;

		if (wf is null)
			return WorkflowResult.Fail($"board kind '{kindName}' needs a known type ({validTypes}); got '{type}'");

		var to = wf.Status(toSlug);
		if (to is null)
			return WorkflowResult.Fail($"invalid status '{toSlug}' for {kindName}/{wf.Type}; valid: {wf.Slugs()}");

		// Recovery: if the current status isn't known to this workflow (legacy/invalid), treat the
		// move as a fresh start so a stuck node can be brought back to any valid status.
		if (fromSlug is not null && wf.Status(fromSlug) is null)
			fromSlug = null;

		var isChange = fromSlug is not null && !string.Equals(fromSlug, toSlug, StringComparison.OrdinalIgnoreCase);
		if (isChange)
		{
			var tr = wf.Transition(fromSlug!, toSlug);
			if (tr is null)
				return WorkflowResult.Fail($"no transition '{fromSlug}' -> '{toSlug}'; from '{fromSlug}' you can go to: {string.Join("|", wf.NextFrom(fromSlug!))}");
			if (tr.RequiresReason && !hasReason)
				return WorkflowResult.Fail($"transition '{fromSlug}' -> '{toSlug}' requires a reason (non-empty body)");
			if (enforceApproval && tr.RequiresApproval && !actorCanApprove)
				return WorkflowResult.Fail($"transition '{fromSlug}' -> '{toSlug}' requires maintainer approval");
		}
		else if (fromSlug is null && enforceApproval && to.Kind == StatusKind.TerminalOk && !actorCanApprove)
		{
			// creating a node directly in a terminal-ok status is an approval too
			return WorkflowResult.Fail($"only a maintainer can set status '{toSlug}'");
		}

		return WorkflowResult.Success;
	}
}
