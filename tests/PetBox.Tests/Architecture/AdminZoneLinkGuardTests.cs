using System.Text.RegularExpressions;

namespace PetBox.Tests.Architecture;

// Guard for work `onboarding-first-workspace`, defect #3: on /ui/admin/ws/{ws}/projects, the project
// Name link pointed at Routes.Project(...) — the /ui USER-zone dashboard — instead of the project's
// admin page. Same root-cause FAMILY as the other two defects in that card (a workspace-create
// redirect and a missing-route-value asp-page link): link generation that produces a plausible URL
// which is simply the WRONG ZONE. A page under Pages/Admin/** exists so an admin can act on
// something; a primary link that silently drops them into /ui is the same shape of bug as a
// silently-wrong URL, just wrong on the ZONE axis instead of the missing-parameter axis
// (AspPageRouteValuesGuardTests covers that other axis).
//
// Text scan over the shipped .cshtml source, same technique/tradeoff as
// UiStateSingleMechanismGuardTests and AspPageRouteValuesGuardTests: a guardrail against an honest
// next admin page reusing a /ui route builder for its primary link, not a defense against someone
// determined to evade it (e.g. by hand-writing the literal URL string instead of using Routes.*).
public sealed class AdminZoneLinkGuardTests
{
	static string AdminPagesDir()
	{
		var dir = AppContext.BaseDirectory;
		while (!string.IsNullOrEmpty(dir))
		{
			var candidate = Path.Combine(dir, "src", "PetBox.Web", "Pages", "Admin");
			if (Directory.Exists(candidate) && File.Exists(Path.Combine(candidate, "Index.cshtml")))
				return candidate;
			dir = Path.GetDirectoryName(dir);
		}
		throw new FileNotFoundException("src/PetBox.Web/Pages/Admin not found walking up from the test bin.");
	}

	// The USER-zone "bare dashboard" route builders — Routes.Project (the plain /ui/{ws}/{key} project
	// home) and Routes.Workspace (the plain /ui/{ws} status page). Both have a direct, near-universally
	// preferred admin-zone equivalent (Routes.ProjectSettings/ProjectSettingsAdmin, Routes.WorkspaceAdmin)
	// that an admin catalog/list page should link to instead — this is the exact shape of the reported
	// defect (the projects-list Name link, and the sysadmin per-workspace overview's project list).
	//
	// Deliberately NARROW, not "every /ui/{ws}/... route builder": most of them (ProjectLog/
	// ProjectMemoryStore/ProjectTaskBoard/ProjectSession/ProjectTrace/ProjectDatabase/ProjectTable,
	// their plural "list" siblings, ProjectConfig*, ProjectLlmRouter, SharedConfig, SharedMemory) are
	// content VIEWERS with no separate admin-side duplicate at all — Pages/Admin/ProjectLogs.cshtml,
	// ProjectMemory.cshtml and ProjectTasks.cshtml legitimately open the single shared log/store/board
	// viewer from their admin catalog page, the same way the admin sidebar itself links to
	// Routes.SharedConfig. Denylisting those would flag long-standing, intentional, non-duplicated
	// navigation — the actual failure mode this guard exists for is narrower: a route that has a KNOWN
	// admin equivalent being used instead of it.
	static readonly string[] UserZoneRouteBuilders = ["Routes.Project(", "Routes.Workspace("];

	sealed record Offender(string File, string RouteBuilder);

	static IEnumerable<Offender> ScanFile(string file, string relPath)
	{
		var text = File.ReadAllText(file);
		foreach (var builder in UserZoneRouteBuilders)
			if (text.Contains(builder, StringComparison.Ordinal))
				yield return new Offender(relPath, builder.TrimEnd('('));
	}

	[Fact]
	public void NoAdminPage_LinksIntoTheUserZone()
	{
		var dir = AdminPagesDir();

		var offenders = Directory.EnumerateFiles(dir, "*.cshtml", SearchOption.AllDirectories)
			.SelectMany(file => ScanFile(file, Path.GetRelativePath(dir, file)))
			.ToList();

		offenders.Should().BeEmpty(
			"a page under Pages/Admin/** exists so an admin can ACT on something — a link that quietly "
			+ "drops them into the /ui user-zone dashboard instead (onboarding-first-workspace: the "
			+ "projects-list Name link pointed at Routes.Project(...)) throws them out of the zone they "
			+ "were just working in. Use the admin-zone equivalent (Routes.ProjectSettings / "
			+ "Routes.ProjectSettingsAdmin / Routes.WorkspaceAdmin / ...) instead. A deliberate "
			+ "\"view it as a user\" affordance is fine but must be an explicit, separately-labelled "
			+ "link, not the primary one, and does not belong on this denylist (extend the exclusion "
			+ "list in this test with a reasoned comment if one is ever added). Offenders: "
			+ string.Join("; ", offenders.Select(o => $"{o.File} -> {o.RouteBuilder}")));
	}

	// Guard-the-guard: if AdminPagesDir()/the sweep ever silently found nothing, the assertion above
	// would pass by vacuity and stop protecting anything.
	[Fact]
	public void TheGuard_ActuallyScansAdminPages()
	{
		var dir = AdminPagesDir();
		var files = Directory.EnumerateFiles(dir, "*.cshtml", SearchOption.AllDirectories).ToList();

		files.Should().HaveCountGreaterThan(15, "the Pages/Admin sweep must cover the real admin page tree");
		files.Should().Contain(f => Path.GetFileName(f) == "Projects.cshtml");
	}
}
