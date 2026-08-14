namespace PetBox.Tests.Infra;

// Guards the fix for prod-container-diagnosability-gaps #1: before this, `petbox` in
// deploy/compose.yaml declared no mem_limit at all, so every deploy inherited "unlimited" —
// every OOM before 2026-08-12 logged `constraint=CONSTRAINT_NONE` (a HOST-level OOM), taking
// sibling services (Caddy-L4, accel-ppp, 3proxy, yobapub-proxy) and TCP/443 down with it.
// Nothing but a test keeps this from silently regressing back to "no limit" on a future edit —
// same rationale as ComposeStopGraceTests next to this file.
public sealed class ComposeMemoryLimitTests
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

	// The `petbox` service block only — petbox-backup has its own (unrelated) resource profile,
	// and a limit found anywhere else in the file would be a false pass.
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
	public void PetBox_Service_Declares_A_Memory_Limit()
	{
		PetBoxServiceBlock().Should().MatchRegex(@"mem_limit:\s*\$\{PETBOX_MEM_LIMIT:-\S+\}",
			"without it the container inherits `unlimited` and can OOM the whole host " +
			"(constraint=CONSTRAINT_NONE) instead of just itself (constraint=CONSTRAINT_MEMCG) — " +
			"must stay overridable via PETBOX_MEM_LIMIT, matching the VAR-with-fallback substitution " +
			"pattern the rest of this file already uses (see BACKUP_CRON etc.)");
	}

	[Fact]
	public void PetBox_Service_Declares_A_Memswap_Limit()
	{
		PetBoxServiceBlock().Should().MatchRegex(@"memswap_limit:\s*\$\{PETBOX_MEMSWAP_LIMIT:-\S+\}",
			"without it a degraded-but-not-crashed process can swap-thrash on this 1-vCPU host " +
			"for hours instead of hitting a hard ceiling quickly — must stay overridable via " +
			"PETBOX_MEMSWAP_LIMIT");
	}
}
