using System.Net;
using System.Text.Json;
using LinqToDB;
using LinqToDB.Async;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Log.Core.Data;

namespace PetBox.Tests.Mcp;

// Per-MCP-tool-call economy metrics (work: surface-economy-metrics, spec: economy-measurable).
// McpTracingFilter emits ONE always-on Information event per CallTool over category
// "PetBox.Mcp.ToolCalls" → SystemLogger → SystemLogFlusher → the ($system, petbox) self-log,
// gated only on Seq:SelfLog:Enabled + Features:Logging (both on here). The event's named
// template args become KQL-addressable Properties.<Name>, so `log_query` can report call
// COUNT and request/response SIZE attributed to the MCP session — the before/after "cheaper"
// signal. This fixture drives real tool calls over the streamable-HTTP MCP surface and reads
// the self-log back via REST (REST queries do NOT pass through the CallTool filter, so they
// never pollute the tool-call events being counted).
public sealed class McpToolCallMetricsFixture : IAsyncLifetime
{
	// Seeded by M001/M004: the $system project + a key scoped logs:query (+ingest/config) — the
	// self-log's own project, so log_query over $system/petbox passes the ownership check.
	public const string SystemApiKey = "yb_key_system_internal";

	// A SECOND key on the same ($system) project, scoped memory:read. Needed because the two
	// tools SystemApiKey can call (whoami, log_query) carry NO [LogArg] markup at all — log_query's
	// `take` hides inside the free-text `kql` and must never be logged. memory_search is the tool
	// with the marked knobs (q/scope/store/limit/bodyLen/includeUsage), so it is the only way to
	// prove Arg_* end-to-end: emitted → flushed → QUERYABLE FROM KQL, which is the whole point.
	public const string MemoryApiKey = "yb_key_system_memory";

	HttpClient _http = null!;
	HttpClient _memoryHttp = null!;

	public WebApplicationFactory<Program> Factory { get; }
	public HttpClient Http => _http;
	public McpClient Mcp { get; private set; } = null!;
	public McpClient MemoryMcp { get; private set; } = null!;

	public McpToolCallMetricsFixture()
	{
		Environment.SetEnvironmentVariable("PETBOX_MASTER_KEY", "test-key-for-secrets");
		// Read at CreateBuilder time (before WithWebHostBuilder), so the feature-gated
		// registrations (self-log, MCP surface) are in place at ConfigureServices.
		Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Testing");
		Environment.SetEnvironmentVariable("Features__Logging", "true");
		Environment.SetEnvironmentVariable("Features__Data", "true");
		// The SystemLogger (ILogger → self-log) is gated at build time (Program.cs) on
		// Seq:SelfLog:Enabled + Feature.Logging. That gate reads config BEFORE the
		// WebApplicationFactory's in-memory ConfigureAppConfiguration is visible, so — like
		// Features__* — it must arrive as an env var to register the provider + flusher.
		Environment.SetEnvironmentVariable("Seq__SelfLog__Enabled", "true");

		Factory = new WebApplicationFactory<Program>()
			.WithWebHostBuilder(b =>
			{
				b.UseEnvironment("Testing");
				b.ConfigureAppConfiguration((_, cfg) =>
				{
					cfg.AddInMemoryCollection(new Dictionary<string, string?>
					{
						// Uniquely-directoried Core db → its own logs/$system/petbox.db self-log,
						// so only THIS host's MCP calls populate the tool-call events we count.
						["ConnectionStrings:PetBox"] = TestSchema.NewTempConnectionString(),
						["Features:Logging"] = "true",
						["Features:Data"] = "true",
						// memory_search asserts Feature.Memory at CALL time (FeatureFlags reads
						// IConfiguration), so — unlike the build-time-gated flags above — the
						// in-memory value is enough; no process-wide env var needed.
						["Features:Memory"] = "true",
						["Seq:SelfLog:Enabled"] = "true",
					});
				});
			});
	}

