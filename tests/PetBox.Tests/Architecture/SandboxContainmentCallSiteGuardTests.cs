using System.Text.RegularExpressions;
using Xunit;

namespace PetBox.Tests.Architecture;

// THE CALL-SITE GUARD for SandboxContainment — work `sandbox-containment-callsite-ratchet`.
//
// SandboxContainment.cs (work `memory-container-sandbox-containment-bypass`) states the rule —
// containment is enforced on the target the caller NAMES, and every surface that then acts on a
// container it DERIVES must ask the predicate again — and its first edition guarded that rule with
// a HAND-MAINTAINED list of its three call sites: "if you add a fourth, add it to this list". That
// is a human count, the exact practice this codebase banned after three independent human counts of
// the MCP tool surface came back 92 / 100 / 122 when the truth was 97 (the reasoning is written in
// TenantEnforcementAllowlist.cs). A comment asking future authors to remember is not a guard.
//
// WHAT THIS FILE DOES INSTEAD: it mechanically enumerates every source file that can HOLD a
// workspace-memory container key and every file that can ACT on one, and fails the build for any
// site that derives-and-acts without routing through SandboxContainment.Permits*Async. The list in
// the SandboxContainment.cs header is GONE; the machine inventory this test writes
// (.tmp/sandbox-containment-callsites.txt) is what replaces it.
//
// THE TOKEN DICTIONARY, and why it is sufficient rather than merely plausible. A container can only
// be reached by NAMING it, and there are exactly two ways a name arrives at a storage door:
//
//   1. THE NAMED-TARGET PLANE: raw caller input ("$workspace" / "$ws-x" as a projectKey) passed
//      straight through. That plane is NOT this guard's job — it is covered by the tenant
//      declaration ratchet (AuthzDeclarationRatchetTests): every surface declares its tenant
//      source, the PEP resolves the named container via TenantRef.FromProjectKeyOrContainer, and
//      TenantAuthorizer.KeyOnWorkspaceAsync applies containment centrally.
//   2. THE DERIVED PLANE — this guard's job: code that MANUFACTURES or BRANCHES ON container keys
//      itself. Doing that in C# requires touching the WorkspaceMemory vocabulary (ContainerKeyFor,
//      WorkspaceKeyOfContainer, IsWorkspaceContainer, the SystemContainer/ContainerPrefix consts)
//      or spelling the literal ("$workspace" / "$ws-…"), and ACTING on the result requires a
//      storage door (the IWorkspaceMemoryDirectory / WorkspaceMemory doors, or the memory module
//      via IMemoryService / MemoryDb). A file is a SITE when it holds both halves; a site must
//      call the predicate.
//
// WHY A TEXT SCAN AND NOT REFLECTION: the question is about CALL SITES inside method bodies, which
// reflection cannot see (same reason DbLayerGuardTests grew its source plane). Comments are
// stripped first — the tokens live in prose all over this codebase, and DbLayerGuardTests paid for
// that lesson with a false green ("Describe the old call, do not spell it"). The stripper is the
// same naive one PresetKindProcessRoleGuardTests uses, and the same tradeoff: a guardrail against
// an honest next feature, not a lexer defending against someone determined to evade it.
public sealed class SandboxContainmentCallSiteGuardTests
{
	// ── THE TOKEN DICTIONARY ─────────────────────────────────────────────────────────────────────

	// Ways to HOLD a container key: manufacture one, decode one, branch on one, or spell one.
	// IsWorkspaceContainer( is here although it only TESTS a key: a file that branches on
	// container-ness and then routes the key into storage is exactly the `scope:"workspace"` /
	// direct-addressing shape MemoryTools has, and the branch is the observable trace of it.
	static readonly string[] DeriveTokens =
	[
		"ContainerKeyFor(",
		"WorkspaceKeyOfContainer(",
		"IsWorkspaceContainer(",
		"WorkspaceMemory.SystemContainer",
		"WorkspaceMemory.ContainerPrefix",
		"\"$workspace\"",
		"\"$ws-",
	];

	// The container DOORS: every method through which a container key becomes a Projects row or a
	// resolved storage target. The full public surface of IWorkspaceMemoryDirectory plus the
	// WorkspaceMemory statics behind it — a file calling any of these is acting, not spelling.
	static readonly string[] ReachTokens =
	[
		"EnsureWorkspaceContainerAsync(",
		"EnsureAddressedContainerAsync(",
		"EnsureContainerAsync(",
		"ResolveContainerForProjectAsync(",
		"ResolveAndEnsureContainerAsync(",
		"ReachableByAsync(",
	];

