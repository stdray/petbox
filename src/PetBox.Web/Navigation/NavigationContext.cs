using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using PetBox.Core.Auth;
using PetBox.Core.Features;
using PetBox.Core.Models;
using PetBox.Web.Auth;

namespace PetBox.Web.Navigation;

public interface INavigationContext
{
	bool IsAuthenticated { get; }
	string? Username { get; }
	// NULL when the signed-in user belongs to no workspace at all (a fresh Regular account with
	// no membership). It used to fall back to "$system", which handed a non-member a workspace
	// they had no right to and neutralised the dashboard's not-found guard — see
	// workspace-access-isolation. Callers that need a rendered link must check HasWorkspace.
	string? CurrentWorkspaceKey { get; }
	bool HasWorkspace { get; }
	string? CurrentProjectKey { get; }
	IReadOnlyList<WorkspaceOption> AvailableWorkspaces { get; }
	IReadOnlyList<Project> ProjectsInCurrentWorkspace { get; }
	IReadOnlyDictionary<string, IReadOnlyList<Project>> ProjectsByWorkspace { get; }
	bool DataEnabled { get; }
	bool TasksEnabled { get; }
	bool MemoryEnabled { get; }
	bool LlmRouterEnabled { get; }
}

public sealed record WorkspaceOption(string Key, string Name);

