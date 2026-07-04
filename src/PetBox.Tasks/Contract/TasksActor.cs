namespace PetBox.Tasks.Contract;

// WHO is writing, as a CAPABILITY (not an identity): the Tasks module stays
// request-agnostic, so each door translates its own auth into this record — the MCP door
// maps the session key's scopes (tasks:approve => CanApprove), the interactive UI counts
// the cookie-authenticated owner as an approver. Consumed by WorkflowEngine.Validate for
// transitions whose methodology declares EnforceApproval; the builtin presets declare
// none, so an omitted actor (None) changes nothing for existing callers.
public sealed record TasksActor(bool CanApprove)
{
	// The default: an unprivileged agent — approval-gated ENFORCED transitions are blocked.
	public static readonly TasksActor None = new(false);

	// A maintainer-capable actor: a key holding tasks:approve, or the interactive owner.
	public static readonly TasksActor Approver = new(true);
}
