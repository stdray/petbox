using System.Text.Json;
using System.Text.Json.Serialization;
using LinqToDB;
using Microsoft.Extensions.Logging;
using PetBox.Core.Data;
using PetBox.Core.Data.Temporal;
using PetBox.Tasks.Workflow;

namespace PetBox.Tasks.Data;

// One-time, idempotent (work-preset-drop-deferred): the `work` kind's built-in preset
// (MethodologyPresets.WorkKind) no longer declares the `Deferred` status — the maintainer
// decided the kanban board shouldn't carry a column for it. Editing the preset alone is NOT
// enough: MethodologyInstanceService.CreateAsync / MethodologyTemplateService materialize a
// preset kind VERBATIM into the STORED MethodologyDefinition at creation time
// (RenderBuiltinTemplate → MethodologyInstanceRow.Json / MethodologyDefRow.Json), so any
// instance/definition created before this change still carries `Deferred` (and its
// transitions) baked into its own document — the preset code change never reaches it
// (same class of miss as board-view-defaults-not-applied-existing-instances, but for a
// PROCESS field: MethodologyRuntime reads a declared kind's statuses/transitions WHOLE-
// OBJECT from the stored document, by design — a per-field merge would be wrong here, the
// definition IS the source of truth for process shape).
//
// Strategy: scan every project's stored methodology documents (the project-singleton
// methodology_defs row + every methodology_instances row) for a `work` kind whose workflow
// blocks still carry a status literally named `Deferred`; strip that status AND every
// transition that names it as From/To (dangling FSM edges are worse than a missing status).
// Nothing else about the document changes. A document with no `work`/`Deferred` combination
// (already migrated, or a project with no work board, or a custom kind literally called
// something else) is left untouched — re-run is a no-op.
//
// Scoped to project databases only (per-project tasks files); board membership (Core DB) is
// not touched. Runs at startup like MethodologyInstanceBackfill / FlatNodePartOfMigrator —
// content lives in per-project files a FluentMigrator schema migration cannot reach.
public sealed class WorkDeferredStatusMigrator
{
	const string WorkKind = "work";
	const string DeferredStatus = "Deferred";

	static readonly JsonSerializerOptions DefinitionJson = new(JsonSerializerDefaults.Web)
	{
		Converters = { new JsonStringEnumConverter() },
	};

	readonly ICoreDbFactory _dbf;
	readonly IScopedDbFactory<TasksDb> _factory;
	readonly ILogger? _log;

	public WorkDeferredStatusMigrator(ICoreDbFactory dbf, IScopedDbFactory<TasksDb> factory, ILogger? log = null)
	{
		_dbf = dbf;
		_factory = factory;
		_log = log;
	}

	// Returns the number of project documents (definition + instance rows, summed) rewritten.
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
					"Tasks work-preset-drop-deferred migration failed for project {Project}; left as-is",
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
		if (defRow is not null && TryStrip(defRow.Json, out var newDefJson))
		{
			var next = defRow with { Version = defRow.Version, Json = newDefJson };
			var r = TemporalStore.UpsertAsync(ctx, new[] { next }).GetAwaiter().GetResult();
			if (r.Applied)
			{
				rewritten++;
				_log?.LogInformation(
					"Tasks: dropped 'Deferred' from the work kind's project methodology definition in {Project}",
					projectKey);
			}
			else
			{
				_log?.LogWarning(
					"Tasks work-preset-drop-deferred: project {Project}'s methodology definition changed concurrently — left as-is, will retry next startup",
					projectKey);
			}
		}

		var instanceRows = ctx.GetTable<MethodologyInstanceRow>()
			.Where(r => r.ActiveTo == null)
			.ToList();
		foreach (var row in instanceRows)
		{
			if (!TryStrip(row.Json, out var newJson)) continue;
			var next = row with { Version = row.Version, Json = newJson };
			var r = TemporalStore.UpsertAsync(ctx, new[] { next }).GetAwaiter().GetResult();
			if (r.Applied)
			{
				rewritten++;
				_log?.LogInformation(
					"Tasks: dropped 'Deferred' from the work kind's methodology instance '{Instance}' in {Project}",
					row.Key, projectKey);
			}
			else
			{
				_log?.LogWarning(
					"Tasks work-preset-drop-deferred: methodology instance '{Instance}' in {Project} changed concurrently — left as-is, will retry next startup",
					row.Key, projectKey);
			}
		}

		return rewritten;
	}

	// Deserializes `json`, strips `Deferred` (status + referencing transitions) from every
	// workflow block of every `work`-slug kind, and re-serializes IF anything changed.
	// Returns false (out set to the input) when the document has no `work` kind or the kind
	// has no `Deferred` status — the no-op path a repeat run and every other document take.
	static bool TryStrip(string json, out string result)
	{
		result = json;
		MethodologyDefinition? def;
		try
		{
			def = JsonSerializer.Deserialize<MethodologyDefinition>(json, DefinitionJson);
		}
		catch (JsonException)
		{
			return false; // not a shape we understand — leave it for a human, not a crash loop
		}
		if (def is null) return false;

		var changed = false;
		var kinds = def.Kinds.Select(kind =>
		{
			if (!string.Equals(kind.Kind, WorkKind, StringComparison.OrdinalIgnoreCase))
				return kind;
			var workflows = kind.Workflows.Select(block =>
			{
				if (!block.Statuses.Any(s => string.Equals(s.Slug, DeferredStatus, StringComparison.OrdinalIgnoreCase)))
					return block;
				changed = true;
				var statuses = block.Statuses
					.Where(s => !string.Equals(s.Slug, DeferredStatus, StringComparison.OrdinalIgnoreCase))
					.ToList();
				var transitions = block.Transitions
					.Where(t => !string.Equals(t.From, DeferredStatus, StringComparison.OrdinalIgnoreCase)
						&& !string.Equals(t.To, DeferredStatus, StringComparison.OrdinalIgnoreCase))
					.ToList();
				return block with { Statuses = statuses, Transitions = transitions };
			}).ToList();
			return changed ? kind with { Workflows = workflows } : kind;
		}).ToList();

		if (!changed) return false;
		result = JsonSerializer.Serialize(def with { Kinds = kinds }, DefinitionJson);
		return true;
	}
}