// The sidebar's view of the catalog. It sees NO database: the workspace list comes from
// IWorkspaceAdminService, the project tree from IProjectDirectory, the memberships from
// IWorkspaceMembershipService (AGENTS.md — the database is visible only in the service layer). This
// type is resolved during LAYOUT render, i.e. on every single page, which is why it was the worst
// offender: it used to open core.db 3-4 times per request of its own.
//
// It is now TWO opens on a rendered page, and the shape is what keeps it there:
//   * the project tree is fetched ONCE, grouped by workspace (IProjectDirectory.ListByWorkspaceAsync),
//     and both ProjectsInCurrentWorkspace and the route-project→workspace resolution are SLICES of
//     that one read rather than reads of their own;
//   * memberships are read from the yb:ws_roles claim, which WorkspaceClaimsRefresher rebuilds from
//     WorkspaceMembers on every authenticated request — so the row is already in memory and reading
//     it again would be the third open (see MembershipKeys for the fallback that keeps this an
//     optimisation and not a correctness dependency).
//
// Every memoised member still holds a RESULT, never a connection: the services open and close their
// own inside each call. Holding a DataConnection as a field here would be especially fatal — the nav
// context renders AFTER the handler, so a shared connection the handler disposed would be dead
// (ObjectDisposedException on every page that fanned out).
public sealed class NavigationContext(
	IHttpContextAccessor accessor,
	IProjectDirectory projects,
	IWorkspaceAdminService workspaces,
	IWorkspaceMembershipService memberships,
	FeatureFlags features) : INavigationContext
{
	const string WorkspaceCookie = "yb_ws";
	const string ProjectCookie = "yb_project";

	IReadOnlyList<WorkspaceOption>? _workspaces;
	IReadOnlyList<Project>? _projects;
	IReadOnlyDictionary<string, IReadOnlyList<Project>>? _projectsByWs;
	string? _resolvedWorkspace;
	bool _workspaceResolved;
	string? _resolvedProject;
	bool _projectResolved;

	HttpContext? Http => accessor.HttpContext;

	// INavigationContext is consumed from Razor LAYOUTS as properties (@Nav.AvailableWorkspaces), so
	// it cannot go async without rewriting every layout and partial that reads it. The services are
	// async, so the two meet here. This blocks the request thread exactly as much as the synchronous
	// LinqToDB reads it replaces did — and on strictly FEWER calls — so it is not a new cost; ASP.NET
	// Core installs no SynchronizationContext, so it cannot deadlock either.
	static T Sync<T>(Task<T> task) => task.GetAwaiter().GetResult();

	public bool IsAuthenticated => Http?.User.Identity?.IsAuthenticated == true;
	public string? Username => Http?.User.Identity?.Name;
	public bool DataEnabled => features.IsEnabled(Feature.Data);
	// Sessions ship with the Tasks module — gated on the same flag (see SessionTools).
	public bool TasksEnabled => features.IsEnabled(Feature.Tasks);
	public bool MemoryEnabled => features.IsEnabled(Feature.Memory);
	public bool LlmRouterEnabled => features.IsEnabled(Feature.LlmRouter);

	public string? CurrentWorkspaceKey
	{
		get
		{
			if (_workspaceResolved) return _resolvedWorkspace;
			_resolvedWorkspace = ResolveWorkspace();
			_workspaceResolved = true;
			return _resolvedWorkspace;
		}
	}

	public bool HasWorkspace => CurrentWorkspaceKey is not null;

	// Resolution order (mirrors ResolveWorkspace): explicit URL segment → yb_project cookie
	// (validated against the current workspace) → first available project. The cookie fallback
	// lets the sidebar's project selector stay populated on pages that carry no project in the
	// URL (workspace Status, Shared config, etc.). Returns null only when the workspace has no
	// projects at all.
	public string? CurrentProjectKey
	{
		get
		{
			if (_projectResolved) return _resolvedProject;
			_resolvedProject = ResolveProject();
			_projectResolved = true;
			return _resolvedProject;
		}
	}

	string? ResolveProject()
	{
		// 1. Explicit URL segment wins (page scoped to a concrete project).
		var fromProjectKey = Http?.GetRouteValue("projectKey")?.ToString();
		if (!string.IsNullOrEmpty(fromProjectKey)) return fromProjectKey;
		var fromKey = Http?.GetRouteValue("key")?.ToString();
		if (!string.IsNullOrEmpty(fromKey) && IsProjectRoute()) return fromKey;

		if (!IsAuthenticated) return null;

		var projectsHere = ProjectsInCurrentWorkspace;
		if (projectsHere.Count == 0) return null;

		// 2. Cookie — only honoured if the project actually lives in the current workspace,
		//    otherwise a stale cross-workspace value would point at a phantom section list.
		if (Http?.Request.Cookies.TryGetValue(ProjectCookie, out var cookieProj) == true
			&& !string.IsNullOrEmpty(cookieProj))
		{
			foreach (var p in projectsHere)
				if (string.Equals(p.Key, cookieProj, StringComparison.Ordinal))
					return cookieProj;
		}

		// 3. First available project.
		return projectsHere[0].Key;
	}

	bool IsProjectRoute() => Http?.GetRouteValue("projectKey") is not null;

	public IReadOnlyList<WorkspaceOption> AvailableWorkspaces
	{
		get
		{
			if (_workspaces is not null) return _workspaces;
			if (!IsAuthenticated)
				return _workspaces = [];

			// ONE read of the catalog (ordered by key, as the selector renders it); the membership
			// filter is applied to it in memory. The workspace table is an operator-sized list — the
			// filter that used to run in SQL cost a second query on the same connection, and there is
			// no service door for "the workspaces of this user" to replace it with.
			var all = Sync(workspaces.ListAsync());

			// Sysadmin sees everything regardless of membership.
			var isSysAdmin = Http!.User.HasClaim(PetBoxClaims.IsSysAdmin, "true");
			var userIdRaw = Http!.User.FindFirst(PetBoxClaims.UserId)?.Value;

			// Second arm: a legacy admin with no User row yet — no identity to filter BY, so it keeps
			// its historical free pass (show everything) rather than an empty sidebar.
			if (isSysAdmin || !long.TryParse(userIdRaw, out var userId))
				return _workspaces = [.. all.Select(w => new WorkspaceOption(w.Key, w.Name))];

			var memberKeys = MembershipKeys(userId);
			return _workspaces =
			[
				.. all
					.Where(w => memberKeys.Contains(w.Key))
					.Select(w => new WorkspaceOption(w.Key, w.Name)),
			];
		}
	}

	// The workspaces this account belongs to — WITHOUT reading WorkspaceMembers, in the normal case.
	//
	// yb:ws_roles is not a sign-in snapshot: WorkspaceClaimsRefresher (an IClaimsTransformation)
	// rebuilds it from the WorkspaceMembers table on EVERY authenticated request, and the authorization
	// pipeline already treats it as the current truth (WorkspaceRoleRequirement decides who may enter a
	// workspace from this same claim). A navigation list read from it is therefore exactly as fresh as
	// the guard on the page it links to — and it costs no core.db open.
	//
	// An ABSENT or empty claim is ambiguous — "no memberships" and "the refresher did not run for this
	// identity" (a non-cookie scheme) look identical — so that case, and only that case, asks the
	// service. Which keeps the claim a pure optimisation: if it is not there, the database still is.
	HashSet<string> MembershipKeys(long userId)
	{
		var claim = Http!.User.FindFirst(PetBoxClaims.WorkspaceRoles)?.Value;
		if (!string.IsNullOrEmpty(claim))
		{
			// The "ws=Role,ws=Role" wire format is owned by WorkspaceRoleAuthorizationHandler
			// .SerializeRoles; only the KEYS are wanted here. NavigationContextTests round-trips a
			// claim built by that serializer, so the two cannot drift apart unnoticed.
			var keys = new HashSet<string>(StringComparer.Ordinal);
			foreach (var pair in claim.Split(',', StringSplitOptions.RemoveEmptyEntries))
			{
				var eq = pair.IndexOf('=', StringComparison.Ordinal);
				if (eq > 0) keys.Add(pair[..eq]);
			}
			if (keys.Count > 0) return keys;
		}

		return [.. Sync(memberships.GetRolesAsync(userId)).Select(m => m.WorkspaceKey)];
	}

	// Sliced from the one grouped read below — never a query of its own. The current workspace is by
	// construction one the user may see (ResolveWorkspace returns nothing else), so it is a key of that
	// dictionary; the empty list is the answer for a workspace with no user projects.
	public IReadOnlyList<Project> ProjectsInCurrentWorkspace
	{
		get
		{
			if (_projects is not null) return _projects;
			var wsKey = CurrentWorkspaceKey;
			if (wsKey is null) return _projects = [];
			return _projects = ProjectsByWorkspace.TryGetValue(wsKey, out var list) ? list : [];
		}
	}

	// The whole project tree of every workspace the user can see, in ONE read. Workspace memory
	// containers ($workspace / $ws-*) are not user projects — they have no logs/dbs/tasks — and
	// IProjectDirectory drops them by default, which is the one definition of that rule now.
	public IReadOnlyDictionary<string, IReadOnlyList<Project>> ProjectsByWorkspace =>
		_projectsByWs ??= Sync(projects.ListByWorkspaceAsync(
			[.. AvailableWorkspaces.Select(w => w.Key)]));

	// Never invents a workspace: a user with no membership resolves to null (empty state), not
	// to "$system" — the fallback that let a fresh account land on someone else's dashboard.
	string? ResolveWorkspace()
	{
		// 1. Route param wins (page explicitly scoped to a workspace)
		var routeWs = Http?.GetRouteValue("workspaceKey")?.ToString();
		if (!string.IsNullOrEmpty(routeWs) && IsMember(routeWs))
			return routeWs;

		// 2. Project route → resolve from project's workspace
		var routeProject = Http?.GetRouteValue("projectKey")?.ToString()
			?? (IsProjectRoute() ? Http?.GetRouteValue("key")?.ToString() : null);
		if (!string.IsNullOrEmpty(routeProject))
		{
			// The tree the sidebar needs anyway already answers this for every project the user can
			// see — and a hit is, BY CONSTRUCTION, in a workspace they are a member of (the tree is
			// built from AvailableWorkspaces), so the membership test is not skipped, it is implied.
			foreach (var (wsKey, list) in ProjectsByWorkspace)
				foreach (var p in list)
					if (string.Equals(p.Key, routeProject, StringComparison.Ordinal))
						return wsKey;

			// A miss is one of three things: no such project, a project of a workspace this user
			// cannot see, or a workspace memory CONTAINER — which the tree deliberately omits but
			// which DOES have routes (/ui/{ws}/$ws-{ws}/memory). Only the container is a real answer,
			// so the cold path asks the directory — one open, exactly as before, and never on a
			// normal project page.
			var project = Sync(projects.GetAsync(routeProject));
			if (project is not null && IsMember(project.WorkspaceKey))
				return project.WorkspaceKey;
		}

		// 3. Cookie
		if (Http?.Request.Cookies.TryGetValue(WorkspaceCookie, out var cookieWs) == true
			&& !string.IsNullOrEmpty(cookieWs) && IsMember(cookieWs))
			return cookieWs;

		// 4. Active-workspace claim from login
		var claimWs = Http?.User.FindFirst(PetBoxClaims.ActiveWorkspace)?.Value;
		if (!string.IsNullOrEmpty(claimWs) && IsMember(claimWs))
			return claimWs;

		// 5. First available membership — or none at all.
		var available = AvailableWorkspaces;
		return available.Count > 0 ? available[0].Key : null;
	}

	bool IsMember(string wsKey)
	{
		if (!IsAuthenticated) return false;
		// Free-pass for legacy admin without claims (AvailableWorkspaces falls back to all)
		foreach (var w in AvailableWorkspaces)
			if (string.Equals(w.Key, wsKey, StringComparison.Ordinal))
				return true;
		return false;
	}
}
