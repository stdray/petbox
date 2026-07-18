using System.Text.RegularExpressions;
using FluentValidation;
using PetBox.Tasks.Workflow;

namespace PetBox.Tasks.Validation;

// Integrity rules for a user-defined methodology definition, checked over the WHOLE
// document before it is stored: slugs are canonical, every reference resolves inside its
// own block, and nothing is duplicated. The definition is stored verbatim (data, not
// normalized input), so slugs are matched exactly — "Feature" is invalid, not silently
// lowercased. Status SLUGS are exempt from the slug spec (the built-in catalog itself uses
// "InProgress"-style slugs); they only need to be non-empty and unique per block
// (case-insensitive, matching the Workflow slug-matching convention).
internal sealed partial class MethodologyDefinitionValidator : AbstractValidator<MethodologyDefinition>
{
	// Same spec as boards/nodes/logs: starts a-z, then a-z/0-9/_/- up to 100 chars.
	[GeneratedRegex(@"^[a-z][a-z0-9_-]{0,99}$")]
	private static partial Regex SlugRegex();

	static bool IsSlug(string? s) => s is not null && SlugRegex().IsMatch(s);

	// STATUS-slug shape: like the slug spec but case-insensitive, because status slugs are
	// exempt from the canonical spec (the built-in catalog itself uses "InProgress"-style
	// slugs). Used for cross-kind status references (effect Set/OnlyFrom, constraint
	// TargetStatuses) that can't be resolved against a declared vocabulary.
	[GeneratedRegex(@"^[a-zA-Z][a-zA-Z0-9_-]{0,99}$")]
	private static partial Regex StatusSlugRegex();

	static bool IsStatusSlug(string? s) => s is not null && StatusSlugRegex().IsMatch(s);

	// Link kinds a creation constraint may name: the ones expressible IN the upsert call
	// (task_spec = specRef, blocks = blockedBy, idea_spec = ideaRef). Any other kind is
	// wired post-hoc via relations_create and therefore can't gate creation.
	static readonly string[] UpsertExpressibleLinks = ["task_spec", "blocks", "idea_spec"];

	// A transition-effect direction relative to the OWNING node's edge.
	static readonly string[] EffectDirections = ["incoming", "outgoing"];

	// Checklist items are one-line free-text conditions, not documents.
	const int ChecklistItemMaxLength = 500;

