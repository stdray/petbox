namespace PetBox.Memory.Contract;

// The SENSITIVITY marker of a memory store — the one place that answers "may an entry of this
// store be surfaced by an AUTOMATIC affordance?" (spec: memory-entry-url / memory-key-mention-link).
// A sensitive store is one that has held secrets/credentials: it must never be auto-pulled into an
// agent's context and must never get an auto-generated link (a linked key is a pointer that invites
// exactly that pull). Members: "ops" (sensitive operational).
//
// Deliberately NARROWER than two neighbouring sets, which is why it is its own:
//   - MemoryStore.SystemStoreNames (the IsSystem badge + delete-guard) covers plumbing stores that
//     are perfectly linkable knowledge ("canon", "autocaptured").
//   - MemoryService.SweepExcludedStores (implicit-search recall) = these sensitive stores PLUS
//     "session-digests", which is excluded for double-counting, not for secrecy — a digest is
//     linkable.
// Lives in Contract (not Data) so Web pages/renderers can ask the question without reaching the
// store door (MemoryBoundaryTests forbids that dependency).
public static class MemoryStores
{
	public static readonly IReadOnlySet<string> SensitiveNames =
		new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "ops" };

	// True when the store's entries must not be auto-linked / auto-pulled. Null/empty is not a
	// store name — treated as non-sensitive (the caller has nothing to link anyway).
	public static bool IsSensitive(string? store) =>
		!string.IsNullOrWhiteSpace(store) && SensitiveNames.Contains(store.Trim());
}
