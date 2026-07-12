using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using PetBox.Memory.Contract;

namespace PetBox.Tests.Web;

// UI regression guard for work-item memory-usage-ui-cost-fit (spec: memory-usage-aggregate).
// The store page (MemoryStore.cshtml) used to show ONLY the impression counters
// (Surfaced/Opened) — memory-value-signal-vs-impression established that reading those as a
// value verdict is actively wrong ("surfaced 40, opened 0" can mean "the snippet was enough
// every time", not "useless"). The page now also renders the delivery-derived COST
// (DeliveredChars) and FIT (AvgKRel), both per-entry and as a store-wide aggregate, and — this
// is the load-bearing part — must show an ABSENCE-OF-DATA placeholder ("—") rather than 0/0%
// when an entry/store had no deliveries in the window (delivery_events only exists since
// 2026-07-12, so most pre-existing counter rows have Deliveries == 0).
public sealed class MemoryStoreCostFitViewTests : IClassFixture<ModuleViewsFixture>
{
	readonly ModuleViewsFixture _fx;

	public MemoryStoreCostFitViewTests(ModuleViewsFixture fx) => _fx = fx;

	// Local copy of ModuleViewsTests' login helper (not exposed on the shared fixture).
	async Task<HttpResponseMessage> GetAuthedAsync(string url)
	{
		var client = _fx.Client;
		var resp = await client.GetAsync(url);
		if (resp.StatusCode != HttpStatusCode.Found) return resp;

		var loginPage = await client.GetAsync("/Login");
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
			["password"] = "test123", // ModuleViewsFixture.TestPasswordHash's plaintext (Admin:PasswordHash pbkdf2 of it)
			["returnUrl"] = url,
			["__RequestVerificationToken"] = token,
		});
		foreach (var c in cookies) loginReq.Headers.Add("Cookie", c.Split(';')[0]);

		var loginResp = await client.SendAsync(loginReq);
		var authCookie = loginResp.Headers.GetValues("Set-Cookie").First();
		var req = new HttpRequestMessage(HttpMethod.Get, url);
		req.Headers.Add("Cookie", authCookie.Split(';')[0]);
		return await client.SendAsync(req);
	}

	[Fact]
	public async Task Entry_WithDeliveries_ShowsCostAndFit_NotJustImpressions()
	{
		const string store = "costfit-hit";
		using (var scope = _fx.Factory.Services.CreateScope())
		{
			var stores = scope.ServiceProvider.GetRequiredService<PetBox.Memory.Data.IMemoryStore>();
			if (!await stores.ExistsAsync("$system", store))
				await stores.CreateAsync("$system", store, "cost/fit happy path");

			var memory = scope.ServiceProvider.GetRequiredService<IMemoryService>();
			await memory.UpsertAsync("$system", store, [
				new MemoryEntryInput { Key = "hit", Version = 0, Type = "Project", Description = "d", Body = "b" },
			], []);

			var recorder = scope.ServiceProvider.GetRequiredService<IMemoryUsageRecorder>();
			recorder.Surfaced("$system", store, ["hit"]);
			recorder.Delivered("$system", [
				new MemoryDeliveryEvent("search", "project", store, "hit",
					DeliveredChars: 500, BodyChars: 500, RowChars: 600, Rank: 1,
					ScoreRaw: 0.02, KRel: 0.8, SessionId: null, UsageSource: "deliberate"),
			]);
			await recorder.FlushAsync();
		}

		using var resp = await GetAuthedAsync($"/ui/$system/$system/memory/{store}");
		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		var html = await resp.Content.ReadAsStringAsync();

		// Per-entry badge: real cost (chars) and fit (percent), not a placeholder.
		html.Should().MatchRegex("""data-testid="entry-cost"[^>]*>\s*💰 500""");
		html.Should().MatchRegex("""data-testid="entry-fit"[^>]*>\s*🎯 80\s*%""");

		// Store-wide aggregate: same two numbers surface.
		html.Should().MatchRegex("""data-testid="agg-cost"[^>]*>\s*500""");
		html.Should().MatchRegex("""data-testid="agg-fit"[^>]*>\s*80\s*%""");

		// The Opened caption no longer reads as a value verdict on its own.
		html.Should().Contain("not a value verdict");
	}

	[Fact]
	public async Task Entry_WithoutDeliveries_ShowsDash_NotZero()
	{
		const string store = "costfit-miss";
		using (var scope = _fx.Factory.Services.CreateScope())
		{
			var stores = scope.ServiceProvider.GetRequiredService<PetBox.Memory.Data.IMemoryStore>();
			if (!await stores.ExistsAsync("$system", store))
				await stores.CreateAsync("$system", store, "cost/fit absent data");

			var memory = scope.ServiceProvider.GetRequiredService<IMemoryService>();
			await memory.UpsertAsync("$system", store, [
				new MemoryEntryInput { Key = "surfaced-only", Version = 0, Type = "Project", Description = "d", Body = "b" },
			], []);

			// Impressions only — no Delivered call at all, so Deliveries == 0 / DeliveredChars == 0 /
			// AvgKRel == null for this entry AND for the store's windowed Cost aggregate. This is the
			// exact shape delivery_events pre-2026-07-12 rows have (absence of data, not zero value).
			var recorder = scope.ServiceProvider.GetRequiredService<IMemoryUsageRecorder>();
			recorder.Surfaced("$system", store, ["surfaced-only"]);
			recorder.Surfaced("$system", store, ["surfaced-only"]);
			await recorder.FlushAsync();
		}

		using var resp = await GetAuthedAsync($"/ui/$system/$system/memory/{store}");
		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		var html = await resp.Content.ReadAsStringAsync();

		// The old counters still show real numbers (2 surfaces, 0 opens)…
		html.Should().MatchRegex("""data-testid="entry-usage"[^>]*>\s*👁 2 · 📖 0""");
		// …but cost/fit must NOT collapse absence-of-data into 0 / 0% — an em dash placeholder
		// instead (Razor HTML-encodes "—" to the numeric entity, so match either form).
		const string dash = "(—|&#x2014;)";
		html.Should().MatchRegex($"""data-testid="entry-cost"[^>]*>\s*💰 {dash}""");
		html.Should().MatchRegex($"""data-testid="entry-fit"[^>]*>\s*🎯 {dash}""");
		html.Should().NotMatchRegex("""data-testid="entry-cost"[^>]*>\s*💰 0[^%]""");
		html.Should().NotMatchRegex("""data-testid="entry-fit"[^>]*>\s*🎯 0\s*%""");

		// Same absence-of-data guard on the store-wide aggregate tiles.
		html.Should().MatchRegex($"""data-testid="agg-cost"[^>]*>\s*{dash}""");
		html.Should().MatchRegex($"""data-testid="agg-fit"[^>]*>\s*{dash}""");
		html.Should().Contain("no deliveries in window");
		html.Should().Contain("no fit measured in window");
	}
}
