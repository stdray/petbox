using LinqToDB;
using LinqToDB.Async;
using Microsoft.Extensions.DependencyInjection;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.E2ETests.Infrastructure;
using PetBox.Memory.Contract;

namespace PetBox.E2ETests;

// ui-search-ranking-mode-preference: the owner asked directly "where is the toggle between fast
// search and normal search?" — there was none. This is the proof that /ui/me/preferences' new
// SearchRankingMode field is not merely a saved value nobody reads: flipping it must change what
// ACTUALLY happens on every UI search path (spec search-ranking-mode-is-caller-choice), visible
// through the SAME honest three-way provenance MCP callers get (spec search-rerank-in-loop —
// Reranked / DegradedRrf / ChosenRrf).
//
// The E2E fixture (WebAppFixture) wires no LLM/rerank route, so the reranker is never constructed
// regardless of mode — which makes the two outcomes exact and deterministic rather than merely
// "different": Speed always answers ChosenRrf (an honest CHOICE to skip the rerank attempt
// entirely), Precision always falls through to the SAME RRF answer but reports it as DegradedRrf
// (an honest DEGRADATION — the rerank path was asked for and could not run). A toggle that didn't
// actually flow into the request would leave both reads at ChosenRrf; that is exactly the defect
// this test guards against (verified red by reverting the RankingMode wiring — see task report).
[Collection(nameof(UiCollection))]
public sealed class SearchRankingModePreferenceTests(WebAppFixture app, ITestOutputHelper output) : IAsyncLifetime
{
	const string Ws = "ranking-pref-ws";
	const string Proj = "ranking-pref-proj";
	const string Store = "rankingprefnotes";
	// A made-up token that appears in NOTHING else, so the match set is exactly the seeded rows.
	const string Marker = "флоксинумерация";

	IBrowserContext? _ctx;
	IPage? _page;

	public async ValueTask InitializeAsync()
	{
		using var scope = app.Services.CreateScope();
		using var db = scope.ServiceProvider.GetRequiredService<ICoreDbFactory>().Open();

		if (!await db.Workspaces.AnyAsync(w => w.Key == Ws))
			await db.InsertAsync(new Workspace { Key = Ws, Name = Ws, CreatedAt = DateTime.UtcNow });
		if (!await db.Projects.AnyAsync(p => p.Key == Proj))
			await db.InsertAsync(new Project { Key = Proj, WorkspaceKey = Ws, Name = "Ranking Pref" });

		var memory = scope.ServiceProvider.GetRequiredService<IMemoryService>();
		if (!await memory.StoreExistsAsync(Proj, Store))
			await memory.CreateStoreAsync(Proj, Store, "ui-search-ranking-mode-preference fixture");

		var existing = await memory.ListActiveEntriesAsync(Proj, Store);
		if (existing.Count == 0)
		{
			await memory.UpsertAsync(Proj, Store, [
				new MemoryEntryInput
				{
					Key = "note-a",
					Type = "Project",
					Description = $"{Marker} запись первая",
					Body = $"Заметка про {Marker}: подробности для проверки режима ранжирования.",
				},
				new MemoryEntryInput
				{
					Key = "note-b",
					Type = "Project",
					Description = $"{Marker} запись вторая",
					Body = $"Другая заметка про {Marker}, чтобы кандидатов на реранк было несколько.",
				},
			], []);
		}

		_ctx = await app.NewContextAsync(authenticated: true);
		_page = await _ctx.NewPageAsync();
	}

	public async ValueTask DisposeAsync()
	{
		if (_ctx is not null)
		{
			// Restore the shared admin user's preference so this test doesn't taint sibling tests.
			try { await SetRankingModeAsync("Speed"); }
			catch { /* best-effort cleanup */ }
			await TraceArtifact.StopAndSaveAsync(_ctx, output);
			await _ctx.CloseAsync();
		}
	}

	async Task SetRankingModeAsync(string value)
	{
		await _page!.GotoAsync("/ui/me/preferences");
		await Expect(_page.GetByTestId("setting-input-SearchRankingMode")).ToBeVisibleAsync();
		await _page.GetByTestId("setting-input-SearchRankingMode").SelectOptionAsync(value);
		await _page.GetByTestId("me-preferences-form-submit").ClickAsync();
		await Expect(_page.GetByTestId("me-preferences-form-submit")).ToBeVisibleAsync();
	}

	async Task<string> SearchRankingAsync()
	{
		await _page!.GotoAsync($"/ui/{Ws}/{Proj}/memory/{Store}?q={Uri.EscapeDataString(Marker)}");
		await Expect(_page.GetByTestId("store-search-ranking")).ToBeVisibleAsync();
		return await _page.GetByTestId("store-search-ranking").InnerTextAsync();
	}

	[Fact]
	public async Task TogglingThePreference_ChangesTheRankingOutcomeOnTheSameQuery()
	{
		await SetRankingModeAsync("Speed");
		var speedRanking = await SearchRankingAsync();
		speedRanking.Should().Contain("ChosenRrf",
			"Speed is an honest CHOICE to skip the rerank attempt entirely, never a degradation");

		await SetRankingModeAsync("Precision");
		var precisionRanking = await SearchRankingAsync();
		// This fixture wires no rerank route, so Precision always falls through — but it must report
		// that fall-through HONESTLY as a degradation, never silently reuse the Speed choice's label.
		precisionRanking.Should().Contain("DegradedRrf",
			"Precision asked for the rerank; with no route available that is an honest DEGRADATION, not the Speed choice");
		precisionRanking.Should().NotBe(speedRanking,
			"the preference must change the ACTUAL ranking outcome the search performs, not just persist a value nobody reads");
	}

	[Fact]
	public async Task ThePreferencePage_StatesTheTradeoff_NotACorrectnessClaim()
	{
		await _page!.GotoAsync("/ui/me/preferences");
		var formText = await _page.GetByTestId("me-preferences-form").InnerTextAsync();

		formText.Should().Contain("trade-off",
			"the owner's own call: RRF is a different price, never framed as wrong");
		formText.ToLowerInvariant().Should().NotContain("correct sort",
			"the copy must not claim Precision is the 'correct' ordering — see the task brief's explicit ban on that framing");
		formText.Should().Contain("ms",
			"the copy must name the measured price of Precision (RerankCandidateBudget), not just praise it");
	}
}
