using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using LinqToDB;
using LinqToDB.Async;
using PetBox.Core.Data.Temporal;
using PetBox.Tasks.Contract;
using PetBox.Tasks.Data;
using PetBox.Tasks.Validation;
using PetBox.Tasks.Workflow;

namespace PetBox.Tasks.Services.Methodology;

// Named methodology-template storage. Owns the multi-key methodology_templates temporal
// documents, the builtin-template read contract (quartet|classic|simple), the dual-read
// of the project's utility-layer singleton (methodology_defs, spec methodology-utility-
// kinds — MethodologyDefinitionService's header has the full history of that storage) as
// a virtual template, and snapshot-from-effective-rules.
//
// HARD INVARIANT: every write path here stores a document only — it NEVER creates boards,
// never rewrites live nodes, and never mutates methodology_defs. Live process mutation
// stays on MethodologyDefinitionService / EnableMethodology / board_create.
//
// Private collaborator of TasksService (not DI-registered) — same posture as
// MethodologyDefinitionService.
public sealed partial class MethodologyTemplateService
{
	// Builtin template keys — readable via the same get/list contract as stored templates;
	// never written into methodology_templates and rejected on upsert/delete.
	public static readonly IReadOnlyList<string> BuiltinKeys = ["quartet", "classic", "simple"];

	// Compat dual-read key: the project's utility-layer singleton definition (MethodologyDefRow)
	// surfaces under this key with source="definition" when present and no STORED template
	// owns it. Same string as MethodologyDefRow.SingletonKey so the dual-read is obvious.
	public const string LegacyDefinitionKey = MethodologyDefRow.SingletonKey;

	// Source tags on MethodologyTemplateView / ListItem.
	public const string SourceStored = "stored";
	public const string SourceBuiltin = "builtin";
	public const string SourceDefinition = "definition";

	readonly ITaskBoardStore _boards;
	readonly MethodologyDefinitionService _defs;
	// Optional instance-rules resolver (wired by TasksService after MethodologyInstanceService
	// is constructed — breaks the circular collaborator dependency). Null until bound →
	// instance:<key> snapshot still rejects with a clear message.
	Func<string, string, CancellationToken, Task<MethodologyDefinition?>>? _instanceRules;

	static readonly MethodologyDefinitionValidator DefinitionValidator = new();

	static readonly JsonSerializerOptions DefinitionJson = new(JsonSerializerDefaults.Web)
	{
		Converters = { new JsonStringEnumConverter() },
	};

	// Same slug shape as boards/nodes/methodology names.
	[GeneratedRegex(@"^[a-z][a-z0-9_-]{0,99}$")]
	private static partial Regex SlugRegex();

	public MethodologyTemplateService(
		ITaskBoardStore boards,
		MethodologyDefinitionService defs,
		Func<string, string, CancellationToken, Task<MethodologyDefinition?>>? instanceRules = null)
	{
		_boards = boards;
		_defs = defs;
		_instanceRules = instanceRules;
	}

	// Late-bind the instance rules resolver (TasksService constructs templates first).
	public void BindInstanceRules(Func<string, string, CancellationToken, Task<MethodologyDefinition?>> resolver) =>
		_instanceRules = resolver;

	public async Task<IReadOnlyList<MethodologyTemplateListItem>> ListAsync(string projectKey, CancellationToken ct = default)
	{
		var items = new List<MethodologyTemplateListItem>();

		// Builtins first — always present, independent of project state.
		foreach (var key in BuiltinKeys)
		{
			var def = MethodologyPresets.RenderBuiltinTemplate(key);
			items.Add(new MethodologyTemplateListItem(key, SourceBuiltin, def.Name, Version: 0, Updated: null));
		}

		// Stored templates (real rows).
		using var ctx = _boards.NewEnsuredConnection(projectKey);
		var rows = await ctx.GetTable<MethodologyTemplateRow>()
			.Where(r => r.ActiveTo == null)
			.OrderBy(r => r.Key)
			.ToListAsync(ct);
		var storedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (var row in rows)
		{
			storedKeys.Add(row.Key);
			var def = Deserialize(projectKey, row);
			items.Add(new MethodologyTemplateListItem(row.Key, SourceStored, def.Name, row.Version, row.Updated));
		}

		// Dual-read: surface the legacy singleton definition when present and not shadowed
		// by a stored template under the same key.
		if (!storedKeys.Contains(LegacyDefinitionKey))
		{
			var legacy = await _defs.GetAsync(projectKey, ct);
			if (legacy is not null)
				items.Add(new MethodologyTemplateListItem(
					LegacyDefinitionKey, SourceDefinition, legacy.Definition.Name,
					legacy.Version, legacy.Updated));
		}

		return items;
	}

