using System.Text.RegularExpressions;

namespace PetBox.Tests.AgentDefs;

// AGENTS.md tells the agent about the skills `disable-model-invocation: true` hides from its own
// skill listing ENTIRELY, not just from auto-invocation (work
// user-invocable-skills-invisible-to-model, umbrella umbrella-agent-text-names-both-axes). Found
// live 2026-09-07: the owner typed `/petbox-factory-run` and the agent had no such name in its
// listing, so it had to `find` the file on disk instead — the exact move the agent's own "don't
// guess" instruction forbids.
//
// A hand-written name list drifts from the flag the moment either side changes without the other
// (observation skill-constants-drift-unguarded: "text authoritatively lies once the code moves
// on" — see WireSkillDocsSyncTests for the same failure mode on a different pair of documents).
// This test is that ratchet: it recomputes the flagged set straight off the template frontmatter
// and requires AGENTS.md's dedicated section to name EXACTLY that set — neither a stale extra
// entry (the flag was lifted and AGENTS.md forgot) nor a missing one (a skill was newly hidden and
// AGENTS.md was never told).
public sealed class AgentsMdOwnerOnlySkillsSyncTests
{
	const string TemplatesRelPath = "src/clients-ts/petbox-wire/src/templates";
	const string AgentsMdRelPath = "AGENTS.md";
	const string SectionHeading = "## Skills invoked by name only (hidden from your own listing)";

	static string RepoRoot()
	{
		var dir = AppContext.BaseDirectory;
		while (!string.IsNullOrEmpty(dir))
		{
			if (Directory.Exists(Path.Combine(dir, Path.Combine(TemplatesRelPath.Split('/'))))) return dir;
			dir = Path.GetDirectoryName(dir);
		}
		throw new DirectoryNotFoundException($"{TemplatesRelPath} not found walking up from the test bin.");
	}

	static List<string> OwnerOnlySkillNames(string repoRoot)
	{
		var templatesDir = Path.Combine(repoRoot, Path.Combine(TemplatesRelPath.Split('/')));
		return Directory.GetDirectories(templatesDir)
			.Where(dir =>
			{
				var skillFile = Path.Combine(dir, "SKILL.md");
				return File.Exists(skillFile)
					&& Regex.IsMatch(File.ReadAllText(skillFile), @"^disable-model-invocation:\s*true\s*$", RegexOptions.Multiline);
			})
			.Select(Path.GetFileName)
			.Where(n => !string.IsNullOrEmpty(n))
			.Select(n => n!)
			.OrderBy(n => n, StringComparer.Ordinal)
			.ToList();
	}

	// Pulls just the dedicated section out of AGENTS.md — not the whole file — so a name that
	// merely happens to appear elsewhere (prose, an unrelated example) can never count as part of
	// the authoritative listing.
	static string ExtractSection(string agentsMd)
	{
		var start = agentsMd.IndexOf(SectionHeading, StringComparison.Ordinal);
		start.Should().BeGreaterThanOrEqualTo(0, $"AGENTS.md must contain the heading '{SectionHeading}'");
		var afterHeading = start + SectionHeading.Length;
		var rest = agentsMd[afterHeading..];
		var nextHeading = Regex.Match(rest, @"^## ", RegexOptions.Multiline);
		return nextHeading.Success ? rest[..nextHeading.Index] : rest;
	}

	static List<string> BulletedSkillNames(string section) =>
		Regex.Matches(section, @"^- \*\*([a-z0-9-]+)\*\*", RegexOptions.Multiline)
			.Select(m => m.Groups[1].Value)
			.OrderBy(n => n, StringComparer.Ordinal)
			.ToList();

	[Fact]
	public void AgentsMdOwnerOnlySection_NamesExactlyTheFlaggedSkills()
	{
		var repoRoot = RepoRoot();
		var flagged = OwnerOnlySkillNames(repoRoot);
		flagged.Should().NotBeEmpty(
			"at least one template currently carries disable-model-invocation: true — an empty " +
			"set means this test resolved the wrong directory and is silently proving nothing");

		var agentsMdPath = Path.Combine(repoRoot, AgentsMdRelPath);
		File.Exists(agentsMdPath).Should().BeTrue();
		var section = ExtractSection(File.ReadAllText(agentsMdPath));
		var listed = BulletedSkillNames(section);

		listed.Should().Equal(flagged,
			$"AGENTS.md's '{SectionHeading}' section must name EXACTLY the skills currently " +
			$"flagged disable-model-invocation: true in {TemplatesRelPath} — no more (a stale entry " +
			"left after the flag was lifted), no fewer (a newly hidden skill AGENTS.md was never " +
			$"told about). Flagged: [{string.Join(", ", flagged)}]. Listed: [{string.Join(", ", listed)}].");
	}
}