	// The STORAGE doors a derived key can meet: the memory module's service contract and its
	// database. A derived container key is inert until it reaches one of these.
	static readonly string[] StorageTokens =
	[
		"IMemoryService",
		"MemoryDb",
	];

	// The predicate. `\b…\s*\.` tolerates the fully qualified spelling
	// (PetBox.Core.Auth.SandboxContainment.PermitsAsync) — the namespace-qualified hole
	// DbLayerGuardTests' service-locator pattern shipped with and had to re-close.
	static readonly Regex PredicatePattern = new(
		@"\bSandboxContainment\s*\.\s*Permits(Workspace)?Async\s*\(",
		RegexOptions.Compiled);

	// ── THE MECHANISM EXEMPTION — AND WHAT KEEPS IT HONEST ──────────────────────────────────────
	//
	// src/PetBox.Core/Data/** IS the container mechanism: WorkspaceMemory defines the key scheme,
	// WorkspaceMemoryDirectory owns the connection, the migrations seed the rows, ProjectDeletion
	// reserves them. Containment is a question about a CALLER, and this layer has no caller — no
	// ClaimsPrincipal, no HttpContext ever reaches it. That claim is not trusted, it is ASSERTED
	// (TheMechanismLayer_StaysPrincipalFree): the moment someone threads an identity into the data
	// layer, this exemption stops being self-evident and the build says so.
	const string MechanismPrefix = "PetBox.Core/Data/";

	// ── THE ALLOWLIST — INHERITED SITES, SHRINK-ONLY ─────────────────────────────────────────────
	//
	// Same contract as DbLayerGuardTests' allowlist and TenantEnforcementAllowlist: these entries
	// PREDATE the guard, the list only ever shrinks, a stale entry fails
	// AllowlistEntries_AreStillNeeded, and a NEW site never goes in here — it routes through
	// SandboxContainment.Permits*Async like everything written after the rule. Each entry names WHY
	// the site does not ask the predicate itself; the reasons are of two different strengths, and
	// the difference is spelled per entry rather than averaged.
	static readonly IReadOnlyDictionary<string, string> Allowlist = new Dictionary<string, string>(StringComparer.Ordinal)
	{
		// SAME-TENANT ENSURE, no read. The page's declared tenant IS the route workspace, so the
		// PEP (TenantAuthorizer.KeyOnWorkspaceAsync) has already applied containment to exactly the
		// workspace whose container this ensures — the derived hop lands on the tenant the PEP
		// judged, not on a second one, which is what distinguishes it from the CanonAsync leak.
		// EnsureAddressedContainerAsync creates a row; it discloses nothing.
		["PetBox.Web/Pages/Dashboard/Index.cshtml.cs"] =
			"ensures the container OF THE PEP-JUDGED ROUTE WORKSPACE (same tenant, creation only, no read)",

		// Same shape: the route names the container itself, the PEP judged that exact spelling as a
		// workspace tenant (TenantRef.FromProjectKeyOrContainer → KeyOnWorkspaceAsync, containment
		// included), and the page additionally welds the container to the route workspace before
		// ensuring. The subsequent store read is of the PEP-judged named target.
		["PetBox.Web/Pages/ProjectHome/Memory.cshtml.cs"] =
			"ensures/reads the PEP-JUDGED NAMED TARGET (route container welded to route workspace)",

		// MemoryRefMap.cs PAID OFF (work memoryrefmap-workspace-leg-ungated): it now threads the
		// caller's ClaimsPrincipal + IProjectCatalog through BuildAsync and asks
		// SandboxContainment.PermitsAsync before reading the derived workspace leg — same
		// suppress-the-leg shape MemoryApi.CanonAsync uses. This list is shrink-only; the entry is
		// gone, not corrected in place.
	};

	// ── THE SWEEP ────────────────────────────────────────────────────────────────────────────────

	static string SrcDir()
	{
		var dir = AppContext.BaseDirectory;
		while (!string.IsNullOrEmpty(dir))
		{
			var candidate = Path.Combine(dir, "src");
			if (Directory.Exists(Path.Combine(candidate, "PetBox.Web"))) return candidate;
			dir = Path.GetDirectoryName(dir);
		}

		throw new DirectoryNotFoundException("src/ (with PetBox.Web) not found walking up from the test bin.");
	}

