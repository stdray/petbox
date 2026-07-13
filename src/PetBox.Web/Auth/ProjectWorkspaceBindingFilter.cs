using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Routing;

namespace PetBox.Web.Auth;

// Applied globally to every Razor Page (Program.cs: options.Conventions.ConfigureFilter(new
// ServiceFilterAttribute(typeof(ProjectWorkspaceBindingFilter)))). Closes an entire CLASS of
// cross-tenant IDOR in ONE place instead of a per-page `p.Key == ProjectKey && p.WorkspaceKey ==
// WorkspaceKey` check that a new page can simply forget to write (workspace-access-isolation's
// own follow-up — same-class-cross-tenant-field-id-4c0359: Logs/Index, TaskBoard(+Node) and
// Llm/Index all shipped without it).
//
// A Workspace* authorization policy (WorkspaceRoleAuthorizationHandler) proves membership of the
// ROUTE {workspaceKey}. It says nothing about whether {projectKey} — also a route value, on every
// project-scoped page — actually belongs to that workspace. Without this, a member of wsA could
// point a wsA URL at a wsB project (/ui/wsA/{project-of-wsB}/...) and the policy would still pass
// (wsA is the route workspace); the handler then resolves the project by KEY ALONE and reads — or,
// on a POST handler, MUTATES — another tenant's data. Comments on the boards pages called this out
// explicitly ("the page never opens the DB context itself") without registering that ITasksService
// itself takes no workspace argument, so nothing anywhere checked it.
//
// Runs on EVERY page handler (GET and POST alike — a form POST is a page handler too, and a
// mutation through a wrong-tenant project is worse than a read) that carries BOTH route values; a
// page with only {workspaceKey} (the workspace dashboard) or only {projectKey} (none exist) is a
// no-op. 404s a mismatch or a nonexistent project — the same response shape every affected page
// already gave for "unknown project", now enforced once instead of on each new page.
//
// The binding question is asked of IProjectDirectory, not of core.db: a filter is pipeline code and
// the DB belongs to the service layer. Since this is the SOLE enforcement point, the ten per-page
// `p.WorkspaceKey == WorkspaceKey` copies it replaced were deleted (db-access-layer-cleanup) — do
// not "restore" one to a page, restore it here.
public sealed class ProjectWorkspaceBindingFilter(IProjectDirectory projects) : IAsyncPageFilter
{
	public Task OnPageHandlerSelectionAsync(PageHandlerSelectedContext context) => Task.CompletedTask;

	public async Task OnPageHandlerExecutionAsync(PageHandlerExecutingContext context, PageHandlerExecutionDelegate next)
	{
		var workspaceKey = context.HttpContext.GetRouteValue("workspaceKey")?.ToString();
		var projectKey = context.HttpContext.GetRouteValue("projectKey")?.ToString();

		if (!string.IsNullOrEmpty(workspaceKey) && !string.IsNullOrEmpty(projectKey))
		{
			var belongs = await projects.BelongsAsync(
				projectKey, workspaceKey, context.HttpContext.RequestAborted);
			if (!belongs)
			{
				context.Result = new NotFoundResult();
				return;
			}
		}

		await next();
	}
}
