using System.Text.Json;
using PetBox.Core.Contract;

namespace PetBox.Tests.AgentDefs;

// Work seed-agent-def-on-project-create: src/common/default-agents.json is THE canonical portable
// roster — one file, two readers. The .NET server embeds it (PetBox.Core.csproj) and seeds it into
// every project it creates (ProjectAgentDefSeeder); the wiring kit copies it into its npm package
// and exports it as DEFAULT_AGENT_DEFINITION, its offline fallback.
//
// THIS IS NOT A DRIFT RATCHET, and the difference matters. An earlier design kept a C#
// transcription of the kit's TS constant and compared the two; comparing copies only guards a
// problem the copies create. With one file there is nothing to compare — so what a test can still
// be useful for is whether the SINGLE SOURCE IS CORRECT. Everything below asks that question:
// it parses, it carries the roles the wiring expects, its cross-references resolve, its prose is
// actually there, and it stays portable (no model binding).
//
// The rules live in DefaultAgentDefinition.Validate (which runs on LOAD, so a broken document
// fails the first read on a server too, rather than being seeded into somebody's project). The
// tests exercise THAT method — they do not re-implement it, which would just be another copy.
public sealed class DefaultAgentDefinitionTests
{
	// The roster the wiring expects to find. Not a style preference: petbox-wire renders one agent
	// artifact per role, and a harness roster that silently lost `reserve` or `worker-highstakes`
	// would fail as "the orchestrator spawns a role that does not exist" much later and elsewhere.
	static readonly string[] ExpectedSlugs =
		["orchestrator", "worker", "worker-highstakes", "utility", "reserve", "explore"];

	// Tier is FREE TEXT everywhere else in the system (the admin form takes any string, and there
	// is no tier enum in either the C# contract or the kit), so this is deliberately a check on THIS
	// document rather than a new global rule: the canonical roster's tiers must be the four it
	// actually uses, which is what turns a typo ("wroker") into a red build.
	static readonly string[] KnownTiers = ["orchestrator", "worker", "utility", "reserve"];

	static string CanonicalFileOnDisk()
	{
		var dir = AppContext.BaseDirectory;
		while (!string.IsNullOrEmpty(dir))
		{
			var candidate = Path.Combine(dir, "src", "common", "default-agents.json");
			if (File.Exists(candidate)) return candidate;
			dir = Path.GetDirectoryName(dir);
		}
		throw new FileNotFoundException("src/common/default-agents.json not found walking up from the test bin.");
	}

	[Fact]
	public void CanonicalJson_IsEmbedded_AndIsTheFileOnDisk()
	{
		var embedded = DefaultAgentDefinition.ReadEmbeddedJson();
		embedded.Should().NotBeNullOrWhiteSpace();

		var act = () => JsonDocument.Parse(embedded).Dispose();
		act.Should().NotThrow("the embedded canonical roster must be valid JSON");

		// Guards the csproj wiring itself: an <EmbeddedResource> pointing at the wrong path (or a
		// stale copy left inside the project directory) would still produce a green parse above.
		JsonNormalize(embedded).Should().Be(JsonNormalize(File.ReadAllText(CanonicalFileOnDisk())),
			"the resource embedded into PetBox.Core must be src/common/default-agents.json itself, " +
			"not a second copy that can drift from it");
	}

	[Fact]
	public void Document_CarriesTheExpectedRoles_WithUniqueSlugs()
	{
		var doc = DefaultAgentDefinition.Document;

		doc.Name.Should().Be(DefaultAgentDefinition.Key);
		doc.Roles.Select(r => r.Slug).Should().BeEquivalentTo(ExpectedSlugs,
			"petbox-wire renders one agent artifact per role and the protocol prose names these by slug");
		doc.Roles.Select(r => r.Slug).Should().OnlyHaveUniqueItems();
	}

	[Fact]
	public void EveryRole_CarriesItsRequiredFields_AndRealProse()
	{
		foreach (var role in DefaultAgentDefinition.Document.Roles)
		{
			role.Slug.Should().NotBeNullOrWhiteSpace();
			role.Tier.Should().NotBeNullOrWhiteSpace();
			role.Tier.Should().BeOneOf(KnownTiers, "a mistyped tier is invisible until an artifact is rendered");
			role.RequiredCapabilities.Should().NotBeNull("the field is required, though it may be empty");
			role.Notes.Should().NotBeNullOrWhiteSpace(
				$"role '{role.Slug}' with no notes is the empty definition this baseline exists to replace — " +
				"a seeded project must get usable prose, not a skeleton");
		}
	}

