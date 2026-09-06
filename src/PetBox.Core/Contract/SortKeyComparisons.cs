using System.Globalization;

namespace PetBox.Core.Contract;

// Comparisons for canonical cursor sort-key strings — the `Comparison<string>` delegates
// KeysetCursor.Advance takes. The caller supplies the delegate because only the caller knows
// whether "12" is a number, a title or a timestamp; what is shared here is the IDIOM, not the
// choice: every layer that pages numerically (session_search's Length, tasks_search's and the
// ProjectHome tasks page's Priority) used to re-derive the same invariant-culture long parse.
// Method-group compatible with Comparison<string>, so a switch arm can name it directly.
public static class SortKeyComparisons
{
	// Numeric comparison of two sort keys that are invariant-culture longs.
	public static int CompareLong(string a, string b) =>
		long.Parse(a, CultureInfo.InvariantCulture).CompareTo(long.Parse(b, CultureInfo.InvariantCulture));
}
