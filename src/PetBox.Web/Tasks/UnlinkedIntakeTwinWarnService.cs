using Microsoft.Extensions.Options;
using PetBox.LlmRouter.Contract;
using PetBox.Tasks.Contract;
using PetBox.Tasks.Data;
using PetBox.Tasks.Workflow;
using PetBox.Web.Search;

namespace PetBox.Web.Tasks;

// Tunable for the unlinked-intake-twin rule's semantic leg (idea discipline-rules-warn-
// in-tool-response, spec unlinked-intake-twin-warns). A SEPARATE knob from both
// ObservationDedupOptions (0.75, incident narratives) and AutocaptureDedupOptions (0.92, atomic
// memory facts) — an intake request and its work-card twin are typically a SHORT title copy
// (the observed live pair: intake/add-chore-type-to-classic-preset vs
// work/chore-type-in-classic-preset), closer in wording than a paraphrased incident but still not
// the near-verbatim repeat memory-fact dedup guards against. Bound from configuration section
// "UnlinkedIntakeTwinWarn". Calibration numbers (quartet pair + 3-5 unrelated pairs) belong in
// this card's verdict comment, not in code.
public sealed class UnlinkedIntakeTwinWarnOptions
{
	public double SemanticThreshold { get; set; } = 0.72;
}

public interface IUnlinkedIntakeTwinWarnService
{
	// Branch (a) of the rule: a node just CREATED on a board whose kind is the declared TARGET
	// of a process link (the quartet's `issue_task`: intake -> work) resembles an OPEN node on
	// the declared SOURCE board that carries no outgoing edge of that kind yet. `created` are
	// this call's own newly-born nodes on `board` (already fully written — edges included, so a
	// node this SAME call also linked is correctly excluded downstream). Never throws — a
	// judging failure (no embedder, a transient read error) degrades to "no warning", exactly
	// like ObservationDedupService's semantic leg.
	// `created` are FULL TaskNode rows (never the bodyLen-truncated wire echo — the semantic
	// comparison needs the real title+body, not whatever the caller's bodyLen knob left of it).
	Task<IReadOnlyList<UpsertWarningView>> WarnOnCreateAsync(
		string projectKey, string board, IReadOnlyList<TaskNode> created, CancellationToken ct = default);
}

