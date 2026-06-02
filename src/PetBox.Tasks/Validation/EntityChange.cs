namespace PetBox.Tasks.Validation;

// The unit a change-aware validator sees: the prior active row (Old, null for a brand-new
// node) paired with the desired row (New). It lets a rule reason about the *transition*,
// not just the new value — e.g. "this field may not change once set" — without collapsing
// into hand-rolled if/else in the service. An INTERNAL implementation detail of the Tasks
// module's validation, not part of the public contract.
internal sealed record EntityChange<T>(T? Old, T New);