	public async Task InitializeAsync()
	{
		var cs = Factory.Services.GetRequiredService<IConfiguration>().GetConnectionString("PetBox")!;
		TestSchema.Core(cs);
		_http = Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

		// The self-log is auto-created at boot when Features:Logging is on; ensure it regardless.
		using (var scope = Factory.Services.CreateScope())
		{
			var store = scope.ServiceProvider.GetRequiredService<ILogStore>();
			if (!await store.ExistsAsync(LogNames.SystemProject, LogNames.SelfLog))
				await store.CreateAsync(LogNames.SystemProject, LogNames.SelfLog, null);

			// The memory:read key on $system (the migrations seed the project, not this key).
			var db = scope.ServiceProvider.GetRequiredService<PetBoxDb>();
			await db.ApiKeys.Where(k => k.Key == MemoryApiKey).DeleteAsync();
			await db.InsertAsync(new ApiKey
			{
				Key = MemoryApiKey,
				ProjectKey = LogNames.SystemProject,
				Scopes = "memory:read",
				CreatedAt = DateTime.UtcNow,
			});
		}

		_http.DefaultRequestHeaders.Add("X-Api-Key", SystemApiKey);
		var transport = new HttpClientTransport(new HttpClientTransportOptions
		{
			Endpoint = new Uri(_http.BaseAddress!, "/mcp"),
			AdditionalHeaders = new Dictionary<string, string> { ["X-Api-Key"] = SystemApiKey },
		}, _http);
		Mcp = await McpClient.CreateAsync(transport, cancellationToken: default);

		// Its own HttpClient (no default X-Api-Key) so the two MCP sessions can never share or
		// shadow each other's key header.
		_memoryHttp = Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
		var memoryTransport = new HttpClientTransport(new HttpClientTransportOptions
		{
			Endpoint = new Uri(_memoryHttp.BaseAddress!, "/mcp"),
			AdditionalHeaders = new Dictionary<string, string> { ["X-Api-Key"] = MemoryApiKey },
		}, _memoryHttp);
		MemoryMcp = await McpClient.CreateAsync(memoryTransport, cancellationToken: default);
	}

	public async Task DisposeAsync()
	{
		await MemoryMcp.DisposeAsync();
		await Mcp.DisposeAsync();
		_memoryHttp.Dispose();
		_http.Dispose();
		await Factory.DisposeAsync();
	}
}

public sealed class McpToolCallMetricsTests : IClassFixture<McpToolCallMetricsFixture>
{
	readonly McpToolCallMetricsFixture _fx;

	public McpToolCallMetricsTests(McpToolCallMetricsFixture fx) => _fx = fx;

	// REST query against the ($system, petbox) self-log — this path does NOT go through the
	// CallTool filter, so it never adds "mcp tool" events (keeps counts deterministic).
	async Task<JsonDocument> QueryAsync(string kql)
	{
		var url = $"/api/logs/{LogNames.SystemProject}/{LogNames.SelfLog}/query?q={Uri.EscapeDataString(kql)}";
		using var resp = await _fx.Http.GetAsync(url);
		resp.StatusCode.Should().Be(HttpStatusCode.OK, "self-log query must succeed");
		return JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
	}

	// Drive a measured call over the MCP surface. whoami and log_query are the two tools the
	// $system key can call; both flow through McpTracingFilter.
	async Task CallWhoAmIAsync() =>
		await (await Tool("whoami")).CallAsync(new Dictionary<string, object?>());

	async Task CallLogQueryAsync() =>
		await (await Tool("log_query")).CallAsync(new Dictionary<string, object?>
		{
			["projectKey"] = LogNames.SystemProject,
			["logName"] = LogNames.SelfLog,
			["kql"] = "events | take 1",
		});

	// The [LogArg]-marked tool, driven over the memory:read MCP session. The args are chosen so
	// the call SUCCEEDS against an empty memory store (an empty hit list is fine — we measure the
	// CALL, not the hits) while turning the exact knobs the economy queries care about.
	const string SearchQuery = "petbox-secret-query-text";

	async Task CallMemorySearchAsync() =>
		await (await MemoryTool("memory_search")).CallAsync(new Dictionary<string, object?>
		{
			["q"] = SearchQuery,
			["bodyLen"] = 0,
			["limit"] = 5,
		});

	async Task<McpClientTool> Tool(string name) =>
		(await _fx.Mcp.ListToolsAsync()).First(t => t.Name == name);

	async Task<McpClientTool> MemoryTool(string name) =>
		(await _fx.MemoryMcp.ListToolsAsync()).First(t => t.Name == name);

	// Count "mcp tool {Tool}" self-log events for a tool via KQL. The SystemLogFlusher is async
	// (2s / 200-batch), so poll until the count reaches an expected minimum.
	async Task<long> ToolCallCountAsync(string tool)
	{
		var doc = await QueryAsync(
			$"events | where MessageTemplate contains 'mcp tool' | where Properties.Tool == '{tool}' | count");
		return doc.RootElement.GetProperty("rows")[0][0].GetInt64();
	}

