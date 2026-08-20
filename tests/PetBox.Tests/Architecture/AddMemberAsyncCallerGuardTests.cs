namespace PetBox.Tests.Architecture;

// Guard for work `add-member-hardening-cluster`, defect #3.
//
// AddMemberOutcome carries an ORACLE CONTRACT written as a comment above the enum
// (WorkspaceMembershipService.cs): Added, AlreadyMember and NoSuchUser depend on the Users table
// and MUST render identically to the caller, or a workspace admin can read account existence back
// out of the response — the exact leak add-member-composite-fix closed. WorkspaceUsersModel.
// OnPostAddAsync is the one caller reviewed against that contract today (verified by NDepend at
// triage time, per the card). A SECOND caller that rendered NoSuchUser as an error, say, would
// reopen the oracle, and nothing but a human re-reading the comment would notice — the comment
// enforces nothing by itself.
//
// This is that noticing, mechanised: a text scan over src/ (same technique/tradeoff as
// AdminZoneLinkGuardTests and SandboxContainmentCallSiteGuardTests — a guardrail against an honest
// next caller, not a defense against someone determined to evade a substring match) that fails the
// build the moment a second call site of AddMemberAsync appears, forcing an explicit look at the
// contract before the caller list can grow.
//
// Why a denylist-of-one rather than option (1) from the card (split the result type so the
// table-shaped outcomes are physically unobservable outside the service): a physically-narrower
// type is the stronger fix, but AddMemberOutcome's granularity is exactly what
// AddMemberCompositeFixTests asserts against in fifteen places (Added vs AlreadyMember vs
// NoSuchUser, by name) to prove the transaction and the timing-channel fixes actually did what they
// claim. Narrowing the return type would mean re-deriving every one of those assertions through a
// second, service-internal-only channel — a large, unrelated rewrite for a risk that today has
// exactly one caller. This guard is the cheap fix that matches the actual risk: it costs nothing on
// the path that already exists and it catches precisely the scenario the card is worried about (a
// second caller nobody reviewed).
public sealed class AddMemberAsyncCallerGuardTests
{
	// The one caller the contract is written for. A new entry is never added quietly — see the
	// failure message on KnownCaller_IsTheOnlyCaller for what must happen before one is.
	const string KnownCaller = "PetBox.Web/Pages/Admin/WorkspaceUsers.cshtml.cs";

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

	static string Rel(string src, string path) =>
		Path.GetRelativePath(src, path).Replace(Path.DirectorySeparatorChar, '/');

	// A call site is `.AddMemberAsync(` — the leading dot excludes the interface declaration
	// (`Task<AddMemberOutcome> AddMemberAsync(` in IWorkspaceMembershipService) and the
	// implementation's own signature, neither of which is preceded by a dot.
	static IReadOnlyList<string> CallerFiles()
	{
		var src = SrcDir();
		return [.. Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
			.Where(p => !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
				&& !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
			.Where(p => File.ReadAllText(p).Contains(".AddMemberAsync(", StringComparison.Ordinal))
			.Select(p => Rel(src, p))
			.OrderBy(p => p, StringComparer.Ordinal)];
	}

	[Fact]
	public void KnownCaller_IsTheOnlyCaller()
	{
		var callers = CallerFiles();

		callers.Should().BeEquivalentTo([KnownCaller],
			"IWorkspaceMembershipService.AddMemberAsync returns Added/AlreadyMember/NoSuchUser under an "
			+ "ORACLE CONTRACT (the enum doc on AddMemberOutcome, WorkspaceMembershipService.cs): those "
			+ "three MUST render identically to the caller, or a workspace admin can read account "
			+ "existence back out of the response. WorkspaceUsersModel.OnPostAddAsync is the one caller "
			+ "reviewed against that contract. A new caller found here means: (1) read the contract "
			+ "comment, (2) render the three table-shaped outcomes identically — same status, same "
			+ "text, same redirect, exactly as WorkspaceUsersModel does — and (3) only then add the "
			+ "file to KnownCaller (or widen it to a set) with a comment recording that the review "
			+ "happened. Found: " + string.Join(", ", callers));
	}

	// Guard-the-guard: if the token scan ever silently stopped seeing the known caller, the
	// assertion above would pass by vacuity (an empty "only expected caller" list satisfied by
	// finding nobody) and stop protecting anything.
	[Fact]
	public void TheGuard_ActuallySeesTheKnownCaller()
	{
		var src = SrcDir();
		var path = Path.Combine(src, "PetBox.Web", "Pages", "Admin", "WorkspaceUsers.cshtml.cs");
		File.Exists(path).Should().BeTrue("the known caller's file must exist for this guard to mean anything");

		File.ReadAllText(path).Should().Contain(".AddMemberAsync(",
			"if the token scan cannot see the one known caller, KnownCaller_IsTheOnlyCaller passes "
			+ "vacuously and guards nothing");
	}

	[Theory]
	[InlineData("var outcome = await _members.AddMemberAsync(ws, name, mode, pw, quota, role, ct);", true)]
	[InlineData("await dbf.Memberships().AddMemberAsync(workspaceKey, username, mode, null, null, role);", true)]
	// The declaration and the implementation signature must NOT count as call sites.
	[InlineData("Task<AddMemberOutcome> AddMemberAsync(string workspaceKey, string username);", false)]
	[InlineData("public async Task<AddMemberOutcome> AddMemberAsync(string workspaceKey, string username)", false)]
	// A mention in prose/comments alone is not a call.
	[InlineData("// see AddMemberAsync for the oracle contract", false)]
	public void Detector_ClassifiesCallSites(string snippet, bool expectMatch)
	{
		snippet.Contains(".AddMemberAsync(", StringComparison.Ordinal).Should().Be(expectMatch, snippet);
	}
}
