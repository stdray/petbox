using LinqToDB;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using YobaBox.Core.Auth;
using YobaBox.Core.Data;
using YobaBox.Core.Features;
using YobaBox.Core.Models;

namespace YobaBox.Web.Navigation;

public interface INavigationContext
{
	bool IsAuthenticated { get; }
	string? Username { get; }
	string CurrentWorkspaceKey { get; }
	string? CurrentProjectKey { get; }
	IReadOnlyList<WorkspaceOption> AvailableWorkspaces { get; }
	IReadOnlyList<Project> ProjectsInCurrentWorkspace { get; }
	IReadOnlyDictionary<string, IReadOnlyList<Project>> ProjectsByWorkspace { get; }
	bool DataEnabled { get; }
}

public sealed record WorkspaceOption(string Key, string Name);

public sealed class NavigationContext(
	IHttpContextAccessor accessor,
	YobaBoxDb db,
	YobaBox.Core.Features.FeatureFlags features) : INavigationContext
{
	const string WorkspaceCookie = "yb_ws";

	IReadOnlyList<WorkspaceOption>? _workspaces;
	IReadOnlyList<Project>? _projects;
	IReadOnlyDictionary<string, IReadOnlyList<Project>>? _projectsByWs;
	string? _resolvedWorkspace;

	HttpContext? Http => accessor.HttpContext;

	public bool IsAuthenticated => Http?.User.Identity?.IsAuthenticated == true;
	public string? Username => Http?.User.Identity?.Name;
	public bool DataEnabled => features.IsEnabled(Feature.Data);

	public string CurrentWorkspaceKey => _resolvedWorkspace ??= ResolveWorkspace();

	public string? CurrentProjectKey
	{
		get
		{
			var fromProjectKey = Http?.GetRouteValue("projectKey")?.ToString();
			if (!string.IsNullOrEmpty(fromProjectKey)) return fromProjectKey;
			var fromKey = Http?.GetRouteValue("key")?.ToString();
			return !string.IsNullOrEmpty(fromKey) && IsProjectRoute() ? fromKey : null;
		}
	}

	bool IsProjectRoute() => Http?.GetRouteValue("projectKey") is not null;

	public IReadOnlyList<WorkspaceOption> AvailableWorkspaces
	{
		get
		{
			if (_workspaces is not null) return _workspaces;
			if (!IsAuthenticated)
			{
				_workspaces = [];
				return _workspaces;
			}

			// Sysadmin sees everything regardless of membership.
			var isSysAdmin = Http!.User.HasClaim(YobaBoxClaims.IsSysAdmin, "true");
			if (isSysAdmin)
			{
				_workspaces = [.. db.Workspaces
					.OrderBy(w => w.Key)
					.Select(w => new WorkspaceOption(w.Key, w.Name))];
				return _workspaces;
			}

			var userIdRaw = Http!.User.FindFirst(YobaBoxClaims.UserId)?.Value;
			if (!long.TryParse(userIdRaw, out var userId))
			{
				// Fall back: show all workspaces (e.g. legacy admin without User row yet)
				_workspaces = [.. db.Workspaces
					.OrderBy(w => w.Key)
					.Select(w => new WorkspaceOption(w.Key, w.Name))];
				return _workspaces;
			}

			var memberKeys = db.WorkspaceMembers
				.Where(m => m.UserId == userId)
				.Select(m => m.WorkspaceKey)
				.ToList();

			_workspaces = [.. db.Workspaces
				.Where(w => memberKeys.Contains(w.Key))
				.OrderBy(w => w.Key)
				.Select(w => new WorkspaceOption(w.Key, w.Name))];
			return _workspaces;
		}
	}

	public IReadOnlyList<Project> ProjectsInCurrentWorkspace
	{
		get
		{
			if (_projects is not null) return _projects;
			var wsKey = CurrentWorkspaceKey;
			_projects = [.. db.Projects.Where(p => p.WorkspaceKey == wsKey).OrderBy(p => p.Key)];
			return _projects;
		}
	}

	public IReadOnlyDictionary<string, IReadOnlyList<Project>> ProjectsByWorkspace
	{
		get
		{
			if (_projectsByWs is not null) return _projectsByWs;
			var wsKeys = AvailableWorkspaces.Select(w => w.Key).ToHashSet(StringComparer.Ordinal);
			var grouped = db.Projects
				.Where(p => wsKeys.Contains(p.WorkspaceKey))
				.OrderBy(p => p.Key)
				.ToList()
				.GroupBy(p => p.WorkspaceKey, StringComparer.Ordinal)
				.ToDictionary(g => g.Key, g => (IReadOnlyList<Project>)g.ToList(), StringComparer.Ordinal);
			_projectsByWs = grouped;
			return _projectsByWs;
		}
	}

	string ResolveWorkspace()
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
			var p = db.Projects.FirstOrDefault(pr => pr.Key == routeProject);
			if (p is not null && IsMember(p.WorkspaceKey))
				return p.WorkspaceKey;
		}

		// 3. Cookie
		if (Http?.Request.Cookies.TryGetValue(WorkspaceCookie, out var cookieWs) == true
			&& !string.IsNullOrEmpty(cookieWs) && IsMember(cookieWs))
			return cookieWs;

		// 4. Active-workspace claim from login
		var claimWs = Http?.User.FindFirst(YobaBoxClaims.ActiveWorkspace)?.Value;
		if (!string.IsNullOrEmpty(claimWs) && IsMember(claimWs))
			return claimWs;

		// 5. First available membership
		var workspaces = AvailableWorkspaces;
		return workspaces.Count > 0 ? workspaces[0].Key : "$system";
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