	async Task WaitForToolCallCountAsync(string tool, long atLeast)
	{
		for (var i = 0; i < 400; i++)
		{
			if (await ToolCallCountAsync(tool) >= atLeast) return;
			await Task.Delay(25);
		}
		throw new Xunit.Sdk.XunitException(
			$"self-log did not reach {atLeast} 'mcp tool {tool}' events within 10s");
	}

	static double AsNumber(JsonElement e) => e.ValueKind switch
	{
		JsonValueKind.Number => e.GetDouble(),
		JsonValueKind.String => double.Parse(e.GetString()!),
		_ => throw new Xunit.Sdk.XunitException($"expected a numeric cell, got {e.ValueKind}"),
	};

	[Fact]
	public async Task ToolCall_EmitsQueryableMetricEvent_WithSessionAndSizes()
	{
		// One measured call over the MCP surface → one always-on self-log write. Wait for OUR OWN
		// event (before+1), not `atLeast: 1`: the flusher is async and the self-log is shared by
		// every test in this class, so a bare `>= 1` can be satisfied by a SIBLING test's event
		// while this call is still queued — letting it escape unflushed and rot the next test's
		// baseline (that was the MultipleCalls_… "3 where 2 expected" flake).
		var before = await ToolCallCountAsync("log_query");
		await CallLogQueryAsync();
		await WaitForToolCallCountAsync("log_query", before + 1);

		// The exact query the spec targets: attribute a call to its session and read the sizes.
		// Two backend accommodations (both faithful to the spec's intent): this KQL engine has
		// no `startswith`, so `contains 'mcp tool'` is the anchor (no other MessageTemplate holds
		// that literal); and a `project` of a dynamic member must be aliased.
		var doc = await QueryAsync(
			"events | where MessageTemplate contains 'mcp tool' | where Properties.Tool == 'log_query' " +
			"| project Session=Properties.Session, ReqChars=Properties.ReqChars, RespChars=Properties.RespChars | take 1");

		var rows = doc.RootElement.GetProperty("rows");
		rows.GetArrayLength().Should().Be(1, "the projection must return the single measured row");

		var row = rows[0];
		row[0].GetString().Should().NotBeNullOrEmpty("the call must be attributed to a session id");
		AsNumber(row[1]).Should().BeGreaterThan(0, "log_query carries request args → ReqChars > 0");
		AsNumber(row[2]).Should().BeGreaterThan(0, "a successful call has a response body → RespChars > 0");
	}

	[Fact]
	public async Task MultipleCalls_SummarizePerTool_YieldsCorrectCountsAndBytes()
	{
		// Before/after deltas under the shared host (earlier tests may have logged calls too).
		var whoBefore = await ToolCallCountAsync("whoami");
		var logBefore = await ToolCallCountAsync("log_query");

		await CallWhoAmIAsync();
		await CallWhoAmIAsync();
		await CallLogQueryAsync();

		await WaitForToolCallCountAsync("whoami", whoBefore + 2);
		await WaitForToolCallCountAsync("log_query", logBefore + 1);

		// The before/after "cheaper" signal: per-tool call count + summed response bytes.
		// Alias the group key so the column is 'Tool' (unaliased it would be 'tostring').
		var doc = await QueryAsync(
			"events | where MessageTemplate contains 'mcp tool' " +
			"| summarize Calls=count(), Bytes=sum(tolong(Properties.RespChars)) by Tool=tostring(Properties.Tool)");

		var cols = doc.RootElement.GetProperty("columns").EnumerateArray().Select(c => c.GetString()).ToList();
		var toolCol = cols.IndexOf("Tool");
		var callsCol = cols.IndexOf("Calls");
		var bytesCol = cols.IndexOf("Bytes");
		toolCol.Should().BeGreaterThanOrEqualTo(0);
		callsCol.Should().BeGreaterThanOrEqualTo(0);
		bytesCol.Should().BeGreaterThanOrEqualTo(0);

		// Skip the non-tool group (RequestLoggingMiddleware events → null Tool key).
		var byTool = doc.RootElement.GetProperty("rows").EnumerateArray()
			.Where(r => r[toolCol].ValueKind == JsonValueKind.String)
			.ToDictionary(r => r[toolCol].GetString()!, r => (Calls: AsNumber(r[callsCol]), Bytes: AsNumber(r[bytesCol])));

		byTool.Should().ContainKey("whoami");
		byTool.Should().ContainKey("log_query");
		byTool["whoami"].Calls.Should().Be(whoBefore + 2);
		byTool["log_query"].Calls.Should().Be(logBefore + 1);
		byTool["log_query"].Bytes.Should().BeGreaterThan(0, "summed response volume is the economy metric");
		byTool["whoami"].Bytes.Should().BeGreaterThan(0);
	}

