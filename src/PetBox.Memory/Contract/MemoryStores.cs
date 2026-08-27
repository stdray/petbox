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

	// Stores an AUTOMATIC whole-container sweep must skip — the shared shape of "read every store
	// this project has" (spec: autocapture-dedup, work `autocapture-dedup-blind-to-canon`). Two
	// unrelated reasons live in one set because any such sweep must honour both:
	//   the SENSITIVE names — have held secrets, must never be auto-pulled into an agent's context
	//     or an outbound prompt. A HARD veto: not a policy, a rule.
	//   "session-digests"  — the digest job's summaries of the very sessions a sweep reads. Excluded
	//     for DOUBLE-COUNTING, not for secrecy; a digest is perfectly linkable knowledge.
	//
	// Consequently the two legs are NOT interchangeable and a caller that cares about the first must
	// say so itself rather than lean on this set: the recall leg is a policy someone may reasonably
	// tune, and narrowing it must never be able to reopen a sensitive store. See the deliberately
	// redundant pair of filters in SessionFactsJob.CollectNeighborsAsync.
	//
	// MemoryService.SweepExcludedStores computes the same union for memory_search's implicit sweep
	// and predates this; the two are kept identical by hand until that private copy can be collapsed
	// onto this one (the file is owned by another branch right now).
	public static readonly IReadOnlySet<string> AutoSweepExcludedNames =
		new HashSet<string>(SensitiveNames.Append("session-digests"), StringComparer.OrdinalIgnoreCase);

	// True when an automatic whole-container sweep may include this store. Naming the sensitive leg
	// separately is not redundancy for its own sake — it is what keeps the veto readable at every
	// call site that must not be allowed to drift.
	public static bool IsAutoSweepable(string? store) =>
		!string.IsNullOrWhiteSpace(store)
		&& !IsSensitive(store)
		&& !AutoSweepExcludedNames.Contains(store.Trim());
}
