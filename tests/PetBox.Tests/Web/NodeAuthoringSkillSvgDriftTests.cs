using System.Text.RegularExpressions;
using PetBox.Web.Rendering;

namespace PetBox.Tests.Web;

// Drift guard for the petbox-node-authoring skill
// (src/clients-ts/petbox-wire/src/templates/petbox-node-authoring/SKILL.md). The kit ships that
// SKILL.md — and NOTHING else of the template dir (skill-files.ts reads only SKILL.md and writes
// one file per surface) — into wired projects that have NO PetBox sources at all. Its prose list
// and its embedded validator's constants are the only rules the author there ever sees. When
// MarkdownRenderer's SvgTags changes and the skill is forgotten, authors in those projects are
// taught a stale allowlist with nothing to catch it — this test is that catch. It reads the
// SKILL.md template from disk (repo root found by walking up from the test bin) and pins both the
// prose declaration and the validator's machine-parseable constants against the sanitizer itself.

public sealed class NodeAuthoringSkillSvgDriftTests
{
	static string RepoRoot()
	{
		var dir = AppContext.BaseDirectory;
		while (!string.IsNullOrEmpty(dir))
		{
			if (File.Exists(Path.Combine(dir, "src", "clients-ts", "petbox-wire", "src", "templates",
				"petbox-node-authoring", "SKILL.md"))) return dir;
			dir = Path.GetDirectoryName(dir);
		}
		throw new DirectoryNotFoundException(
			"petbox-wire's petbox-node-authoring template not found walking up from the test bin");
	}

	static readonly string SkillText = File.ReadAllText(Path.Combine(
		RepoRoot(), "src", "clients-ts", "petbox-wire", "src", "templates",
		"petbox-node-authoring", "SKILL.md"));

	// The prose declaration in skill section (c): a backtick span beginning with the word "svg".
	static readonly Regex ProseList = new(@"`svg [^`]+`", RegexOptions.Compiled);

	// The embedded validator's machine-parseable constants. SKILL.md must keep them single-line,
	// double-quoted, in exactly this shape — the skill itself says so in the validator's header
	// comment, and this test is the reason why.
	static readonly Regex ValidatorAllowed =
		new(@"const ALLOWED_TAGS = ""([^""]+)"";", RegexOptions.Compiled);
	static readonly Regex ValidatorForbidden =
		new(@"const FORBIDDEN_TAGS = ""([^""]+)"";", RegexOptions.Compiled);

	// Split on ANY whitespace run: the prose list in (c) wraps across a source line, so a plain
	// space-split would merge the two tags around the newline into one token. Casing is kept —
	// `foreignObject` must survive intact (SvgTags itself is OrdinalIgnoreCase, so set comparison
	// is case-insensitive on its side).
	static HashSet<string> Split(string list) => list
		.Split([" ", "\r\n", "\n", "\t", "\r"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
		.ToHashSet();

	[Fact]
	public void SkillProseList_MatchesSanitizerAllowlist()
	{
		var match = ProseList.Match(SkillText);
		Assert.True(match.Success,
			"SKILL.md section (c) must declare the SVG tag list in one backtick span starting with 'svg '");
		Split(match.Value.Trim('`')).Should().BeEquivalentTo(MarkdownRenderer.SvgTags,
			"the skill is shipped to projects that cannot read MarkdownRenderer — its prose allowlist must be pinned to it");
	}

	[Fact]
	public void ValidatorAllowedTags_MatchSanitizerAllowlist()
	{
		var match = ValidatorAllowed.Match(SkillText);
		Assert.True(match.Success,
			"the embedded validator must carry a machine-parseable single-line ALLOWED_TAGS constant");
		Split(match.Groups[1].Value).Should().BeEquivalentTo(MarkdownRenderer.SvgTags,
			"the validator is the author's only pre-write check — a stale allowlist would pass drafts the server will strip");
	}

	[Fact]
	public void ForbiddenTags_AreAbsentFromAllowlist_AndPresentInValidator()
	{
		string[] forbidden = ["script", "style", "foreignObject", "image"];
		var match = ValidatorForbidden.Match(SkillText);
		Assert.True(match.Success,
			"the embedded validator must carry a machine-parseable single-line FORBIDDEN_TAGS constant");
		Split(match.Groups[1].Value).Should().BeEquivalentTo(forbidden,
			"the validator's forbidden list is the author-facing statement of what the sanitizer strips outright");
		foreach (var tag in forbidden)
		{
			MarkdownRenderer.SvgTags.Should().NotContain(tag,
				$"'{tag}' must never join the sanitizer allowlist (see MarkdownRenderer's own exclusion notes)");
			Split(ValidatorAllowed.Match(SkillText).Groups[1].Value).Should().NotContain(tag,
				$"'{tag}' must never appear in the validator's allowlist either");
		}
	}
}
