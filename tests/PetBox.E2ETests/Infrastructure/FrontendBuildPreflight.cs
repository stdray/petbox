namespace PetBox.E2ETests.Infrastructure;

/// <summary>
/// Fail-loud guard against the #1 confusing-red cause in this suite: `wwwroot/js/site.js` and
/// `wwwroot/css/app.css` are gitignored (.gitignore: "Frontend build outputs (bun + tailwind)")
/// and are produced ONLY by the Release-gated `BuildFrontend` MSBuild target
/// (PetBox.Web.csproj, Condition="'$(Configuration)' == 'Release'"). A plain `dotnet test`
/// (Debug) in a fresh worktree never runs it, so the app boots with no client JS/CSS at all.
/// Purely client-side E2E assertions (aria-pressed toggles, theme classes, ...) then fail with
/// a generic UI assertion that looks like a product regression — it isn't. See
/// logs-pin-toggle-e2e-red (closed wontfix) and e2e-frontend-build-preflight.
///
/// Run BEFORE the host starts so a missing/stale bundle fails immediately with an actionable
/// message instead of surfacing later as a mysterious element-state assertion mid-test.
/// </summary>
static class FrontendBuildPreflight
{
	// bun build's --splitting also emits content-hashed chunk files (e.g. site-6ffx6y7t.js,
	// snowball-stemmers-x5fcx2qc.js) alongside the entry point. Those hashes change on every
	// rebuild, so only the stable entry names below are checked.
	const string JsEntryRelative = "wwwroot/js/site.js";
	const string CssEntryRelative = "wwwroot/css/app.css";

	// Tolerance for the staleness comparison. In CI the bundle is built moments (seconds) before
	// the test run in the same job, and NTFS mtime resolution/clock reporting can jitter by a
	// tick or two — without slack that could flag a build that in fact just finished. A real
	// stale-bundle scenario (edited source, forgot to rebuild) trails by minutes/hours, not
	// seconds, so this tolerance cannot mask it while still absorbing CI's build-then-test gap.
	static readonly TimeSpan StalenessTolerance = TimeSpan.FromSeconds(5);

	public static void EnsureBuilt()
	{
		var webDir = KestrelAppHost.WebProjectDir();
		var jsEntry = Path.Combine(webDir, "wwwroot", "js", "site.js");
		var cssEntry = Path.Combine(webDir, "wwwroot", "css", "app.css");

		var missing = new List<string>();
		if (!File.Exists(jsEntry)) missing.Add(JsEntryRelative);
		if (!File.Exists(cssEntry)) missing.Add(CssEntryRelative);

		if (missing.Count > 0)
		{
			throw new InvalidOperationException(
				$"""
				E2E preflight: frontend bundle not built — missing {string.Join(", ", missing)} under {webDir}.
				This is an ENVIRONMENT problem, not a product regression: wwwroot/js and wwwroot/css are
				gitignored and are only produced by the Release-gated BuildFrontend MSBuild target, which a
				plain (Debug) `dotnet test` never runs. Fix: run `./build.ps1 -Target Test` (Release) from
				the repo root, or `bun run build` in src/PetBox.Web, then re-run the E2E suite.
				""");
		}

		var newestSourceFile = EnumerateSources(webDir)
			.Select(f => (Path: f, Mtime: File.GetLastWriteTimeUtc(f)))
			.OrderByDescending(f => f.Mtime)
			.First();
		// Staleness is keyed on site.js's mtime alone, NOT app.css. Verified empirically: `bun
		// build` unconditionally rewrites site.js on every invocation, but the tailwindcss CLI is
		// content-addressed and SKIPS writing app.css when the generated CSS is byte-identical to
		// what's already there — so app.css's mtime can legitimately lag behind an unrelated .ts
		// edit even immediately after a successful `bun run build`. Both build:ts and build:css
		// always run together in the `build` script, so "site.js was rewritten after the newest
		// source edit" reliably means "a full build ran after that edit" — using app.css instead
		// (or the min of both) would false-positive on the very "touch a .ts file, rebuild" flow
		// this check exists to survive.
		var bundleMtime = File.GetLastWriteTimeUtc(jsEntry);

		if (newestSourceFile.Mtime > bundleMtime + StalenessTolerance)
		{
			throw new InvalidOperationException(
				$"""
				E2E preflight: frontend bundle is STALE — {Path.GetRelativePath(webDir, newestSourceFile.Path)}
				({newestSourceFile.Mtime:O}) is newer than the built bundle ({bundleMtime:O}).
				This is an ENVIRONMENT problem, not a product regression: the bundle under wwwroot/js and
				wwwroot/css was built before this source change. Fix: run `./build.ps1 -Target Test`
				(Release) from the repo root, or `bun run build` in src/PetBox.Web, then re-run the E2E suite.
				""");
		}
	}

	static IEnumerable<string> EnumerateSources(string webDir)
	{
		foreach (var f in Directory.EnumerateFiles(Path.Combine(webDir, "ts"), "*", SearchOption.AllDirectories))
			yield return f;
		yield return Path.Combine(webDir, "package.json");
		yield return Path.Combine(webDir, "tailwind.config.js");
	}
}
