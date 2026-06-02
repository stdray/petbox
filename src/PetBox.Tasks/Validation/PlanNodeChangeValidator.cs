using FluentValidation;
using PetBox.Tasks.Data;

namespace PetBox.Tasks.Validation;

// Declarative immutable-field rules for a plan-node upsert, expressed over the old-vs-new
// change so they read as invariants rather than scattered throws. These hold things that
// must not drift once a node exists:
//   - NodeId never changes (links bind to it; the service carries it across edits/renames,
//     so a mismatch means something tried to repoint identity).
//   - type is fixed after creation (a feature stays a feature; reclassifying would silently
//     change which workflow + delivery rules apply). Only enforced once a type is set, so a
//     legacy node with an empty type can still be given one.
// Status/transition legality stays in WorkflowEngine (already the single point); the async
// cross-entity rules (spec links, blockers) stay in the service. This validator owns only
// what is genuinely a declarative, context-carrying invariant.
internal sealed class PlanNodeChangeValidator : AbstractValidator<EntityChange<PlanNode>>
{
	public PlanNodeChangeValidator()
	{
		// New nodes (Old is null) have no prior to violate — every rule is guarded on Old.
		RuleFor(c => c.New.NodeId)
			.Equal(c => c.Old!.NodeId)
			.When(c => c.Old is not null)
			.WithMessage(c => $"NodeId is immutable (node '{c.New.Key}')");

		RuleFor(c => c.New.Type)
			.Equal(c => c.Old!.Type)
			.When(c => c.Old is not null && c.Old!.Type.Length > 0)
			.WithMessage(c => $"a node's type is immutable — '{c.New.Key}' was '{c.Old!.Type}', cannot become '{c.New.Type}'");
	}
}
