using System.Text.Json;
using System.Text.Json.Serialization;
using LinqToDB;
using Microsoft.Extensions.Logging;
using PetBox.Core.Data;
using PetBox.Core.Data.Temporal;
using PetBox.Tasks.Workflow;

namespace PetBox.Tasks.Data;

// One-time, idempotent (spec methodology-link-kinds-declared): idea_spec/task_spec/issue_task used
// to be builtin string kinds in MethodologyRuntime.ProcessRelationKinds; they are now DECLARED
// relation kinds carried on a methodology document's LinkKinds, each with its stored-edge Direction
// (MethodologyPresets.QuartetLinkKinds), and delivery roll-up now names its link kind as DATA
// (MethodologyDeliveryDef.Link, no default). A stored quartet document materialized BEFORE this
// change still carries constraints/effects/delivery that REFERENCE the trio but declares none of
// them as linkKinds, and its delivery has no `link` — the code change never reaches it
// (MethodologyRuntime reads a stored document's linkKinds/delivery WHOLE-OBJECT, by design). This
// migrator backfills those stored documents so the guide, DeclaredLinkKinds and the delivery
// roll-up read the trio + link straight from the data, exactly like a freshly-provisioned instance.
//
// PetBox is a build-your-own-methodology product: a stored document may be a project's OWN process.
// So this migrator is surgical:
//   • it INJECTS a canonical trio linkKind (idea_spec/task_spec/issue_task) ONLY for a slug the
//     document already REFERENCES (in a linkConstraint/effect/delivery) AND does not already declare
//     — a slug the document already declares (even with a different direction) is a deliberate
//     customization and is left untouched;
//   • it backfills delivery.link to the old literal `task_spec` ONLY when a delivery declares none
//     (the exact pre-field roll-up semantics).
// Anything else — a project's own link kinds, a customized trio direction — is left alone.
//
// Scans every project's stored methodology documents (the project-singleton methodology_defs row +
// every active methodology_instances row + every active methodology_templates row). Runs at startup
// like WorkDeferredStatusMigrator — content lives in per-project tasks files a FluentMigrator schema
// migration cannot reach.
public sealed class LinkKindsDeclaredMigrator
{
	// The canonical trio slugs, lower-cased for reference detection.
	static readonly HashSet<string> TrioSlugs = new(
		MethodologyPresets.QuartetLinkKinds.Select(k => k.Slug), StringComparer.OrdinalIgnoreCase);

	// The old delivery roll-up literal — the exact semantics a pre-field delivery had.
	const string LegacyDeliveryLink = "task_spec";

	static readonly JsonSerializerOptions DefinitionJson = new(JsonSerializerDefaults.Web)
	{
		Converters = { new JsonStringEnumConverter() },
	};

	readonly ICoreDbFactory _dbf;
	readonly IScopedDbFactory<TasksDb> _factory;
	readonly ILogger? _log;

	public LinkKindsDeclaredMigrator(ICoreDbFactory dbf, IScopedDbFactory<TasksDb> factory, ILogger? log = null)
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
					"Tasks methodology-link-kinds-declared migration failed for project {Project}; left as-is",
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
		if (defRow is not null && TryDeclare(defRow.Json, $"project {projectKey}'s methodology definition", out var newDefJson))
		{
			var r = TemporalStore.UpsertAsync(ctx, new[] { defRow with { Version = defRow.Version, Json = newDefJson } }).GetAwaiter().GetResult();
			rewritten += LogWrite(r.Applied, projectKey, "project methodology definition");
		}

		foreach (var row in ctx.GetTable<MethodologyInstanceRow>().Where(r => r.ActiveTo == null).ToList())
			if (TryDeclare(row.Json, $"methodology instance '{row.Key}' in project {projectKey}", out var newJson))
			{
				var r = TemporalStore.UpsertAsync(ctx, new[] { row with { Version = row.Version, Json = newJson } }).GetAwaiter().GetResult();
				rewritten += LogWrite(r.Applied, projectKey, $"methodology instance '{row.Key}'");
			}

