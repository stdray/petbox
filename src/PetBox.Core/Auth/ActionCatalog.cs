using System.Text.Json;
using System.Text.Json.Serialization;

namespace PetBox.Core.Auth;

// THE CATALOG OF DOMAIN ACTIONS — the reader for Auth/action-catalog.json, the one artifact that
// says WHAT PetBox does and, for each of those things, WHAT KIND OF TARGET it acts on.
//
// IT LIVES IN PRODUCT CODE FOR THE SAME REASON THE ALLOWLIST DOES (read the header of
// TenantEnforcementAllowlist first — this is the same argument, one layer up). A catalog that lives
// in the test assembly is a one-off document again, only written in C#: nothing but the test can
// read it, so nothing but the test can be made to agree with it. The declaration layer that comes
// after this (spec `action-catalog-artifact`, and the `Identity.Abstract` work that reads it) has to
// resolve an action to a TARGET KIND at run time, in product code — so the artifact is embedded here
// and parsed here, and the ratchet is merely one of its readers rather than its owner.
//
// WHAT A RECORD MEANS: one domain action, named by a stable machine `id`, carried by zero or more
// externally reachable SURFACES (`surfaces`, keys in the same `mcp:`/`rest:`/`page:` format the
// authz inventory uses) and/or by a TYPE that executes it with no external surface at all (`bg` for
// a background job, `internal` for an inside step of a synchronous flow — see below). `target` is
// the KIND of thing the action acts on, from a closed list; `targetNote` carries the source's own
// caveat wherever the kind is not one of the four plain ones. `source` is the provenance line of the
// transfer that produced the record, so any row can be traced back to the document it came from.
//
// WHAT `unmapped` MEANS: a LIVE surface with no catalog record behind it, each with a REASON from a
// closed list of three. Two of them are terminal statements — `no-domain-action` (a liveness probe,
// an error page, a transport door) and `ui-preference` (the caller's own view, no domain object
// touched). The third, `not-covered-by-source-pass`, is DEBT: the surface does perform a domain
// action and the transfer simply did not reach it. That third list ONLY SHRINKS, and
// ActionCatalogCompletenessTests.UncataloguedDebt_OnlyShrinks is what makes that a rule rather than
// an intention.
//
// IT MUST CHANGE IN BOTH DIRECTIONS, AND A TEST FORCES BOTH. A new surface with no record and no
// `unmapped` line fails EveryLiveSurface_IsInTheCatalog_OrIsExplicitlyUnmapped; a record naming a
// surface or a type that no longer exists fails EveryCatalogSurface_IsLive /
// EveryTypeBackedAction_NamesALiveType. So the catalog cannot drift quietly in either direction —
// which is the whole reason it is machine-readable instead of a page in doc/.
//
// NUMBERS ARE NEVER WRITTEN IN A COMMENT. The counts (actions, subdomains, target kinds, unmapped by
// reason) are printed by a run into .tmp/action-catalog-inventory.txt. Three independent human counts
// of the MCP surface alone once came back 92 / 100 / 122; that is why nothing here is counted by hand.
//
// LOUD ON LOAD, in the style of DefaultAgentDefinition: parsing happens in a Lazy that THROWS. A
// missing required field, an unknown `target`/`reason` value, or a malformed record fails the first
// read — no silent default that would let a record mean something other than what it says.

/// The KIND of thing a domain action acts on. Closed list — the taxonomy of `.tmp/SCHEMA.md`,
/// serialized kebab-case-lower (`ShareToken` ⇄ "share-token").
public enum ActionTarget
{
	/// No target: a system-level action.
	System,

	/// The target is a workspace.
	Workspace,

	/// The target is a project.
	Project,

	/// The target is a deployment node (claim).
	Node,

	/// The target is the bearer token itself.
	ShareToken,

	/// The target is the caller's own principal (change my own password).
	Self,

	/// Introspection: there is no target at all.
	None,

	/// Two arms at once — the memory cascade.
	ProjectAndWorkspace,

	/// The level is derived rather than named — the LLM registry.
	WorkspaceOrSystem,

	/// A chain of scopes — settings.
	ScopeChain,
}

