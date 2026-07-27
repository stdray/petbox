using System.Net;
using System.Text.RegularExpressions;
using LinqToDB;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PetBox.Core.Data;
using PetBox.Core.Settings;
using PetBox.Memory.Contract;

namespace PetBox.Tests.Web;

// Live end-to-end check of the memory store page's KEYSET listing (card
// listing-keyset-memory-sessions, spec listing-tail-reachable): runs the ACTUAL app
// (WebApplicationFactory<Program>, real HTTP + cookie login + Razor rendering) against a real
// SQLite-backed IMemoryService and exercises the adapter-level cursor logic in
// MemoryStoreModel — the part a service-level test (MemoryStorePagingTests) cannot reach, since
// the seek/slice/fingerprint machinery lives in the page model, not the service.
public sealed class MemoryStoreKeysetPagingTests : IAsyncLifetime
{
	const string Ws = "ws";
	const string Proj = "proj";
	const string Store = "notes";

	string _baseDir = "";
	WebApplicationFactory<Program> _factory = null!;
	HttpClient _client = null!;

	public async ValueTask InitializeAsync()
	{
		Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Testing");
		_baseDir = Path.Combine(Path.GetTempPath(), "petbox-memkeyset-" + Guid.NewGuid().ToString("N"));
		_factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
		{
			b.UseEnvironment("Testing");
			b.ConfigureAppConfiguration((_, cfg) =>
			{
				cfg.AddInMemoryCollection(new Dictionary<string, string?>
				{
					["ConnectionStrings:PetBox"] = TestSchema.NewTempConnectionString(),
					["Host:BackgroundServices"] = "false",
					["Features:Memory"] = "true",
					["Admin:Username"] = "admin",
					["Admin:PasswordHash"] = ModuleViewsFixture.TestPasswordHash,
				});
			});
			b.ConfigureServices(svc =>
			{
				var existing = svc.SingleOrDefault(d => d.ServiceType == typeof(IScopedDbFactory<PetBox.Memory.Data.MemoryDb>));
				if (existing is not null) svc.Remove(existing);
				svc.AddSingleton<IScopedDbFactory<PetBox.Memory.Data.MemoryDb>>(_ => new ScopedDbFactory<PetBox.Memory.Data.MemoryDb>(
					Path.Combine(_baseDir, "memory"), Scope.Project,
					c => new PetBox.Memory.Data.MemoryDb(PetBox.Memory.Data.MemoryDb.CreateOptions(c)), TestSchema.Memory));
			});
		});

		var cs = _factory.Services.GetRequiredService<IConfiguration>().GetConnectionString("PetBox")!;
		TestSchema.Core(cs);
		using (var db = new PetBoxDb(PetBoxDb.CreateOptions(cs)))
			db.Insert(new PetBox.Core.Models.Project { Key = Proj, WorkspaceKey = Ws, Name = "P", Description = "" });

		_client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