	// `//`, `/* */` and Razor `@* *@` comments go; strings stay (the "$workspace" literal IS a
	// string). Naive by design — see the class header.
	static string StripComments(string source)
	{
		var noBlock = Regex.Replace(source, @"/\*.*?\*/", "", RegexOptions.Singleline);
		var noRazor = Regex.Replace(noBlock, @"@\*.*?\*@", "", RegexOptions.Singleline);
		return Regex.Replace(noRazor, @"//[^\n]*", "");
	}

	// One classified row per file that touches ANY token. Sites are the subset that can act.
	sealed record Row(string RelPath, string[] Derive, string[] Reach, string[] Storage, bool Predicated)
	{
		public bool IsSite => Reach.Length > 0 || (Derive.Length > 0 && Storage.Length > 0);
		public bool IsMechanism => RelPath.StartsWith(MechanismPrefix, StringComparison.Ordinal);

		public string Classification =>
			!IsSite ? "spell-only" :
			IsMechanism ? "site/mechanism" :
			Predicated ? "site/predicated" :
			Allowlist.ContainsKey(RelPath) ? "site/allowlisted-debt" :
			"site/UNGUARDED";
	}

	static string[] Hits(string code, string[] tokens) =>
		[.. tokens.Where(t => code.Contains(t, StringComparison.Ordinal))];

	static Row Classify(string relPath, string strippedCode) => new(
		relPath,
		Hits(strippedCode, DeriveTokens),
		Hits(strippedCode, ReachTokens),
		Hits(strippedCode, StorageTokens),
		PredicatePattern.IsMatch(strippedCode));