	[Fact]
	public void SpawnAndEscalationTargets_ResolveToRolesInThisDocument()
	{
		var doc = DefaultAgentDefinition.Document;
		var slugs = doc.Roles.Select(r => r.Slug).ToHashSet(StringComparer.Ordinal);

		foreach (var role in doc.Roles)
		{
			foreach (var target in role.Spawn?.AllowedRoles ?? [])
				slugs.Should().Contain(target, $"role '{role.Slug}' claims it may spawn '{target}'");
			foreach (var target in role.Escalation?.Targets ?? [])
				slugs.Should().Contain(target, $"role '{role.Slug}' claims it escalates to '{target}'");
		}
	}

	[Fact]
	public void Document_CarriesNoModelBinding_Anywhere()
	{
		// Model binding is LOCAL (~/.petbox/roles.json); a portable definition carrying one is
		// rejected by both sides. AgentDefinitionJson.Parse walks the whole tree, so this asserts
		// the real rule rather than grepping the text.
		var act = () => AgentDefinitionJson.Parse(DefaultAgentDefinition.ReadEmbeddedJson());
		act.Should().NotThrow();

		DefaultAgentDefinition.ReadEmbeddedJson().Should().NotContain("\"model\"");
	}

	// ── the validator itself, proven against synthetic documents ──────────────────────────────
	// A green suite above must mean "the canonical file is sound", not "Validate never says no".

	[Fact]
	public void Validate_RejectsADuplicateSlug()
	{
		var act = () => DefaultAgentDefinition.Validate(new AgentDefinitionDoc("d",
			[Role("worker"), Role("worker")]));

		act.Should().Throw<InvalidOperationException>().WithMessage("*duplicate role slug 'worker'*");
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("   ")]
	public void Validate_RejectsARoleWithoutNotes(string? notes)
	{
		var act = () => DefaultAgentDefinition.Validate(new AgentDefinitionDoc("d",
			[Role("worker") with { Notes = notes }]));

		act.Should().Throw<InvalidOperationException>().WithMessage("*has no notes*");
	}

	[Fact]
	public void Validate_RejectsASpawnTargetThatIsNotARole()
	{
		var act = () => DefaultAgentDefinition.Validate(new AgentDefinitionDoc("d",
			[Role("orchestrator") with { Spawn = new AgentDefinitionSpawn(true, ["wroker"]) }]));

		act.Should().Throw<InvalidOperationException>().WithMessage("*may spawn 'wroker'*");
	}

	[Fact]
	public void Validate_RejectsAnEscalationTargetThatIsNotARole()
	{
		var act = () => DefaultAgentDefinition.Validate(new AgentDefinitionDoc("d",
			[Role("worker") with { Escalation = new AgentDefinitionEscalation(true, ["nobody"]) }]));

		act.Should().Throw<InvalidOperationException>().WithMessage("*escalates to 'nobody'*");
	}

	[Fact]
	public void Validate_RejectsARoleWithoutATier()
	{
		var act = () => DefaultAgentDefinition.Validate(new AgentDefinitionDoc("d",
			[Role("worker") with { Tier = "  " }]));

		act.Should().Throw<ArgumentException>().WithMessage("*tier is required*");
	}

	[Fact]
	public void Validate_AcceptsTheCanonicalDocument()
	{
		var act = () => DefaultAgentDefinition.Validate(DefaultAgentDefinition.Document);
		act.Should().NotThrow();
	}

	static AgentDefinitionRole Role(string slug) => new(slug, "worker", [], Notes: "1. something");

	// Compare MEANING, not bytes: the on-disk file and the embedded stream can legitimately differ
	// in line endings (git autocrlf) without being different documents.
	static string JsonNormalize(string json)
	{
		using var doc = JsonDocument.Parse(json);
		return JsonSerializer.Serialize(doc.RootElement);
	}
}
