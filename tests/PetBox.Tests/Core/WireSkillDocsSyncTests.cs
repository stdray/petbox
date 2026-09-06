using System.Text.RegularExpressions;

namespace PetBox.Tests.AgentDefs;

// The skill list in doc/agent-wiring.md and src/PetBox.Web/Pages/Doc/content/wire.md drifted away
// from the delivered set TWICE (observation wire-docs-skill-list-stale, then work
// docs-catch-up-with-kit-and-get-pinned): a skill was added to the kit and the prose was not
// touched, so a reader was told about five skills while the wire installed eight. The first fix was
// a hand-edited list with no ratchet behind it — which is precisely why there was a second drift.
//
// The kit already pins two links of the chain from inside its own npm package
// (src/clients-ts/petbox-wire/src/skill-files.test.ts): templates/ <-> PROJECT_SKILLS in both
// directions, and PROJECT_SKILLS -> the kit's own README.md. It deliberately stops there: this file
// and the Doc-site page live OUTSIDE the npm package, and reaching across that boundary from a test
// that ships inside the package was judged not worth the coupling.
//
// So the last link is closed from the other side, exactly like AgentDefinitionCapabilitiesSyncTests
// reads harness-capabilities.ts from .NET: this test takes the template directory names off the
// FILESYSTEM (no TypeScript parsing — the directory listing IS the delivered set, and the kit's own
// tests already tie it to PROJECT_SKILLS) and requires each name to appear in both documents. The
// full chain is then templates/ <-> PROJECT_SKILLS (kit test) <-> the two documents (this test).
//
// SCOPE, stated so nobody mistakes it for more: this is a NAME-PRESENCE ratchet. It proves the eight
// names are mentioned. It cannot prove that what the prose says ABOUT them is true — a false claim
// passes it (that is how the README's since-corrected claim about `petbox-digest` survived). Correct
// behaviour descriptions stay a human review duty.
public sealed class WireSkillDocsSyncTests
{
	const string TemplatesRelPath = "src/clients-ts/petbox-wire/src/templates";

	static readonly string[] DocsThatMustListEverySkill =
	[
		"doc/agent-wiring.md",
		"src/PetBox.Web/Pages/Doc/content/wire.md",
	];

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

	static List<string> DeliveredSkillNames(string repoRoot) =>
		Directory.GetDirectories(Path.Combine(repoRoot, Path.Combine(TemplatesRelPath.Split('/'))))
			.Select(Path.GetFileName)
			.Where(n => !string.IsNullOrEmpty(n))
			.Select(n => n!)
			.OrderBy(n => n, StringComparer.Ordinal)
			.ToList();

	// A skill name counts as mentioned only as a WHOLE token: neither a letter/digit nor a hyphen may
	// sit on either side. Without that, "petbox-node-authoring" in the prose would silently satisfy a
	// check for a skill named "petbox" or "petbox-node" — a rename could then land with no document
	// touched at all, which is the failure this test exists to catch.
	static bool MentionsSkill(string text, string skill) =>
		Regex.IsMatch(text, $"(?<![A-Za-z0-9-]){Regex.Escape(skill)}(?![A-Za-z0-9-])");

	[Fact]
	public void EveryDeliveredSkill_IsNamedInBothWiringDocuments()
	{
		var repoRoot = RepoRoot();
		var skills = DeliveredSkillNames(repoRoot);

		skills.Should().NotBeEmpty($"{TemplatesRelPath} must hold the delivered skill templates — an empty listing means this test resolved the wrong directory and is silently proving nothing");

		foreach (var doc in DocsThatMustListEverySkill)
		{
			var path = Path.Combine(repoRoot, Path.Combine(doc.Split('/')));
			File.Exists(path).Should().BeTrue($"{doc} must exist — it is one of the two documents pinned to the delivered skill set");
			var text = File.ReadAllText(path);

			var missing = skills.Where(s => !MentionsSkill(text, s)).ToList();
			missing.Should().BeEmpty(
				$"{doc} must name every skill the kit installs. Missing: {string.Join(", ", missing)}. " +
				$"A skill was added to {TemplatesRelPath}/ (and to PROJECT_SKILLS, which the kit's own test pins) " +
				"without the documentation catching up — that exact drift has already happened twice.");
		}
	}

	// The matcher itself, proven on synthetic text, so a red run above means real drift rather than a
	// regex that stopped matching the documents' actual formatting.
	[Fact]
	public void MentionsSkill_RequiresAWholeToken()
	{
		MentionsSkill("`.claude/skills/petbox-card-check/SKILL.md`", "petbox-card-check").Should().BeTrue();
		MentionsSkill("**petbox-factory-run** drives a batch", "petbox-factory-run").Should().BeTrue();
		MentionsSkill("the `petbox` skill", "petbox").Should().BeTrue();

		// A longer name must not count as a mention of its prefix, in either direction.
		MentionsSkill("only petbox-node-authoring is listed", "petbox").Should().BeFalse();
		MentionsSkill("only petbox-node-authoring is listed", "petbox-node").Should().BeFalse();
		MentionsSkill("only petbox-node is listed", "petbox-node-authoring").Should().BeFalse();
		MentionsSkill("nothing relevant here", "petbox-card-check").Should().BeFalse();
	}
}
