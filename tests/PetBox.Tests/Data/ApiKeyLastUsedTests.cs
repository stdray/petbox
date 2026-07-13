using System.Text.Json;
using LinqToDB;
using LinqToDB.Async;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using PetBox.Core.Auth;
using PetBox.Core.Data;
using PetBox.Core.Models;

namespace PetBox.Tests.Data;

// work/apikey-last-used-stamp, spec apikey-last-used.
//
// The mechanism (owner's design): the auth middleware stamps an in-memory singleton, a background
// flusher batches the marks into ApiKeys.LastUsedAt every ~5 minutes, and every READ merges the two
// and takes the later. These tests pin all three claims — and, crucially, that the hot path wrote
// NOTHING to SQLite (the column is still NULL while apikey_list already shows the fresh value).
[Collection("ProvisioningTools")]
public sealed class ApiKeyLastUsedTests
{
	readonly WebApplicationFactory<Program> _factory;
	readonly McpClient _mcp;

	public ApiKeyLastUsedTests(ProvisioningToolsFixture fx)
	{
		_factory = fx.Factory;
		_mcp = fx.Mcp;
	}

	static string Text(ModelContextProtocol.Protocol.CallToolResult r) =>
		r.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().First().Text;

	async Task<ApiKey> RowAsync(string key)
	{
		using var scope = _factory.Services.CreateScope();
		using var db = scope.ServiceProvider.GetRequiredService<ICoreDbFactory>().Open();
		return await db.ApiKeys.FirstAsync(k => k.Key == key);
	}

	// A key of its own per test (the project is shared, the key is not) — so a parallel test's calls
	// can never stamp the key under assertion.
	async Task<string> SeedAsync(string project)
	{
		var key = $"yb_key_{Guid.NewGuid():N}";
		using var scope = _factory.Services.CreateScope();
		using var db = scope.ServiceProvider.GetRequiredService<ICoreDbFactory>().Open();
		await db.InsertAsync(new ApiKey
		{
			Key = key,
			ProjectKey = project,
			Scopes = "tasks:read",
			Name = "usage-probe",
			CreatedAt = DateTime.UtcNow,
		});
		return key;
	}

	// A project of its own per test, so apikey_list returns exactly the key under test.
	async Task<string> SeedProjectAsync()
	{
		var project = "p" + Guid.NewGuid().ToString("N")[..8];
		Text(await _mcp.CallToolAsync("project_create", new Dictionary<string, object?>
		{
			["workspaceKey"] = ProvisioningToolsFixture.Workspace,
			["key"] = project,
			["name"] = "usage",
		})).Should().NotContain("\"error\"");
		return project;
	}

	// Make ONE authenticated call with the given key (any authenticated call is a "use").
	async Task CallWithAsync(string key)
	{
		var http = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
		var transport = new HttpClientTransport(new HttpClientTransportOptions
		{
			Endpoint = new Uri(http.BaseAddress!, "/mcp"),
			AdditionalHeaders = new Dictionary<string, string> { ["X-Api-Key"] = key },
		}, http);
		var mcp = await McpClient.CreateAsync(transport, cancellationToken: default);
		try
		{
			await mcp.CallToolAsync("tasks_search", new Dictionary<string, object?>
			{
				["projectKey"] = "$system",
				["bodyLen"] = 0,
			});
		}
		finally
		{
			await mcp.DisposeAsync();
			http.Dispose();
		}
	}

	async Task<DateTime?> ListedLastUsedAsync(string project, string key)
	{
		var json = Text(await _mcp.CallToolAsync("apikey_list", new Dictionary<string, object?> { ["projectKey"] = project }));
		using var doc = JsonDocument.Parse(json);
		var row = doc.RootElement.GetProperty("keys").EnumerateArray().Single(k => k.GetProperty("key").GetString() == key);
		return row.TryGetProperty("lastUsedAt", out var v) && v.ValueKind is not JsonValueKind.Null
			? v.GetDateTime()
			: null;
	}

	KeyStatFlusher Flusher => _factory.Services.GetRequiredService<KeyStatFlusher>();
	IKeyStatService Stats => _factory.Services.GetRequiredService<IKeyStatService>();

	[Fact]
	public async Task UnusedKey_ListsAsNull()
	{
		var project = await SeedProjectAsync();
		var key = await SeedAsync(project);

		(await ListedLastUsedAsync(project, key)).Should().BeNull();   // never used ≠ used long ago
		(await RowAsync(key)).LastUsedAt.Should().BeNull();
	}

	[Fact]
	public async Task AfterOneCall_ListShowsItImmediately_WithoutAnyDbWrite()
	{
		var project = await SeedProjectAsync();
		var key = await SeedAsync(project);
		var before = DateTime.UtcNow.AddSeconds(-1);

		await CallWithAsync(key);

		// (1) fresh in the read surface AT ONCE — not in five minutes' time.
		var listed = await ListedLastUsedAsync(project, key);
		listed.Should().NotBeNull();
		listed!.Value.Should().BeAfter(before).And.BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(30));

