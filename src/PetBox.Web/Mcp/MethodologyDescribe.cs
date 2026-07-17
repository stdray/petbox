using PetBox.Tasks.Workflow;

namespace PetBox.Web.Mcp;

// tasks_methodology_describe (spec methodology-describe-verb): edit ONE primitive's prose
// Description by its NATURAL KEY, apart from the structural whole-document upsert
// (rules_upsert/template_upsert — version-CAS, full replace). This is the pure
// lookup-and-replace half; TasksTools.MethodologyDescribeAsync does the read-current/
// write-back around it (rules_get → Apply → rules_upsert, retried a few times on a version
// race — the caller of THIS verb never sees or supplies that version).
//
// Granular STRUCTURE patching was rejected (spec methodology-describe-verb — "structure is a
// whole-doc upsert with version-CAS"); this only ever replaces a Description string, never
// adds/removes/reorders a kind, block, status, transition, effect or constraint.
static class MethodologyDescribe
{
	public static readonly IReadOnlyList<string> Primitives =
		["kind", "status", "transition", "effect", "constraint", "linkKind", "tagAxis"];

	// Returns the definition with the ONE matching primitive's Description replaced, and
	// whether a match was found at all (false ⇒ the caller's natural key named nothing —
	// TasksTools turns that into a clear ArgumentException, nothing is written).
	public static (MethodologyDefinition Def, bool Matched) Apply(
		MethodologyDefinition def, string primitive,
		string? kind, string? type, string? slug,
		string? from, string? to,
		string? on, string? link, string? direction, bool onLeave,
		string? @namespace, string description)
	{
		// "" explicitly clears (stored/serialized as null — the wire omits null, same as
		// every other optional prose field here); a non-empty string sets it.
		var text = description.Length == 0 ? null : description;
		var matched = false;

		switch (primitive.Trim().ToLowerInvariant())
		{
			case "kind":
				RequireKind(kind);
				return (def with { Kinds = MapKind(def.Kinds, kind!, k => { matched = true; return k with { Description = text }; }) }, matched);

			case "status":
				RequireKind(kind);
				RequireType(type);
				RequireSlug(slug);
				return (def with
				{
					Kinds = MapKind(def.Kinds, kind!, k => k with
					{
						Workflows = MapBlock(k.Workflows, type!, b => b with
						{
							Statuses = b.Statuses.Select(s =>
							{
								if (!string.Equals(s.Slug, slug, StringComparison.OrdinalIgnoreCase)) return s;
								matched = true;
								return s with { Description = text };
							}).ToList(),
						}),
					}),
				}, matched);

			case "transition":
				RequireKind(kind);
				RequireType(type);
				if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to))
					throw new ArgumentException("primitive 'transition' needs both 'from' and 'to' (the transition's natural key)");
				return (def with
				{
					Kinds = MapKind(def.Kinds, kind!, k => k with
					{
						Workflows = MapBlock(k.Workflows, type!, b => b with
						{
							Transitions = b.Transitions.Select(t =>
							{
								if (!string.Equals(t.From, from, StringComparison.OrdinalIgnoreCase) ||
									!string.Equals(t.To, to, StringComparison.OrdinalIgnoreCase))
									return t;
								matched = true;
								return t with { Description = text };
							}).ToList(),
						}),
					}),
				}, matched);

			case "effect":
				RequireKind(kind);
				if (string.IsNullOrWhiteSpace(on) || string.IsNullOrWhiteSpace(link) || string.IsNullOrWhiteSpace(direction))
					throw new ArgumentException("primitive 'effect' needs 'on', 'link' and 'direction' (the effect's natural key; 'onLeave' defaults to false)");
				return (def with
				{
					Kinds = MapKind(def.Kinds, kind!, k => k with
					{
						Effects = k.Effects.Select(e =>
						{
							if (!string.Equals(e.On, on, StringComparison.OrdinalIgnoreCase) ||
								!string.Equals(e.Link, link, StringComparison.OrdinalIgnoreCase) ||
								!string.Equals(e.Direction, direction, StringComparison.OrdinalIgnoreCase) ||
								e.OnLeave != onLeave)
								return e;
							matched = true;
							return e with { Description = text };
						}).ToList(),
					}),
				}, matched);

