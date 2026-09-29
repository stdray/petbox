using PetBox.Web.Mcp;

namespace PetBox.Tests.Mcp;

// Card delta-verbs-response-cap: the cut shared by tasks_delta / comments_delta / memory_delta.
// Pure over (added, updated, current, version selector, limit, budget) — the tool-level walks live in
// ListBudgetTests. A store is simulated: `Fetch(since)` returns the rows with Version > since and the
// store's max version, exactly what the services hand the adapter.
public sealed class McpDeltaPageTests
{
	sealed record Row(string Id, long Version, string Pad = "");

	sealed class FakeStore(IReadOnlyList<Row> rows)
	{
		public long Max => rows.Max(r => r.Version);

		public McpDeltaPage.Slice<Row> Fetch(long since, int? limit, int budget)
		{
			var changed = rows.Where(r => r.Version > since).ToList();
			// alternate rows between added/updated so both lists are exercised
			var added = changed.Where((_, i) => i % 2 == 0).ToList();
			var updated = changed.Where((_, i) => i % 2 == 1).ToList();
			return McpDeltaPage.Cut(added, updated, Max, r => r.Version, limit, budget);
		}
	}

	static List<Row> Rows(params (int Count, long Version)[] groups) =>
		groups.SelectMany(g => Enumerable.Range(0, g.Count).Select(i => new Row($"v{g.Version}-{i}", g.Version, new string('p', 200)))).ToList();

	// A row costs ~250 wire chars (200 of padding); the tight budget below fits about two rows.
	const int Tight = 700;

	[Fact]
	public void CompletePage_HasNoMarkers_AndTheStoreWatermark()
	{
		var store = new FakeStore(Rows((2, 1), (2, 2)));

		var page = store.Fetch(0, limit: null, budget: 30_000);

		page.Omitted.Should().Be(0);
		page.CurrentVersion.Should().Be(store.Max);
		(page.Added.Count + page.Updated.Count).Should().Be(4);
	}

	[Fact]
	public void WalkByTheReturnedWatermark_DeliversEveryRowOnce_AndEachPageStaysInBudgetOrIsOneGroup()
	{
		var rows = Rows((1, 1), (3, 2), (1, 3), (2, 4), (5, 5), (1, 6), (2, 7));
		var store = new FakeStore(rows);
		var seen = new List<string>();
		long since = 0;
		var pages = 0;
		McpDeltaPage.Slice<Row> page;
		do
		{
			page = store.Fetch(since, limit: null, Tight);
			var delivered = page.Added.Concat(page.Updated).ToList();
			seen.AddRange(delivered.Select(r => r.Id));
			// version-aligned: a group is never split across pages
			delivered.Select(r => r.Version).Distinct().ToList()
				.ForEach(v => delivered.Count(r => r.Version == v).Should().Be(rows.Count(r => r.Version == v)));
			since = page.CurrentVersion;
			pages++;
		} while (page.Omitted > 0 && pages < 50);

		pages.Should().BeGreaterThan(1);
		seen.Should().BeEquivalentTo(rows.Select(r => r.Id)).And.OnlyHaveUniqueItems();
		since.Should().Be(store.Max);
	}

	[Fact]
	public void ClientThatIgnoresTruncated_AndLoopsUntilAnEmptyDelta_StillReachesTheEnd()
	{
		var rows = Rows((2, 1), (2, 2), (2, 3), (2, 4), (2, 5));
		var store = new FakeStore(rows);
		var seen = new List<string>();
		long since = 0;
		for (var guard = 0; guard < 50; guard++)
		{
			var page = store.Fetch(since, limit: null, Tight);
			var delivered = page.Added.Concat(page.Updated).ToList();
			if (delivered.Count == 0) break;
			seen.AddRange(delivered.Select(r => r.Id));
			since = page.CurrentVersion;
		}

		seen.Should().BeEquivalentTo(rows.Select(r => r.Id)).And.OnlyHaveUniqueItems();
	}

	[Fact]
	public void OneGiantVersionGroup_LargerThanTheBudget_IsReturnedWhole()
	{
		var rows = Rows((30, 1), (1, 2));
		var store = new FakeStore(rows);

		var page = store.Fetch(0, limit: null, Tight);

		(page.Added.Count + page.Updated.Count).Should().Be(30, "a batch is never split, however far it overshoots the budget");
		page.CurrentVersion.Should().Be(1);
		page.Omitted.Should().Be(1);
	}

	[Fact]
	public void Limit_CutsByRows_ExtendedToTheEndOfTheGroup()
	{
		var rows = Rows((2, 1), (3, 2), (2, 3));
		var store = new FakeStore(rows);

		var page = store.Fetch(0, limit: 3, budget: 30_000);

		// limit 3 lands inside group v2 (rows 3..5) -> the group is taken whole: 2 + 3 rows
		(page.Added.Count + page.Updated.Count).Should().Be(5);
		page.CurrentVersion.Should().Be(2);
		page.Omitted.Should().Be(2);
	}

	[Fact]
	public void LimitCoveringEverything_IsNotACut()
	{
		var store = new FakeStore(Rows((2, 1), (2, 2)));

		var page = store.Fetch(0, limit: 10, budget: 30_000);

		page.Omitted.Should().Be(0);
		page.CurrentVersion.Should().Be(store.Max);
	}

	[Fact]
	public void CutThatReachesTheLastGroup_IsComplete_NotTruncated()
	{
		// the limit lands in the LAST group, which is then extended to the end: nothing is left behind
		var store = new FakeStore(Rows((2, 1), (3, 2)));

		var page = store.Fetch(0, limit: 3, budget: 30_000);

		page.Omitted.Should().Be(0);
		(page.Added.Count + page.Updated.Count).Should().Be(5);
		page.CurrentVersion.Should().Be(store.Max);
	}

	[Fact]
	public void EmptyDelta_ReturnsTheCurrentVersion()
	{
		var page = McpDeltaPage.Cut<Row>([], [], 42, r => r.Version, limit: 5);

		page.Omitted.Should().Be(0);
		page.CurrentVersion.Should().Be(42);
	}
}
