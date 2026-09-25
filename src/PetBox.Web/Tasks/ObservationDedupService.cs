using Microsoft.Extensions.Options;
using PetBox.LlmRouter.Contract;
using PetBox.Tasks.Contract;
using PetBox.Web.Mcp.Contract;
using PetBox.Web.Search;

namespace PetBox.Web.Tasks;

// Tunables for the observation-dedup guard's semantic leg (work
// observation-dedup-semantic-leg). A SEPARATE threshold from AutocaptureDedupOptions
// (memory-fact dedup, default 0.92): an observation is a free-form incident narrative
// (headers, code blocks, version strings), not an atomic extracted fact, and a genuine
// paraphrase of the SAME finding scores markedly lower on cosine than a paraphrase of a
// one-line memory fact does. Measured live 2026-09-22 (qwen3-embed-4b via the LLM router,
// project $system, `DedupText` shape — title + body) on the two most recent real observation
// twins: a genuine paraphrase pair scored 0.882 and 0.776 cosine — both BELOW the 0.92
// default, which is exactly why they missed and forked into twin nodes. Two genuinely
// different observations scored 0.50-0.54 cosine even sharing a component ("codex",
// "agent-wiring"). 0.75 sits below both measured paraphrase scores and >0.20 above the
// measured negative ceiling — that margin is the false-positive guard, not a heuristic.
// Bound from configuration section "ObservationDedup" (mirrors AutocaptureDedupOptions'
// own "AutocaptureDedup" section).
//
// Cross-language remeasurement (work observation-dedup-false-merge-and-missed-repeat, (2)):
// the ONE real RU/EN pair on record as a missed repeat (18-D8 §2a — `obs-dbd57e835dcf`, RU,
// created 2026-09-08, vs `codex-hook-trust-gate-recheck-0155-0157`, EN, created 2026-09-22,
// same "codex headless silently skips hooks" finding) was re-embedded live 2026-09-25 with
// the SAME model/shape (qwen3-embed-4b, title+body) as the calibration above: cosine 0.7758 —
// ABOVE this 0.75 threshold, not below it. So THIS pair's historical miss was not a threshold
// problem and lowering/raising 0.75 for it is unsupported by the only number available (a
// single pair; per-language-pair calibration the way `unlinked-intake-twin` calibrates its own
// separate threshold would need 3-5+ pairs to generalize, not attempted here — out of this
// card's budget). The live embed call and the two node bodies it was computed from are
// reproducible via `llm_embed` + `tasks_node_get` against $system; not captured here as an
// automated test because it needs a live network call to the router, which a unit test must
// not depend on. Whatever DID cause that specific write to skip the semantic leg (embedder
// transient unavailability at write time is one candidate — PreProcessCreatesAsync silently
// falls back to text-only when EmbedAsync returns null, see AutocaptureDedup.FindDuplicateKeyAsync)
// is a SEPARATE, unconfirmed question from the threshold value and is left for its own card.
public sealed class ObservationDedupOptions
{
	public double SemanticThreshold { get; set; } = 0.75;
}

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
	Task<ObservationDedupOutcome> PreProcessCreatesAsync(string projectKey, string board, TaskNodeInput[] nodes, string? sessionId = null, CancellationToken ct = default);
}

public sealed class ObservationDedupService(ITasksService tasks, ILlmClient? llm = null, IOptions<ObservationDedupOptions>? options = null) : IObservationDedupService
{
	readonly double _semanticThreshold = options?.Value.SemanticThreshold ?? new ObservationDedupOptions().SemanticThreshold;

