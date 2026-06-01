using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace PetBox.Tests.Web;

// Covers the main-UI read views for the Tasks / Memory / Sessions modules:
// the feature gate (module off → 404 on a board/store detail), the happy-path
// board/store listing for the seeded $system project, and unknown-container 404.
// Mirrors NavTreeAndDataViewTests (cookie auth + in-memory config).
[Collection("WebAppFactory")]
public sealed class ModuleViewsTests : IAsyncLifetime
{
	readonly WebApplicationFactory<Program> _factory;
	HttpClient _client = null!;

	const string TestPassword = "test123";
	const string TestPasswordHash = "pbkdf2$100000$h1twJi/he3s8S7jSM9pkGQ==$efnLBffww5Gprn6BjpNgZkTcG+1zNu2L6z3TZ7YvD/o=";

	public ModuleViewsTests()
	{
		Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Testing");
		var dbPath = Path.Combine(Path.GetTempPath(), $"petbox-test-{Guid.NewGuid():N}.db");
		_factory = new WebApplicationFactory<Program>()
			.WithWebHostBuilder(b =>
			{
				b.UseEnvironment("Testing");
				b.ConfigureAppConfiguration((_, cfg) =>
				{
					cfg.AddInMemoryCollection(new Dictionary<string, string?>
					{
						["ConnectionStrings:PetBox"] = $"Data Source={dbPath};Cache=Shared",
						["Features:Tasks"] = "true",
						["Features:Memory"] = "true",
						["Admin:Username"] = "admin",
						["Admin:PasswordHash"] = TestPasswordHash,
					});
				});
			});
	}

	public async Task InitializeAsync()
	{
		var cs = _factory.Services.GetRequiredService<IConfiguration>().GetConnectionString("PetBox")!;
		PetBox.Core.Data.MigrationRunner.Run(cs);
		_client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

		using var scope = _factory.Services.CreateScope();
		var boards = scope.ServiceProvider.GetRequiredService<PetBox.Tasks.Data.ITaskBoardStore>();
		if (!await boards.ExistsAsync("$system", "roadmap"))
			await boards.CreateAsync("$system", "roadmap", "the plan");
		var stores = scope.ServiceProvider.GetRequiredService<PetBox.Memory.Data.IMemoryStore>();
		if (!await stores.ExistsAsync("$system", "notes"))
			await stores.CreateAsync("$system", "notes", "agent notes");
	}

	public async Task DisposeAsync()
	{
		_client.Dispose();
		await _factory.DisposeAsync();
	}

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

	[Fact]
	public async Task Tasks_ListsCreatedBoard()
	{
		using var resp = await GetAuthedAsync("/ui/$system/$system/tasks");
		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		var html = await resp.Content.ReadAsStringAsync();
		html.Should().Contain("data-board-name=\"roadmap\"");
	}

	[Fact]
	public async Task TaskBoard_UnknownBoard_Returns404()
	{
		using var resp = await GetAuthedAsync("/ui/$system/$system/tasks/does-not-exist");
		resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
	}