		using var scope = _factory.Services.CreateScope();
		var memory = scope.ServiceProvider.GetRequiredService<IMemoryService>();
		await memory.CreateStoreAsync(Proj, Store, "keyset paging smoke");
	}

	public async ValueTask DisposeAsync()
	{
		_client.Dispose();
		await _factory.DisposeAsync();
		TestDirs.CleanupOrDefer(_baseDir);
	}

	// Zero-padded keys upserted in ONE call share the same Updated timestamp (the listing default
	// sort, Updated desc, then ties on Key ascending — MemoryService.SortSelected), so ascending
	// hex key order IS the listing order: entry #i has rank i-1, matching the old FindActiveEntryPage
	// test's key scheme.
	static string Key(int i) => $"k{i:0000}";

	async Task SeedAsync(int count, string type = "Project")
	{
		using var scope = _factory.Services.CreateScope();
		var memory = scope.ServiceProvider.GetRequiredService<IMemoryService>();
		await memory.UpsertAsync(Proj, Store,
			Enumerable.Range(1, count).Select(i => new MemoryEntryInput
			{
				Key = Key(i),
				Version = 0,
				Type = type,
				Description = $"entry {i}",
				Body = $"body {i}",
			}).ToList(), []);
	}

	async Task<HttpResponseMessage> GetAuthedAsync(string url)
	{
		var resp = await _client.GetAsync(url);
		if (resp.StatusCode != HttpStatusCode.Found) return resp;

		var loginPage = await _client.GetAsync("/Login");
		var loginHtml = await loginPage.Content.ReadAsStringAsync();
		var tokenStart = loginHtml.IndexOf("__RequestVerificationToken", StringComparison.Ordinal);
		var valueStart = loginHtml.IndexOf("value=\"", tokenStart, StringComparison.Ordinal) + 7;
		var valueEnd = loginHtml.IndexOf('"', valueStart);
		var token = loginHtml[valueStart..valueEnd];
		var cookies = loginPage.Headers.GetValues("Set-Cookie").ToList();

		var loginReq = new HttpRequestMessage(HttpMethod.Post, "/Login?returnUrl=" + Uri.EscapeDataString(url));
		loginReq.Content = new FormUrlEncodedContent(new Dictionary<string, string>
		{
			["username"] = "admin",
			["password"] = "test123",
			["returnUrl"] = url,
			["__RequestVerificationToken"] = token,
		});
		foreach (var c in cookies) loginReq.Headers.Add("Cookie", c.Split(';')[0]);

		var loginResp = await _client.SendAsync(loginReq);
		var authCookie = loginResp.Headers.GetValues("Set-Cookie").First();
		var req = new HttpRequestMessage(HttpMethod.Get, url);
		req.Headers.Add("Cookie", authCookie.Split(';')[0]);
		return await _client.SendAsync(req);
	}

	static List<string> ExtractEntryKeys(string html) =>
		[.. Regex.Matches(html, "data-testid=\"memory-entry\" data-entry-key=\"(?<k>[^\"]+)\"").Select(m => m.Groups["k"].Value)];

	static string? ExtractNextCursor(string html)
	{
		// The anchor is written `<a href="...?cursor=..." ... data-testid="store-next">` — href
		// comes first in the markup, so anchor on THAT order rather than assuming attribute order
		// is arbitrary (a disabled "Next" is a plain <button>, which never matches this at all).
		var m = Regex.Match(html, "<a href=\"(?<h>[^\"]*)\"[^>]*data-testid=\"store-next\"");
		if (!m.Success) return null;
		var href = WebUtility.HtmlDecode(m.Groups["h"].Value);
		var cm = Regex.Match(href, "[?&]cursor=(?<c>[^&]+)");
		return cm.Success ? Uri.UnescapeDataString(cm.Groups["c"].Value) : null;
	}

	// THE CORE PROMISE (spec listing-tail-reachable): walking the keyset cursor page-by-page visits
	// every active entry EXACTLY ONCE — no row skipped, none duplicated — and the tail (rows past
	// the first page) is genuinely reachable, unlike the old offset scheme's silent failure modes.
	[Fact]
	public async Task Listing_WalkedPageByPage_VisitsEveryEntryExactlyOnce()
	{
		await SeedAsync(95);

		var seen = new List<string>();
		string? cursor = null;
		var pages = 0;
		do
		{
			var url = $"/ui/{Ws}/{Proj}/memory/{Store}" + (cursor is null ? "" : $"?cursor={Uri.EscapeDataString(cursor)}");
			using var resp = await GetAuthedAsync(url);
			resp.StatusCode.Should().Be(HttpStatusCode.OK);
			var html = await resp.Content.ReadAsStringAsync();
			var pageKeys = ExtractEntryKeys(html);
			pageKeys.Should().NotBeEmpty($"page {pages} must render at least one row");
			seen.AddRange(pageKeys);
			cursor = ExtractNextCursor(html);
			pages++;
			pages.Should().BeLessThan(20, "a bug here must not spin forever");
		} while (cursor is not null);

		pages.Should().Be(3, "95 entries at 40/page is 3 windows (40, 40, 15)");
		seen.Should().HaveCount(95);
		seen.Distinct(StringComparer.Ordinal).Should().HaveCount(95, "no row may repeat across pages");
		seen.Should().BeEquivalentTo(Enumerable.Range(1, 95).Select(Key), "every seeded key must be reachable");
	}

	// Changing a fingerprinted control (type) while reusing an OLD cursor must be a LOUD refusal
	// (KeysetCursor's own contract) — the page still renders (falls back to the first window)
	// rather than splicing two orderings together or 500ing.
	[Fact]
	public async Task Listing_CursorReusedAfterTypeChanges_ShowsLoudErrorAndFallsBackToFirstWindow()
	{
		await SeedAsync(95);

		using var page0 = await GetAuthedAsync($"/ui/{Ws}/{Proj}/memory/{Store}");
		var cursor = ExtractNextCursor(await page0.Content.ReadAsStringAsync());
		cursor.Should().NotBeNull();

		using var resp = await GetAuthedAsync(
			$"/ui/{Ws}/{Proj}/memory/{Store}?cursor={Uri.EscapeDataString(cursor!)}&type=Feedback");
		resp.StatusCode.Should().Be(HttpStatusCode.OK, "a stale cursor must never 500");
		var html = await resp.Content.ReadAsStringAsync();
		html.Should().Contain("data-testid=\"store-cursor-error\"");
		html.Should().Contain("DIFFERENT query", "the message must name what went wrong, not fail silently");
	}

	// A garbage cursor value is the same class of refusal as a stale-fingerprint one — loud, not a
	// crash, not a silent restart pretending nothing was passed.
	[Fact]
	public async Task Listing_GarbageCursor_ShowsLoudErrorAndFallsBackToFirstWindow()
	{
		await SeedAsync(5);

		using var resp = await GetAuthedAsync($"/ui/{Ws}/{Proj}/memory/{Store}?cursor=not-a-real-token");
		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		var html = await resp.Content.ReadAsStringAsync();
		html.Should().Contain("data-testid=\"store-cursor-error\"");
		ExtractEntryKeys(html).Should().HaveCount(5, "falls back to rendering the first window, not an empty page");
	}

	// The deep-link half of the stable entry URL (MemoryLinks, spec memory-entry-url): on a store
	// bigger than one window, `?key=` must seek the cursor so the target is the FIRST row of the
	// window it renders — not buried inside a fixed-size block the way an offset page would leave it.
	[Fact]
	public async Task DeepLink_OnAMultiWindowStore_SeeksTheTargetToTheFirstRow()
	{
		await SeedAsync(95);
		var target = Key(80); // well past the first 40-row window

		using var resp = await GetAuthedAsync($"/ui/{Ws}/{Proj}/memory/{Store}?key={target}");
		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		var html = await resp.Content.ReadAsStringAsync();

		var keys = ExtractEntryKeys(html);
		keys.Should().NotBeEmpty();
		keys[0].Should().Be(target, "the deep-linked entry must be the first row of the window it lands on");
		html.Should().MatchRegex($"id=\"{target}\"[^>]*data-highlight=\"true\"");
	}

	// The very inconsistency the card closed: LISTING mode (no `q`) used to ignore type entirely.
	// It must now narrow the set, same as search mode does.
	[Fact]
	public async Task Listing_TypeFilter_Narrows()
	{
		await SeedAsync(3, type: "Project");
		using (var scope = _factory.Services.CreateScope())
		{
			var memory = scope.ServiceProvider.GetRequiredService<IMemoryService>();
			await memory.UpsertAsync(Proj, Store,
				[new MemoryEntryInput { Key = "fb001", Version = 0, Type = "Feedback", Description = "fb", Body = "b" }], []);
		}

		using var resp = await GetAuthedAsync($"/ui/{Ws}/{Proj}/memory/{Store}?type=Feedback");
		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		var html = await resp.Content.ReadAsStringAsync();
		ExtractEntryKeys(html).Should().BeEquivalentTo(["fb001"]);
	}
}