	public MethodologyDefinitionValidator()
	{
		RuleFor(d => d.Name)
			.Must(IsSlug)
			.WithMessage(d => $"methodology name '{d.Name}' is not a valid slug (^[a-z][a-z0-9_-]{{0,99}}$)");

		RuleFor(d => d.Kinds)
			.NotEmpty()
			.WithMessage("a methodology needs at least one kind");

		RuleFor(d => d.Kinds)
			.Custom((kinds, ctx) =>
			{
				// Cross-kind context for the schema-v2 declarations: effect links resolve
				// against the whole relation-kind vocabulary (builtin process + neutral +
				// definition-declared), and a constraint's TargetKind/TargetStatuses resolve
				// against the definition's OWN kinds when it declares the target.
				var knownLinks = new HashSet<string>(
					MethodologyRuntime.ProcessRelationKinds
						.Concat(MethodologyRuntime.NeutralRelationKinds)
						.Concat((ctx.InstanceToValidate.LinkKinds ?? []).Select(lk => lk.Slug ?? "")),
					StringComparer.OrdinalIgnoreCase);
				var declaredKindStatuses = (kinds ?? [])
					.Where(k => IsSlug(k.Kind))
					.DistinctBy(k => k.Kind, StringComparer.OrdinalIgnoreCase)
					.ToDictionary(k => k.Kind, KindStatuses, StringComparer.OrdinalIgnoreCase);

				var seenKinds = new HashSet<string>(StringComparer.Ordinal);
				foreach (var kind in kinds ?? [])
				{
					if (!IsSlug(kind.Kind))
					{
						ctx.AddFailure($"kind '{kind.Kind}' is not a valid slug (^[a-z][a-z0-9_-]{{0,99}}$)");
						continue;
					}
					if (!seenKinds.Add(kind.Kind))
					{
						ctx.AddFailure($"kind '{kind.Kind}' is defined more than once");
						continue;
					}
					ValidateKind(kind, knownLinks, declaredKindStatuses, ctx);
				}
			});

		RuleFor(d => d.LinkKinds)
			.Custom((linkKinds, ctx) =>
			{
				var def = ctx.InstanceToValidate;
				// Direction ends must resolve to a kind THIS definition declares (a valid slug).
				var declaredKinds = new HashSet<string>(
					(def.Kinds ?? []).Where(k => IsSlug(k.Kind)).Select(k => k.Kind),
					StringComparer.OrdinalIgnoreCase);

				var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
				foreach (var lk in linkKinds ?? [])
				{
					if (!IsSlug(lk.Slug))
						ctx.AddFailure($"link kind '{lk.Slug}' is not a valid slug (^[a-z][a-z0-9_-]{{0,99}}$)");
					else if (MethodologyRuntime.ProcessRelationKinds.Contains(lk.Slug, StringComparer.OrdinalIgnoreCase)
						|| MethodologyRuntime.NeutralRelationKinds.Contains(lk.Slug, StringComparer.OrdinalIgnoreCase))
						ctx.AddFailure($"link kind '{lk.Slug}' collides with a builtin relation kind ({string.Join("|", MethodologyRuntime.ProcessRelationKinds.Concat(MethodologyRuntime.NeutralRelationKinds))})");
					else if (!seen.Add(lk.Slug))
						ctx.AddFailure($"link kind '{lk.Slug}' is declared more than once");

					// Category-enum integrity — belt-and-suspenders: the wire (ParseLinkCategory)
					// rejects an unknown string before it reaches here, but a directly-constructed
					// definition could still carry an out-of-range enum value.
					if (!Enum.IsDefined(lk.Category))
						ctx.AddFailure($"link kind '{lk.Slug}': category '{lk.Category}' is not valid ({string.Join("|", Enum.GetNames<LinkCategory>().Select(n => n.ToLowerInvariant()))})");

					ValidateLinkDirection(lk, declaredKinds, def, ctx);
				}
			});

		RuleFor(d => d.TagAxes)
			.Custom((axes, ctx) =>
			{
				var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
				foreach (var axis in axes ?? [])
				{
					if (!IsSlug(axis.Namespace))
						ctx.AddFailure($"tag axis '{axis.Namespace}' is not a valid slug (^[a-z][a-z0-9_-]{{0,99}}$)");
					else if (!seen.Add(axis.Namespace))
						ctx.AddFailure($"tag axis '{axis.Namespace}' is declared more than once");
				}
			});
	}