			case "constraint":
				RequireKind(kind);
				RequireType(type);
				if (string.IsNullOrWhiteSpace(link))
					throw new ArgumentException("primitive 'constraint' needs 'link' in addition to 'kind'/'type' (the constraint's natural key: {type, link})");
				return (def with
				{
					Kinds = MapKind(def.Kinds, kind!, k => k with
					{
						LinkConstraints = k.LinkConstraints.Select(c =>
						{
							if (!string.Equals(c.Type, type, StringComparison.OrdinalIgnoreCase) ||
								!string.Equals(c.Link, link, StringComparison.OrdinalIgnoreCase))
								return c;
							matched = true;
							return c with { Description = text };
						}).ToList(),
					}),
				}, matched);

			case "linkkind":
				if (string.IsNullOrWhiteSpace(slug))
					throw new ArgumentException("primitive 'linkKind' needs 'slug' (the declared relation kind's natural key)");
				return (def with
				{
					LinkKinds = def.LinkKinds.Select(lk =>
					{
						if (!string.Equals(lk.Slug, slug, StringComparison.OrdinalIgnoreCase)) return lk;
						matched = true;
						return lk with { Description = text };
					}).ToList(),
				}, matched);

			case "tagaxis":
				if (string.IsNullOrWhiteSpace(@namespace))
					throw new ArgumentException("primitive 'tagAxis' needs 'namespace' (the declared axis's natural key)");
				return (def with
				{
					TagAxes = def.TagAxes.Select(a =>
					{
						if (!string.Equals(a.Namespace, @namespace, StringComparison.OrdinalIgnoreCase)) return a;
						matched = true;
						return a with { Description = text };
					}).ToList(),
				}, matched);

			default:
				throw new ArgumentException($"primitive '{primitive}' is not one of: {string.Join(", ", Primitives)}");
		}
	}

	static void RequireKind(string? kind)
	{
		if (string.IsNullOrWhiteSpace(kind))
			throw new ArgumentException("'kind' is required to address a primitive nested under a kind");
	}

	static void RequireType(string? type)
	{
		if (string.IsNullOrWhiteSpace(type))
			throw new ArgumentException("'type' is required — it names any ONE type slug of the workflow block that owns this primitive");
	}

	static void RequireSlug(string? slug)
	{
		if (string.IsNullOrWhiteSpace(slug))
			throw new ArgumentException("'slug' is required (the status's natural key within its block)");
	}

	// Transform ONLY the kind named `kindSlug` — every other kind of the definition passes
	// through unchanged. `map` runs even if nothing inside it ends up matching (e.g. a bad
	// `type`/`slug` under a real kind) — the caller's `matched` closure is what actually
	// reports whether anything was found.
	static List<MethodologyKindDef> MapKind(IReadOnlyList<MethodologyKindDef> kinds, string kindSlug, Func<MethodologyKindDef, MethodologyKindDef> map) =>
		kinds.Select(k => string.Equals(k.Kind, kindSlug, StringComparison.OrdinalIgnoreCase) ? map(k) : k).ToList();

	// Transform ONLY the workflow block that declares `type` among its Types — a block is
	// the natural container for statuses/transitions, and any one type slug of the block
	// disambiguates it (spec methodology-describe-verb: natural keys, not positional index).
	static List<MethodologyWorkflowDef> MapBlock(IReadOnlyList<MethodologyWorkflowDef> blocks, string type, Func<MethodologyWorkflowDef, MethodologyWorkflowDef> map) =>
		blocks.Select(b => b.Types.Any(t => string.Equals(t, type, StringComparison.OrdinalIgnoreCase)) ? map(b) : b).ToList();
}