	// The [LogArg] chore's END-TO-END claim (chore: toolcalls-log-params): the marked args of a
	// REAL tool call reach the self-log as Properties.Arg_<paramName> and are QUERYABLE FROM KQL —
	// which is the whole point, because the economy measurements ARE KQL over the ToolCalls log.
	// Unit tests cover the extraction; only this proves emit → flush → query.
	[Fact]
	public async Task ToolCall_MarkedArgs_AreQueryableFromKql_AndCarryNoFreeText()
	{
		// A real memory_search over MCP with an explicit shape: a `q`, bodyLen:0, limit:5. The hit
		// list is empty (nothing was ever remembered on this host) and that is fine — the CALL is
		// the measurement, not its hits.
		await CallMemorySearchAsync();
		await WaitForToolCallCountAsync("memory_search", atLeast: 1);

		// The knob-reading query an economy report actually runs. Same accommodations as above:
		// `contains 'mcp tool'` is the anchor (no startswith), and every projected dynamic member
		// must be aliased. Arg_includeUsage was NOT passed — present-only semantics means the
		// property is absent from the event, and this engine renders an absent member as a NULL
		// cell (not "", not a missing column).
		var doc = await QueryAsync(
			"events | where MessageTemplate contains 'mcp tool' | where Properties.Tool == 'memory_search' " +
			"| project BodyLen=Properties.Arg_bodyLen, Limit=Properties.Arg_limit, Q=Properties.Arg_q, " +
			"Usage=Properties.Arg_includeUsage | take 1");

		var rows = doc.RootElement.GetProperty("rows");
		rows.GetArrayLength().Should().Be(1, "the marked call must be one addressable row");

		var row = rows[0];
		AsNumber(row[0]).Should().Be(0, "bodyLen:0 — the cheap-path knob the agent turned is visible");
		AsNumber(row[1]).Should().Be(5, "limit:5 — the other knob, readable per call");
		row[3].ValueKind.Should().Be(JsonValueKind.Null,
			"includeUsage was never passed → present-only semantics leaves Arg_includeUsage ABSENT");

		// PRIVACY, the load-bearing assertion: `q` is marked Presence, so what lands is the BOOL
		// true — never the query text. Read the RAW event (not just the projection) so the check is
		// on what is actually stored: the property's raw JSON must be the bare literal `true`
		// (a string value would be quoted, as Properties.Tool is), and the text must appear nowhere.
		var raw = await QueryAsync(
			"events | where MessageTemplate contains 'mcp tool' | where Properties.Tool == 'memory_search' | take 1");
		var props = raw.RootElement.GetProperty("events")[0].GetProperty("Properties");
		var argQ = props.GetProperty("Arg_q").GetString();
		argQ.Should().Be("true", "Presence mode logs the bare bool true, not the query text");
		props.GetRawText().Should().NotContain(SearchQuery, "the free-text query must never reach the log");
	}

	// The safe default, end-to-end: a tool NOBODY marked up contributes NO arg telemetry. log_query
	// is exactly that tool — its `take` hides inside the free-text `kql`, which must never be logged.
	[Fact]
	public async Task ToolCall_WithoutMarkup_CarriesNoArgProperties()
	{
		// Delta, not `atLeast: 1` — see ToolCall_EmitsQueryableMetricEvent: every test must leave the
		// self-log fully flushed, or its stragglers land in a later test's counts.
		var before = await ToolCallCountAsync("log_query");
		await CallLogQueryAsync();
		await WaitForToolCallCountAsync("log_query", before + 1);

		var raw = await QueryAsync(
			"events | where MessageTemplate contains 'mcp tool' | where Properties.Tool == 'log_query' | take 1");
		var props = raw.RootElement.GetProperty("events")[0].GetProperty("Properties");

		// Not "Arg_kql is absent" (that would be a tautology — the param is unmarked) but the
		// stronger shape claim: the event carries NO Arg_* property AT ALL.
		props.EnumerateObject().Select(p => p.Name)
			.Should().NotContain(n => n.StartsWith("Arg_", StringComparison.Ordinal),
				"an unmarked tool emits zero arg telemetry")
			.And.Contain("Tool", "…while the always-on economy columns are still there");
	}
}