	// Direction rules for a declared relation kind (v1, spec methodology-link-kinds-declared /
	// design 03 A.2). FromKind/ToKind name the KINDS at the ends of the STORED edge (from→to);
	// they must resolve to a kind this definition declares. A self-kind edge (both ends the same
	// non-null kind) is out of v1 — the stored orientation can't tell the two ends apart, so it
	// needs an explicit end-target discriminator that doesn't exist yet. Finally, any
	// LinkConstraint opening THIS declared kind to a writer must be owned by a kind sitting on one
	// END of the direction (design 03 A.2 degenerate case b) — a null end is unconstrained and
	// satisfies the rule on its own. Only runs for declared kinds that CARRY a Direction, so the
	// direction-less builtins/quartet never fall under it.
	static void ValidateLinkDirection(
		MethodologyLinkKindDef lk, HashSet<string> declaredKinds,
		MethodologyDefinition def, ValidationContext<MethodologyDefinition> ctx)
	{
		var dir = lk.Direction;
		if (dir is null) return;
		var label = $"link kind '{lk.Slug}': direction";

		if (dir.FromKind is not null && !declaredKinds.Contains(dir.FromKind))
			ctx.AddFailure($"{label} references an unknown kind '{dir.FromKind}' — fromKind must be a kind this definition declares (kinds: {string.Join("|", declaredKinds)})");
		if (dir.ToKind is not null && !declaredKinds.Contains(dir.ToKind))
			ctx.AddFailure($"{label} references an unknown kind '{dir.ToKind}' — toKind must be a kind this definition declares (kinds: {string.Join("|", declaredKinds)})");

		if (dir.FromKind is not null && dir.ToKind is not null
			&& string.Equals(dir.FromKind, dir.ToKind, StringComparison.OrdinalIgnoreCase))
			ctx.AddFailure($"{label} is self-kind (fromKind == toKind == '{dir.FromKind}') — a self-kind link needs an explicit end-target discriminator, not supported in v1");

		foreach (var k in def.Kinds ?? [])
		{
			foreach (var c in k.LinkConstraints ?? [])
			{
				if (!string.Equals(c.Link, lk.Slug, StringComparison.OrdinalIgnoreCase)) continue;
				var ownerOnAnEnd =
					dir.FromKind is null || dir.ToKind is null
					|| string.Equals(k.Kind, dir.FromKind, StringComparison.OrdinalIgnoreCase)
					|| string.Equals(k.Kind, dir.ToKind, StringComparison.OrdinalIgnoreCase);
				if (!ownerOnAnEnd)
					ctx.AddFailure($"link '{lk.Slug}' is unavailable to kind '{k.Kind}' — its direction (from:{dir.FromKind ?? "*"} to:{dir.ToKind ?? "*"}) includes '{k.Kind}' at neither end");
			}
		}
	}

	// The status vocabulary of one kind: every status slug across ALL its workflow blocks
	// (case-insensitive — the Workflow slug-matching convention).
	static HashSet<string> KindStatuses(MethodologyKindDef kind) =>
		(kind.Workflows ?? [])
			.SelectMany(b => b.Statuses ?? [])
			.Select(s => s.Slug ?? "")
			.ToHashSet(StringComparer.OrdinalIgnoreCase);

	static void ValidateKind(
		MethodologyKindDef kind,
		HashSet<string> knownLinks,
		Dictionary<string, HashSet<string>> declaredKindStatuses,
		ValidationContext<MethodologyDefinition> ctx)
	{
		if (kind.Workflows is not { Count: > 0 })
		{
			ctx.AddFailure($"kind '{kind.Kind}' needs at least one workflow block");
			return;
		}

		// Type slugs are unique ACROSS blocks within the kind — a type resolves to exactly
		// one state machine.
		var seenTypes = new HashSet<string>(StringComparer.Ordinal);
		foreach (var block in kind.Workflows)
		{
			if (block.Types is not { Count: > 0 })
			{
				ctx.AddFailure($"kind '{kind.Kind}': every workflow block needs at least one type");
				continue;
			}
			foreach (var type in block.Types)
			{
				if (!IsSlug(type))
					ctx.AddFailure($"kind '{kind.Kind}': type '{type}' is not a valid slug (^[a-z][a-z0-9_-]{{0,99}}$)");
				else if (!seenTypes.Add(type))
					ctx.AddFailure($"kind '{kind.Kind}': type '{type}' appears in more than one workflow block — a type maps to exactly one state machine");
			}
			ValidateBlock(kind.Kind, block, ctx);
		}

		// Creation link constraints: each names a type the kind's workflow blocks declare
		// and an upsert-expressible link kind; a (type, link) pair is stated at most once.
		var seenConstraints = new HashSet<(string, string)>();
		foreach (var c in kind.LinkConstraints ?? [])
		{
			if (!UpsertExpressibleLinks.Contains(c.Link, StringComparer.OrdinalIgnoreCase))
				ctx.AddFailure($"kind '{kind.Kind}': link constraint ({c.Type}, {c.Link}): only upsert-expressible link kinds can be creation constraints ({string.Join("|", UpsertExpressibleLinks)}) — a post-hoc relation kind can't gate creation");
			else if (!seenTypes.Contains(c.Type))
				ctx.AddFailure($"kind '{kind.Kind}': link constraint ({c.Type}, {c.Link}): type '{c.Type}' is not declared by this kind's workflow blocks (types: {string.Join("|", seenTypes)})");
			else if (!seenConstraints.Add((c.Type.ToLowerInvariant(), c.Link.ToLowerInvariant())))
				ctx.AddFailure($"kind '{kind.Kind}': duplicate link constraint ({c.Type}, {c.Link})");
			ValidateConstraintTarget(kind.Kind, c, declaredKindStatuses, ctx);
		}

		ValidateEffects(kind, KindStatuses(kind), knownLinks, ctx);
		ValidateAutoWire(kind, ctx);
		ValidateDelivery(kind, ctx);
		ValidateDefaultView(kind, ctx);
		ValidateOutlineReveal(kind, ctx);
		ValidateBoardName(kind, ctx);
	}