	// `sessionId` (work observation-recurrence-session-provenance): the caller's own session,
	// forwarded onto a HIT (RecordObservationRecurrenceAsync unions it into the existing
	// node's originSessions) — a genuinely NEW node still gets it the normal way, through
	// tasks.UpsertAsync's own sessionId parameter at the call site, not through here.
	public async Task<ObservationDedupOutcome> PreProcessCreatesAsync(string projectKey, string board, TaskNodeInput[] nodes, string? sessionId = null, CancellationToken ct = default)
	{
		var candidates = await tasks.ListObservationDedupCandidatesAsync(projectKey, board, ct);
		if (candidates.Count == 0)
			return new ObservationDedupOutcome(nodes, []);

		var remaining = new List<TaskNodeInput>(nodes.Length);
		var hits = new List<ObservationDedupHit>();
		foreach (var n in nodes)
		{
			// observation-dedup-false-merge-and-missed-repeat: `corrects` names the ONE existing
			// observation this write is explicitly refuting/correcting — that node is excluded from
			// BOTH dedup passes (the pool it never reaches at all) so a correction can never fold
			// onto the very finding it contradicts, no matter how textually/semantically close the
			// two are. Everything ELSE in the pool still applies normally — `corrects` narrows the
			// exclusion to exactly one node, it is not an opt-out of dedup altogether.
			var correctsTarget = ResolveCorrects(n.Corrects, candidates);
			var pool = candidates
				.Where(c => correctsTarget is null || (c.Key != correctsTarget.Key && c.NodeId != correctsTarget.NodeId))
				.Select(c => (c.Key, c.Text))
				.ToList();

			var text = DedupText(n);
			var dupKey = pool.Count == 0 ? null : await AutocaptureDedup.FindDuplicateKeyAsync(projectKey, text, pool, llm, ct, _semanticThreshold);
			var outNode = WithCorrectsLink(n, correctsTarget);
			if (dupKey is null)
			{
				remaining.Add(outNode);
				continue;
			}
			var existing = candidates.First(c => c.Key == dupKey);
			var currentlyFixed = string.Equals(existing.Status, "fixed", StringComparison.OrdinalIgnoreCase);
			var count = await tasks.RecordObservationRecurrenceAsync(projectKey, existing.NodeId, currentlyFixed, sessionId, ct);
			hits.Add(new ObservationDedupHit(n.Key ?? "", existing.Key, existing.NodeId, count));
		}
		return new ObservationDedupOutcome(remaining.ToArray(), hits);
	}

	// `corrects` accepts either form a node reference takes elsewhere in the API — a slug key or
	// a 32-hex NodeId — resolved against the SAME candidate pool the dedup guard already fetched
	// (no extra read). An unresolvable reference (typo, or a target outside this board) is simply
	// not found — the guard runs unmodified in that case, same as if `corrects` were absent,
	// rather than refusing the whole write over a hint that failed to resolve.
	static ObservationDedupCandidate? ResolveCorrects(string? corrects, IReadOnlyList<ObservationDedupCandidate> candidates)
	{
		if (string.IsNullOrWhiteSpace(corrects)) return null;
		var r = corrects.Trim();
		return candidates.FirstOrDefault(c => string.Equals(c.Key, r, StringComparison.OrdinalIgnoreCase))
			?? candidates.FirstOrDefault(c => string.Equals(c.NodeId, r, StringComparison.OrdinalIgnoreCase));
	}

	// Installs the real edge: `corrects` is sugar over the builtin neutral `relates_to` kind (spec
	// observation-recurrence-is-ranked names no dedicated "corrects" link kind, and one is not
	// declared here — see TaskNodeInput.Corrects), addressed by the resolved candidate's stable
	// NodeId so the edge survives a rename. Any relates_to refs the caller already sent are kept;
	// `corrects` only ADDS to that list, never replaces it. A `corrects` that failed to resolve
	// contributes nothing (see ResolveCorrects) — never a raw, unvalidated ref forwarded downstream.
	static TaskNodeInput WithCorrectsLink(TaskNodeInput n, ObservationDedupCandidate? correctsTarget)
	{
		if (correctsTarget is null) return n;
		var links = n.Links is null
			? new Dictionary<string, LinkRefs>(StringComparer.OrdinalIgnoreCase)
			: new Dictionary<string, LinkRefs>(n.Links, StringComparer.OrdinalIgnoreCase);
		var existingRefs = links.TryGetValue("relates_to", out var cur) ? cur.Values : [];
		if (!existingRefs.Contains(correctsTarget.NodeId, StringComparer.OrdinalIgnoreCase))
			links["relates_to"] = new LinkRefs([.. existingRefs, correctsTarget.NodeId]);
		return n with { Links = links };
	}

	static string DedupText(TaskNodeInput n)
	{
		var parts = new List<string>(2);
		if (!string.IsNullOrWhiteSpace(n.Title)) parts.Add(n.Title!.Trim());
		if (!string.IsNullOrWhiteSpace(n.Body)) parts.Add(n.Body!.Trim());
		return string.Join("\n\n", parts);
	}
}