	static IReadOnlyList<string> ProductSourceFiles()
	{
		var src = SrcDir();
		return [.. Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
			.Concat(Directory.EnumerateFiles(src, "*.cshtml", SearchOption.AllDirectories))
			.Where(p => !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
				&& !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))];
	}

	static string Rel(string path) =>
		Path.GetRelativePath(SrcDir(), path).Replace(Path.DirectorySeparatorChar, '/');

	static IReadOnlyList<Row> Sweep() =>
		[.. ProductSourceFiles()
			.Select(p => Classify(Rel(p), StripComments(File.ReadAllText(p))))
			.Where(r => r.Derive.Length > 0 || r.Reach.Length > 0)
			.OrderBy(r => r.RelPath, StringComparer.Ordinal)];

	// ── THE ASSERTIONS ───────────────────────────────────────────────────────────────────────────

	[Fact]
	public void EverySiteThatReachesAContainer_RoutesThroughThePredicate()
	{
		var offenders = Sweep()
			.Where(r => r.IsSite && !r.IsMechanism && !r.Predicated && !Allowlist.ContainsKey(r.RelPath))
			.Select(r => $"  {r.RelPath}\n      derives: [{string.Join(", ", r.Derive)}] doors: [{string.Join(", ", r.Reach.Concat(r.Storage))}]")
			.ToList();

		offenders.Should().BeEmpty(
			"this file derives or branches on a workspace-memory container key AND holds a door that can "
			+ "act on it — storage the PEP never judged, because the PEP judged whatever the caller NAMED "
			+ "(SandboxContainment.cs states the rule; the production leak of 2026-07-25 is what it costs). "
			+ "Ask the predicate before acting on the derived container: "
			+ "SandboxContainment.PermitsAsync(user, storageKey, catalog, ct) — the key that actually gets "
			+ "read or written, NOT the tenant the caller named — or PermitsWorkspaceAsync for a workspace "
			+ "key. Identity first, containment last, exactly as ProjectScope.EvaluateAsync orders it. Do "
			+ "NOT add an allowlist entry: the list holds sites that predate this guard and it only ever "
			+ "shrinks. If the site cannot see a principal, that is a layering question, not an exemption. "
			+ "Offenders:\n" + string.Join("\n", offenders));
	}

	// The allowlist may only SHRINK. An entry whose file is gone, no longer a site, or now
	// predicated is finished work — leaving the line silently re-grants the exemption to whoever
	// edits that file next.
	[Fact]
	public void AllowlistEntries_AreStillNeeded()
	{
		var rows = Sweep().ToDictionary(r => r.RelPath, StringComparer.Ordinal);

		var stale = Allowlist.Keys
			.Where(key => !rows.TryGetValue(key, out var r) || !r.IsSite || r.IsMechanism || r.Predicated)
			.Order(StringComparer.Ordinal)
			.ToList();

		stale.Should().BeEmpty(
			"these allowlist entries name a file that no longer derives-and-acts without the predicate — "
			+ "converted, moved, or deleted. Delete the line: the allowlist only ever shrinks, and a stale "
			+ "entry both hides finished work and re-grants the exemption to the file's next editor. Stale:\n  "
			+ string.Join("\n  ", stale));
	}

	// The exemption src/PetBox.Core/Data enjoys is "this layer has no caller". Assert it, so the
	// exemption dies the moment it stops being true instead of surviving as a stale assumption.
	[Fact]
	public void TheMechanismLayer_StaysPrincipalFree()
	{
		string[] principalTokens = ["ClaimsPrincipal", "IHttpContextAccessor", "HttpContext"];

		var tainted = ProductSourceFiles()
			.Where(p => Rel(p).StartsWith(MechanismPrefix, StringComparison.Ordinal))
			.Select(p => (Path: Rel(p), Hits: Hits(StripComments(File.ReadAllText(p)), principalTokens)))
			.Where(f => f.Hits.Length > 0)
			.Select(f => $"  {f.Path}: {string.Join(", ", f.Hits)}")
			.ToList();

		tainted.Should().BeEmpty(
			"src/PetBox.Core/Data is exempt from the call-site rule BECAUSE it is principal-free — a "
			+ "mechanism cannot ask a containment question it has no caller to ask about. A principal in "
			+ "the data layer voids that reasoning: either move the identity-aware code up a layer, or "
			+ "make the file ask the predicate and re-scope the exemption. Tainted:\n"
			+ string.Join("\n", tainted));
	}

	// ── THE MACHINE INVENTORY ────────────────────────────────────────────────────────────────────

	// The replacement for the hand-maintained list SandboxContainment.cs used to carry: the live
	// enumeration, written where a human or an agent can read it, reproducible by running this test.
	[Fact]
	public void TheInventory_IsMachineReadable()
	{
		var rows = Sweep();

		var rendered = "# sandbox-containment call sites — generated by SandboxContainmentCallSiteGuardTests\n"
			+ "# site = file that can ACT on a workspace-memory container (reach door, or derive+storage)\n"
			+ string.Join("\n", rows.Select(r =>
				$"{r.Classification}\t{r.RelPath}\tderive=[{string.Join(",", r.Derive)}] reach=[{string.Join(",", r.Reach)}] storage=[{string.Join(",", r.Storage)}]"))
			+ "\n";

		var dir = Path.Combine(RepoRoot(), ".tmp");
		Directory.CreateDirectory(dir);
		File.WriteAllText(Path.Combine(dir, "sandbox-containment-callsites.txt"), rendered);

		rows.Should().NotBeEmpty("an inventory of nothing is a sweep that broke, not a clean tree");
	}

	// ── GUARD THE GUARD ──────────────────────────────────────────────────────────────────────────
	//
	// Every assertion above is an "is empty" — vacuously green if the sweep, the stripper or the
	// patterns ever silently stop seeing the code. Same section, same reason as DbLayerGuardTests.

	[Fact]
	public void TheSweep_ActuallySeesTheKnownSites()
	{
		var rows = Sweep().ToDictionary(r => r.RelPath, StringComparer.Ordinal);

		// The two production surfaces the original hand-list named that ARE sites in this file's
		// sense, each proving a different half of the site definition:
		//   MemoryTools — found via a REACH token (the IWorkspaceMemoryDirectory doors);
		//   MemoryApi   — found via DERIVE + STORAGE (ContainerKeyFor meeting IMemoryService).
		rows.Should().ContainKey("PetBox.Web/Mcp/MemoryTools.cs")
			.WhoseValue.Should().Match<Row>(r => r.IsSite && r.Predicated,
				"the memory verbs derive containers and act on them, and they ask the predicate");
		rows["PetBox.Web/Mcp/MemoryTools.cs"].Reach.Should().NotBeEmpty(
			"MemoryTools calls the directory doors — if the reach tokens cannot see that, the reach "
			+ "half of the site definition is blind");
		rows.Should().ContainKey("PetBox.Web/Memory/MemoryApi.cs")
			.WhoseValue.Should().Match<Row>(r => r.IsSite && r.Predicated && r.Reach.Length == 0,
				"CanonAsync derives a container and hands it to IMemoryService without touching a "
				+ "directory door — the derive+storage half of the definition exists for exactly this "
				+ "shape, the shape of the 2026-07-25 leak");

		// The mechanism is seen and classified as such, not unseen.
		rows.Should().ContainKey("PetBox.Core/Data/WorkspaceMemoryDirectory.cs")
			.WhoseValue.Should().Match<Row>(r => r.IsSite && r.IsMechanism);
		rows.Should().ContainKey("PetBox.Core/Data/WorkspaceMemory.cs")
			.WhoseValue.Should().Match<Row>(r => r.IsSite && r.IsMechanism);

		// The third of the original three call sites carries the predicate but derives nothing —
		// SandboxContainment does the deriving FOR it (PermitsWorkspaceAsync). It must NOT be a
		// site, and the predicate pattern must still see its call: both facts pinned, because a
		// change to either means the wiring this guard reasons about has moved.
		var authorizer = File.ReadAllText(Path.Combine(SrcDir(), "PetBox.Core", "Auth", "TenantAuthorizer.cs"));
		PredicatePattern.IsMatch(authorizer).Should().BeTrue(
			"the central PEP calls SandboxContainment.PermitsWorkspaceAsync — if the predicate pattern "
			+ "cannot see that call, it cannot see one anywhere, and every 'is empty' above is vacuous");
		rows.Should().NotContainKey("PetBox.Core/Auth/TenantAuthorizer.cs",
			"TenantAuthorizer names a workspace, never a container — the derivation lives inside "
			+ "SandboxContainment.PermitsWorkspaceAsync. If it starts deriving, this pin is the prompt "
			+ "to reclassify it rather than let it ride");

		// And the sweep is reading a real tree.
		ProductSourceFiles().Should().HaveCountGreaterThan(200, "the source scan must see the real tree");
	}

	// The detector logic against known snippets, independent of what src/ currently contains —
	// THIS is what proves the patterns work, not that today's tree happens to be clean.
	[Theory]
	// A reach door alone is a site.
	[InlineData("await wsmem.ResolveContainerForProjectAsync(proj, ct);", true)]
	// Derive + storage door is a site.
	[InlineData("IMemoryService m; var c = WorkspaceMemory.ContainerKeyFor(ws);", true)]
	// The literal spelling counts as deriving.
	[InlineData("IMemoryService m; var c = \"$ws-\" + wsKey;", true)]
	[InlineData("IMemoryService m; var c = \"$workspace\";", true)]
	// Deriving with no door in reach is spelling, not acting.
	[InlineData("var url = WorkspaceMemory.ContainerKeyFor(ws) + \"/memory\";", false)]
	// A door named only in a comment is not a door.
	[InlineData("// calls ResolveContainerForProjectAsync( eventually\nvar x = 1;", false)]
	[InlineData("/* \"$workspace\" via IMemoryService */\nvar x = 1;", false)]
	public void Detector_ClassifiesSites(string snippet, bool expectSite)
	{
		Classify("probe.cs", StripComments(snippet)).IsSite.Should().Be(expectSite, snippet);
	}

	[Theory]
	[InlineData("await SandboxContainment.PermitsAsync(ctx.User, key, catalog, ct)", true)]
	[InlineData("await SandboxContainment.PermitsWorkspaceAsync(sandboxOnly, ws, catalog, ct)", true)]
	// The fully qualified spelling must not evade the pattern (DbLayerGuardTests' lesson, pinned).
	[InlineData("await PetBox.Core.Auth.SandboxContainment.PermitsAsync(user, key, catalog, ct)", true)]
	[InlineData("SandboxContainment .PermitsAsync (user, key, catalog, ct)", true)]
	// Naming the class without asking the question is not asking the question.
	[InlineData("// see SandboxContainment.PermitsAsync\nvar x = SandboxContainment.AppliesTo(user);", false)]
	public void Detector_SeesThePredicate(string snippet, bool expectMatch)
	{
		PredicatePattern.IsMatch(StripComments(snippet)).Should().Be(expectMatch, snippet);
	}

	static string RepoRoot()
	{
		var dir = AppContext.BaseDirectory;
		while (!string.IsNullOrEmpty(dir))
		{
			if (Directory.Exists(Path.Combine(dir, "src", "PetBox.Web"))) return dir;
			dir = Path.GetDirectoryName(dir);
		}

		throw new DirectoryNotFoundException("repo root (with src/PetBox.Web) not found walking up from the test bin.");
	}
}