	public async Task<MethodologyTemplateView?> GetAsync(string projectKey, string key, CancellationToken ct = default)
	{
		var k = NormalizeKey(key);

		// Builtins are virtual — never hit storage.
		if (IsBuiltin(k))
		{
			var def = MethodologyPresets.RenderBuiltinTemplate(k);
			return new MethodologyTemplateView(k, SourceBuiltin, def, Version: 0, Created: null, Updated: null);
		}

		// Stored template wins over the dual-read of the singleton definition.
		using (var ctx = _boards.NewEnsuredConnection(projectKey))
		{
			var row = await ctx.GetTable<MethodologyTemplateRow>()
				.FirstOrDefaultAsync(r => r.Key == k && r.ActiveTo == null, ct);
			if (row is not null)
			{
				var def = Deserialize(projectKey, row);
				return new MethodologyTemplateView(k, SourceStored, def, row.Version, row.Created, row.Updated);
			}
		}

		// Dual-read: key "methodology" without a stored row → the singleton def, if any.
		if (string.Equals(k, LegacyDefinitionKey, StringComparison.OrdinalIgnoreCase))
		{
			var legacy = await _defs.GetAsync(projectKey, ct);
			if (legacy is not null)
				return new MethodologyTemplateView(
					LegacyDefinitionKey, SourceDefinition, legacy.Definition,
					legacy.Version, legacy.Created, legacy.Updated);
		}

		return null;
	}

	// Store a named template document. Validates the definition; NEVER provisions boards
	// or rewrites live nodes (no migration planner — templates are inert documents).
	public async Task<MethodologyTemplateAck> UpsertAsync(
		string projectKey, string key, MethodologyDefinition def, long version, CancellationToken ct = default)
	{
		var k = NormalizeKey(key);
		RejectBuiltinWrite(k, "upsert");

		var result = DefinitionValidator.Validate(def);
		if (!result.IsValid)
			throw new ArgumentException(result.Errors[0].ErrorMessage);

		using var ctx = _boards.NewEnsuredConnection(projectKey);
		var row = new MethodologyTemplateRow
		{
			Key = k,
			Version = version,
			Json = JsonSerializer.Serialize(def, DefinitionJson),
		};

		var r = await TemporalStore.UpsertAsync(ctx, new[] { row }, ct: ct);
		if (!r.Applied)
		{
			var c = r.Conflicts[0];
			throw new InvalidOperationException(c.Kind switch
			{
				TemporalConflictKind.FutureBaseline =>
					$"methodology template '{k}' conflict: your baseline version {version} is ahead of this project's cursor {c.ActiveVersion} — re-read with tasks_methodology_template_get and resubmit against the current version",
				TemporalConflictKind.Vanished =>
					$"methodology template '{k}' conflict: your baseline version {version} no longer exists (the template was removed); re-read with tasks_methodology_template_get and resubmit with version 0",
				_ =>
					$"methodology template '{k}' conflict: your baseline version {version} is stale — the current version is {c.ActiveVersion}; pass the version from your last tasks_methodology_template_get (0 = create)",
			});
		}
		return new MethodologyTemplateAck(k, r.CurrentVersion, Changed: r.Inserted > 0);
	}

	public async Task<MethodologyTemplateAck> DeleteAsync(
		string projectKey, string key, long version, CancellationToken ct = default)
	{
		var k = NormalizeKey(key);
		RejectBuiltinWrite(k, "delete");

		using var ctx = _boards.NewEnsuredConnection(projectKey);
		var current = await ctx.GetTable<MethodologyTemplateRow>()
			.FirstOrDefaultAsync(r => r.Key == k && r.ActiveTo == null, ct);
		if (current is null)
			return new MethodologyTemplateAck(k, Version: 0, Changed: false); // idempotent

		var r = await TemporalStore.UpsertAsync(ctx, Array.Empty<MethodologyTemplateRow>(),
			[(k, version)], ct: ct);
		if (!r.Applied)
		{
			var c = r.Conflicts[0];
			throw new InvalidOperationException(c.Kind switch
			{
				TemporalConflictKind.FutureBaseline =>
					$"methodology template '{k}' conflict: your baseline version {version} is ahead of this project's cursor {c.ActiveVersion} — re-read with tasks_methodology_template_get and retry the delete against the current version",
				_ =>
					$"methodology template '{k}' conflict: your baseline version {version} is stale — the current version is {c.ActiveVersion}; re-read with tasks_methodology_template_get and retry the delete against the current version",
			});
		}
		return new MethodologyTemplateAck(k, r.CurrentVersion, Changed: r.Closed > 0);
	}

