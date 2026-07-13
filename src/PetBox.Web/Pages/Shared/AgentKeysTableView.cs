using PetBox.Web.Auth;

namespace PetBox.Web.Pages.Shared;

// View model of _AgentKeysTable — the ONE table+revoke-form markup shared by the sysadmin
// (/ui/admin/sys/agent-keys) and workspace-admin (/ui/admin/ws/{ws}/agent-keys) key pages.
// The test ids stay per-page (the E2E suite addresses the sysadmin table by name), the markup
// does not fork.
public sealed record AgentKeysTableView(
	IReadOnlyList<AgentKeyRow> Keys,
	string TableTestId,
	string EmptyTestId);
