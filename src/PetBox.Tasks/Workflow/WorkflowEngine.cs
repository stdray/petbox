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
	public static WorkflowResult Validate(
		BoardKind kind, string? type, string? fromSlug, string toSlug,
		bool enforceApproval = false, bool actorCanApprove = false, bool hasReason = true)
	{
		if (kind == BoardKind.Free)
			return WorkflowResult.Success; // free boards: any status, no flow

		var wf = WorkflowCatalog.For(kind, type);
		if (wf is null)
			return WorkflowResult.Fail($"board kind '{kind.ToString().ToLowerInvariant()}' needs a known type ({WorkflowCatalog.ValidTypes(kind)}); got '{type}'");

		var to = wf.Status(toSlug);
		if (to is null)
			return WorkflowResult.Fail($"invalid status '{toSlug}' for {kind.ToString().ToLowerInvariant()}/{wf.Type}; valid: {wf.Slugs()}");

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
