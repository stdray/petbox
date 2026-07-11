using LinqToDB;
using LinqToDB.Data;
using LinqToDB.Mapping;

namespace PetBox.Core.Search;

// The three numbers that make a dead semantic index VISIBLE, read straight off the project's
// own file (the Class-B state is co-located with the data it enriches):
//   VectorRows   — rows in search_vec. 0 for a project with entries = the index never ran.
//   DeadLettered — search_deadletter rows with Dead=1: entities permanently dropped from the index.
//   Lag          — how far the cursor trails the source's max version (supplied by the drain,
//                  which is the only place that knows the source's version space).
// Emitted by the vectorization jobs as a structured log line per project — the EXISTING
// observability pipeline (PetBox.Log.Core ingests logs; there is no in-process metric meter to
// hang a gauge on), so an operator can chart/alert on them without a new mechanism.
public readonly record struct SearchIndexStats(long VectorRows, long DeadLettered, long Lag);

public static class SearchIndexStatsReader
{
	public static async Task<(long VectorRows, long DeadLettered)> ReadAsync(DataConnection db, CancellationToken ct = default)
	{
		var vectors = await db.GetTable<VecRow>().LongCountAsync(ct);
		var dead = await db.GetTable<DeadRow>().Where(r => r.Dead).LongCountAsync(ct);
		return (vectors, dead);
	}

	[Table("search_vec")]
	sealed class VecRow
	{
		[Column] public string Id { get; set; } = string.Empty;
	}

	[Table("search_deadletter")]
	sealed class DeadRow
	{
		[Column] public string Id { get; set; } = string.Empty;
		[Column] public bool Dead { get; set; }
	}
}