// Reuses AutocaptureDedup.FindDuplicateKeyAsync verbatim (see ObservationDedupService's header
// comment for why this class of check lives in PetBox.Web rather than PetBox.Tasks: the DECISION
// needs a concrete ILlmClient/embedder, which PetBox.Tasks cannot see). The link kind and the
// source/target board PAIR are read from MethodologyRuntime.EffectiveLinkKinds() — nothing here
// is a literal "issue_task"/"intake"/"work" special case; a project's own declared process link
// with a Direction plays the identical role.
public sealed class UnlinkedIntakeTwinWarnService(
	ITasksService tasks, IRelationStore relations, ILlmClient? llm = null,
	IOptions<UnlinkedIntakeTwinWarnOptions>? options = null,
	ILogger<UnlinkedIntakeTwinWarnService>? log = null) : IUnlinkedIntakeTwinWarnService
{
	const string Rule = "unlinked-intake-twin";
	readonly double _threshold = options?.Value.SemanticThreshold ?? new UnlinkedIntakeTwinWarnOptions().SemanticThreshold;

	public async Task<IReadOnlyList<UpsertWarningView>> WarnOnCreateAsync(
		string projectKey, string board, IReadOnlyList<TaskNode> created, CancellationToken ct = default)
	{
		if (created.Count == 0) return [];
		try
		{
			var runtime = await tasks.GetRuntimeForBoardAsync(projectKey, board, ct);
			var boards = await tasks.ListBoardsAsync(projectKey, ct);
			var thisMeta = boards.FirstOrDefault(b => string.Equals(b.Name, board, StringComparison.OrdinalIgnoreCase));
			if (thisMeta is null) return [];
			var thisKind = runtime.KindName(thisMeta.Kind);

			// Same exclusion as TasksService.ComputeUnlinkedIntakeCloseWarningsAsync (live false
			// positive 2026-09-25): a link the SOURCE kind's own LinkConstraints already requires
			// at creation (e.g. work's task_spec, required for feature/bug) is a DIFFERENT
			// obligation, not the "populated later, at promotion" edge this rule means — excluding
			// it keeps ToKind matching unambiguous even if a future methodology declares two
			// process links into the same target kind.
			var link = runtime.EffectiveLinkKinds().FirstOrDefault(l =>
				l.Category == LinkCategory.Process
				&& l.Direction?.ToKind is not null
				&& string.Equals(l.Direction.ToKind, thisKind, StringComparison.OrdinalIgnoreCase)
				&& !runtime.LinkConstraints(l.Direction.FromKind).Any(c => string.Equals(c.Link, l.Slug, StringComparison.OrdinalIgnoreCase)));
			if (link?.Direction?.FromKind is null) return [];

			var sourceBoards = boards.Where(b =>
				string.Equals(runtime.KindName(b.Kind), link.Direction.FromKind, StringComparison.OrdinalIgnoreCase)).ToList();
			if (sourceBoards.Count == 0) return [];

			// Every node with an outgoing edge of this link kind, project-wide — ONE query, same
			// "batch the relation read" discipline as TasksService.ComputeUnlinkedIntakeCloseWarningsAsync
			// and the pre-existing part_of/delivery sweeps (_relations.ListByKindAsync).
			var alreadyLinked = (await relations.ListByKindAsync(projectKey, link.Slug, ct))
				.Select(rel => rel.FromNodeId).ToHashSet(StringComparer.Ordinal);

			var warnings = new List<UpsertWarningView>();
			foreach (var sourceBoard in sourceBoards)
			{
				ct.ThrowIfCancellationRequested();
				var open = await tasks.ListActiveNodesAsync(projectKey, sourceBoard.Name, ct);
				var sourceRuntime = string.Equals(sourceBoard.Name, board, StringComparison.OrdinalIgnoreCase)
					? runtime : await tasks.GetRuntimeForBoardAsync(projectKey, sourceBoard.Name, ct);
				var candidates = open.Where(n =>
				{
					if (alreadyLinked.Contains(n.NodeId)) return false;
					var wf = sourceRuntime.For(sourceBoard.Kind, n.Type.Length == 0 ? null : n.Type);
					return wf?.Status(n.Status)?.Kind == StatusKind.Open;
				}).ToList();
				if (candidates.Count == 0) continue;

				var pool = candidates.Select(c => (c.Key, Text: DedupText(c))).ToList();
				foreach (var n in created)
				{
					var text = DedupText(n);
					if (text.Length == 0) continue;
					var matchKey = await AutocaptureDedup.FindDuplicateKeyAsync(projectKey, text, pool, llm, ct, _threshold);
					if (matchKey is null) continue;
					warnings.Add(new UpsertWarningView(Rule, n.Key,
						$"resembles open '{sourceBoard.Name}/{matchKey}', which carries no outgoing '{link.Slug}' edge yet — consider linking it."));
				}
			}
			return warnings;
		}
		catch (OperationCanceledException) { throw; }
		catch (Exception ex)
		{
			// Judging never turns applied:true into a refusal — swallow, log, no warning (same
			// discipline as AutocaptureDedup's own embed-down degrade).
			log?.LogWarning(ex, "unlinked-intake-twin create check failed: project={Project} board={Board}", projectKey, board);
			return [];
		}
	}

	static string DedupText(TaskNode n)
	{
		var parts = new List<string>(2);
		if (!string.IsNullOrWhiteSpace(n.Name)) parts.Add(n.Name.Trim());
		if (!string.IsNullOrWhiteSpace(n.Body)) parts.Add(n.Body.Trim());
		return string.Join("\n\n", parts);
	}
}
