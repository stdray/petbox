namespace PetBox.Web;

// Centralized URL templates for the PetBox UI.
// Source of truth — keep in sync with @page directives in Pages/.
// Use these instead of hard-coded strings or asp-page name-based linking where practical.
public static class Routes
{
	public const string UiPrefix = "/ui";

	public static string Home() => "/";

	// All admin pages live under /ui/admin/ — workspace admin under ws/{ws}/...,
	// sysadmin under sys/.... Phase 24 unification. Account pages stay at /ui/me/*
	// with their own _AccountLayout.
	public const string AdminPrefix = $"{UiPrefix}/admin";

	// System level (sysadmin)
	public static string Sys() => $"{AdminPrefix}/sys";
	public static string SysWorkspaces() => $"{AdminPrefix}/sys/workspaces";
	public static string SysWorkspace(string key) => $"{AdminPrefix}/sys/workspaces/{key}";
	public static string SysUsers() => $"{AdminPrefix}/sys/users";
	public static string SysAgentKeys() => $"{AdminPrefix}/sys/agent-keys";
	public static string SysDefaults() => $"{AdminPrefix}/sys/defaults";

	// Workspace level
	public static string Workspace(string ws) => $"{UiPrefix}/{ws}";
	public static string WorkspaceTasks(string ws) => $"{UiPrefix}/{ws}/tasks";

	public static string SharedConfig(string ws) => $"{UiPrefix}/{ws}/config";
	public static string SharedConfigEditor(string ws) => $"{UiPrefix}/{ws}/config/editor";
	public static string SharedConfigEditor(string ws, long bindingId) => $"{UiPrefix}/{ws}/config/editor/{bindingId}";
	public static string SharedConfigHistory(string ws) => $"{UiPrefix}/{ws}/config/history";
	public static string SharedConfigPreview(string ws) => $"{UiPrefix}/{ws}/config/preview";
	public static string SharedConfigTags(string ws) => $"{UiPrefix}/{ws}/config/tags";

	public static string WorkspaceAdmin(string ws) => $"{AdminPrefix}/ws/{ws}";
	public static string WorkspaceAdminMembers(string ws) => $"{AdminPrefix}/ws/{ws}/members";
	public static string WorkspaceAdminProjects(string ws) => $"{AdminPrefix}/ws/{ws}/projects";
	public static string WorkspaceAdminInfo(string ws) => $"{AdminPrefix}/ws/{ws}/info";
	public static string WorkspaceAdminDefaults(string ws) => $"{AdminPrefix}/ws/{ws}/defaults";

	// Project level — /ui/{ws}/{key} IS the Logs view directly (no redirect).
	// A project has many named logs; /ui/{ws}/{key}/logs/{log} views a specific one,
	// the bare project URL picks the default/first log.
	public static string Project(string ws, string key) => $"{UiPrefix}/{ws}/{key}";          // project dashboard
	public static string ProjectLogs(string ws, string key) => $"{Project(ws, key)}/logs";     // logs dashboard
	public static string ProjectLog(string ws, string key, string log) => $"{Project(ws, key)}/logs/{log}";
	public static string ProjectDatabases(string ws, string key) => $"{Project(ws, key)}/databases";
	public static string ProjectDatabase(string ws, string key, string db) => $"{Project(ws, key)}/databases/{db}";
	public static string ProjectTable(string ws, string key, string db, string table) => $"{Project(ws, key)}/databases/{db}/{table}";
	public static string ProjectTraces(string ws, string key) => $"{Project(ws, key)}/traces";
	public static string ProjectTrace(string ws, string key, string traceId) => $"{Project(ws, key)}/traces/{traceId}";

