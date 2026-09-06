namespace PetBox.Core.Contract;

// THE CI RATCHET over the portable baseline roster — src/common/default-agents.json, embedded into
// this assembly at build time and parsed + validated here.
//
// IT IS A RATCHET AND NOTHING ELSE NOW. The server used to also STORE a per-project copy of this
// document (a temporal table, a REST surface, `agent_def_*`, an admin editor) and seed it into every
// project it created. That whole surface is gone — work agent-defs-server-teardown: the wiring kit
// builds its definition from files on disk (base < user < project) and never asks the server for
// one, which left the stored copies as authoritative-looking drift nothing read. What survives is
// this: the shipped baseline is loaded and checked ON BUILD, so a malformed or self-inconsistent
// `default-agents.json` goes red in CI instead of on a user's machine at wire time.
//
// ONE FILE, TWO READERS. The wiring kit reads the SAME src/common/default-agents.json (a build step
// copies it into the npm package — see src/common/README.md and
// src/clients-ts/petbox-wire/scripts/sync-default-agents.mjs). There is no second C# transcription
// to compare against, so this does not ratchet two copies into agreement — it validates that the
// SINGLE source is CORRECT (DefaultAgentDefinitionTests).
//
// LOUD ON LOAD. Parse + validation happen in a Lazy that THROWS: a broken baseline fails the first
// read rather than degrading into something a caller has to notice.
public static class DefaultAgentDefinition
{
	// The document's own name/slug — what the shipped baseline calls itself, asserted by
	// DefaultAgentDefinitionTests.
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
	/// artifact that points at nothing — the failure the kit would otherwise hit at wire time, on
	/// a user's machine). Public so the test suite exercises the real rule rather than a
	/// re-implementation of it.
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
					$"default agent definition: role '{role.Slug}' has no notes — the kit renders this role's " +
					"artifact from that prose, and a role that briefs nobody is the empty skeleton this " +
					"baseline exists to replace");

			// The capability axis, checked against the ONE server-side catalog
			// (AgentDefinitionCapabilities, kept equal to the kit's harness-capabilities.ts by
			// AgentDefinitionCapabilitiesSyncTests). A capability id is only meaningful if the
			// harness matrix declares it: a typo here does not fail anywhere in the kit — it
			// silently makes the role require a capability NO harness has, and the compiler drops
			// the role's artifact on every harness. That is precisely the failure this baseline
			// must not be allowed to ship, so it is a hard error on THIS document (a project's own
			// on-disk layers stay free to name a capability a future harness declares).
			foreach (var capability in role.RequiredCapabilities ?? [])
			{
				if (!AgentDefinitionCapabilities.Set.Contains(capability))
					throw new InvalidOperationException(
						$"default agent definition: role '{role.Slug}' requires capability '{capability}', which no " +
						$"harness declares (known: {string.Join(", ", AgentDefinitionCapabilities.All)}) — add it to " +
						"src/clients-ts/petbox-wire/src/harness-capabilities.ts and AgentDefinitionCapabilities, or fix the typo");
			}
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
