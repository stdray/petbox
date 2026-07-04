namespace PetBox.Tasks.Workflow;

public sealed record WorkflowResult(bool Ok, string? Error)
{
	public static readonly WorkflowResult Success = new(true, null);
	public static WorkflowResult Fail(string error) => new(false, error);
}

// THE single validation point for task status / transitions. Both "unknown status"
// and "no such transition" are decided here so the rule lives in one place.
//
// The approve gate has TWO switches: the global `enforceApproval` flag (OFF by default —
// the historical v1 capability, kept for tests/callers that enforce wholesale) and the
// per-transition `EnforceApproval` MODE (schema v2) a methodology declares as data. Either
// one demands `actorCanApprove` (tasks:approve at the MCP door, the cookie-authenticated
// owner in the UI). The builtin presets declare no enforced gate, so preset behavior is
// unchanged; a definition opts in per transition.
public static class WorkflowEngine
{
	// The resolution-agnostic core: `wf` is the already-resolved state machine (preset or
	// definition, via MethodologyRuntime — null = the kind needs a known type),
	// `kindName`/`validTypes` only feed the error messages.
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
			if (tr.RequiresApproval && (enforceApproval || tr.EnforceApproval) && !actorCanApprove)
				return WorkflowResult.Fail($"transition '{fromSlug}' -> '{toSlug}' requires maintainer approval");
		}
		else if (fromSlug is null && !actorCanApprove && GatedAtBirth(wf, to, toSlug, enforceApproval))
		{
			// creating a node directly in an approval-gated status is an approval too
			return WorkflowResult.Fail($"only a maintainer can set status '{toSlug}'");
		}

		return WorkflowResult.Success;
	}

	// Birth into a gated status can't bypass the gate: under the global flag any TerminalOk
	// status is maintainer-only (the historical rule); under per-transition enforcement a
	// status that is the target of an ENFORCED approval transition is too (mirrors the
	// precondition-artifact birth rule in RequirePreconditionArtifactsAsync).
	static bool GatedAtBirth(Workflow wf, WorkflowStatus to, string toSlug, bool enforceApproval) =>
		(enforceApproval && to.Kind == StatusKind.TerminalOk)
		|| wf.Transitions.Any(t => t.RequiresApproval && t.EnforceApproval && string.Equals(t.To, toSlug, StringComparison.OrdinalIgnoreCase));
}