	// BoardName is a board-name candidate PickBoardName tries first (spec primitives-enum-
	// residual) — format-checked against the same slug spec TaskBoardStore.CreateAsync enforces
	// on every board name. The "node"-reserved / already-taken skip is PickBoardName's own
	// runtime concern (a definition-time check here can't see sibling instances' boards), so
	// this only rejects a shape that could never be a valid board name at all.
	static void ValidateBoardName(MethodologyKindDef kind, ValidationContext<MethodologyDefinition> ctx)
	{
		if (kind.BoardName is null) return;
		if (!IsSlug(kind.BoardName))
			ctx.AddFailure($"kind '{kind.Kind}': boardName '{kind.BoardName}' is not a valid slug (^[a-z][a-z0-9_-]{{0,99}}$)");
	}

	// DefaultView names a BoardViewModeNames entry (methodology-default-view-field). Format-
	// checked against the known mode-name set only — whether PetBox.Web has shipped a
	// renderer for it yet is a resolve-time (not definition-time) concern, so `kanban`/
	// `outline`/`table` are already valid here before their partials exist.
	static void ValidateDefaultView(MethodologyKindDef kind, ValidationContext<MethodologyDefinition> ctx)
	{
		if (kind.DefaultView is null) return;
		if (!BoardViewModeNames.IsKnown(kind.DefaultView))
			ctx.AddFailure($"kind '{kind.Kind}': defaultView '{kind.DefaultView}' is not a known view mode ({string.Join("|", BoardViewModeNames.All)})");
	}

	// OutlineReveal names an OutlineRevealModeNames entry (board-view-mode-framework). Format-
	// checked against the known reveal-mode set only, same posture as DefaultView.
	static void ValidateOutlineReveal(MethodologyKindDef kind, ValidationContext<MethodologyDefinition> ctx)
	{
		if (kind.OutlineReveal is null) return;
		if (!OutlineRevealModeNames.IsKnown(kind.OutlineReveal))
			ctx.AddFailure($"kind '{kind.Kind}': outlineReveal '{kind.OutlineReveal}' is not a known reveal mode ({string.Join("|", OutlineRevealModeNames.All)})");
	}

	// AutoWireSpecFrom is a kind slug naming the board to wire SpecBoard to. Format-checked
	// only (the target may be a preset kind outside this definition); cannot equal self.
	static void ValidateAutoWire(MethodologyKindDef kind, ValidationContext<MethodologyDefinition> ctx)
	{
		if (kind.AutoWireSpecFrom is null) return;
		if (!IsSlug(kind.AutoWireSpecFrom))
			ctx.AddFailure($"kind '{kind.Kind}': autoWireSpecFrom '{kind.AutoWireSpecFrom}' is not a valid slug (^[a-z][a-z0-9_-]{{0,99}}$)");
		else if (string.Equals(kind.AutoWireSpecFrom, kind.Kind, StringComparison.OrdinalIgnoreCase))
			ctx.AddFailure($"kind '{kind.Kind}': autoWireSpecFrom cannot name the same kind");
	}

