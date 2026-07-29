using PetBox.Web;

namespace PetBox.Tests.Infra;

// The two halves of the stop budget live in different systems and neither can read the other, so
// nothing but a test can keep them consistent. The failure this guards is silent by construction:
// docker SIGKILLs at its own deadline and the app never gets to say it was cut off — the only
// symptom is telemetry that stops arriving (buffered log rows, usage counters, ApiKey.LastUsedAt
// marks) after a deploy.
public sealed class ComposeStopGraceTests
{
	static string FindComposeYaml()
	{
		var dir = AppContext.BaseDirectory;
		while (!string.IsNullOrEmpty(dir))
		{
			var candidate = Path.Combine(dir, "deploy", "compose.yaml");
			if (File.Exists(candidate))
				return candidate;
			dir = Path.GetDirectoryName(dir);
		}
		throw new FileNotFoundException("deploy/compose.yaml not found walking up from test bin");
	}

	// The `petbox` service block only — the backup sidecar has its own (unrelated) stop behaviour,
	// and a grace period found anywhere in the file would be a false pass.
	static string PetBoxServiceBlock()
	{
		var lines = File.ReadAllLines(FindComposeYaml());
		var start = Array.FindIndex(lines, l => l.StartsWith("  petbox:", StringComparison.Ordinal));
		start.Should().BeGreaterThanOrEqualTo(0, "deploy/compose.yaml must still declare a `petbox` service");

		var end = Array.FindIndex(lines, start + 1, l =>
			l.Length > 2 && l[0] == ' ' && l[1] == ' ' && l[2] != ' ' && l.TrimEnd().EndsWith(':'));
		if (end < 0) end = lines.Length;

		return string.Join('\n', lines[start..end]);
	}

	[Fact]
	public void PetBox_Service_Declares_A_Stop_Grace_Period()
	{
		PetBoxServiceBlock().Should().MatchRegex(@"stop_grace_period:\s*\d+s",
			"without it docker falls back to 10 s and kills the app mid-drain — the app's own " +
			"ShutdownTimeout is 30 s and cannot extend docker's patience");
	}

	[Fact]
	public void Stop_Grace_Period_Covers_The_Hosts_Whole_Budget()
	{
		var match = System.Text.RegularExpressions.Regex.Match(
			PetBoxServiceBlock(), @"stop_grace_period:\s*(\d+)s");
		match.Success.Should().BeTrue("the previous test explains this");

		var declared = TimeSpan.FromSeconds(int.Parse(match.Groups[1].Value));
		declared.Should().BeGreaterThanOrEqualTo(ShutdownBudget.MinimumStopGracePeriod,
			"docker's deadline has to outlast ShutdownTimeout ({0}) PLUS the container-disposal tail " +
			"({1}), which runs after every StopAsync has returned and is not covered by the host's " +
			"own timeout — raise this value whenever either of those grows",
			ShutdownBudget.HostShutdownTimeout, ShutdownBudget.DisposalTail);
	}
}
