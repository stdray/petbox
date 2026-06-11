using System.Diagnostics;

namespace PetBox.Web.Search;

// Pacing for the chat-distillation jobs (work: enrich-drain-budget). The enrichment pass
// is SHARED — vectorization, digest, facts and mining run sequentially in one
// BackgroundService loop — so a job drains its backlog until EMPTY or until its time
// budget is spent, never longer: a freshly imported archive warms up fast without
// starving vector freshness for hours. Two fairness devices:
//   round-robin — each pass starts the project sweep one position further, so a fat
//     project can't starve the others for more than one pass;
//   project sub-cap — inside a pass one project may spend at most half the budget.
public static class DrainPacing
{
	public static readonly TimeSpan DefaultBudget = TimeSpan.FromMinutes(3);

	// Rotate `items` to start at position (counter % count). The caller owns the counter
	// (a static per job type); passes run strictly sequentially, so a plain increment is
	// safe.
	public static IReadOnlyList<string> Rotate(IReadOnlyList<string> items, ref int counter)
	{
		var start = items.Count <= 1 ? 0 : ((counter % items.Count) + items.Count) % items.Count;
		counter++;
		if (start == 0) return items;
		var rotated = new List<string>(items.Count);
		for (var i = 0; i < items.Count; i++)
			rotated.Add(items[(start + i) % items.Count]);
		return rotated;
	}
}

// Budget clock with a PROGRESS GUARANTEE: it never reports exhausted before at least one
// unit of work happened this pass — a zero/tiny budget still advances the backlog by one
// batch instead of stalling forever.
public sealed class DrainClock
{
	readonly Stopwatch _sw = Stopwatch.StartNew();
	readonly TimeSpan _budget;
	readonly TimeSpan _projectCap;
	TimeSpan _projectStart;
	int _units;

	public DrainClock(TimeSpan budget)
	{
		_budget = budget;
		_projectCap = TimeSpan.FromTicks(Math.Max(1, budget.Ticks / 2));
	}

	public void StartProject() => _projectStart = _sw.Elapsed;

	public void Unit() => _units++;

	public bool Exhausted => _units > 0 && _sw.Elapsed >= _budget;

	public bool ProjectExhausted => Exhausted || (_units > 0 && _sw.Elapsed - _projectStart >= _projectCap);
}
