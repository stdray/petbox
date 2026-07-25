using System.Text.Json;
using System.Text.Json.Nodes;
using LinqToDB;
using Microsoft.Extensions.Logging;
using PetBox.Core.Data;
using PetBox.Core.Data.Temporal;

namespace PetBox.Tasks.Data;

// One-time, idempotent (delivery-autowire-still-hardcoded-spec): the methodology field that names a
// kind's auto-wire target was renamed `AutoWireSpecFrom` -> `AutoWireFrom` (JSON key
// `autoWireSpecFrom` -> `autoWireFrom`) when the wired/set_wire contract dropped the word "spec"
// from its public surface. The VALUE is unchanged — it is a kind slug (the quartet stores "spec"),
// only the FIELD NAME moved.
//
// Editing the C# property alone is NOT enough: a stored methodology document (the live quartet
// included, confirmed v16 — the guide renders `auto_wire:spec` straight from its instance data)
// carries the OLD key `autoWireSpecFrom` baked into its JSON. The renamed deserializer looks for
// `autoWireFrom`, does not find it, and reads null — auto-wire (TasksService.AutoWireSpecAsync) and
// the set-wire target validation would SILENTLY go dead on every already-provisioned project. Same class
// of miss as LinkKindsDeclaredMigrator / WorkDeferredStatusMigrator: MethodologyRuntime reads a
// stored document's kind fields WHOLE-OBJECT, by design, so the code change never reaches the row.
//
// Unlike those two, this needs no "is it OUR shape" discrimination: `autoWireSpecFrom` is OUR field's
// old serialized name and nothing else — a project cannot inject an arbitrary JSON key (rules_upsert
// flows through the typed MethodologyKindInput). So the migrator renames the key wherever it appears,
// unconditionally, but CONSERVATIVELY: it operates on the RAW JsonNode (NOT the typed deserializer,
// which would drop the old key it no longer binds), touches only kind objects that actually carry
// the old key, and leaves every other byte alone. When a kind somehow carries BOTH keys (a partially
// migrated / hand-edited row), the new key wins and the stale old one is dropped.
//
// Scans every project's stored methodology documents (the project-singleton methodology_defs row +
// every active methodology_instances row + every active methodology_templates row). Runs at startup
// like LinkKindsDeclaredMigrator — content lives in per-project tasks files a FluentMigrator schema
// migration cannot reach. The CAS write bumps the document a version (the live quartet v16 -> v17).
public sealed class AutoWireFieldRenamedMigrator
{
	const string OldKey = "autoWireSpecFrom";
	const string NewKey = "autoWireFrom";

	// Compact, camelCase — the same posture the store serializes methodology documents with, so a
	// rewritten row keeps the store's shape (only the renamed key differs, and it round-trips
	// through the typed model verbatim thereafter).
	static readonly JsonSerializerOptions Compact = new(JsonSerializerDefaults.Web);

	readonly ICoreDbFactory _dbf;
	readonly IScopedDbFactory<TasksDb> _factory;
	readonly ILogger? _log;

	public AutoWireFieldRenamedMigrator(ICoreDbFactory dbf, IScopedDbFactory<TasksDb> factory, ILogger? log = null)
	{
		_dbf = dbf;
		_factory = factory;
		_log = log;
	}

	// Returns the number of stored documents (definition + instance + template rows, summed) rewritten.
	public int Migrate()
	{
		using var db = _dbf.Open();
		var projects = db.TaskBoards
			.Select(b => b.ProjectKey)
			.Distinct()
			.OrderBy(k => k)
			.ToList();
		var touched = 0;
		foreach (var project in projects)
		{
			try
			{
				touched += MigrateProject(project);
			}
			catch (Exception ex)
			{
				_log?.LogError(ex,
					"Tasks delivery-autowire-field-rename migration failed for project {Project}; left as-is",
					project);
			}
		}
		return touched;
	}

	// Exposed for tests: run a single project, return the number of documents rewritten.
	internal int MigrateProject(string projectKey)
	{
		using var ctx = _factory.NewEnsuredConnection(projectKey);
		var rewritten = 0;

		var defRow = ctx.GetTable<MethodologyDefRow>()
			.FirstOrDefault(r => r.Key == MethodologyDefRow.SingletonKey && r.ActiveTo == null);
		if (defRow is not null && TryRename(defRow.Json, out var newDefJson))
		{
			var r = TemporalStore.UpsertAsync(ctx, new[] { defRow with { Version = defRow.Version, Json = newDefJson } }).GetAwaiter().GetResult();
			rewritten += LogWrite(r.Applied, projectKey, "project methodology definition");
		}

		foreach (var row in ctx.GetTable<MethodologyInstanceRow>().Where(r => r.ActiveTo == null).ToList())
			if (TryRename(row.Json, out var newJson))
			{
				var r = TemporalStore.UpsertAsync(ctx, new[] { row with { Version = row.Version, Json = newJson } }).GetAwaiter().GetResult();
				rewritten += LogWrite(r.Applied, projectKey, $"methodology instance '{row.Key}'");
			}

		foreach (var row in ctx.GetTable<MethodologyTemplateRow>().Where(r => r.ActiveTo == null).ToList())
			if (TryRename(row.Json, out var newJson))
			{
				var r = TemporalStore.UpsertAsync(ctx, new[] { row with { Version = row.Version, Json = newJson } }).GetAwaiter().GetResult();
				rewritten += LogWrite(r.Applied, projectKey, $"methodology template '{row.Key}'");
			}

		return rewritten;
	}

	int LogWrite(bool applied, string projectKey, string subject)
	{
		if (applied)
		{
			_log?.LogInformation(
				"Tasks: renamed the auto-wire field autoWireSpecFrom -> autoWireFrom in the {Subject} of project {Project}",
				subject, projectKey);
			return 1;
		}
		_log?.LogWarning(
			"Tasks delivery-autowire-field-rename: the {Subject} of project {Project} changed concurrently — left as-is, will retry next startup",
			subject, projectKey);
		return 0;
	}

	// Parse `json` as a raw JsonNode and rename the `autoWireSpecFrom` key to `autoWireFrom` on every
	// kind object that carries it (value preserved verbatim). Returns false (result = input) when
	// nothing carries the old key — already migrated, or never touched auto-wire. A bad shape is left
	// for a human, not a crash loop. Deliberately RAW-node, not the typed deserializer: the renamed
	// MethodologyKindDef no longer binds `autoWireSpecFrom`, so a deserialize-then-reserialize would
	// DROP the value — the exact silent break this migrator exists to prevent.
	static bool TryRename(string json, out string result)
	{
		result = json;
		JsonNode? root;
		try { root = JsonNode.Parse(json); }
		catch (JsonException) { return false; }
		if (root is not JsonObject obj || obj["kinds"] is not JsonArray kinds) return false;

		var changed = false;
		foreach (var node in kinds)
		{
			if (node is not JsonObject kind || !kind.ContainsKey(OldKey)) continue;
			if (kind.ContainsKey(NewKey))
			{
				// Both keys present (a partially migrated / hand-edited row): the new key is
				// authoritative, drop the stale old one rather than clobbering the live value.
				kind.Remove(OldKey);
			}
			else
			{
				var value = kind[OldKey];
				kind.Remove(OldKey);
				kind[NewKey] = value?.DeepClone();
			}
			changed = true;
		}

		if (!changed) return false;
		result = root.ToJsonString(Compact);
		return true;
	}
}