	// Delivery type roles: RequiredTypes non-empty (otherwise the roll-up is always
	// not_started and the declaration is noise); every type is a valid slug. Types name
	// LINKED tasks (typically of another kind), so they are NOT required to appear in this
	// kind's workflow blocks.
	static void ValidateDelivery(MethodologyKindDef kind, ValidationContext<MethodologyDefinition> ctx)
	{
		if (kind.Delivery is null) return;
		var d = kind.Delivery;
		if (d.RequiredTypes is not { Count: > 0 })
		{
			ctx.AddFailure($"kind '{kind.Kind}': delivery.requiredTypes must be non-empty (omit delivery for no roll-up)");
			return;
		}
		var seenReq = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (var t in d.RequiredTypes)
		{
			if (!IsSlug(t))
				ctx.AddFailure($"kind '{kind.Kind}': delivery.requiredTypes '{t}' is not a valid slug (^[a-z][a-z0-9_-]{{0,99}}$)");
			else if (!seenReq.Add(t))
				ctx.AddFailure($"kind '{kind.Kind}': delivery.requiredTypes '{t}' is declared more than once");
		}
		var seenDef = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (var t in d.DefectTypes ?? [])
		{
			if (!IsSlug(t))
				ctx.AddFailure($"kind '{kind.Kind}': delivery.defectTypes '{t}' is not a valid slug (^[a-z][a-z0-9_-]{{0,99}}$)");
			else if (!seenDef.Add(t))
				ctx.AddFailure($"kind '{kind.Kind}': delivery.defectTypes '{t}' is declared more than once");
			else if (seenReq.Contains(t))
				ctx.AddFailure($"kind '{kind.Kind}': delivery type '{t}' cannot be both required and defect");
		}
	}

	// Link-constraint TARGET declaration (schema v2): `TargetKind` is a kind slug; when the
	// definition itself declares that kind, `TargetStatuses` must be statuses of it — a
	// target OUTSIDE the definition (a preset kind, or another project's vocabulary) is
	// format-checked only and resolves at runtime (a later task).
	static void ValidateConstraintTarget(
		string kindSlug, MethodologyLinkConstraintDef c,
		Dictionary<string, HashSet<string>> declaredKindStatuses,
		ValidationContext<MethodologyDefinition> ctx)
	{
		var label = $"kind '{kindSlug}': link constraint ({c.Type}, {c.Link})";
		if (c.TargetKind is not null && !IsSlug(c.TargetKind))
			ctx.AddFailure($"{label}: targetKind '{c.TargetKind}' is not a valid slug (^[a-z][a-z0-9_-]{{0,99}}$)");
		if (c.TargetStatuses is null) return;
		if (c.TargetStatuses.Count == 0)
		{
			ctx.AddFailure($"{label}: targetStatuses must be non-empty when provided (omit it for no status restriction)");
			return;
		}
		var declared = c.TargetKind is not null && declaredKindStatuses.TryGetValue(c.TargetKind, out var s) ? s : null;
		foreach (var status in c.TargetStatuses)
		{
			if (declared is not null && !declared.Contains(status ?? ""))
				ctx.AddFailure($"{label}: target status '{status}' is not a status of kind '{c.TargetKind}' (statuses: {string.Join("|", declared)})");
			else if (declared is null && !IsStatusSlug(status))
				ctx.AddFailure($"{label}: target status '{status}' is not a valid status slug (^[a-zA-Z][a-zA-Z0-9_-]{{0,99}}$)");
		}
	}

