using System.Text.Json;
using System.Text.RegularExpressions;
using LinqToDB;
using PetBox.Core.Contract;
using PetBox.Core.Data;
using PetBox.Core.Data.Temporal;

namespace PetBox.Core.Services;

// Project-scoped portable agent-definition store (agent-definition-as-data).
// Temporal SCD-2 documents in the main Core DB (NOT the Tasks DB — no feature coupling).
// Selection key note: server stores portable definitions only; owner ($HOME) and
// active profile / role→model binding are out of scope here.
public interface IAgentDefinitionService
{
	Task<IReadOnlyList<AgentDefinitionListItem>> ListAsync(string projectKey, CancellationToken ct = default);
	Task<AgentDefinitionView?> GetAsync(string projectKey, string key, CancellationToken ct = default);
	// The RAW stored document (properties outside the typed schema included) — what the raw
	// write path persisted, verbatim. Null when the key is unknown.
	Task<string?> GetJsonAsync(string projectKey, string key, CancellationToken ct = default);
	Task<AgentDefinitionAck> UpsertAsync(string projectKey, string key, AgentDefinitionDoc definition, long version, CancellationToken ct = default);
	// Accepts raw JSON so the model-field reject runs on the wire shape (not only typed records),
	// and so unknown properties SURVIVE the store instead of being dropped by re-serialization.
	Task<AgentDefinitionAck> UpsertJsonAsync(string projectKey, string key, string json, long version, CancellationToken ct = default);
	Task<AgentDefinitionAck> DeleteAsync(string projectKey, string key, long version, CancellationToken ct = default);
}

public sealed partial class AgentDefinitionService : IAgentDefinitionService
{
	// A FACTORY, not a context: every method opens its own connection and disposes it, so no
	// DataConnection outlives a call or is reachable from two threads (a linq2db DataConnection is
	// not thread-safe; the scoped PetBoxDb this replaces was shared across a request's fan-out).
	readonly ICoreDbFactory _factory;

	// Same slug shape as boards/nodes/methodology template keys.
	[GeneratedRegex(@"^[a-z][a-z0-9_-]{0,99}$")]
	private static partial Regex SlugRegex();

	public AgentDefinitionService(ICoreDbFactory factory) => _factory = factory;

	public async Task<IReadOnlyList<AgentDefinitionListItem>> ListAsync(string projectKey, CancellationToken ct = default)
	{
		var pk = RequireProjectKey(projectKey);
		using var db = _factory.Open();
		var rows = await db.AgentDefinitions
			.Where(r => r.ProjectKey == pk && r.ActiveTo == null)
			.OrderBy(r => r.Key)
			.ToListAsync(ct);
		return rows.Select(r =>
		{
			var def = Deserialize(pk, r);
			return new AgentDefinitionListItem(r.Key, def.Name, r.Version, r.Updated);
		}).ToList();
	}

	public async Task<AgentDefinitionView?> GetAsync(string projectKey, string key, CancellationToken ct = default)
	{
		var pk = RequireProjectKey(projectKey);
		var k = NormalizeKey(key);
		using var db = _factory.Open();
		var row = await db.AgentDefinitions
			.FirstOrDefaultAsync(r => r.ProjectKey == pk && r.Key == k && r.ActiveTo == null, ct);
		if (row is null) return null;
		var def = Deserialize(pk, row);
		return new AgentDefinitionView(k, def, row.Version, row.Created, row.Updated);
	}

	public async Task<string?> GetJsonAsync(string projectKey, string key, CancellationToken ct = default)
	{
		var pk = RequireProjectKey(projectKey);
		var k = NormalizeKey(key);
		using var db = _factory.Open();
		var row = await db.AgentDefinitions
			.FirstOrDefaultAsync(r => r.ProjectKey == pk && r.Key == k && r.ActiveTo == null, ct);
		return row?.Json;
	}

	// TYPED path: the record IS the document — re-serializing it loses nothing it could carry.
	public Task<AgentDefinitionAck> UpsertAsync(
		string projectKey, string key, AgentDefinitionDoc definition, long version, CancellationToken ct = default)
	{
		var pk = RequireProjectKey(projectKey);
		var k = NormalizeKey(key);
		// Prefer the document's own name when present; fall back to the key slug.
		var def = string.IsNullOrWhiteSpace(definition.Name) ? definition with { Name = k } : definition;
		AgentDefinitionJson.Validate(def);
		return UpsertCoreAsync(pk, k, AgentDefinitionJson.Serialize(def), version, ct);
	}

