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

	IReadOnlyList<WorkspaceOption>? _catalog;
	IReadOnlyList<WorkspaceOption>? _membership;
	IReadOnlyList<WorkspaceOption>? _reachable;
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

	// THE ZONE. /ui/admin/* is the administrative zone; everything else is the caller's own zone.
	// This is the SAME path test both shared selectors already make (they use it to keep a workspace
	// switch inside the zone it was made in), and it is exhaustive: every page rendering _AdminSidebar
	// declares an "/ui/admin/..." route in its own @page directive.
	//
	// It lives HERE rather than in the partials on purpose. _WorkspaceSelector and _ProjectSelector are
	// SHARED by both zones, so "which tenants does this list show" cannot be a property of the partial —
	// it is a property of the REQUEST. Deciding it once, at the source of the enumeration, is what makes
	// it impossible for a consumer to pick the wrong list: a page under /ui gets the membership list
	// without asking for it, a page under /ui/admin gets the reachable one, and a NEW page is
	// fail-safe either way (the narrow list is the default, the wide one needs the admin route).
	bool IsAdminZone =>
		Http?.Request.Path.Value?.StartsWith("/ui/admin", StringComparison.OrdinalIgnoreCase) == true;

	// ONE read of the catalog per request (ordered by key, as the selector renders it); both lists
	// below are in-memory filters of it. The workspace table is an operator-sized list — the filter
	// that used to run in SQL cost a second query on the same connection, and there is no service
	// door for "the workspaces of this user" to replace it with.
	IReadOnlyList<WorkspaceOption> Catalog =>
		_catalog ??= [.. Sync(workspaces.ListAsync()).Select(w => new WorkspaceOption(w.Key, w.Name))];

	// VISIBILITY — the tenants the caller's OWN zone shows them: the ones they are a member of, and
	// nothing else. Spec tenant-visibility-by-membership: this list is decided by MEMBERSHIP and never
	// by the system permission, so a sysadmin who is not a member of W does not find W here. That is
	// not a denial — the right to reach W is a different list (ReachableWorkspaces) and is unchanged.
	IReadOnlyList<WorkspaceOption> MembershipWorkspaces
	{
		get
		{
			if (_membership is not null) return _membership;
			if (!IsAuthenticated) return _membership = [];

			// No usable identity means there is nothing to filter BY, and the honest answer to "which
			// tenants is this caller a member of" is NONE. This arm used to return the WHOLE CATALOG
			// instead — a free pass defended as "a legacy admin with no User row would otherwise get an
			// empty sidebar". That premise does not hold: CredentialAuthenticator cannot sign anyone in
			// without a Users row (it reads db.Users and rejects a miss), and AdminBootstrapper seeds
			// the bootstrap admin's Users row together with its $system Admin membership in ONE
			// transaction — so no cookie session can arrive here, and the bootstrap admin keeps a
			// populated /ui sidebar through its $system membership like any other member. What DID
			// arrive here is an api-key principal rendering a /ui page (that scheme mints no yb:user_id
			// — see WorkspaceClaimsRefresher), and the free pass showed it every tenant in the install.
			var userIdRaw = Http!.User.FindFirst(PetBoxClaims.UserId)?.Value;
			if (!long.TryParse(userIdRaw, out var userId)) return _membership = [];

			var memberKeys = MembershipKeys(userId);
			return _membership = [.. Catalog.Where(w => memberKeys.Contains(w.Key))];
		}
	}

	// THE RIGHT — the tenants this caller may REACH: their memberships, plus, for a holder of the
	// system permission, every tenant in the catalog. This is spec workspace-read-isolation ("its
	// participants and the holder of the system permission") expressed as a list, and
	// tenant-visibility-by-membership deliberately does NOT narrow it: that node governs what the user
	// zone SHOWS, never what the caller may open.
	//
	// Two consumers, and they are the reason the split has to exist at all:
	//   * the ADMIN ZONE's selectors — enumerating every tenant is that zone's SUBJECT, not a leak;
	//   * CanReachWorkspace/ResolveWorkspace — an addressed /ui/{W}/... URL must still resolve W for an
	//     operator who is not a member of it. Narrowing this to membership would silently convert a
	//     visibility change into a loss of access, i.e. exactly the half of the decision NOT changing.
	//
	// Deliberately NOT on INavigationContext: nothing outside this class needs it by name (the admin
	// zone reaches it through AvailableWorkspaces), and putting it on the interface would oblige five
	// unrelated test fakes to implement a second enumeration they have no opinion about.
	public IReadOnlyList<WorkspaceOption> ReachableWorkspaces
	{
		get
		{
			if (_reachable is not null) return _reachable;
			if (!IsAuthenticated) return _reachable = [];
			return _reachable = Http!.User.HasClaim(PetBoxClaims.IsSysAdmin, "true")
				? Catalog
				: MembershipWorkspaces;
		}
	}

	// What the CURRENT ZONE lists. Both zones read this one property and get different answers — that
	// IS the mechanism: the shared partials keep no zone logic of their own, and no consumer can pick
	// the wrong list by accident. /ui/search rides on it too, through ProjectsByWorkspace.
	public IReadOnlyList<WorkspaceOption> AvailableWorkspaces =>
		IsAdminZone ? ReachableWorkspaces : MembershipWorkspaces;

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

	// Sliced from the one grouped read below — never a query of its own. The empty list is the answer
	// both for a workspace with no user projects AND for a workspace the current zone does not list:
	// since tenant-visibility-by-membership, CurrentWorkspaceKey can be a tenant the caller REACHED by
	// URL without being a member of it (ResolveWorkspace authorizes reach, not listing), and such a
	// workspace is deliberately absent from ProjectsByWorkspace in the /ui zone.
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

	// The whole project tree of every workspace THIS ZONE lists, in ONE read — it is keyed off
	// AvailableWorkspaces, so it inherits the zone split for free and there is no second place where
	// projects could leak. That transitivity is the whole reason /ui/search needed no change of its
	// own: CrossScopeTaskSearchService fans out over exactly this dictionary, /ui/search is not an
	// /ui/admin route, so its fan-out is now the caller's memberships even for a sysadmin.
	//
	// Consequence worth stating: on an ADDRESSED /ui/{W}/... page where W is reachable but not a
	// membership, W is not a key here, so the project selector renders empty. The page itself works
	// (CurrentProjectKey comes from the route) — the personal zone simply does not adopt a tenant the
	// caller has no membership in. Browsing W's project list is what the admin zone is for.
	//
	// Workspace memory containers ($workspace / $ws-*) are not user projects — they have no
	// logs/dbs/tasks — and IProjectDirectory drops them by default, the one definition of that rule.
	public IReadOnlyDictionary<string, IReadOnlyList<Project>> ProjectsByWorkspace =>
		_projectsByWs ??= Sync(projects.ListByWorkspaceAsync(
			[.. AvailableWorkspaces.Select(w => w.Key)]));

	// Never invents a workspace: a user with no membership resolves to null (empty state), not
	// to "$system" — the fallback that let a fresh account land on someone else's dashboard.
	string? ResolveWorkspace()
	{
		// 1. Route param wins (page explicitly scoped to a workspace)
		var routeWs = Http?.GetRouteValue("workspaceKey")?.ToString();
		if (!string.IsNullOrEmpty(routeWs) && CanReachWorkspace(routeWs))
			return routeWs;

		// 2. Project route → resolve from project's workspace
		var routeProject = Http?.GetRouteValue("projectKey")?.ToString()
			?? (IsProjectRoute() ? Http?.GetRouteValue("key")?.ToString() : null);
		if (!string.IsNullOrEmpty(routeProject))
		{
			// The tree the sidebar needs anyway already answers this for every project this zone
			// lists — and a hit is, BY CONSTRUCTION, in a workspace the caller may reach (the tree is
			// built from AvailableWorkspaces, itself a subset of ReachableWorkspaces in either zone),
			// so the access test is not skipped here, it is implied. A project of a reachable but
			// NON-listed workspace (a sysadmin's addressed /ui/{W}/{P}/... in the user zone) misses
			// the tree and falls through to the cold path below, which authorizes it explicitly.
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
			if (project is not null && CanReachWorkspace(project.WorkspaceKey))
				return project.WorkspaceKey;
		}

		// 3. Cookie
		if (Http?.Request.Cookies.TryGetValue(WorkspaceCookie, out var cookieWs) == true
			&& !string.IsNullOrEmpty(cookieWs) && CanReachWorkspace(cookieWs))
			return cookieWs;

		// 4. Active-workspace claim from login
		var claimWs = Http?.User.FindFirst(PetBoxClaims.ActiveWorkspace)?.Value;
		if (!string.IsNullOrEmpty(claimWs) && CanReachWorkspace(claimWs))
			return claimWs;

		// 5. First available membership — or none at all.
		var available = AvailableWorkspaces;
		return available.Count > 0 ? available[0].Key : null;
	}

	// REACHABILITY, not visibility — it reads ReachableWorkspaces and pointedly not the zone's list.
	// A sysadmin who opens /ui/{W}/... while not a member of W must still resolve W: the tenant is
	// absent from their sidebar and the page still works. Reading AvailableWorkspaces here instead
	// would make the /ui zone's narrowing decide ACCESS, which is the one thing this change must not
	// do — and in the admin zone it would leave the operator unable to open another tenant at all.
	bool CanReachWorkspace(string wsKey)
	{
		if (!IsAuthenticated) return false;
		foreach (var w in ReachableWorkspaces)
			if (string.Equals(w.Key, wsKey, StringComparison.Ordinal))
				return true;
		return false;
	}
}
