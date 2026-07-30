using PetBox.Core.Auth;
using PetBox.Web.Auth;

namespace PetBox.Web.Pages.Shared;

// View model of _AgentKeysTable — the ONE table+revoke+edit markup shared by the sysadmin
// (/ui/admin/sys/agent-keys) and workspace-admin (/ui/admin/ws/{ws}/agent-keys) key pages.
// The test ids stay per-page (the E2E suite addresses the sysadmin table by name), the markup
// does not fork: the last-used column and the key editor land on BOTH pages by construction.
//
// AllScopes is the catalog subset the editor renders as a checkbox group — the same source the
// server validates against, so the form cannot offer a scope the server would then reject.
//
// IT IS NO LONGER `ApiKeyScopes.All` UNCONDITIONALLY (work
// workspaceadmin-self-issue-admin-provision-root). This ONE view record feeds both key pages, and
// the workspace-admin one used to render `admin:provision` as a checkbox indistinguishable from
// `memory:read` — so the shared markup handed a tenant admin a root-equivalent affordance. The fix
// is a PARAMETER, not a fork: forking the markup for the sysadmin page is exactly how the workspace
// page drifted into a lesser surface last time (see _AgentKeysTable's own header). The page supplies
// the set its caller may actually grant; the markup keeps not knowing who is looking.
//
// Cosmetics, and the emphasis is deliberate: an edit is addressed by key VALUE off a form, so what
// this record chooses to render was never the guard. AgentKeyAdminService's grant gate is.
public sealed record AgentKeysTableView(
	IReadOnlyList<AgentKeyRow> Keys,
	string TableTestId,
	string EmptyTestId)
{
	// Defaults to the tenant-confined subset, so a page that forgets to pass one cannot accidentally
	// re-open the hole — the safe direction is the one you get by omission.
	public IReadOnlyList<ApiKeyScope> AllScopes { get; init; } =
		ApiKeyScopes.GrantableBy(mayGrantPrivileged: false);
}
