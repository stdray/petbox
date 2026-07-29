using LinqToDB;
using LinqToDB.Async;
using Microsoft.Extensions.DependencyInjection;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Memory.Contract;

namespace PetBox.E2ETests;

// ui-search-pagination-parity: the contracts learned to page a RELEVANCE result and to name why the
// walk stopped (spec result-set-pageable), and this is the proof that a HUMAN gets it — not just an
// agent reading `nextCursor` off the wire.
//
// The defect these guard against is not "the button is missing". It is that a search page which
// shows the first screen and nothing else looks IDENTICAL to a search that genuinely found that
// much — the same lie in the UI that `stop` was introduced to kill on the wire (spec
// search-one-engine-for-human-and-agent: the surfaces may differ in presentation, never in the
// completeness of the search or the set of orders and filters they expose).
//
// Seeds PageSize+5 matching entries so page 1 is FULL and a second page provably exists; every
// entry carries the same rare marker word so relevance selects all of them and nothing else.
[Collection(nameof(UiCollection))]
public sealed class MemorySearchPagingUiTests(WebAppFixture app, ITestOutputHelper output) : IAsyncLifetime
{
	const string Ws = "mem-paging-ws";
	const string Proj = "mem-paging-proj";
	const string Store = "pagingnotes";
	// MemoryStoreModel.PageSize — page 1 must come back FULL for a second page to exist at all.
	const int PageSize = 40;
	const int Seeded = PageSize + 5;
	// A word that appears in NOTHING else in this store, so the match set is exactly the seeded rows.
	const string Marker = "гидропоника";

	IBrowserContext? _ctx;
	IPage? _page;

	public async ValueTask InitializeAsync()
	{
		using var scope = app.Services.CreateScope();
		using var db = scope.ServiceProvider.GetRequiredService<ICoreDbFactory>().Open();

		if (!await db.Workspaces.AnyAsync(w => w.Key == Ws))
			await db.InsertAsync(new Workspace { Key = Ws, Name = Ws, CreatedAt = DateTime.UtcNow });
		if (!await db.Projects.AnyAsync(p => p.Key == Proj))
			await db.InsertAsync(new Project { Key = Proj, WorkspaceKey = Ws, Name = "Memory Paging" });

		var memory = scope.ServiceProvider.GetRequiredService<IMemoryService>();
		if (!await memory.StoreExistsAsync(Proj, Store))
			await memory.CreateStoreAsync(Proj, Store, "ui-search-pagination-parity fixture");

		var page = await memory.ListActiveEntriesAsync(Proj, Store);
		if (page.Count < Seeded)
		{
			var inputs = Enumerable.Range(0, Seeded).Select(i => new MemoryEntryInput
			{
				Key = $"note-{i:D3}",
				Type = "Project",
				Description = $"{Marker} запись номер {i}",
				Body = $"Заметка про {Marker}: строка {i} корпуса для проверки постраничного прохода.",
			}).ToList();
			await memory.UpsertAsync(Proj, Store, inputs, []);
		}

		_ctx = await app.NewContextAsync(authenticated: true);
		_page = await _ctx.NewPageAsync();
	}

	public async ValueTask DisposeAsync()
	{
		if (_ctx is not null)
		{
			await TraceArtifact.StopAndSaveAsync(_ctx, output);
			await _ctx.CloseAsync();
		}
	}

	[Fact]
	public async Task SearchResults_PageForward_WithoutRepeatingOrDroppingRows()
	{
		await _page!.GotoAsync($"/ui/{Ws}/{Proj}/memory/{Store}?q={Uri.EscapeDataString(Marker)}");

		var first = await KeysOnScreenAsync();
		first.Should().HaveCount(PageSize, "page 1 of a match set larger than a page must come back full");

		// The whole point of the feature: there IS a way onward, and it is offered to the human.
		var next = _page.GetByTestId("store-search-next");
		await Expect(next).ToBeVisibleAsync();
		await next.ClickAsync();

		var second = await KeysOnScreenAsync();
		second.Should().NotBeEmpty("the cursor must actually advance rather than hand back an empty page");
		second.Should().NotIntersectWith(first, "a keyset walk repeats no row it already delivered");
		first.Concat(second).Should().OnlyHaveUniqueItems();
		// Nothing may vanish between the pages either — the two together are the whole match set.
		first.Concat(second).Should().HaveCount(Seeded);
	}

