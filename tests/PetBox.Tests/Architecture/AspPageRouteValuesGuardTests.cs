using System.Text.RegularExpressions;

namespace PetBox.Tests.Architecture;

// Guard for work `onboarding-first-workspace`. Dashboard/Index.cshtml carried
// `<a asp-page="/Admin/Projects" class="link">Admin</a>` with NO route values, while that page's
// route (`/ui/admin/ws/{workspaceKey}/projects`) requires `workspaceKey`. Link generation did not
// throw — it silently produced a wrong URL (`/ui/test`), so the page rendered fine and no test
// caught it. "The `<a href>` exists" is not "the link is right", and nothing checked the difference.
//
// This is a text scan over the ACTUAL shipped .cshtml source (same technique and same tradeoff as
// UiStateSingleMechanismGuardTests: no NetArchTest reflection surface exists for Razor markup), not a
// full Razor/HTML parser — it is a guardrail against an honest next feature reintroducing the same
// missing-route-value shape, not a defense against someone determined to evade it.
//
// It builds a catalog of every page's route template (from its own `@page "..."` directive) and its
// REQUIRED route parameters (no `{x?}`, no `{**x}` catch-all), then checks every `asp-page="/Target"`
// tag and every `Url.Page("/Target", new { ... })` call against that catalog: every required
// parameter of the TARGET page must be supplied (`asp-route-{name}` on the same tag, or a matching
// property in the anonymous object) — and the target page must actually exist in the catalog at all
// (a typo'd page name is the same "silently wrong URL" shape).
public sealed class AspPageRouteValuesGuardTests
{
	static string PagesDir()
	{
		var dir = AppContext.BaseDirectory;
		while (!string.IsNullOrEmpty(dir))
		{
			var candidate = Path.Combine(dir, "src", "PetBox.Web", "Pages");
			if (Directory.Exists(candidate) && File.Exists(Path.Combine(candidate, "Index.cshtml")))
				return candidate;
			dir = Path.GetDirectoryName(dir);
		}
		throw new FileNotFoundException("src/PetBox.Web/Pages not found walking up from the test bin.");
	}

	static readonly Regex PageDirective = new(@"@page\s+""([^""]*)""", RegexOptions.Compiled);
	static readonly Regex RouteSegment = new(@"\{([^{}]+)\}", RegexOptions.Compiled);

	// Every `{...}` route segment's bare parameter name, filtered to the REQUIRED ones: not optional
	// (`{x?}` / `{x:constraint?}`) and not a catch-all (`{*x}` / `{**x}`).
	static IReadOnlyList<string> RequiredParams(string routeTemplate)
	{
		var result = new List<string>();
		foreach (Match m in RouteSegment.Matches(routeTemplate))
		{
			var seg = m.Groups[1].Value;
			var optional = seg.EndsWith('?');
			var body = optional ? seg[..^1] : seg;
			var name = body.Split(':')[0];
			if (!optional && !name.StartsWith('*'))
				result.Add(name);
		}
		return result;
	}

	// Page name as `asp-page`/`Url.Page` address it: "/Admin/Projects" for Pages/Admin/Projects.cshtml.
	// Partials (`_Foo.cshtml`) carry no `@page` directive and are not addressable this way — skipped.
	static IReadOnlyDictionary<string, IReadOnlyList<string>> BuildRouteCatalog(string pagesDir)
	{
		var catalog = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
		foreach (var file in Directory.EnumerateFiles(pagesDir, "*.cshtml", SearchOption.AllDirectories))
		{
			var fileName = Path.GetFileNameWithoutExtension(file);
			if (fileName.StartsWith('_')) continue;

			var rel = Path.GetRelativePath(pagesDir, file);
			var pageName = "/" + Path.ChangeExtension(rel, null).Replace('\\', '/');

			var text = File.ReadAllText(file);
			var m = PageDirective.Match(text);
			catalog[pageName] = m.Success ? RequiredParams(m.Groups[1].Value) : [];
		}
		return catalog;
	}

	// One flagged usage: a link/URL-generation call whose target page either doesn't exist in the
	// catalog, or is missing one or more of the target's required route parameters.
	sealed record Offender(string File, string TargetPage, IReadOnlyList<string> Missing);

