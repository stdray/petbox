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

	// Link kinds a creation constraint may name: the ones expressible IN the upsert call
	// (task_spec = specRef, blocks = blockedBy, idea_spec = ideaRef). Any other kind is
	// wired post-hoc via relations_create and therefore can't gate creation.
	static readonly string[] UpsertExpressibleLinks = ["task_spec", "blocks", "idea_spec"];

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
					ValidateKind(kind, ctx);
				}
			});

		RuleFor(d => d.LinkKinds)
			.Custom((linkKinds, ctx) =>
			{
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

	static void ValidateKind(MethodologyKindDef kind, ValidationContext<MethodologyDefinition> ctx)
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
		}
	}
}
