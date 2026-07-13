using PetBox.Tasks.Workflow;

namespace PetBox.Web.Rendering;

// Which node properties a board's cards/rows/columns show (spec board-view-fields): the ONE
// config every view partial (Tree/Outline/Kanban's card, Table's row) reads to decide what to
// render, so the SAME 8-field vocabulary (BoardFieldNames) works identically in every view mode —
// the mode only picks the DEFAULT (below), never a ceiling on what's togglable. Threaded through
// TaskBoardModel.PlanNodeCard and read directly off TaskBoardModel by the partials that don't go
// through PlanNodeCard (Kanban/Outline/Table).
public sealed record BoardFieldConfig(
	bool Type, bool Status, bool Priority, bool Tags, bool UpdatedAt,
	bool Delivery, bool BlockedBy, bool Body)
{
	public static readonly BoardFieldConfig None = new(false, false, false, false, false, false, false, false);

	public bool Has(string key) => key switch
	{
		BoardFieldNames.Type => Type,
		BoardFieldNames.Status => Status,
		BoardFieldNames.Priority => Priority,
		BoardFieldNames.Tags => Tags,
		BoardFieldNames.UpdatedAt => UpdatedAt,
		BoardFieldNames.Delivery => Delivery,
		BoardFieldNames.BlockedBy => BlockedBy,
		BoardFieldNames.Body => Body,
		_ => false,
	};

	// The enabled keys in BoardFieldNames.Options order — the canonical order ToCsv()/persistence
	// round-trips through, so a saved preference and a freshly resolved config serialize
	// identically when they carry the same set (board.ts's reconcile compares these as sets, not
	// strings, but a stable order keeps the wire value readable/diffable regardless).
	public IEnumerable<string> Keys() => BoardFieldNames.Options.Select(o => o.Key).Where(Has);

	public string ToCsv() => string.Join(",", Keys());

	// Parse a query-string `fields=a&fields=b` bind (or any key list) into a config — an unknown
	// key is silently dropped (never a 500; keeps a future BoardFieldNames addition from breaking
	// an old saved/linked value the same way BoardFieldConfig's sibling BoardViewModeNames already
	// tolerates unknown view names).
	public static BoardFieldConfig FromKeys(IEnumerable<string>? keys)
	{
		var set = (keys ?? []).Where(BoardFieldNames.IsKnown).ToHashSet(StringComparer.OrdinalIgnoreCase);
		return new BoardFieldConfig(
			set.Contains(BoardFieldNames.Type), set.Contains(BoardFieldNames.Status),
			set.Contains(BoardFieldNames.Priority), set.Contains(BoardFieldNames.Tags),
			set.Contains(BoardFieldNames.UpdatedAt), set.Contains(BoardFieldNames.Delivery),
			set.Contains(BoardFieldNames.BlockedBy), set.Contains(BoardFieldNames.Body));
	}

	// The DEFAULT preset for a (view mode, kind) pair — PURE CODE, not methodology-definition data
	// (see BoardFieldNames' header comment for why that matters): the mode's traditional look,
	// minus two adjustments the spec calls out explicitly —
	//   - Delivery drops out unless the RESOLVED kind actually computes it (Runtime.DeliveryOf
	//     non-null: work/intake nodes always carry Delivery:null, so the column/badge is dead
	//     weight there today — table currently shows it unconditionally, which is exactly the
	//     "dead column" board-view-display-config-impl calls out).
	//   - Status defaults OFF in tree/outline ("it cuts the eye" — bullet 3): the terminal-cancel
	//     strikethrough (see _PlanNodeCard/_BoardView* title rendering) already carries the one bit
	//     that actually matters (is this node dead?) independent of this toggle, so the badge
	//     becomes optional detail rather than default noise. Kanban already communicates status via
	//     its column, so it defaults off there too; table's whole point is the flat column list, so
	//     it stays on.
	// outlineBodyDefault is Outline's own inline-lazy-reveal opt-in (Runtime.OutlineReveal) —
	// Body defaults on there ONLY when the kind already reveals bodies inline (spec today),
	// preserving exactly what outline showed before this config existed.
	public static BoardFieldConfig Default(string viewMode, MethodologyRuntime runtime, string? kindSlug, bool outlineBodyDefault)
	{
		var delivery = runtime.DeliveryOf(kindSlug) is not null;
		return viewMode switch
		{
			BoardViewModeNames.Kanban => new BoardFieldConfig(
				Type: true, Status: false, Priority: true, Tags: true, UpdatedAt: false,
				Delivery: delivery, BlockedBy: false, Body: false),
			BoardViewModeNames.Table => new BoardFieldConfig(
				Type: true, Status: true, Priority: true, Tags: true, UpdatedAt: true,
				Delivery: delivery, BlockedBy: false, Body: false),
			BoardViewModeNames.Outline => new BoardFieldConfig(
				Type: false, Status: false, Priority: false, Tags: false, UpdatedAt: false,
				Delivery: delivery, BlockedBy: false, Body: outlineBodyDefault),
			// Tree and Tags (tag-groups is a projection over the tree, same card) — close to the
			// pre-config tree-card look (status badge, delivery badge, body) plus the two fields
			// most useful in a spacious card: Tags (the grouping context) and BlockedBy (the one
			// dependency signal that changes what "do this next" means — worth defaulting on where
			// there's room for it). Type/Priority/UpdatedAt default off — they read fine as sort/
			// filter criteria without being restated on every card.
			_ => new BoardFieldConfig(
				Type: false, Status: false, Priority: false, Tags: true, UpdatedAt: false,
				Delivery: delivery, BlockedBy: true, Body: true),
		};
	}
}