	[Fact]
	public async Task TaskBoard_OrdersByTree_NotFlatPriority_AndRendersThreeLevels()
	{
		const string board = "ordertest";
		using (var scope = _factory.Services.CreateScope())
		{
			var boards = scope.ServiceProvider.GetRequiredService<PetBox.Tasks.Data.ITaskBoardStore>();
			if (!await boards.ExistsAsync("$system", board))
				await boards.CreateAsync("$system", board, "ordering");
			var ctx = boards.GetContext("$system", board);
			// Early phase p1 (priority 10) whose wave has a deliberately huge priority,
			// and a later phase p2 (priority 500) in between. A flat priority sort would
			// emit p1(10), p2(500), p1/wlow(900) — the wave drifting past p2 (finding D11).
			await PetBox.Core.Data.Temporal.TemporalStore.UpsertAsync(ctx, new[]
			{
				new PetBox.Tasks.Data.PlanNode { Key = "p1", Version = 0, Status = "Pending", Name = "Phase one", Body = "", Priority = 10 },
				new PetBox.Tasks.Data.PlanNode { Key = "p1/wlow", Version = 0, Status = "Pending", Name = "Low wave", Body = "", Priority = 900 },
				new PetBox.Tasks.Data.PlanNode { Key = "p1/wlow/deep", Version = 0, Status = "Pending", Name = "Deep task", Body = "", Priority = 1 },
				new PetBox.Tasks.Data.PlanNode { Key = "p2", Version = 0, Status = "Pending", Name = "Phase two", Body = "", Priority = 500 },
			});
		}

		using var resp = await GetAuthedAsync($"/ui/$system/$system/tasks/{board}");
		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		var html = await resp.Content.ReadAsStringAsync();

		var wlow = html.IndexOf("data-node-key=\"p1/wlow\"", StringComparison.Ordinal);
		var deep = html.IndexOf("data-node-key=\"p1/wlow/deep\"", StringComparison.Ordinal);
		var p2 = html.IndexOf("data-node-key=\"p2\"", StringComparison.Ordinal);

		wlow.Should().BeGreaterThan(0);
		deep.Should().BeGreaterThan(0);
		p2.Should().BeGreaterThan(0);
		// DFS keeps the wave (and its deep task) under p1, before p2 — not flat by priority.
		wlow.Should().BeLessThan(p2);
		deep.Should().BeLessThan(p2);
		// The third level (Phase/Wave/Task) renders, indented at depth 3.
		html.Should().Contain("data-node-key=\"p1/wlow/deep\" data-depth=\"3\"");
	}

	[Fact]
	public async Task Memory_ListsCreatedStore()
	{
		using var resp = await GetAuthedAsync("/ui/$system/$system/memory");
		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		var html = await resp.Content.ReadAsStringAsync();
		html.Should().Contain("data-store-name=\"notes\"");
	}

	[Fact]
	public async Task MemoryStore_UnknownStore_Returns404()
	{
		using var resp = await GetAuthedAsync("/ui/$system/$system/memory/does-not-exist");
		resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
	}

	[Fact]
	public async Task Sessions_EmptyList_RendersOk()
	{
		using var resp = await GetAuthedAsync("/ui/$system/$system/sessions");
		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		var html = await resp.Content.ReadAsStringAsync();
		html.Should().Contain("sessions-empty");
	}

	[Fact]
	public async Task TasksAdmin_RendersCreateForm_AndListsBoard()
	{
		using var resp = await GetAuthedAsync("/ui/admin/ws/$system/projects/$system/tasks");
		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		var html = await resp.Content.ReadAsStringAsync();
		html.Should().Contain("board-create-form");
		html.Should().Contain("data-board-name=\"roadmap\"");
	}

	[Fact]
	public async Task MemoryAdmin_RendersCreateForm_AndListsStore()
	{
		using var resp = await GetAuthedAsync("/ui/admin/ws/$system/projects/$system/memory");
		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		var html = await resp.Content.ReadAsStringAsync();
		html.Should().Contain("store-create-form");
		html.Should().Contain("data-store-name=\"notes\"");
	}

	[Fact]
	public async Task Doc_Index_IsPublic_NoRedirect()
	{
		// Anonymous client (no cookie) — doc pages must NOT redirect to /Login.
		using var resp = await _client.GetAsync("/doc");
		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		(await resp.Content.ReadAsStringAsync()).Should().Contain("doc-index-title");
	}

	[Fact]
	public async Task Doc_Agent_IsPublic_ShowsMcpUrlAndTree()
	{
		using var resp = await _client.GetAsync("/doc/agent");
		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		var html = await resp.Content.ReadAsStringAsync();
		html.Should().Contain("doc-agent");
		html.Should().Contain("/mcp");
		html.Should().Contain("Phase"); // tree model documented
	}
}
