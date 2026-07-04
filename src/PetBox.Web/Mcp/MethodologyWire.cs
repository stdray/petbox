using System.Text.Json;
using ModelContextProtocol;
using PetBox.Tasks.Workflow;
using PetBox.Web.Mcp.Contract;

namespace PetBox.Web.Mcp;

// The methodology-definition WIRE SEAM shared by the MCP tools (tasks_methodology_def_get /
// def_upsert) and the admin methodology-editor page: ONE domain→document projector and ONE
// document→domain parser, so a JSON definition document moves freely between the editor
// textarea and the MCP tools — same shape, same serializer posture (the MCP SDK's
// McpJsonUtilities.DefaultOptions: camelCase properties, nulls omitted).
static class MethodologyWire
{
	// The exact options the MCP SDK serializes tool results / deserializes tool args with —
	// reused verbatim so the editor's JSON is shape-identical to the def_get/def_upsert wire.
	public static readonly JsonSerializerOptions WireOptions = McpJsonUtilities.DefaultOptions;

	// Textarea-facing variant: same shape, indented for hand editing.
	static readonly JsonSerializerOptions Indented = new(McpJsonUtilities.DefaultOptions) { WriteIndented = true };

	public static string ToJson(MethodologyDefGetResult doc) => JsonSerializer.Serialize(doc, Indented);

	// Parse a pasted definition document (the def_get output / def_upsert `definition` shape;
	// extra envelope fields like `defined`/`version` are ignored). Bad JSON and a bad status
	// kind both surface as ArgumentException — the same actionable-message posture as the
	// service's validation errors.
	public static MethodologyDefinition ParseDocument(string? json)
	{
		if (string.IsNullOrWhiteSpace(json))
			throw new ArgumentException("the definition document is empty — paste JSON or load a preset template");
		MethodologyDefInput? input;
		try { input = JsonSerializer.Deserialize<MethodologyDefInput>(json, WireOptions); }
		catch (JsonException ex) { throw new ArgumentException($"invalid JSON: {ex.Message}"); }
		return ParseDefinition(input ?? new MethodologyDefInput());
	}

	// Parse an optional migration document (the def_upsert `migration` shape:
	// [{ kind, types?:[{from,to}], statuses?:[{from,to}] }]); blank = none declared.
	public static IReadOnlyList<MethodologyMigration>? ParseMigrationDocument(string? json)
	{
		if (string.IsNullOrWhiteSpace(json)) return null;
		MethodologyMigrationInput[]? migration;
		try { migration = JsonSerializer.Deserialize<MethodologyMigrationInput[]>(json, WireOptions); }
		catch (JsonException ex) { throw new ArgumentException($"invalid migration JSON: {ex.Message}"); }
		return ParseMigration(migration);
	}

	// Project a MethodologyDefinition onto the def_get wire result (Defined=true). Shared by the
	// stored-definition read, the preset-copy render and the editor prefill, so every path uses
	// ONE shape — the strict outputSchema clients validate against is identical whichever source
	// produced the document.
	public static MethodologyDefGetResult ProjectDefinition(MethodologyDefinition def, long version, DateTime? created, DateTime? updated) =>
		new(
			Defined: true,
			Name: def.Name,
			Kinds: def.Kinds.Select(k => new MethodologyKindView(
				k.Kind, k.QuickAddAllowed,
				k.Workflows.Select(w => new MethodologyWorkflowBlockView(
					Types: w.Types,
					Initial: w.Initial,
					Statuses: w.Statuses.Select(s => new WorkflowStatusView(s.Slug, s.Name, s.Kind.ToString().ToLowerInvariant())).ToList(),
					Transitions: w.Transitions.Select(t => new MethodologyTransitionView(
						t.From, t.To, t.RequiresApproval, t.RequiresReason, t.PreconditionArtifact,
						t.EnforceApproval,
						Checklist: t.Checklist is { Count: > 0 } ? t.Checklist : null)).ToList())).ToList(),
				LinkConstraints: k.LinkConstraints is { Count: > 0 }
					? k.LinkConstraints.Select(c => new MethodologyLinkConstraintView(
						c.Type, c.Link, c.TargetKind,
						TargetStatuses: c.TargetStatuses is { Count: > 0 } ? c.TargetStatuses : null)).ToList()
					: null,
				Effects: k.Effects is { Count: > 0 }
					? k.Effects.Select(e => new MethodologyEffectView(e.On, e.Link, e.Direction, e.Set, e.OnlyFrom)).ToList()
					: null)).ToList(),
			Version: version,
			Created: created,
			Updated: updated,
			LinkKinds: def.LinkKinds is { Count: > 0 }
				? def.LinkKinds.Select(lk => new MethodologyLinkKindView(lk.Slug, lk.Description)).ToList()
				: null,
			TagAxes: def.TagAxes is { Count: > 0 }
				? def.TagAxes.Select(a => new MethodologyTagAxisView(a.Namespace, a.Description)).ToList()
				: null);

