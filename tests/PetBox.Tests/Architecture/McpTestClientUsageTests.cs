namespace PetBox.Tests.Architecture;

// The lock on McpTestClient (work item gate-flake-parallel-builds).
//
// The flake this closes did not come from any one fixture being wrong — it came from 25 fixtures
// each spelling out `McpClient.CreateAsync(transport, cancellationToken: default)` by hand, so the
// SDK's default 60-second InitializationTimeout applied everywhere and nowhere was it a decision.
// Twenty-five point edits would have fixed today's 25 sites and lost the property again on the
// 26th fixture, which will be written by copying the fixture next door.
//
// So the shape is the thing being defended, not the sites: a bare `McpClient.CreateAsync` under
// tests/ fails here, by name, with the reason.
public sealed class McpTestClientUsageTests
{
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

	// The ONE file allowed to call the SDK factory: the helper itself.
	const string HelperFile = "McpTestClient.cs";

	// Assembled, not written out, so this guard does not flag ITSELF as an offender — and so the
	// exemption list stays at exactly one file (the helper) rather than growing to include every
	// file that merely talks about the pattern.
	const string Needle = "McpClient" + ".CreateAsync(";

	static IEnumerable<(string Path, string Text)> TestSources()
	{
		var root = RepoRoot();
		return Directory
			.EnumerateFiles(Path.Combine(root, "tests"), "*.cs", SearchOption.AllDirectories)
			.Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
			.Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
			.Select(f => (Path.GetRelativePath(root, f), File.ReadAllText(f)));
	}

	[Fact]
	public void NoTestCallsTheSdkFactoryDirectly_TheyAllGoThroughMcpTestClient()
	{
		// `McpClient.CreateAsync(` — the CALL, not the words. Prose mentioning the method (this
		// file, the helper's own comment, McpProtocolVersionGateTests explaining why it uses raw
		// JSON-RPC instead) must not trip the guard, so lines that are comments are skipped.
		var offenders = TestSources()
			.Where(s => !s.Path.EndsWith(HelperFile, StringComparison.Ordinal))
			.SelectMany(s => s.Text
				.Split('\n')
				.Select((line, i) => (s.Path, No: i + 1, Line: line.Trim()))
				.Where(l => !l.Line.StartsWith("//", StringComparison.Ordinal))
				.Where(l => l.Line.Contains(Needle, StringComparison.Ordinal)))
			.Select(l => $"{l.Path}:{l.No}")
			.ToList();

		offenders.Should().BeEmpty(
			"every MCP session in tests/ must be opened through PetBox.Tests.Support.McpTestClient.ConnectAsync, "
			+ "which is the single place that sets McpClientOptions.InitializationTimeout. A direct "
			+ "McpClient.CreateAsync silently takes the SDK default of 60 seconds, and under the parallel "
			+ "load of several agents each running ./build.ps1 -Target Test, that budget is what kills whole "
			+ "test classes in their fixture's InitializeAsync with no assert message at all. Offenders: "
			+ string.Join(", ", offenders));
	}

	[Fact]
	public void TheGuard_ActuallyReadsTheTestSources()
	{
		// Without this, deleting the tests/ path or breaking RepoRoot() would make the check above
		// pass by scanning nothing — the same failure mode the Slow-category guard next door names.
		var sources = TestSources().ToList();

		sources.Should().NotBeEmpty("the guard scans tests/**/*.cs — an empty scan is a broken guard, not a clean repo");

		sources.Should().Contain(s => s.Path.EndsWith(HelperFile, StringComparison.Ordinal),
			"the helper itself must be among the scanned files, otherwise the exemption above is exempting nothing");

		sources.Count(s => s.Text.Contains("McpTestClient.ConnectAsync(", StringComparison.Ordinal))
			.Should().BeGreaterThan(1,
				"the helper is supposed to be in real use; if the call sites vanished, this guard would be "
				+ "protecting a property nothing depends on any more");
	}
}
