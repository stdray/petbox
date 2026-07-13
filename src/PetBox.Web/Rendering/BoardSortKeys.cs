namespace PetBox.Web.Rendering;

// board-filters-server-state: the sort-key vocabulary board.ts's client-side comparator
// (sortKeyValue/compareSortValues) and TaskBoardModel's server-side one both switch over — kept as
// one named list so the two can't silently drift apart, and so BoardFilterPrefsEndpoint has
// somewhere to validate an incoming `sortBy` against without hardcoding the array a third time.
public static class BoardSortKeys
{
	public const string Priority = "priority";
	public const string Created = "created";
	public const string Updated = "updated";
	public const string Title = "title";

	public static readonly IReadOnlyList<string> All = [Priority, Created, Updated, Title];

	public static bool IsKnown(string? key) => key is not null && All.Contains(key, StringComparer.OrdinalIgnoreCase);
}