		// (2) …and the hot path did NOT touch SQLite: the column is still NULL. The value the list
		// just served came from memory. This is the whole point of the design — zero DB work on auth.
		(await RowAsync(key)).LastUsedAt.Should().BeNull();
	}

	[Fact]
	public async Task Flush_PersistsTheStamp_SoItSurvivesRestart()
	{
		var project = await SeedProjectAsync();
		var key = await SeedAsync(project);
		await CallWithAsync(key);

		(await Flusher.FlushAsync()).Should().BeGreaterThan(0);

		// Now it is IN THE DB (read back through the linq2db mapping — M043 + the Fluent property).
		var stored = (await RowAsync(key)).LastUsedAt;
		stored.Should().NotBeNull();
		stored!.Value.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));

		// And the merged read agrees with it (no double-counting, no regression to the older value).
		(await ListedLastUsedAsync(project, key)).Should().BeCloseTo(stored.Value, TimeSpan.FromSeconds(1));
	}

	[Fact]
	public async Task ConcurrentCallsWithOneKey_LoseNoStamp_AndFlushAsOneRow()
	{
		var project = await SeedProjectAsync();
		var key = await SeedAsync(project);
		var before = DateTime.UtcNow.AddSeconds(-1);

		// 24 calls in flight with the SAME key — the singleton is the shared state they all hit.
		await Task.WhenAll(Enumerable.Range(0, 24).Select(_ => CallWithAsync(key)));

		var inMemory = Stats.LastUsed(key);
		inMemory.Should().NotBeNull();
		inMemory!.Value.Should().BeAfter(before);   // the LATEST stamp won, not an older one

		// Not one stamp was lost, and the batch carries the freshest one. (24 uses of one key collapse
		// to ONE row — that per-key collapse is pinned directly on the singleton in
		// KeyStatServiceTests.ManyStampsOfOneKey_DrainAsASingleEntry; the flusher's count here also
		// covers whatever other keys the shared host stamped meanwhile, so it is not asserted as 1.)
		(await Flusher.FlushAsync()).Should().BeGreaterThan(0);
		(await RowAsync(key)).LastUsedAt.Should().BeCloseTo(inMemory.Value, TimeSpan.FromSeconds(1));

		// Nothing left pending for this key: the second flush has nothing more to say about it.
		await Flusher.FlushAsync();
		(await RowAsync(key)).LastUsedAt.Should().BeCloseTo(inMemory.Value, TimeSpan.FromSeconds(1));
	}

	[Fact]
	public async Task Flush_WritesTheWholeBatch_InOneGo()
	{
		var project = await SeedProjectAsync();
		var keys = new List<string>();
		for (var i = 0; i < 5; i++) keys.Add(await SeedAsync(project));

		await Flusher.FlushAsync();                                  // drain whatever other tests left
		foreach (var key in keys) Stats.Stamp(key);

		(await Flusher.FlushAsync()).Should().BeGreaterThanOrEqualTo(5);   // one statement, N rows
		foreach (var key in keys)
			(await RowAsync(key)).LastUsedAt.Should().NotBeNull();
	}

	[Fact]
	public async Task FailedAuth_DoesNotStamp()
	{
		// A bogus key must not keep anything looking alive — only a SUCCESSFUL authentication counts.
		var project = await SeedProjectAsync();
		var key = await SeedAsync(project);

		var http = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
		http.DefaultRequestHeaders.Add("X-Api-Key", key + "-wrong");
		using var response = await http.GetAsync("/mcp");
		http.Dispose();

		Stats.LastUsed(key + "-wrong").Should().BeNull();
		(await ListedLastUsedAsync(project, key)).Should().BeNull();
	}
}

// The singleton in isolation: the auth path calls Stamp from every request thread at once, and the
// flusher drains concurrently. No lock, so the interleavings are the thing worth pinning.
public sealed class KeyStatServiceTests
{
	[Fact]
	public void Stamp_IsMonotonic_UnderParallelCallers()
	{
		var stats = new KeyStatService();
		var start = DateTime.UtcNow;

		Parallel.For(0, 5_000, _ => stats.Stamp("k"));

		var last = stats.LastUsed("k");
		last.Should().NotBeNull();
		last!.Value.Should().BeOnOrAfter(start);
	}

	[Fact]
	public async Task DrainDirty_LosesNoKey_WhileStampsKeepArriving()
	{
		var stats = new KeyStatService();
		var drained = new System.Collections.Concurrent.ConcurrentBag<string>();

		// Stampers and a drainer racing: every key stamped must end up in SOME drain (the final one
		// at the latest) — a lost mark would mean a key silently reported as never used.
		var stamping = Task.Run(() => Parallel.For(0, 500, i => stats.Stamp($"k{i}")));
		var draining = Task.Run(async () =>
		{
			while (!stamping.IsCompleted)
			{
				foreach (var pair in stats.DrainDirty()) drained.Add(pair.Key);
				await Task.Yield();
			}
		});
		await Task.WhenAll(stamping, draining);
		foreach (var pair in stats.DrainDirty()) drained.Add(pair.Key);

		drained.Distinct().Should().HaveCount(500);
	}

	[Fact]
	public void ManyStampsOfOneKey_DrainAsASingleEntry()
	{
		// The batching claim, stated exactly: N uses of a key inside one window are ONE row to write,
		// not N — which is what makes the flusher's cost a function of KEYS, not of TRAFFIC.
		var stats = new KeyStatService();
		for (var i = 0; i < 24; i++) stats.Stamp("k");

		var batch = stats.DrainDirty();
		batch.Should().ContainSingle();
		batch[0].Key.Should().Be("k");
		batch[0].Value.Should().Be(stats.LastUsed("k")!.Value);   // …and it is the freshest stamp
	}

	[Fact]
	public void LastUsed_SurvivesADrain()
	{
		// The drain consumes the dirty MARK, not the value: reads must keep merging against memory
		// after a flush, or apikey_list would go blind between flushes.
		var stats = new KeyStatService();
		stats.Stamp("k");
		var stamped = stats.LastUsed("k");

		stats.DrainDirty().Should().ContainSingle();
		stats.DrainDirty().Should().BeEmpty();          // nothing new since
		stats.LastUsed("k").Should().Be(stamped);       // …but the value is still there
	}
}
