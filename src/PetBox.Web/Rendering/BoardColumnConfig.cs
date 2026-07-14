namespace PetBox.Web.Rendering;

// board-kanban-columns (spec board-view-fields' kanban-column-picker sibling): which of a
// kanban board's OWN workflow-status columns are visible — the twin of BoardFieldConfig, but
// the vocabulary here is DYNAMIC (TaskBoardModel.KanbanColumns, sourced from WorkflowBlocks —
// a different methodology has different statuses), never a fixed set of named properties like
// BoardFieldNames. So this config is just a set of selected status slugs, and every method that
// needs a canonical/stable order takes the board's own known-slugs list (KanbanColumns order)
// rather than reading a static Options table the way BoardFieldConfig.Keys()/ToCsv() do.
public sealed record BoardColumnConfig(IReadOnlySet<string> Visible)
{
	public static readonly BoardColumnConfig None = new(new HashSet<string>(StringComparer.OrdinalIgnoreCase));

	public bool Has(string slug) => Visible.Contains(slug);

	// The visible slugs in `knownSlugsInOrder`'s order (the board's own KanbanColumns order) —
	// so ToCsv() below round-trips through FromKeys() to an equal set/order, the same "stable
	// canonical order" discipline BoardFieldConfig.Keys() documents.
	public IEnumerable<string> Keys(IReadOnlyList<string> knownSlugsInOrder) => knownSlugsInOrder.Where(Has);

	public string ToCsv(IReadOnlyList<string> knownSlugsInOrder) => string.Join(",", Keys(knownSlugsInOrder));

	// Parse a query-string `columns=a&columns=b` bind (or a saved CSV split) into a config — a
	// slug this board's CURRENT workflow doesn't recognize (a stale saved value after a
	// methodology change removed/renamed a status) is silently dropped, never a 500 — same
	// tolerance BoardFieldConfig.FromKeys gives an unknown field key.
	public static BoardColumnConfig FromKeys(IEnumerable<string>? keys, IReadOnlyList<string> knownSlugsInOrder)
	{
		var known = knownSlugsInOrder.ToHashSet(StringComparer.OrdinalIgnoreCase);
		var set = (keys ?? []).Where(known.Contains).ToHashSet(StringComparer.OrdinalIgnoreCase);
		return new BoardColumnConfig(set);
	}

	// The default: every column visible (kanban-column-picker bullet 4 — current behavior is
	// unchanged until the user picks otherwise).
	public static BoardColumnConfig AllVisible(IReadOnlyList<string> knownSlugsInOrder) =>
		new(knownSlugsInOrder.ToHashSet(StringComparer.OrdinalIgnoreCase));
}
