using System.Text.RegularExpressions;

namespace PetBox.Tests.Architecture;

// Guard for work `presetkind-spec-blind-spot` (maintainer invariant): MethodologyRuntime.PresetKind
// nulls out for ANY methodology-DEFINED kind (by design — it's the correct guard for genuinely
// preset-only behavior, like Simple's type-vocabulary check). `spec` and `work` are two of the
// quartet's four kinds, and RenderPresetDefinition renders every quartet kind VERBATIM into a real
// project's stored methodology definition at instance-creation time — so on any quartet-provisioned
// project (the default, and $system's own shape) `PresetKind(...)` reads null for BOTH of them, and
// a `PresetKind(...) == BoardKind.Spec` / `== BoardKind.Work` comparison NEVER matches in production,
// silently, with no error. This shipped THREE separate production/latent bugs before anyone noticed
// (the strikethrough regression, PR #21→#22; StatusBadge.Show's spec-board status-noise
// suppression; TasksService's `linkedTasks` gate; and RequireBlockersAsync's "Blocked needs a
// blocker" invariant) — the correct replacement is always an EFFECTIVE-kind check
// (MethodologyRuntime.IsSpecKind/IsWorkKind, KindName(...) == "…", or a data field on
// MethodologyKindDef resolved through ResolvedKind), never a PresetKind(...) equality against one of
// the process-role kinds.
//
// WHY A TEXT SCAN, NOT NetArchTest: the anti-pattern is an EXPRESSION SHAPE (a boolean comparison),
// not a type/member reference NetArchTest can reason about structurally — same reasoning as
// UiStateSingleMechanismGuardTests just above this file in the same folder.
//
// SCOPE, deliberately narrow: this guards `BoardKind.Spec`/`BoardKind.Work` only — the two kinds
// PROVEN (by three separate incidents, reproduced and fixed on this same work item) to be
// always-defined on a real project. `BoardKind.Simple` comparisons are left legitimate and
// unguarded: Simple is not part of MethodologyPresets.ProvisioningPresets — the standard "enable
// methodology" flow never renders it into a definition; it is reachable as a defined kind only via
// the raw tasks_methodology_create(source:builtin, sourceKey:simple) call, bypassing the
// UI-recommended path (see the presetkind-spec-blind-spot verdict comment for the full call-site
// audit). `BoardKind.Ideas`/`.Intake`/`.Classic` are equally quartet/classic-preset kinds and share
// the same theoretical exposure, but no call site compares PresetKind against them today —
// extending the ban to cover them preemptively is a reasonable follow-up, not done here so this
// guard's claim stays exactly as strong as what was actually reproduced.
//
// LIMITS (same posture as the ts-storage guard above: an honest-mistake guardrail, not
// adversarial-proof): this catches (a) a direct `PresetKind(...) == / != BoardKind.Spec|Work` in one
// expression, and (b) a local `var x = ....PresetKind(...)` later compared to `BoardKind.Spec|Work`
// anywhere in the SAME file. It does NOT trace a PresetKind(...) result threaded through a method
// PARAMETER one hop further (TasksService.RequireBlockersAsync's original shape, `BoardKind? kind`)
// — that site's fix removed the BoardKind? parameter entirely in favor of IsWorkKind, so there is no
// natural reason for new code to reintroduce that specific indirection, but a determined rewrite
// could still route around this scan.
public sealed class PresetKindProcessRoleGuardTests
{
	static string SrcDir()
	{
		var dir = AppContext.BaseDirectory;
		while (!string.IsNullOrEmpty(dir))
		{
			var candidate = Path.Combine(dir, "src");
			if (Directory.Exists(Path.Combine(candidate, "PetBox.Web")) && Directory.Exists(Path.Combine(candidate, "PetBox.Tasks")))
				return candidate;
			dir = Path.GetDirectoryName(dir);
		}
		throw new FileNotFoundException("src/ (with PetBox.Web + PetBox.Tasks) not found walking up from the test bin.");
	}

	// Strips `//` / `/* */` (C#) and `@* *@` (Razor) comments before scanning — naive (does not
	// understand string/template literals), the same tradeoff UiStateSingleMechanismGuardTests
	// makes for the identical reason: a guardrail against an honest next feature, not a lexer
	// defending against someone determined to evade it.
	static string StripComments(string source)
	{
		var noBlockComments = Regex.Replace(source, @"/\*.*?\*/", "", RegexOptions.Singleline);
		var noRazorComments = Regex.Replace(noBlockComments, @"@\*.*?\*@", "", RegexOptions.Singleline);
		return Regex.Replace(noRazorComments, @"//[^\n]*", "");
	}

	static IReadOnlyList<(string Path, string Code)> ScanSourceFiles()
	{
		var dir = SrcDir();
		return Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories)
			.Concat(Directory.EnumerateFiles(dir, "*.cshtml", SearchOption.AllDirectories))
			.Where(p => !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
				&& !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
			.Select(p => (Path: p, Code: StripComments(File.ReadAllText(p))))
			.ToList();
	}