	static readonly Regex AspPageTag = new(
		@"<a\b[^>]*\basp-page=""(?<page>[^""]+)""[^>]*>", RegexOptions.Compiled);
	static readonly Regex AspRouteAttr = new(
		@"\basp-route-(?<name>[A-Za-z0-9_]+)\s*=", RegexOptions.Compiled);
	static readonly Regex UrlPageCall = new(
		@"Url\.Page\(\s*""(?<page>[^""]+)""\s*,\s*new\s*\{(?<obj>[^}]*)\}",
		RegexOptions.Compiled | RegexOptions.Singleline);
	static readonly Regex ObjPropName = new(
		@"(?<![.\w])(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*=(?!=)", RegexOptions.Compiled);

	static IEnumerable<Offender> ScanFile(string file, string relPath, IReadOnlyDictionary<string, IReadOnlyList<string>> catalog)
	{
		var text = File.ReadAllText(file);

		foreach (Match tag in AspPageTag.Matches(text))
		{
			var target = tag.Groups["page"].Value;
			var supplied = AspRouteAttr.Matches(tag.Value)
				.Select(m => m.Groups["name"].Value)
				.ToHashSet(StringComparer.OrdinalIgnoreCase);
			foreach (var offender in CheckTarget(relPath, target, supplied, catalog))
				yield return offender;
		}

		foreach (Match call in UrlPageCall.Matches(text))
		{
			var target = call.Groups["page"].Value;
			var supplied = ObjPropName.Matches(call.Groups["obj"].Value)
				.Select(m => m.Groups["name"].Value)
				.ToHashSet(StringComparer.OrdinalIgnoreCase);
			foreach (var offender in CheckTarget(relPath, target, supplied, catalog))
				yield return offender;
		}
	}

	static IEnumerable<Offender> CheckTarget(
		string relPath, string target, HashSet<string> supplied, IReadOnlyDictionary<string, IReadOnlyList<string>> catalog)
	{
		// A relative page name (no leading "/") is resolved by Razor Pages against the CURRENT page's
		// folder, not the Pages root — this sweep only reasons about the unambiguous absolute form
		// (leading "/"), which is also the only form actually used anywhere in this codebase today
		// (see the class doc's grep). A relative reference would need the calling file's own folder to
		// resolve and is deliberately out of scope rather than silently mis-checked.
		if (!target.StartsWith('/')) yield break;

		if (!catalog.TryGetValue(target, out var required))
		{
			yield return new Offender(relPath, target, ["<page not found in Pages/>"]);
			yield break;
		}

		var missing = required.Where(p => !supplied.Contains(p)).ToList();
		if (missing.Count > 0)
			yield return new Offender(relPath, target, missing);
	}

	[Fact]
	public void EveryAspPageLink_SuppliesAllRequiredRouteValues()
	{
		var pagesDir = PagesDir();
		var catalog = BuildRouteCatalog(pagesDir);

		var offenders = Directory.EnumerateFiles(pagesDir, "*.cshtml", SearchOption.AllDirectories)
			.SelectMany(file => ScanFile(file, Path.GetRelativePath(pagesDir, file), catalog))
			.ToList();

		offenders.Should().BeEmpty(
			"every asp-page link / Url.Page(...) call must supply ALL of its target page's required "
			+ "route parameters — a missing one does not throw, it silently generates a wrong URL "
			+ "(onboarding-first-workspace: Dashboard/Index.cshtml's \"Admin\" link pointed at "
			+ "/Admin/Projects with no workspaceKey and resolved to garbage). Offenders: "
			+ string.Join("; ", offenders.Select(o => $"{o.File} -> {o.TargetPage} missing [{string.Join(",", o.Missing)}]")));
	}

	// Guard-the-guard: if PagesDir()/BuildRouteCatalog ever silently found nothing (a moved directory,
	// a test host that doesn't ship Pages/ alongside the binaries), the assertion above would pass by
	// vacuity and stop protecting anything.
	[Fact]
	public void TheGuard_ActuallyScansPagesAndBuildsARealCatalog()
	{
		var pagesDir = PagesDir();
		var catalog = BuildRouteCatalog(pagesDir);

		catalog.Should().HaveCountGreaterThan(30, "the Pages/ sweep must cover the real page tree");
		catalog.Should().ContainKey("/Admin/Projects");
		catalog["/Admin/Projects"].Should().Contain("workspaceKey");
		catalog.Should().ContainKey("/Dashboard/Index");
	}
}