	[Fact]
	public async Task TheEndOfTheWalk_SaysWhyItStopped_RatherThanJustRunningOut()
	{
		await _page!.GotoAsync($"/ui/{Ws}/{Proj}/memory/{Store}?q={Uri.EscapeDataString(Marker)}");
		await _page.GetByTestId("store-search-next").ClickAsync();

		// A page that simply stops offering "Next" is indistinguishable from one that was cut short.
		// The info line has to NAME the reason — this is the UI half of `stop` (card requirement 2).
		await Expect(_page.GetByTestId("store-search-next-disabled")).ToBeVisibleAsync();
		await Expect(_page.GetByTestId("store-search-info")).ToContainTextAsync("end of the match set");
	}

	[Fact]
	public async Task SearchResults_PageForward_RangeAdvances_NotRepeatingFromOne()
	{
		// ui-search-page-position-and-size: the owner's own complaint from the live UI — "на всех те
		// же 40 элементов, и сколько я пролистал, неясно". A page that shows the same "rows 1-40" on
		// every page is indistinguishable from one stuck in a loop; the position counter must advance.
		await _page!.GotoAsync($"/ui/{Ws}/{Proj}/memory/{Store}?q={Uri.EscapeDataString(Marker)}");
		await Expect(_page.GetByTestId("store-search-info")).ToContainTextAsync($"rows 1-{PageSize}");

		await _page.GetByTestId("store-search-next").ClickAsync();

		await Expect(_page.GetByTestId("store-search-info")).ToContainTextAsync($"rows {PageSize + 1}-{Seeded}");
	}

	[Fact]
	public async Task SelectingASmallerPageSize_ReturnsExactlyThatManyRows()
	{
		// ui-search-page-position-and-size: the owner's original ask was literally "iterate 10 at a
		// time" — a size control that is offered but doesn't change the page count would be exactly
		// the kind of setting-with-no-effect this whole task series has been closing.
		await _page!.GotoAsync($"/ui/{Ws}/{Proj}/memory/{Store}?q={Uri.EscapeDataString(Marker)}");

		await _page.GetByTestId("store-search-size").SelectOptionAsync("10");
		await _page.GetByTestId("store-search-submit").ClickAsync();

		var keys = await KeysOnScreenAsync();
		keys.Should().HaveCount(10, "the page-size control must actually shrink the page, not just persist a value nobody reads");
		await Expect(_page.GetByTestId("store-search-info")).ToContainTextAsync("rows 1-10");
	}

	[Fact]
	public async Task AStaleCursor_IsRefusedVisibly_NotWithAServerError()
	{
		// A cursor whose order no longer exists must degrade to an explained restart. Silent
		// re-serving from the top would splice two orderings; a 500 would punish the reader for a
		// race they cannot see.
		var forged = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(
			"""{"v":2,"f":"deadbeefdeadbeefdead","s":"","k":"note-000","b":"project","o":"deadbeefdeadbeefdead"}"""));

		var response = await _page!.GotoAsync(
			$"/ui/{Ws}/{Proj}/memory/{Store}?q={Uri.EscapeDataString(Marker)}&cursor={Uri.EscapeDataString(forged)}");

		response!.Status.Should().Be(200, "a rejected cursor is a stale read, not a server fault");
		await Expect(_page.GetByTestId("store-cursor-error")).ToBeVisibleAsync();
	}

	async Task<IReadOnlyList<string>> KeysOnScreenAsync()
	{
		var handles = await _page!.Locator("[data-entry-key]").AllAsync();
		var keys = new List<string>();
		foreach (var h in handles)
			if (await h.GetAttributeAsync("data-entry-key") is { } k)
				keys.Add(k);
		return keys;
	}
}
