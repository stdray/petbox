namespace PetBox.Core.Contract;

// THE SERVER'S VIEW of the portable baseline roster, loaded from the ONE canonical copy —
// src/common/default-agents.json, embedded into this assembly at build time. It is what
// ProjectAgentDefSeeder writes into every project the server creates, so a fresh project's
// AUTHORITATIVE definition exists instead of being empty (before this, the kit's very first line
// to a newcomer was "no server-side definition for this project yet — using kit default
// baseline", and there was nothing to edit).
//
// ONE FILE, TWO READERS, NO RATCHET. The wiring kit reads the SAME src/common/default-agents.json
// as its offline fallback (a build step copies it into the npm package — see src/common/README.md
// and src/clients-ts/petbox-wire/scripts/sync-default-agents.mjs). An earlier design kept a C#
// transcription of the kit's TS constant plus a test that compared them; that is a ratchet against
// a divergence that only exists because the copy exists. There is no second copy to diverge now,
// so there is nothing to ratchet — the test that remains validates that the single source is
// CORRECT (DefaultAgentDefinitionTests), not that two of them agree.
//
// LOUD ON LOAD, NOT SILENT AT SEED TIME. Parse + validation happen in a Lazy that throws: a
// malformed or self-inconsistent baseline fails the first read (tests, and the first project
// creation on a server) instead of quietly seeding a project a broken document. The seeder's
// catch-all deliberately does NOT hide this from the tests — DefaultAgentDefinitionTests touches
// Document directly.
//
// NOT A LIVE MIRROR of any project's document: once seeded, a project OWNS its definition and
// diverges freely — that is the point of seeding it. Re-seeding when this baseline moves is a
// separate piece of work (work seed-agent-def-on-project-create names it and defers it).
public static class DefaultAgentDefinition
{
	// The key every seeded roster lands on — the same slug the kit asks the server for
	// (agent-def-fetch.ts's DEFAULT_DEFINITION_KEY).
	public const string Key = "default";

	// Set by PetBox.Core.csproj's <EmbeddedResource LogicalName="...">, so the name does not
	// depend on the file's directory relative to the project (src/common is OUTSIDE it).
	internal const string ResourceName = "PetBox.Core.default-agents.json";

	static readonly Lazy<AgentDefinitionDoc> Loaded = new(Load, isThreadSafe: true);

	public static AgentDefinitionDoc Document => Loaded.Value;

	/// The canonical document's raw bytes as embedded — for a caller that wants the source text
	/// itself (a test asserting on the shipped file, a diagnostics surface) rather than the
	/// parsed record.
	public static string ReadEmbeddedJson()
	{
		using var stream = typeof(DefaultAgentDefinition).Assembly.GetManifestResourceStream(ResourceName)
			?? throw new InvalidOperationException(
				$"embedded resource '{ResourceName}' is missing — src/common/default-agents.json must be " +
				$"an <EmbeddedResource> of PetBox.Core.csproj (found: {string.Join(", ", EmbeddedNames())})");
		using var reader = new StreamReader(stream);
		return reader.ReadToEnd();
	}

	static string[] EmbeddedNames() =>
		typeof(DefaultAgentDefinition).Assembly.GetManifestResourceNames();

	static AgentDefinitionDoc Load()
	{
		// Parse already rejects a `model` property anywhere in the tree and runs the shared
		// field-level Validate — the same floor the kit's validateAgentDefinition applies.
		var doc = AgentDefinitionJson.Parse(ReadEmbeddedJson());
		Validate(doc);
		return doc;
	}

	/// The checks the SHARED schema cannot express and the kit's validateAgentDefinition does not
	/// make: unique slugs, prose that is actually there, and every slug named as a spawn or
	/// escalation target resolving to a role in this same document (a typo there produces a role
	/// artifact that points at nothing). Public so the test suite exercises the real rule rather
	/// than a re-implementation of it.
	public static void Validate(AgentDefinitionDoc doc)
	{
		AgentDefinitionJson.Validate(doc);

		var slugs = new HashSet<string>(StringComparer.Ordinal);
		foreach (var role in doc.Roles)
		{
			if (!slugs.Add(role.Slug))
				throw new InvalidOperationException($"default agent definition: duplicate role slug '{role.Slug}'");
			if (string.IsNullOrWhiteSpace(role.Notes))
				throw new InvalidOperationException(
					$"default agent definition: role '{role.Slug}' has no notes — a seeded roster whose roles " +
					"carry no prose is the empty document this baseline exists to replace");
		}

		foreach (var role in doc.Roles)
		{
			foreach (var target in role.Spawn?.AllowedRoles ?? [])
			{
				if (!slugs.Contains(target))
					throw new InvalidOperationException(
						$"default agent definition: role '{role.Slug}' may spawn '{target}', which is not a role in this document");
			}
			foreach (var target in role.Escalation?.Targets ?? [])
			{
				if (!slugs.Contains(target))
					throw new InvalidOperationException(
						$"default agent definition: role '{role.Slug}' escalates to '{target}', which is not a role in this document");
			}
		}
	}
}