	// Snapshot current effective project rules (or an explicit source) into a named template.
	//   - from null / "effective" → singleton definition if present, else builtin "quartet"
	//   - from "preset:<slug>" or a bare builtin slug → that builtin template document
	//   - from "instance:<key>" → the named instance's rules (methodology-instance-core)
	// Never mutates the source (def / boards / instance).
	public async Task<MethodologyTemplateAck> SnapshotAsync(
		string projectKey, string key, long version, string? from = null, CancellationToken ct = default)
	{
		var def = await ResolveSnapshotSourceAsync(projectKey, from, ct);
		return await UpsertAsync(projectKey, key, def, version, ct);
	}

	async Task<MethodologyDefinition> ResolveSnapshotSourceAsync(string projectKey, string? from, CancellationToken ct)
	{
		var raw = (from ?? "").Trim();
		if (raw.Length == 0 || string.Equals(raw, "effective", StringComparison.OrdinalIgnoreCase))
		{
			// Effective project rules: stored singleton def wins; else the default
			// provisioning preset as a copyable document.
			var legacy = await _defs.GetAsync(projectKey, ct);
			return legacy?.Definition ?? MethodologyPresets.RenderBuiltinTemplate(MethodologyPresets.DefaultProvisioningPreset);
		}

		// instance:<key> — snapshot the named instance's rules (closed instances allowed).
		if (raw.StartsWith("instance:", StringComparison.OrdinalIgnoreCase))
		{
			var instKey = raw["instance:".Length..].Trim();
			if (instKey.Length == 0)
				throw new ArgumentException("snapshot from=instance:<key> requires a non-empty instance name");
			if (_instanceRules is null)
				throw new InvalidOperationException("instance rules resolver is not bound");
			var def = await _instanceRules(projectKey, instKey, ct)
				?? throw new ArgumentException($"methodology instance '{instKey}' not found — cannot snapshot");
			return def;
		}

		// preset:<slug> or bare builtin slug.
		var slug = raw.StartsWith("preset:", StringComparison.OrdinalIgnoreCase)
			? raw["preset:".Length..].Trim()
			: raw;
		if (IsBuiltin(slug))
			return MethodologyPresets.RenderBuiltinTemplate(slug);

		throw new ArgumentException(
			$"unknown methodology template snapshot source '{from}' — use effective (default), preset:quartet|classic|simple, or instance:<key>");
	}

	static MethodologyDefinition Deserialize(string projectKey, MethodologyTemplateRow row) =>
		JsonSerializer.Deserialize<MethodologyDefinition>(row.Json, DefinitionJson)
		?? throw new InvalidOperationException($"project '{projectKey}': stored methodology template '{row.Key}' failed to deserialize");

	static string NormalizeKey(string? key)
	{
		var k = key?.Trim().ToLowerInvariant();
		if (string.IsNullOrEmpty(k))
			throw new ArgumentException("a methodology template key (slug) is required", nameof(key));
		if (!SlugRegex().IsMatch(k))
			throw new ArgumentException(
				$"'{key}' is not a valid methodology template key; must match ^[a-z][a-z0-9_-]{{0,99}}$", nameof(key));
		return k;
	}

	static bool IsBuiltin(string key) =>
		BuiltinKeys.Any(b => string.Equals(b, key, StringComparison.OrdinalIgnoreCase));

	static void RejectBuiltinWrite(string key, string action)
	{
		if (IsBuiltin(key))
			throw new ArgumentException(
				$"methodology template '{key}' is a builtin (read-only) — cannot {action}; copy it into a new key via tasks_methodology_template_upsert or tasks_methodology_template_snapshot");
	}
}