/// Why a LIVE surface has no catalog record. Closed list of three; the third one is debt.
public enum UnmappedReason
{
	/// The surface performs no domain action: a liveness probe, an error or login page, a static doc
	/// page, a transport door, a discovery document.
	NoDomainAction,

	/// The surface changes only the caller's own presentation (project/workspace switch, board
	/// filters) and touches no domain object.
	UiPreference,

	/// DEBT: the surface DOES perform a domain action, but the transfer pass did not reach it. This
	/// list may only shrink.
	NotCoveredBySourcePass,
}

/// One domain action. <paramref name="Surfaces"/> may be empty only when <paramref name="Bg"/> or
/// <paramref name="Internal"/> names the type that executes it.
public sealed record DomainAction(
	string Id,
	string Source,
	string Subdomain,
	string Action,
	ActionTarget Target,
	string? TargetNote,
	string? TargetSource,
	IReadOnlyList<string> Surfaces,
	IReadOnlyList<string> Bg,
	IReadOnlyList<string> Internal,
	string? Note);

/// A live surface deliberately left out of the catalog, with the reason it is out.
public sealed record UnmappedSurface(string Surface, UnmappedReason Reason, string? Note);

public static class ActionCatalog
{
	// Set by PetBox.Core.csproj's <EmbeddedResource LogicalName="...">. Pinned explicitly there and
	// named explicitly here for the same reason DefaultAgentDefinition does it: the resource name is
	// then independent of where the file sits relative to the project.
	internal const string ResourceName = "PetBox.Core.action-catalog.json";

	// The five values of the §3 taxonomy (`targetSource`). It is a closed list in the schema but a
	// string in this shape — it records HOW the source classified an action's target, which is
	// provenance about the transfer rather than a property of the action. Closed lists are still
	// closed: an unknown value fails the load rather than travelling on as free text.
	static readonly string[] KnownTargetSources = ["named", "derived", "decoy", "coarse", "body"];