	// Transition effects (schema v2 + Effect.onLeave, methodology-blocks-gate-data): `On`
	// triggers on the OWNING kind's own statuses (entered by default, LEFT when `OnLeave` is
	// true); `Link` must be a relation kind the project knows (builtin process/neutral or a
	// definition-declared linkKind); `Direction` is incoming|outgoing; `Set` is OPTIONAL (null =
	// a pure edge-consumption effect, no status propagated) — when present, `Set`/`OnlyFrom`
	// name statuses of the LINKED node's kind, cross-kind so format-checked only (no static
	// resolution); an (On, Link, Direction, OnLeave) quadruple is declared at most once — an
	// onEnter and an onLeave effect may legitimately share the same (On, Link, Direction).
	static void ValidateEffects(
		MethodologyKindDef kind, HashSet<string> kindStatuses, HashSet<string> knownLinks,
		ValidationContext<MethodologyDefinition> ctx)
	{
		var seen = new HashSet<(string, string, string, bool)>();
		foreach (var e in kind.Effects ?? [])
		{
			var label = $"kind '{kind.Kind}': effect ({(e.OnLeave ? "leave" : "on")} {e.On}, {e.Direction} {e.Link} -> {e.Set ?? "(closed)"})";
			if (string.IsNullOrWhiteSpace(e.On) || !kindStatuses.Contains(e.On))
				ctx.AddFailure($"{label}: 'on' '{e.On}' is not a status this kind's workflow blocks declare (statuses: {string.Join("|", kindStatuses)})");
			if (!EffectDirections.Contains(e.Direction ?? "", StringComparer.OrdinalIgnoreCase))
				ctx.AddFailure($"{label}: direction '{e.Direction}' is not valid ({string.Join("|", EffectDirections)})");
			if (!knownLinks.Contains(e.Link ?? ""))
				ctx.AddFailure($"{label}: link '{e.Link}' is not a relation kind this project knows (builtin: {string.Join("|", MethodologyRuntime.ProcessRelationKinds.Concat(MethodologyRuntime.NeutralRelationKinds))}; or declare it in linkKinds)");
			if (e.Set is not null && !IsStatusSlug(e.Set))
				ctx.AddFailure($"{label}: 'set' '{e.Set}' is not a valid status slug (^[a-zA-Z][a-zA-Z0-9_-]{{0,99}}$)");
			if (e.OnlyFrom is not null && !IsStatusSlug(e.OnlyFrom))
				ctx.AddFailure($"{label}: onlyFrom '{e.OnlyFrom}' is not a valid status slug (^[a-zA-Z][a-zA-Z0-9_-]{{0,99}}$)");
			if (!seen.Add((e.On?.ToLowerInvariant() ?? "", e.Link?.ToLowerInvariant() ?? "", e.Direction?.ToLowerInvariant() ?? "", e.OnLeave)))
				ctx.AddFailure($"kind '{kind.Kind}': duplicate effect ({(e.OnLeave ? "leave" : "on")} {e.On}, {e.Direction} {e.Link}) — one declaration per (on, link, direction, onLeave)");
		}
	}

