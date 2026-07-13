using PetBox.Core.Auth;
using PetBox.Web.Auth;

namespace PetBox.Web.Pages.Shared;

// View model of _AgentKeysTable — the ONE table+revoke+edit markup shared by the sysadmin
// (/ui/admin/sys/agent-keys) and workspace-admin (/ui/admin/ws/{ws}/agent-keys) key pages.
// The test ids stay per-page (the E2E suite addresses the sysadmin table by name), the markup
// does not fork: the last-used column and the key editor land on BOTH pages by construction.
//
// AllScopes is the canonical catalog (ApiKeyScopes.All) the editor renders as a checkbox group —
// the same source the server validates against, so the form cannot offer a scope the server would
// then reject.
public sealed record AgentKeysTableView(
	IReadOnlyList<AgentKeyRow> Keys,
	string TableTestId,
	string EmptyTestId)
{
	public IReadOnlyList<ApiKeyScope> AllScopes { get; init; } = ApiKeyScopes.All;
}