	// (a) a direct `PresetKind(...) == / != BoardKind.Spec|Work` in ONE expression (the shape
	// StatusBadge.cs's original `Runtime.PresetKind(KindSlug) != BoardKind.Spec` had).
	static readonly Regex DirectComparison = new(
		@"PresetKind\s*\([^;]*?\)\s*(==|!=)\s*BoardKind\.(Spec|Work)\b",
		RegexOptions.Compiled);

	// (b) step 1 — a local variable bound to a PresetKind(...) call result (the shape
	// TasksService.cs's original `var presetKind = runtime.PresetKind(meta.Kind);` had).
	static readonly Regex VarBinding = new(
		@"\bvar\s+(\w+)\s*=\s*[\w.]*\bPresetKind\s*\(",
		RegexOptions.Compiled);

	static IEnumerable<string> Violations(string code)
	{
		foreach (Match m in DirectComparison.Matches(code))
			yield return $"direct comparison to BoardKind.{m.Groups[2].Value}";

		// (b) step 2 — the bound variable compared to BoardKind.Spec|Work anywhere later in the file.
		foreach (Match bind in VarBinding.Matches(code))
		{
			var name = bind.Groups[1].Value;
			var laterCompare = new Regex($@"\b{Regex.Escape(name)}\b\s*(==|!=)\s*BoardKind\.(Spec|Work)\b");
			foreach (Match m in laterCompare.Matches(code))
				yield return $"'{name}' (bound to PresetKind(...)) compared to BoardKind.{m.Groups[2].Value}";
		}
	}

	[Fact]
	public void NoPresetKindComparedToSpecOrWork_AnywhereInSrc()
	{
		var offenders = ScanSourceFiles()
			.Select(f => (f.Path, Hits: Violations(f.Code).ToList()))
			.Where(f => f.Hits.Count > 0)
			.ToList();

		offenders.Should().BeEmpty(
			"PresetKind(...) nulls out for ANY methodology-defined kind, and `spec`/`work` are ALWAYS " +
			"defined on a real quartet-provisioned project (RenderPresetDefinition renders every quartet " +
			"kind verbatim into the instance's stored definition) — a PresetKind(...) == /!= " +
			"BoardKind.Spec|Work comparison is DEAD on every real project, silently " +
			"(presetkind-spec-blind-spot: three separate incidents shipped from exactly this pattern). " +
			"Use the EFFECTIVE-kind check instead (MethodologyRuntime.IsSpecKind/IsWorkKind, " +
			"KindName(...) == \"…\", or a data field resolved through ResolvedKind). Offenders: " +
			string.Join("; ", offenders.Select(o => $"{o.Path}: {string.Join(", ", o.Hits)}")));
	}

	// Detector sanity: exercise the regex logic against known-bad/known-good snippets directly,
	// independent of what src/ currently contains — THIS is what proves the pattern actually works,
	// not merely that today's tree happens to be clean.
	[Theory]
	[InlineData("var presetKind = runtime.PresetKind(meta.Kind);\nvar x = presetKind == BoardKind.Spec ? 1 : 0;", true)]
	[InlineData("if (kind != BoardKind.Work) return;", false)] // no PresetKind(...) in scope — outside this guard's traced shape
	[InlineData("Runtime.PresetKind(KindSlug) != BoardKind.Spec || Runtime.IsTerminalStatus(KindSlug, Status)", true)]
	[InlineData("var presetKind = runtime.PresetKind(kindSlug);\nif (presetKind == BoardKind.Work) DoWork();", true)]
	[InlineData("Runtime.IsSpecKind(KindSlug)", false)]
	[InlineData("runtime.PresetKind(kindSlug) == BoardKind.Simple", false)] // Simple is not banned — see class doc
	public void Detector_FlagsExactlyTheBannedShape(string snippet, bool expectViolation)
	{
		Violations(StripComments(snippet)).Any().Should().Be(expectViolation, snippet);
	}

	// Guard-the-guard: if SrcDir()/ScanSourceFiles ever silently found nothing (a moved directory, a
	// test host that doesn't ship src/ alongside the binaries), the assertion above would pass by
	// vacuity and stop protecting anything.
	[Fact]
	public void TheGuard_ActuallyScansFiles()
	{
		var files = ScanSourceFiles();

		files.Should().HaveCountGreaterThan(50, "the src/ sweep must cover the real product source tree");
		files.Should().Contain(f => f.Path.EndsWith("MethodologyRuntime.cs", StringComparison.OrdinalIgnoreCase));
		files.Should().Contain(f => f.Path.EndsWith("TasksService.cs", StringComparison.OrdinalIgnoreCase));
	}
}