	// Enum values are kebab-case-lower on the wire ("share-token", "not-covered-by-source-pass").
	// allowIntegerValues: false — a numeric target in the JSON would be a value nobody wrote on
	// purpose. The converter THROWS on a name outside the enum, which is the strictness the contract
	// asks for: an unknown target kind is a schema change, not a record to be read with a default.
	static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
	{
		Converters = { new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower, allowIntegerValues: false) },
	};

	static readonly Lazy<Catalog> Loaded = new(Load, isThreadSafe: true);

	/// Every domain action the catalog declares.
	public static IReadOnlyList<DomainAction> Actions => Loaded.Value.Actions;

	/// Every live surface the catalog deliberately leaves uncatalogued, with its reason.
	public static IReadOnlyList<UnmappedSurface> Unmapped => Loaded.Value.Unmapped;

	/// Surface key → the actions that CLAIM it. A Razor page is one surface and may legitimately
	/// carry several actions, so the value is a list and not a single id. Ordinal keys, exactly as
	/// AuthzSurface.Key produces them — a lookup that normalized case would be a second key format.
	public static IReadOnlyDictionary<string, IReadOnlyList<string>> SurfaceToActions => Loaded.Value.SurfaceToActions;

	sealed record Catalog(
		IReadOnlyList<DomainAction> Actions,
		IReadOnlyList<UnmappedSurface> Unmapped,
		IReadOnlyDictionary<string, IReadOnlyList<string>> SurfaceToActions);

	static Catalog Load()
	{
		using var doc = JsonDocument.Parse(ReadEmbeddedJson());
		var root = doc.RootElement;

		var actions = ReadArray(root, "actions").Select(ReadAction).ToList();
		var unmapped = ReadArray(root, "unmapped").Select(ReadUnmapped).ToList();

		var index = new Dictionary<string, List<string>>(StringComparer.Ordinal);
		foreach (var action in actions)
		{
			foreach (var surface in action.Surfaces)
			{
				if (!index.TryGetValue(surface, out var claimants))
					index[surface] = claimants = [];
				claimants.Add(action.Id);
			}
		}

		return new Catalog(
			actions,
			unmapped,
			index.ToDictionary(e => e.Key, e => (IReadOnlyList<string>)e.Value, StringComparer.Ordinal));
	}

	static string ReadEmbeddedJson()
	{
		using var stream = typeof(ActionCatalog).Assembly.GetManifestResourceStream(ResourceName)
			?? throw new InvalidOperationException(
				$"embedded resource '{ResourceName}' is missing — Auth/action-catalog.json must be an "
				+ $"<EmbeddedResource> of PetBox.Core.csproj (found: "
				+ $"{string.Join(", ", typeof(ActionCatalog).Assembly.GetManifestResourceNames())})");
		using var reader = new StreamReader(stream);
		return reader.ReadToEnd();
	}

	static DomainAction ReadAction(JsonElement element)
	{
		// The id first, so every later failure can say WHICH record is broken.
		var id = RequireString(element, "id", "action");
		var where = $"action '{id}'";

		var targetSource = OptionalString(element, "targetSource");
		if (targetSource is not null && !KnownTargetSources.Contains(targetSource, StringComparer.Ordinal))
			throw new InvalidOperationException(
				$"action-catalog.json: {where} has targetSource '{targetSource}', which is not one of "
				+ $"{string.Join(" / ", KnownTargetSources)} (the §3 taxonomy is a closed list)");

		return new DomainAction(
			id,
			RequireString(element, "source", where),
			RequireString(element, "subdomain", where),
			RequireString(element, "action", where),
			RequireEnum<ActionTarget>(element, "target", where),
			OptionalString(element, "targetNote"),
			targetSource,
			RequireStringArray(element, "surfaces", where),
			RequireStringArray(element, "bg", where),
			RequireStringArray(element, "internal", where),
			OptionalString(element, "note"));
	}

	static UnmappedSurface ReadUnmapped(JsonElement element)
	{
		var surface = RequireString(element, "surface", "unmapped entry");
		var where = $"unmapped entry '{surface}'";

		return new UnmappedSurface(
			surface,
			RequireEnum<UnmappedReason>(element, "reason", where),
			OptionalString(element, "note"));
	}

	// ── STRICT READS ─────────────────────────────────────────────────────────────────────────────
	//
	// Every one of these throws where a deserializer would hand back a default. That is the point:
	// a record missing its `target` must not read as `system`, and a record missing its `surfaces`
	// must not read as "a background action" — both would be a claim nobody made.

	static JsonElement.ArrayEnumerator ReadArray(JsonElement root, string name)
	{
		if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
			throw new InvalidOperationException(
				$"action-catalog.json: top-level '{name}' must be an array");
		return value.EnumerateArray();
	}

	static string RequireString(JsonElement element, string name, string where)
	{
		if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
			throw new InvalidOperationException(
				$"action-catalog.json: {where} is missing required string '{name}'");

		var text = value.GetString();
		if (string.IsNullOrWhiteSpace(text))
			throw new InvalidOperationException(
				$"action-catalog.json: {where} has an empty '{name}' — a blank required field is a record "
				+ "nobody finished writing, not a record that says nothing");
		return text;
	}

	static string? OptionalString(JsonElement element, string name) =>
		element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
			? value.GetString()
			: null;

	static List<string> RequireStringArray(JsonElement element, string name, string where)
	{
		if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
			throw new InvalidOperationException(
				$"action-catalog.json: {where} is missing required array '{name}' (an empty array is how a "
				+ "record says it has none — an absent property is how it says nothing at all)");

		var items = new List<string>();
		foreach (var item in value.EnumerateArray())
		{
			if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()))
				throw new InvalidOperationException(
					$"action-catalog.json: {where} has a non-string or blank entry in '{name}'");
			items.Add(item.GetString()!);
		}

		return items;
	}

	static TEnum RequireEnum<TEnum>(JsonElement element, string name, string where) where TEnum : struct, Enum
	{
		if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
			throw new InvalidOperationException(
				$"action-catalog.json: {where} is missing required '{name}'");

		try
		{
			return value.Deserialize<TEnum>(Json);
		}
		catch (JsonException ex)
		{
			throw new InvalidOperationException(
				$"action-catalog.json: {where} has '{name}' = {value.GetRawText()}, which is not one of "
				+ $"{string.Join(" / ", Enum.GetNames<TEnum>())} (kebab-case-lower on the wire). The list is "
				+ "CLOSED — a new value is a schema change, not a line in a record.", ex);
		}
	}
}