	// Tasks / Memory / Sessions — read-only views over the per-container temporal
	// stores. A project has many named boards/stores; the bare URL lists them and
	// /{board|store} views one. Sessions have no catalog (one file per project).
	public static string ProjectTasks(string ws, string key) => $"{Project(ws, key)}/tasks";
	public static string ProjectTaskBoard(string ws, string key, string board) => $"{Project(ws, key)}/tasks/{board}";
	// One node of a task board. The CANONICAL address is the human-readable slug path
	// /tasks/{board}/{slug} (TaskBoardNodeBySlug) — what the UI links to and include_url emits.
	// The opaque /tasks/node/{nodeId} form (TaskBoardNode) stays resolvable as a stable alias
	// (the slug changes on rename, the nodeId never does). Both hit the TaskBoardNode page
	// (named so it doesn't shadow the PlanNode data record); `node` is a reserved board name so
	// the literal-segment route can't collide with a board called "node".
	public static string TaskBoardNodeBySlug(string ws, string key, string board, string slug) => $"{ProjectTaskBoard(ws, key, board)}/{slug}";
	public static string TaskBoardNode(string ws, string key, string nodeId) => $"{ProjectTasks(ws, key)}/node/{nodeId}";
	public static string ProjectMemory(string ws, string key) => $"{Project(ws, key)}/memory";
	public static string ProjectMemoryStore(string ws, string key, string store) => $"{Project(ws, key)}/memory/{store}";
	public static string ProjectSessions(string ws, string key) => $"{Project(ws, key)}/sessions";
	public static string ProjectSession(string ws, string key, string sessionId) => $"{Project(ws, key)}/sessions/{sessionId}";

	public static string ProjectConfig(string ws, string key) => $"{Project(ws, key)}/config";
	public static string ProjectConfigEditor(string ws, string key) => $"{Project(ws, key)}/config/editor";
	public static string ProjectConfigEditor(string ws, string key, long bindingId) => $"{Project(ws, key)}/config/editor/{bindingId}";
	public static string ProjectConfigHistory(string ws, string key) => $"{Project(ws, key)}/config/history";
	public static string ProjectConfigPreview(string ws, string key) => $"{Project(ws, key)}/config/preview";

	// LLM router provider/key admin page (workspace-scoped registry, project-scoped URL).
	public static string ProjectLlmRouter(string ws, string key) => $"{Project(ws, key)}/llm";

	// Project admin pages live under /ui/admin/ws/{ws}/projects/{key}/...
	public static string ProjectData(string ws, string key) => $"{AdminPrefix}/ws/{ws}/projects/{key}/data";
	public static string ProjectLogsAdmin(string ws, string key) => $"{AdminPrefix}/ws/{ws}/projects/{key}/logs";
	public static string ProjectTasksAdmin(string ws, string key) => $"{AdminPrefix}/ws/{ws}/projects/{key}/tasks";
	public static string ProjectMemoryAdmin(string ws, string key) => $"{AdminPrefix}/ws/{ws}/projects/{key}/memory";
	public static string ProjectConnect(string ws, string key) => $"{AdminPrefix}/ws/{ws}/projects/{key}/connect";
	public static string ProjectSettings(string ws, string key) => $"{AdminPrefix}/ws/{ws}/projects/{key}/info";
	public static string ProjectLogSettings(string ws, string key) => $"{AdminPrefix}/ws/{ws}/projects/{key}/log";


	// Account / self-service — separate _AccountLayout, deliberately NOT under /ui/admin/.
	public static string MeProfile() => $"{UiPrefix}/me/account";
	public static string MeSecurity() => $"{UiPrefix}/me/security";
	public static string MePreferences() => $"{UiPrefix}/me/preferences";

	// Public documentation — no auth, so agents can fetch it by URL with no cookie/key.
	public static string Doc() => "/doc";
	public static string DocOverview() => "/doc/overview";
	public static string DocOnboarding() => "/doc/onboarding";
	public static string DocAgent() => "/doc/agent";
	public static string DocMethodology() => "/doc/methodology";
	public static string DocPhilosophy() => "/doc/methodology/philosophy";

	// Auth & misc — not under /ui prefix
	public static string Login() => "/Login";
	public static string Login(string returnUrl) => $"/Login?returnUrl={Uri.EscapeDataString(returnUrl)}";
	public static string Logout() => "/api/auth/logout";
	public static string Error() => "/Error";
	public static string Share(string token) => $"/s/{token}";
}
