using System.Text.Json;
using PetBox.Tasks.Contract;
using PetBox.Tasks.Workflow;

namespace PetBox.Web.Pages.ProjectHome;

// Serializes a board's workflow surface (BoardWorkflowView) to the compact JSON island the
// "View workflow" modal reads (ts/workflow-viz.ts). The shape is the render function's PURE
// contract — {kind, blocks:[{types, statuses:[{slug,name,kind}], transitions:[…gates]}]} — so the
// TS module stays Razor-agnostic. StatusKind serializes as its enum name (Open/TerminalOk/
// TerminalCancel), matched verbatim in the module. The default System.Text.Json encoder escapes
// '<' '>' '&', so the payload is safe to drop into a <script type="application/json"> element even
// when a user-defined methodology contributes the slugs/names.
public static class WorkflowGraphJson
{
	static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

	public static string Serialize(BoardWorkflowView view) =>
		JsonSerializer.Serialize(ToDoc(view, effectNotes: []), Options);

	// The methodology editor previews EVERY kind of a definition at once: an ARRAY of the same
	// doc shape, one entry per kind — the render contract per block is unchanged. Each kind may
	// carry `effectNotes`: pre-phrased sentences for its kind-level transition effects
	// (MethodologyGuide.EffectSentence), rendered as an annotation list under the kind's graphs
	// — cross-board actions have no edge to live on, so they surface as prose, not geometry.
	public static string SerializeMany(IEnumerable<(BoardWorkflowView View, IReadOnlyList<string> EffectNotes)> views) =>
		JsonSerializer.Serialize(views.Select(v => ToDoc(v.View, v.EffectNotes)).ToList(), Options);

	public static string SerializeMany(IEnumerable<BoardWorkflowView> views) =>
		SerializeMany(views.Select(v => (v, (IReadOnlyList<string>)[])));

	static GraphDoc ToDoc(BoardWorkflowView view, IReadOnlyList<string> effectNotes) =>
		new(
			view.Kind,
			[.. view.Workflows.Select(b => new GraphBlock(
				b.Types,
				[.. b.Workflow.Statuses.Select(s => new GraphStatus(s.Slug, s.Name, s.Kind.ToString()))],
				[.. b.Workflow.Transitions.Select(t => new GraphTransition(
					t.From, t.To, t.RequiresApproval, t.RequiresReason, t.PreconditionArtifact,
					Checklist: t.Checklist.Count > 0))]))],
			effectNotes);

	sealed record GraphDoc(string Kind, IReadOnlyList<GraphBlock> Blocks, IReadOnlyList<string> EffectNotes);

	sealed record GraphBlock(
		IReadOnlyList<string> Types,
		IReadOnlyList<GraphStatus> Statuses,
		IReadOnlyList<GraphTransition> Transitions);

	sealed record GraphStatus(string Slug, string Name, string Kind);

	sealed record GraphTransition(
		string From,
		string To,
		bool RequiresApproval,
		bool RequiresReason,
		string? PreconditionArtifact,
		bool Checklist);
}