	// RAW path: store the caller's document VERBATIM (canonical whitespace only), so properties
	// outside the typed schema — `notes` and friends — survive instead of being silently dropped.
	// Validation is unchanged: `model` anywhere is rejected, the schema's required fields hold.
	public Task<AgentDefinitionAck> UpsertJsonAsync(
		string projectKey, string key, string json, long version, CancellationToken ct = default)
	{
		var pk = RequireProjectKey(projectKey);
		var k = NormalizeKey(key);
		var canonical = AgentDefinitionJson.CanonicalizeRaw(json, nameFallback: k);
		AgentDefinitionJson.Parse(canonical); // reject `model` / enforce the schema — throws on bad
		return UpsertCoreAsync(pk, k, canonical, version, ct);
	}

	// pk/k are already normalized by the caller (RequireProjectKey / NormalizeKey).
	async Task<AgentDefinitionAck> UpsertCoreAsync(
		string pk, string k, string storedJson, long version, CancellationToken ct)
	{
		var row = new AgentDefinitionRow
		{
			ProjectKey = pk,
			Key = k,
			Version = version,
			Json = storedJson,
		};

		using var db = _factory.Open();
		var r = await TemporalStore.UpsertAsync(db, new[] { row }, partition: x => x.ProjectKey == pk, ct: ct);
		if (!r.Applied)
		{
			var c = r.Conflicts[0];
			throw new InvalidOperationException(c.Kind switch
			{
				TemporalConflictKind.FutureBaseline =>
					$"agent definition '{k}' conflict: your baseline version {version} is ahead of this project's cursor {c.ActiveVersion} — re-read with agent_def_get and resubmit against the current version",
				TemporalConflictKind.Vanished =>
					$"agent definition '{k}' conflict: your baseline version {version} no longer exists (the definition was removed); re-read with agent_def_get and resubmit with version 0",
				_ =>
					$"agent definition '{k}' conflict: your baseline version {version} is stale — the current version is {c.ActiveVersion}; pass the version from your last agent_def_get (0 = create)",
			});
		}
		return new AgentDefinitionAck(k, r.CurrentVersion, Changed: r.Inserted > 0);
	}

	public async Task<AgentDefinitionAck> DeleteAsync(
		string projectKey, string key, long version, CancellationToken ct = default)
	{
		var pk = RequireProjectKey(projectKey);
		var k = NormalizeKey(key);

		// The existence probe and the temporal close share ONE connection for the whole call — the
		// close is a read-modify-write over the same rows, and splitting it across two connections
		// would race a concurrent writer between them.
		using var db = _factory.Open();

		var current = await db.AgentDefinitions
			.FirstOrDefaultAsync(r => r.ProjectKey == pk && r.Key == k && r.ActiveTo == null, ct);
		if (current is null)
			return new AgentDefinitionAck(k, Version: 0, Changed: false); // idempotent

		var r = await TemporalStore.UpsertAsync(db, Array.Empty<AgentDefinitionRow>(),
			[(k, version)], partition: x => x.ProjectKey == pk, ct: ct);
		if (!r.Applied)
		{
			var c = r.Conflicts[0];
			throw new InvalidOperationException(c.Kind switch
			{
				TemporalConflictKind.FutureBaseline =>
					$"agent definition '{k}' conflict: your baseline version {version} is ahead of this project's cursor {c.ActiveVersion} — re-read with agent_def_get and retry the delete against the current version",
				_ =>
					$"agent definition '{k}' conflict: your baseline version {version} is stale — the current version is {c.ActiveVersion}; re-read with agent_def_get and retry the delete against the current version",
			});
		}
		return new AgentDefinitionAck(k, r.CurrentVersion, Changed: r.Closed > 0);
	}

	static AgentDefinitionDoc Deserialize(string projectKey, AgentDefinitionRow row) =>
		JsonSerializer.Deserialize<AgentDefinitionDoc>(row.Json, AgentDefinitionJson.Options)
		?? throw new InvalidOperationException($"project '{projectKey}': stored agent definition '{row.Key}' failed to deserialize");

	static string RequireProjectKey(string? projectKey)
	{
		var pk = projectKey?.Trim();
		if (string.IsNullOrEmpty(pk))
			throw new ArgumentException("projectKey is required", nameof(projectKey));
		return pk;
	}

	static string NormalizeKey(string? key)
	{
		var k = key?.Trim().ToLowerInvariant();
		if (string.IsNullOrEmpty(k))
			throw new ArgumentException("an agent definition key (slug) is required", nameof(key));
		if (!SlugRegex().IsMatch(k))
			throw new ArgumentException(
				$"'{key}' is not a valid agent definition key; must match ^[a-z][a-z0-9_-]{{0,99}}$", nameof(key));
		return k;
	}
}
