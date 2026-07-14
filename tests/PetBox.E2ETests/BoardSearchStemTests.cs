using LinqToDB;
using LinqToDB.Async;
using Microsoft.Extensions.DependencyInjection;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.E2ETests.Infrastructure;
using PetBox.Tasks.Contract;

namespace PetBox.E2ETests;

// board-search-stem-lookup: end-to-end proof that the fetched {stem -> node} lookup
// (TaskBoardModel.OnGetSearchIndexAsync, built server-side by BoardSearchIndexBuilder and
// consumed client-side by ts/search-index.ts + ts/board.ts) actually changes what the free-text
// filter can find, and that the C#<->TS stemmer fixture parity (BoardSearchStemFixtureTests.cs /
// search-index.test.ts) holds up through a REAL browser fetch+filter, not just in isolation.
//   - stemming: the owner's own acceptance example — a query for the nominative "деплой" must
//     find a node whose body only ever says the instrumental "деплоем" (a plain substring match
//     against data-search's title+key+tags fallback could NOT do this; only the stemmed index can).
//   - multi-word AND: two query words must both be present in a node's indexed text — a node
//     missing either one must not match.
[Collection(nameof(UiCollection))]
public sealed class BoardSearchStemTests(WebAppFixture app, ITestOutputHelper output) : IAsyncLifetime
{
	const string Ws = "search-stem-ws";
	const string Proj = "search-stem-proj";
	const string Board = "searchstem";

	IBrowserContext? _ctx;
	IPage? _page;

	public async Task InitializeAsync()
	{
		using var scope = app.Services.CreateScope();
		using var db = scope.ServiceProvider.GetRequiredService<ICoreDbFactory>().Open();

		if (!await db.Workspaces.AnyAsync(w => w.Key == Ws))
			await db.InsertAsync(new Workspace { Key = Ws, Name = Ws, CreatedAt = DateTime.UtcNow });
		if (!await db.Projects.AnyAsync(p => p.Key == Proj))
			await db.InsertAsync(new Project { Key = Proj, WorkspaceKey = Ws, Name = "Search Stem" });

		var tasks = scope.ServiceProvider.GetRequiredService<ITasksService>();
		if (!await tasks.BoardExistsAsync(Proj, Board))
			await tasks.CreateBoardAsync(Proj, Board, "simple", "board-search-stem-lookup fixture", null, null);

		var existing = await tasks.GetAsync(Proj, Board, includeClosed: true);
		if (existing.Nodes.Count == 0)
		{
			await tasks.UpsertAsync(Proj, Board,
			[
				// Stemming: body only ever uses the INSTRUMENTAL "деплоем" — a query for the
				// nominative "деплой" must still find it (owner's own acceptance example). Title
				// carries no trace of either wordform, so this can only match via the body-stem
				// lookup, never the title+key+tags data-search substring fallback.
				new NodePatch { Key = "stem-hit", Title = "Release notes", Body = "Мы закончили работу над деплоем вчера вечером." },
				new NodePatch { Key = "stem-miss", Title = "Unrelated", Body = "Совсем другая задача про кеш." },
				// Multi-word AND: "both" carries both words, "log-only" carries just one.
				new NodePatch { Key = "and-both", Title = "Log rotation", Body = "Настроили ротацию log и wal файлов." },
				new NodePatch { Key = "and-log-only", Title = "Log cleanup", Body = "Почистили старые log файлы." },
			]);
		}

		_ctx = await app.NewContextAsync(authenticated: true);
		_page = await _ctx.NewPageAsync();
	}

	public async Task DisposeAsync()
	{
		if (_ctx is not null)
		{
			await TraceArtifact.StopAndSaveAsync(_ctx, output);
			await _ctx.CloseAsync();
		}
	}

	[Fact]
	public async Task Stemming_QueryForNominative_FindsBodyThatOnlyHasTheInstrumental()
	{
		await _page!.GotoAsync($"/ui/{Ws}/{Proj}/tasks/{Board}");

		await _page.GetByTestId("board-filter-text").FillAsync("деплой");

		await Expect(_page.Locator("[data-node-key='stem-hit']")).ToBeVisibleAsync();
		await Expect(_page.Locator("[data-node-key='stem-miss']")).ToBeHiddenAsync();
	}

	[Fact]
	public async Task MultiWordQuery_ANDsAcrossWords()
	{
		await _page!.GotoAsync($"/ui/{Ws}/{Proj}/tasks/{Board}");

		await _page.GetByTestId("board-filter-text").FillAsync("log wal");

		await Expect(_page.Locator("[data-node-key='and-both']")).ToBeVisibleAsync();
		// A node missing one of the AND words must not match, even though it matches the other.
		await Expect(_page.Locator("[data-node-key='and-log-only']")).ToBeHiddenAsync();
	}

	[Fact]
	public async Task EmptyQuery_ShowsEverything()
	{
		await _page!.GotoAsync($"/ui/{Ws}/{Proj}/tasks/{Board}");

		// Type then clear — proves the filter actively re-widens rather than the rows simply never
		// having been touched.
		await _page.GetByTestId("board-filter-text").FillAsync("деплой");
		await Expect(_page.Locator("[data-node-key='stem-miss']")).ToBeHiddenAsync();
		await _page.GetByTestId("board-filter-text").FillAsync("");

		await Expect(_page.Locator("[data-node-key='stem-hit']")).ToBeVisibleAsync();
		await Expect(_page.Locator("[data-node-key='stem-miss']")).ToBeVisibleAsync();
		await Expect(_page.Locator("[data-node-key='and-both']")).ToBeVisibleAsync();
		await Expect(_page.Locator("[data-node-key='and-log-only']")).ToBeVisibleAsync();
	}
}
