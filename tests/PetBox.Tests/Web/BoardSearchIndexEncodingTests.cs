using System.Net;
using LinqToDB;
using LinqToDB.Async;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Tasks.Contract;

namespace PetBox.Tests.Web;

// json-encoder-shared-globally, Verify requirement: EMPIRICAL proof (a real HTTP round-trip
// through the actual DI-configured JsonOptions, not a direct PageModel call that would skip the
// executor entirely) that TaskBoard's `?handler=SearchIndex` — a `new JsonResult(index)` whose
// stem-lookup KEYS are Cyrillic stems (TaskBoard.cshtml.cs, board-search-stem-lookup) — now emits
// real UTF-8 Cyrillic instead of the default encoder's \uXXXX. This is the freshest of the five
// documented incidents (see the card): the board's 481-node stem index measured ×1.69 raw / ×1.10
// gzip byte inflation under the default encoder.
public sealed class BoardSearchIndexEncodingFixture : IAsyncLifetime
{
	public const string TestPasswordHash = "pbkdf2$100000$h1twJi/he3s8S7jSM9pkGQ==$efnLBffww5Gprn6BjpNgZkTcG+1zNu2L6z3TZ7YvD/o=";
	public const string Ws = "json-enc-ws";
	public const string Proj = "json-enc-proj";
	public const string Board = "json-enc-board";

	public WebApplicationFactory<Program> Factory { get; }
	public HttpClient Client { get; private set; } = null!;

	public BoardSearchIndexEncodingFixture()
	{
		Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Testing");
		Factory = new WebApplicationFactory<Program>()
			.WithWebHostBuilder(b =>
			{
				b.UseEnvironment("Testing");
				b.ConfigureAppConfiguration((_, cfg) =>
				{
					cfg.AddInMemoryCollection(new Dictionary<string, string?>
					{
						["ConnectionStrings:PetBox"] = TestSchema.NewTempConnectionString(),
						["Host:BackgroundServices"] = "false",
						["Features:Tasks"] = "true",
						["Admin:Username"] = "admin",
						["Admin:PasswordHash"] = TestPasswordHash,
					});
				});
			});
	}

	public async Task InitializeAsync()
	{
		using var scope = Factory.Services.CreateScope();
		using var db = scope.ServiceProvider.GetRequiredService<ICoreDbFactory>().Open();

		if (!await db.Workspaces.AnyAsync(w => w.Key == Ws))
			await db.InsertAsync(new Workspace { Key = Ws, Name = Ws, CreatedAt = DateTime.UtcNow });
		if (!await db.Projects.AnyAsync(p => p.Key == Proj))
			await db.InsertAsync(new Project { Key = Proj, WorkspaceKey = Ws, Name = "Json Encoding Fixture" });

		var tasks = scope.ServiceProvider.GetRequiredService<ITasksService>();
		if (!await tasks.BoardExistsAsync(Proj, Board))
			await tasks.CreateBoardAsync(Proj, Board, "simple", "json-encoder-shared-globally fixture", null, null);

		var existing = await tasks.GetAsync(Proj, Board, includeClosed: true);
		if (existing.Nodes.Count == 0)
		{
			await tasks.UpsertAsync(Proj, Board,
			[
				new NodePatch { Key = "cyrillic-node", Title = "Деплой на прод", Body = "Заметки о релизе кириллицей." },
			]);
		}

		Client = Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
	}

	public async Task DisposeAsync()
	{
		Client.Dispose();
		await Factory.DisposeAsync();
	}
}

public sealed class BoardSearchIndexEncodingTests : IClassFixture<BoardSearchIndexEncodingFixture>
{
	const string TestPassword = "test123";
	readonly HttpClient _client;

	public BoardSearchIndexEncodingTests(BoardSearchIndexEncodingFixture fx)
	{
		_client = fx.Client;
	}

	// Copied from NavTreeAndDataViewTests's GetAuthedAsync: logs in (anti-forgery + cookie) and
	// returns the authenticated response for url.
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
			["password"] = TestPassword,
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

	[Fact]
	public async Task SearchIndex_CyrillicTitleAndStems_SurviveAsRealUtf8_NotUnicodeEscapes()
	{
		var url = $"/ui/{BoardSearchIndexEncodingFixture.Ws}/{BoardSearchIndexEncodingFixture.Proj}"
			+ $"/tasks/{BoardSearchIndexEncodingFixture.Board}?handler=SearchIndex";
		using var resp = await GetAuthedAsync(url);

		resp.StatusCode.Should().Be(HttpStatusCode.OK);

		// Raw bytes, not a decoded string helper — the point is what actually went over the wire.
		var bytes = await resp.Content.ReadAsByteArrayAsync();
		var raw = System.Text.Encoding.UTF8.GetString(bytes);

		// The index KEYS are stems (BoardSearchIndexBuilder runs title/body through
		// TokenStemmer.Stem), so we don't assert an exact stemmed form — instead assert that SOME
		// real Cyrillic codepoint (U+0400-U+04FF) made it into the response body as literal UTF-8,
		// and that none was left behind as a \uXXXX escape.
		System.Text.RegularExpressions.Regex.IsMatch(raw, "[Ѐ-ӿ]").Should().BeTrue(
			"the search-index endpoint must emit real UTF-8 Cyrillic stems, not escapes — raw body: " + raw);
		raw.Should().NotContain("\\u04", "no Cyrillic must be left escaped as \\uXXXX (Cyrillic occupies the U+04xx block)");
	}
}
