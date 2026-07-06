using System.Net;
using LinqToDB;
using LinqToDB.Async;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Data;
// PetBoxDb lives in PetBox.Core.Data; DataDbFactory/IDataDbFactory in PetBox.Data.

namespace PetBox.Tests.Web;

// Regression harness for ui-table-pagination-page-param: the Data-module table view must
// honour ?pageNum=N. The handler arg used to be named `page` — a reserved route-key in
// Razor Pages — so the query value never bound and OFFSET was stuck at 0. This fixture
// seeds a 120-row table with unique row tokens ("row-001".."row-120") so a page hop is
// observable: page 0 shows row-001 (not row-051), page 1 (OFFSET 50) shows row-051 (not
// row-001). IDataDbFactory is redirected to a per-run temp dir so nothing touches dev data.
public sealed class DataTablePaginationFixture : IAsyncLifetime
{
	public const string TestPasswordHash = "pbkdf2$100000$h1twJi/he3s8S7jSM9pkGQ==$efnLBffww5Gprn6BjpNgZkTcG+1zNu2L6z3TZ7YvD/o=";
	public const string DbName = "pagedb";
	public const string TableName = "nums";
	public const int RowCount = 120;

	readonly string _baseDir;

	public WebApplicationFactory<Program> Factory { get; }
	public HttpClient Client { get; private set; } = null!;

	public DataTablePaginationFixture()
	{
		Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Testing");
		_baseDir = Path.Combine(Path.GetTempPath(), "petbox-tablepage-" + Guid.NewGuid().ToString("N"));
		Factory = new WebApplicationFactory<Program>()
			.WithWebHostBuilder(b =>
			{
				b.UseEnvironment("Testing");
				b.ConfigureAppConfiguration((_, cfg) =>
				{
					cfg.AddInMemoryCollection(new Dictionary<string, string?>
					{
						["ConnectionStrings:PetBox"] = TestSchema.NewTempConnectionString(),
						["Features:Data"] = "true",
						["Admin:Username"] = "admin",
						["Admin:PasswordHash"] = TestPasswordHash,
					});
				});
				b.ConfigureServices(svc =>
				{
					var existing = svc.SingleOrDefault(d => d.ServiceType == typeof(IDataDbFactory));
					if (existing is not null) svc.Remove(existing);
					svc.AddSingleton<IDataDbFactory>(_ => new DataDbFactory(_baseDir));
				});
			});
	}

	public async Task InitializeAsync()
	{
		var cs = Factory.Services.GetRequiredService<IConfiguration>().GetConnectionString("PetBox")!;
		TestSchema.Core(cs);
		Client = Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });

		using var scope = Factory.Services.CreateScope();
		var db = scope.ServiceProvider.GetRequiredService<PetBoxDb>();
		var factory = scope.ServiceProvider.GetRequiredService<IDataDbFactory>();

		// Physical SQLite file + a table with RowCount uniquely-tokenised rows.
		await factory.CreateAsync("$system", DbName, DataDbFactory.DefaultMaxPageCount);
		await using (var conn = new SqliteConnection(factory.GetConnectionString("$system", DbName)))
		{
			await conn.OpenAsync();
			await using (var create = conn.CreateCommand())
			{
				create.CommandText = $"CREATE TABLE {TableName} (id INTEGER PRIMARY KEY, token TEXT NOT NULL)";
				await create.ExecuteNonQueryAsync();
			}
			await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync();
			await using (var ins = conn.CreateCommand())
			{
				ins.Transaction = tx;
				ins.CommandText = $"INSERT INTO {TableName} (id, token) VALUES (@id, @token)";
				var idP = ins.CreateParameter(); idP.ParameterName = "@id"; ins.Parameters.Add(idP);
				var tokP = ins.CreateParameter(); tokP.ParameterName = "@token"; ins.Parameters.Add(tokP);
				for (var i = 1; i <= RowCount; i++)
				{
					idP.Value = i;
					tokP.Value = $"row-{i:000}";
					await ins.ExecuteNonQueryAsync();
				}
			}
			await tx.CommitAsync();
		}

		// Metadata row so the view's existence check (DataDbs) passes.
		var now = DateTime.UtcNow;
		await db.InsertAsync(new DataDb
		{
			ProjectKey = "$system",
			Name = DbName,
			MaxPageCount = DataDbFactory.DefaultMaxPageCount,
			CreatedAt = now,
			UpdatedAt = now,
		});
	}

	public async Task DisposeAsync()
	{
		Client.Dispose();
		await Factory.DisposeAsync();
		TestDirs.CleanupOrDefer(_baseDir);
	}
}

public sealed class DataTablePaginationTests : IClassFixture<DataTablePaginationFixture>
{
	readonly HttpClient _client;

	const string TestPassword = "test123";
	const string DbName = DataTablePaginationFixture.DbName;
	const string TableName = DataTablePaginationFixture.TableName;

	public DataTablePaginationTests(DataTablePaginationFixture fx) => _client = fx.Client;

	// Logs in (anti-forgery + cookie) and returns the authenticated response for url.
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

	static string TableUrl(string query) => $"/ui/$system/$system/databases/{DbName}/{TableName}{query}";

	[Fact]
	public async Task FirstPage_ShowsLeadingRows_PrevDisabled_NextLinksToPageNum1()
	{
		using var resp = await GetAuthedAsync(TableUrl(""));
		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		var html = await resp.Content.ReadAsStringAsync();

		html.Should().Contain("row-001");
		html.Should().NotContain("row-051"); // page 0 stops before the 51st row
		html.Should().Contain("page 1 ·"); // 1-based page label
		// Prev is a disabled button (no link) on the first page; Next links forward.
		html.Should().NotContain("data-testid=\"table-prev\"");
		html.Should().Contain("data-testid=\"table-next\"");
		html.Should().Contain("pageNum=1"); // the renamed, non-reserved query key
		html.Should().NotContain("&page=1"); // never emit the reserved key
	}

	[Fact]
	public async Task SecondPage_BindsPageNum_AppliesOffset_ShowsSecondPageRows()
	{
		using var resp = await GetAuthedAsync(TableUrl("?pageNum=1"));
		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		var html = await resp.Content.ReadAsStringAsync();

		// OFFSET 50 → the second page: row-051 present, row-001 gone.
		html.Should().Contain("row-051");
		html.Should().NotContain("row-001");
		html.Should().Contain("page 2 ·");
		// Prev now links back to page 0; Next advances to page 2.
		html.Should().Contain("data-testid=\"table-prev\"");
		html.Should().Contain("data-testid=\"table-next\"");
		html.Should().Contain("pageNum=2");
	}

	// The bug proof: the OLD reserved key must NOT bind. Passing ?page=1 leaves OFFSET at 0,
	// so the first page still renders (row-001, not row-051).
	[Fact]
	public async Task ReservedPageKey_DoesNotBind_StaysOnFirstPage()
	{
		using var resp = await GetAuthedAsync(TableUrl("?page=1"));
		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		var html = await resp.Content.ReadAsStringAsync();

		html.Should().Contain("row-001");
		html.Should().NotContain("row-051");
		html.Should().Contain("page 1 ·");
	}
}