		foreach (var row in ctx.GetTable<MethodologyTemplateRow>().Where(r => r.ActiveTo == null).ToList())
			if (TryDeclare(row.Json, $"methodology template '{row.Key}' in project {projectKey}", out var newJson))
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
				"Tasks: declared the quartet's process link kinds (+ delivery.link) in the {Subject} of project {Project}",
				subject, projectKey);
			return 1;
		}
		_log?.LogWarning(
			"Tasks methodology-link-kinds-declared: the {Subject} of project {Project} changed concurrently — left as-is, will retry next startup",
			subject, projectKey);
		return 0;
	}

	// Deserialize `json`, backfill delivery.link where a delivery declares none, then DECLARE every
	// canonical trio slug the document references but does not yet declare. Returns false (result =
	// input) when nothing qualifies — no trio reference the document doesn't already declare, and
	// every delivery already names its link (already migrated, or never touched the quartet). A bad
	// shape is left for a human, not a crash loop.
	bool TryDeclare(string json, string subject, out string result)
	{
		result = json;
		MethodologyDefinition? def;
		try { def = JsonSerializer.Deserialize<MethodologyDefinition>(json, DefinitionJson); }
		catch (JsonException) { return false; }
		if (def is null) return false;

		var changed = false;

		// 1. Backfill delivery.link (the pre-field roll-up went by the task_spec literal).
		var kinds = def.Kinds.Select(k =>
		{
			if (k.Delivery is { } d && string.IsNullOrWhiteSpace(d.Link))
			{
				changed = true;
				return k with { Delivery = d with { Link = LegacyDeliveryLink } };
			}
			return k;
		}).ToList();

		// 2. Collect the canonical trio slugs the document REFERENCES (constraints/effects/delivery,
		//    after the delivery backfill above), and DECLARE each one it doesn't already declare.
		var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (var k in kinds)
		{
			foreach (var c in k.LinkConstraints ?? [])
				if (TrioSlugs.Contains(c.Link)) referenced.Add(c.Link);
			foreach (var e in k.Effects ?? [])
				if (TrioSlugs.Contains(e.Link)) referenced.Add(e.Link);
			if (k.Delivery is { } d && TrioSlugs.Contains(d.Link)) referenced.Add(d.Link);
		}

		var declared = new HashSet<string>((def.LinkKinds ?? []).Select(lk => lk.Slug), StringComparer.OrdinalIgnoreCase);
		var kindSlugs = new HashSet<string>(kinds.Select(k => k.Kind), StringComparer.OrdinalIgnoreCase);
		var toDeclare = MethodologyPresets.QuartetLinkKinds
			.Where(q => referenced.Contains(q.Slug) && !declared.Contains(q.Slug))
			.Where(q => CanonicalEndsDeclared(q, kindSlugs, subject))
			.ToList();

		if (!changed && toDeclare.Count == 0) return false;

		var linkKinds = (def.LinkKinds ?? []).Concat(toDeclare).ToList();
		var newDef = def with { Kinds = kinds, LinkKinds = linkKinds };
		result = JsonSerializer.Serialize(newDef, DefinitionJson);
		if (toDeclare.Count > 0)
			_log?.LogInformation("Tasks methodology-link-kinds-declared: {Subject} — declaring {Slugs}",
				subject, string.Join(", ", toDeclare.Select(t => t.Slug)));
		return true;
	}

	// A referenced trio slug is DECLARED with its canonical quartet Direction only when the document
	// already declares BOTH end-kinds of that direction (idea_spec: ideas→spec, task_spec: work→spec,
	// issue_task: intake→work). A document that references the slug but is NOT the canonical quartet
	// shape — a project's OWN process that happens to reuse the slug with different kinds — is left
	// untouched: injecting a foreign orientation would silently start rejecting that edge's
	// relations_create (direction-enforcement) and, on the next rules_upsert, fail validation because
	// the injected ends aren't among that document's declared kinds, locking the owner out of their
	// own methodology. Same "only touch OURS, untouched" discipline as WorkDeferredStatusMigrator.
	bool CanonicalEndsDeclared(MethodologyLinkKindDef q, HashSet<string> kindSlugs, string subject)
	{
		var from = q.Direction?.FromKind;
		var to = q.Direction?.ToKind;
		if ((from is null || kindSlugs.Contains(from)) && (to is null || kindSlugs.Contains(to)))
			return true;
		_log?.LogInformation(
			"Tasks methodology-link-kinds-declared: {Subject} references '{Slug}' but does not declare its canonical end-kinds ({From} -> {To}) — a non-quartet reuse of the slug, left as-is",
			subject, q.Slug, from, to);
		return false;
	}
}