	static void ValidateBlock(string kind, MethodologyWorkflowDef block, ValidationContext<MethodologyDefinition> ctx)
	{
		var blockName = $"kind '{kind}', block [{string.Join("|", block.Types ?? [])}]";

		if (block.Statuses is not { Count: > 0 })
		{
			ctx.AddFailure($"{blockName}: a workflow block needs at least one status (Statuses[0] is the initial)");
			return;
		}

		var slugs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (var status in block.Statuses)
		{
			if (string.IsNullOrWhiteSpace(status.Slug))
				ctx.AddFailure($"{blockName}: a status needs a non-empty slug");
			else if (!slugs.Add(status.Slug))
				ctx.AddFailure($"{blockName}: duplicate status slug '{status.Slug}' (slugs are case-insensitive)");
			if (!Enum.IsDefined(status.Kind))
				ctx.AddFailure($"{blockName}: status '{status.Slug}' has an unknown kind '{status.Kind}' (valid: open|terminalok|terminalcancel)");
		}

		var edges = new HashSet<(string, string)>();
		foreach (var t in block.Transitions ?? [])
		{
			if (string.IsNullOrWhiteSpace(t.From) || !slugs.Contains(t.From))
				ctx.AddFailure($"{blockName}: transition from '{t.From}' does not reference a status of this block (statuses: {string.Join("|", block.Statuses.Select(s => s.Slug))})");
			if (string.IsNullOrWhiteSpace(t.To) || !slugs.Contains(t.To))
				ctx.AddFailure($"{blockName}: transition to '{t.To}' does not reference a status of this block (statuses: {string.Join("|", block.Statuses.Select(s => s.Slug))})");
			if (!edges.Add((t.From?.ToLowerInvariant() ?? "", t.To?.ToLowerInvariant() ?? "")))
				ctx.AddFailure($"{blockName}: duplicate transition ({t.From} -> {t.To})");
			if (t.PreconditionArtifact is not null && !IsSlug(t.PreconditionArtifact))
				ctx.AddFailure($"{blockName}: transition ({t.From} -> {t.To}): preconditionArtifact '{t.PreconditionArtifact}' is not a valid slug (^[a-z][a-z0-9_-]{{0,99}}$)");
			// enforceApproval is a MODE of the approval gate, not a gate of its own.
			if (t.EnforceApproval && !t.RequiresApproval)
				ctx.AddFailure($"{blockName}: transition ({t.From} -> {t.To}): enforceApproval is only meaningful with requiresApproval — set requiresApproval:true or drop enforceApproval");
			// Same rule for the schema-v2 replacement: enforce.approval is only meaningful with
			// requiresApproval (the WHO gate); it never implies requiresApproval itself.
			if (t.Enforce?.Approval == true && !t.RequiresApproval)
				ctx.AddFailure($"{blockName}: transition ({t.From} -> {t.To}): enforce.approval is only meaningful with requiresApproval — set requiresApproval:true or drop enforce.approval");

			// requiredArtifacts (schema v2, spec methodology-gate-strictness) REPLACES
			// requiresReason/preconditionArtifact — don't declare both shapes on one transition,
			// or which one is authoritative (EffectiveRequiredArtifacts prefers the new shape
			// silently) becomes a trap for whoever edits this document next.
			if (t.RequiredArtifacts.Count > 0 && (t.RequiresReason || t.PreconditionArtifact is not null))
				ctx.AddFailure($"{blockName}: transition ({t.From} -> {t.To}): don't mix legacy requiresReason/preconditionArtifact with requiredArtifacts on the same transition — declare the gate once, as requiredArtifacts:[{{slug,inline}}]");

			var artifactSlugs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			foreach (var a in t.RequiredArtifacts)
			{
				if (!IsSlug(a.Slug))
					ctx.AddFailure($"{blockName}: transition ({t.From} -> {t.To}): requiredArtifacts slug '{a.Slug}' is not a valid slug (^[a-z][a-z0-9_-]{{0,99}}$)");
				else if (!artifactSlugs.Add(a.Slug))
					ctx.AddFailure($"{blockName}: transition ({t.From} -> {t.To}): duplicate requiredArtifacts slug '{a.Slug}'");
				// v1 boundary (spec methodology-gate-strictness): the only inline channel a call
				// can carry is NodePatch.Reason — a custom inline artifact has nowhere to receive
				// its content, so it would be a runtime surprise on the first write, not a
				// declared feature. Reject it at definition time instead.
				if (a.Inline && !string.Equals(a.Slug, "reason", StringComparison.Ordinal))
					ctx.AddFailure($"{blockName}: transition ({t.From} -> {t.To}): requiredArtifacts '{a.Slug}' declares inline:true, but only slug 'reason' may be inline in v1 — no other call field carries inline content");
			}
			foreach (var item in t.Checklist ?? [])
			{
				if (string.IsNullOrWhiteSpace(item))
					ctx.AddFailure($"{blockName}: transition ({t.From} -> {t.To}): a checklist item must be a non-empty string");
				else if (item.Length > ChecklistItemMaxLength)
					ctx.AddFailure($"{blockName}: transition ({t.From} -> {t.To}): a checklist item exceeds {ChecklistItemMaxLength} chars — keep items one-line conditions");
			}
		}
	}
}
