namespace YobaBox.Web;

// Centralized URL templates for the YobaBox UI.
// Source of truth — keep in sync with @page directives in Pages/.
// Use these instead of hard-coded strings or asp-page name-based linking where practical.
public static class Routes
{
	public const string UiPrefix = "/ui";

	public static string Home() => "/";

	// System level (sysadmin)
	public static string Sys() => $"{UiPrefix}/sys";
	public static string SysWorkspaces() => $"{UiPrefix}/sys/workspaces";
	public static string SysWorkspace(string key) => $"{UiPrefix}/sys/workspaces/{key}";
	public static string SysUsers() => $"{UiPrefix}/sys/users";
	public static string SysRetention() => $"{UiPrefix}/sys/retention";

	// Workspace level
	public static string Workspace(string ws) => $"{UiPrefix}/{ws}";
	public static string WorkspaceLogs(string ws) => $"{UiPrefix}/{ws}/logs";
	public static string WorkspaceTraces(string ws) => $"{UiPrefix}/{ws}/traces";
	public static string WorkspaceTasks(string ws) => $"{UiPrefix}/{ws}/tasks";

	public static string SharedConfig(string ws) => $"{UiPrefix}/{ws}/config";
	public static string SharedConfigEditor(string ws) => $"{UiPrefix}/{ws}/config/editor";
	public static string SharedConfigEditor(string ws, long bindingId) => $"{UiPrefix}/{ws}/config/editor/{bindingId}";
	public static string SharedConfigHistory(string ws) => $"{UiPrefix}/{ws}/config/history";
	public static string SharedConfigPreview(string ws) => $"{UiPrefix}/{ws}/config/preview";
	public static string SharedConfigTags(string ws) => $"{UiPrefix}/{ws}/config/tags";

	public static string WorkspaceAdmin(string ws) => $"{UiPrefix}/{ws}/admin";
	public static string WorkspaceAdminMembers(string ws) => $"{UiPrefix}/{ws}/admin/members";
	public static string WorkspaceAdminProjects(string ws) => $"{UiPrefix}/{ws}/admin/projects";
	public static string WorkspaceAdminSettings(string ws) => $"{UiPrefix}/{ws}/admin/settings";

	// Project level — /ui/{ws}/{key} IS the Logs view directly (no redirect).
	public static string Project(string ws, string key) => $"{UiPrefix}/{ws}/{key}";
	public static string ProjectLogs(string ws, string key) => Project(ws, key);
	public static string ProjectLogsForService(string ws, string key, string serviceKey) => $"{Project(ws, key)}?service={serviceKey}";
	public static string ProjectTraces(string ws, string key) => $"{Project(ws, key)}/traces";
	public static string ProjectTrace(string ws, string key, string traceId) => $"{Project(ws, key)}/traces/{traceId}";

	public static string ProjectConfig(string ws, string key) => $"{Project(ws, key)}/config";
	public static string ProjectConfigEditor(string ws, string key) => $"{Project(ws, key)}/config/editor";
	public static string ProjectConfigEditor(string ws, string key, long bindingId) => $"{Project(ws, key)}/config/editor/{bindingId}";
	public static string ProjectConfigHistory(string ws, string key) => $"{Project(ws, key)}/config/history";
	public static string ProjectConfigPreview(string ws, string key) => $"{Project(ws, key)}/config/preview";

	public static string ProjectData(string ws, string key) => $"{Project(ws, key)}/data";
	public static string ProjectSettings(string ws, string key) => $"{Project(ws, key)}/settings";

	public static string Service(string ws, string key, string serviceKey) => $"{Project(ws, key)}/services/{serviceKey}";

	// Auth & misc — not under /ui prefix
	public static string Login() => "/Login";
	public static string Login(string returnUrl) => $"/Login?returnUrl={Uri.EscapeDataString(returnUrl)}";
	public static string Logout() => "/api/auth/logout";
	public static string Error() => "/Error";
	public static string Share(string token) => $"/s/{token}";
}
