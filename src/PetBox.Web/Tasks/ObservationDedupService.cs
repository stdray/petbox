using PetBox.LlmRouter.Contract;
using PetBox.Tasks.Contract;
using PetBox.Web.Mcp.Contract;
using PetBox.Web.Search;

namespace PetBox.Web.Tasks;

// One node the caller asked to CREATE that instead landed on an existing observation
// (work observation-kind-and-dedup): `RequestedKey` is the slug the caller sent (never
// created), `ExistingKey`/`ExistingNodeId` name what absorbed it, `RecurrenceCount` is the
// new total after this hit.
public sealed record ObservationDedupHit(string RequestedKey, string ExistingKey, string ExistingNodeId, long RecurrenceCount);

// `RemainingNodes` are the caller's nodes that did NOT dedup — the normal tasks.UpsertAsync
// path still owns creating these (CAS, cascades, FSM effects, all untouched). `Hits` are the
// ones that did.
public sealed record ObservationDedupOutcome(TaskNodeInput[] RemainingNodes, IReadOnlyList<ObservationDedupHit> Hits);

// The service-layer dedup-with-recurrence guard for kind `observation` writes (spec
// observation-recurrence-is-ranked): "срабатывает на каждой записи узла kind'а observation
// — и автоматической (экстрактор), и ручной (tasks_upsert)". TasksTools.UpsertAsync is the
// ONE caller today (a manual tasks_upsert create); a future extractor is meant to call this
// SAME service rather than re-deriving the guard — that routing is a neighboring card
// (observation-edges-promote-and-nail's sibling), not built here, but this is the seam it
// hangs off.
//
// Reuses AutocaptureDedup.FindDuplicateKeyAsync verbatim (its signature is already generic
// over IReadOnlyList<(string Key, string Text)> — it has never seen a memory type) rather
// than inventing a second dedup algorithm: cheap normalized-text-equality first, an optional
// semantic cosine pass second (degrades to text-only with no embedder configured) — a
// textual identity with an OPTIONAL semantic fallback, never a semantic fingerprint standing
// IN for the textual one (spec's explicit "not a semantic fingerprint instead of a textual
// one").
//
// AutocaptureDedup is `internal` to THIS assembly (PetBox.Web) — one more reason this guard
// cannot live in PetBox.Tasks (which also cannot see PetBox.Web, per the one-way layering
// the NetArchTest on ITasksService enforces): the dedup DECISION lives here, the pool it
// reads and the counter it bumps live behind ITasksService (ListObservationDedupCandidatesAsync
// / RecordObservationRecurrenceAsync).
public interface IObservationDedupService
{
	Task<ObservationDedupOutcome> PreProcessCreatesAsync(string projectKey, string board, TaskNodeInput[] nodes, CancellationToken ct = default);
}

public sealed class ObservationDedupService(ITasksService tasks, ILlmClient? llm = null) : IObservationDedupService
{
	public async Task<ObservationDedupOutcome> PreProcessCreatesAsync(string projectKey, string board, TaskNodeInput[] nodes, CancellationToken ct = default)
	{
		var candidates = await tasks.ListObservationDedupCandidatesAsync(projectKey, board, ct);
		if (candidates.Count == 0)
			return new ObservationDedupOutcome(nodes, []);

		var pool = candidates.Select(c => (c.Key, c.Text)).ToList();
		var remaining = new List<TaskNodeInput>(nodes.Length);
		var hits = new List<ObservationDedupHit>();
		foreach (var n in nodes)
		{
			var text = DedupText(n);
			var dupKey = await AutocaptureDedup.FindDuplicateKeyAsync(projectKey, text, pool, llm, ct);
			if (dupKey is null)
			{
				remaining.Add(n);
				continue;
			}
			var existing = candidates.First(c => c.Key == dupKey);
			var currentlyFixed = string.Equals(existing.Status, "fixed", StringComparison.OrdinalIgnoreCase);
			var count = await tasks.RecordObservationRecurrenceAsync(projectKey, existing.NodeId, currentlyFixed, ct);
			hits.Add(new ObservationDedupHit(n.Key ?? "", existing.Key, existing.NodeId, count));
		}
		return new ObservationDedupOutcome(remaining.ToArray(), hits);
	}

	static string DedupText(TaskNodeInput n)
	{
		var parts = new List<string>(2);
		if (!string.IsNullOrWhiteSpace(n.Title)) parts.Add(n.Title!.Trim());
		if (!string.IsNullOrWhiteSpace(n.Body)) parts.Add(n.Body!.Trim());
		return string.Join("\n\n", parts);
	}
}
