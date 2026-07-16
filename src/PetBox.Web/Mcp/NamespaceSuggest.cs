namespace PetBox.Web.Mcp;

// Shared "did you mean 'X'?" for the namespace-creation gates (memory stores, tasks boards) —
// spec agent-namespace-provisioning. When a write verb names a namespace that does not exist,
// the gate rejects it and offers the nearest existing/reserved name so an agent self-corrects
// on the next call instead of retrying the same misroute.
//
// EDIT-DISTANCE ONLY — deliberately NO prefix leg. A namespace like `notes-archive` is a
// DELIBERATE derivation, not a typo of `notes`; a prefix relation would wrongly flag it as a
// near-miss and nudge the agent to collapse a real, distinct store back into `notes`. The
// project-key sibling (McpProjectExistsFilter) keeps its own prefix+distance ranking — project
// keys ARE hierarchical (`kpvotes-bot` is a relative of `kpvotes`); namespaces are not — but the
// Levenshtein core is shared here so there is one implementation.
static class NamespaceSuggest
{
	// The nearest candidates to `name` by edit distance, closest first, ties broken ordinally.
	// The budget scales with the name length (a longer name tolerates more typo drift) but is
	// capped small so a suggestion that fires on everything cannot become noise.
	public static IReadOnlyList<string> Nearest(string name, IEnumerable<string> candidates, int take = 3)
	{
		var budget = Math.Max(1, Math.Min(3, name.Length / 3));
		return candidates
			.Where(c => !string.IsNullOrWhiteSpace(c))
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.Select(c => (Key: c, Score: Distance(name, c, budget)))
			.Where(x => x.Score <= budget)
			.OrderBy(x => x.Score)
			.ThenBy(x => x.Key, StringComparer.Ordinal)
			.Take(take)
			.Select(x => x.Key)
			.ToList();
	}

	// Levenshtein, abandoned once every cell of a row exceeds the budget (candidates nowhere near
	// the name cost one short row each). Returns int.MaxValue when clearly beyond budget; a
	// returned distance may still exceed the budget, so callers filter on `<= budget`.
	public static int Distance(string a, string b, int budget)
	{
		if (Math.Abs(a.Length - b.Length) > budget) return int.MaxValue;
		var previous = new int[b.Length + 1];
		var current = new int[b.Length + 1];
		for (var j = 0; j <= b.Length; j++) previous[j] = j;

		for (var i = 1; i <= a.Length; i++)
		{
			current[0] = i;
			var best = current[0];
			for (var j = 1; j <= b.Length; j++)
			{
				var cost = char.ToLowerInvariant(a[i - 1]) == char.ToLowerInvariant(b[j - 1]) ? 0 : 1;
				current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
				best = Math.Min(best, current[j]);
			}
			if (best > budget) return int.MaxValue;
			(previous, current) = (current, previous);
		}
		return previous[b.Length];
	}
}