	// Map the typed wire document onto the domain definition 1:1 (nulls → empty lists —
	// the validator then reports "needs at least one ..." instead of an opaque NRE).
	// Only the status-kind STRING needs parsing here; integrity stays in the service.
	public static MethodologyDefinition ParseDefinition(MethodologyDefInput d) => new(
		d.Name ?? string.Empty,
		(d.Kinds ?? []).Select(k => new MethodologyKindDef(
			k.Kind ?? string.Empty,
			k.QuickAddAllowed,
			(k.Workflows ?? []).Select(w => new MethodologyWorkflowDef(
				w.Types ?? [],
				(w.Statuses ?? []).Select(ParseStatus).ToList(),
				(w.Transitions ?? []).Select(t => new MethodologyTransitionDef(
					t.From ?? string.Empty, t.To ?? string.Empty,
					t.RequiresApproval, t.RequiresReason, t.PreconditionArtifact)
				{
					EnforceApproval = t.EnforceApproval,
					Checklist = (t.Checklist ?? []).Select(i => i ?? string.Empty).ToList(),
				}).ToList())).ToList())
		{
			LinkConstraints = (k.LinkConstraints ?? [])
				.Select(c => new MethodologyLinkConstraintDef(c.Type ?? string.Empty, c.Link ?? string.Empty)
				{
					TargetKind = c.TargetKind,
					TargetStatuses = c.TargetStatuses?.Select(s => s ?? string.Empty).ToList(),
				}).ToList(),
			Effects = (k.Effects ?? [])
				.Select(e => new MethodologyTransitionEffectDef(
					e.On ?? string.Empty, e.Link ?? string.Empty, e.Direction ?? string.Empty,
					e.Set ?? string.Empty, e.OnlyFrom)).ToList(),
		}).ToList())
	{
		LinkKinds = (d.LinkKinds ?? [])
			.Select(lk => new MethodologyLinkKindDef(lk.Slug ?? string.Empty, lk.Description)).ToList(),
		TagAxes = (d.TagAxes ?? [])
			.Select(a => new MethodologyTagAxisDef(a.Namespace ?? string.Empty, a.Description)).ToList(),
	};

	// Map the typed migration document 1:1 (nulls → empty, so the service validator reports
	// clear messages); null in = null out (no migration declared).
	public static List<MethodologyMigration>? ParseMigration(MethodologyMigrationInput[]? migration) =>
		migration?.Select(m => new MethodologyMigration(
			m.Kind ?? string.Empty,
			(m.Types ?? []).Select(v => new MethodologyValueMap(v.From ?? string.Empty, v.To ?? string.Empty)).ToList(),
			(m.Statuses ?? []).Select(v => new MethodologyValueMap(v.From ?? string.Empty, v.To ?? string.Empty)).ToList())).ToList();

	static WorkflowStatus ParseStatus(MethodologyStatusInput s)
	{
		var slug = s.Slug ?? string.Empty;
		var kind = StatusKind.Open;
		if (!string.IsNullOrWhiteSpace(s.Kind) && !Enum.TryParse(s.Kind.Trim(), ignoreCase: true, out kind))
			throw new ArgumentException($"status '{slug}': kind '{s.Kind}' is not a status kind (valid: open|terminalok|terminalcancel)");
		return new WorkflowStatus(slug, string.IsNullOrWhiteSpace(s.Name) ? slug : s.Name, kind);
	}
}
