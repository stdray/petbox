using System.Diagnostics;
using System.Net;
using System.Text.Json;
using LinqToDB.Async;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using PetBox.Core.Data;
using PetBox.Web.Mcp;

namespace PetBox.Tests.Mcp;

// work: mcp-error-envelope-leaks-stack-traces — an MCP error must be TRACEABLE.
//
// Two claims, tested at two levels:
//  1. The envelope invariant (pure, exhaustive): exactly ONE of traceId / detail is present.
//     Never both (the stack would leak anyway), never neither (the failure would be invisible).
//  2. The carrier is REAL (end-to-end, over the streamable-HTTP MCP surface): the exception's
//     STACK lands in the self-log's `Exception` COLUMN, addressable by the very trace id the
//     envelope handed out — and when there is no trace id, the stack still reaches the caller.
public sealed class McpErrorEnvelopeBuildTests
{
	const string ValidTrace = "4bf92f3577b34da6a3ce929d0e0e4736";
	const string ZeroTrace = "00000000000000000000000000000000";

	// A THROWN exception — only then does ToString() carry a stack, which is what the
	// fallback branch is for.
	static Exception Thrown(string message = "boom")
	{
		try { throw new InvalidOperationException(message); }
		catch (Exception ex) { return ex; }
	}

	// The machine formulation of the owner's goal: "если есть traceid то пишем его в ответ,
	// если нет оставляем исключение" — one carrier, always exactly one.
	static void AssertInvariant(McpErrorEnvelopeFilter.ErrorEnvelope envelope)
	{
		var e = envelope.Error;
		(e.TraceId is not null).Should().NotBe(e.Detail is not null,
			"the envelope carries EITHER a trace id to look the failure up by OR the exception itself — never both, never neither");
		e.Type.Should().NotBeNullOrEmpty("the error class is unconditional");
		e.Message.Should().NotBeNullOrEmpty("the message is unconditional — it carries contract data");
	}

	public static TheoryData<string?, bool> Cases() => new()
	{
		{ ValidTrace, true },   // the traceable case
		{ ValidTrace, false },  // valid id, but nothing records it → useless token
		{ null, true },         // no Activity at all (OTel off, nothing else tracing)
		{ ZeroTrace, true },    // an Activity in a non-W3C id format yields the all-zeros id
		{ "", true },
		{ "abc", true },        // not a 32-hex W3C id
		{ null, false },
	};

	[Theory]
	[MemberData(nameof(Cases))]
	public void Build_Always_CarriesExactlyOneOf_TraceId_Or_Detail(string? traceId, bool recorded)
	{
		var envelope = McpErrorEnvelopeFilter.Build(Thrown(), traceId, recorded);
		AssertInvariant(envelope);
	}

	[Fact]
	public void Build_WithRecordedValidTrace_HandsOutTheToken_AndNoStack()
	{
		var envelope = McpErrorEnvelopeFilter.Build(Thrown(), ValidTrace, recorded: true);
		AssertInvariant(envelope);

		envelope.Error.TraceId.Should().Be(ValidTrace);
		envelope.Error.Detail.Should().BeNull("the stack lives in the self-log, addressable by that id");
		envelope.Error.Type.Should().Be(nameof(InvalidOperationException));
		envelope.Error.Message.Should().Be("boom", "the message is passed through verbatim — never trimmed or reformatted");
	}

	// The fallback the owner asked for, at the unit level: no usable trace id ⇒ the exception
	// itself rides the response, stack included. Both reasons for "no usable id" are covered.
	[Theory]
	[InlineData(null, true)]      // no Activity
	[InlineData(ZeroTrace, true)] // Activity present but not W3C
	[InlineData(ValidTrace, false)] // valid id, self-log off → nothing to look up
	public void Build_WithoutUsableTrace_FallsBackToTheException(string? traceId, bool recorded)
	{
		var envelope = McpErrorEnvelopeFilter.Build(Thrown(), traceId, recorded);
		AssertInvariant(envelope);

		envelope.Error.TraceId.Should().BeNull();
		envelope.Error.Detail.Should().NotBeNull()
			.And.Contain(nameof(InvalidOperationException))
			.And.Contain("   at ", "the fallback must carry the STACK — it is the only remaining carrier");
	}

	// The wire shape: an omitted field must be ABSENT, not null — an agent parsing `.error.detail`
	// must not see a null key where the old envelope always had a string.
	[Fact]
	public void Serialized_OmitsTheAbsentCarrier()
	{
		var traced = JsonSerializer.Serialize(McpErrorEnvelopeFilter.Build(Thrown(), ValidTrace, recorded: true));
		traced.Should().Contain("\"traceId\"").And.NotContain("\"detail\"");

		var fallback = JsonSerializer.Serialize(McpErrorEnvelopeFilter.Build(Thrown(), null, recorded: true));
		fallback.Should().Contain("\"detail\"").And.NotContain("\"traceId\"");

		// type/message are unconditional on both branches.
		traced.Should().Contain("\"type\"").And.Contain("\"message\"");
		fallback.Should().Contain("\"type\"").And.Contain("\"message\"");
	}
}

// ---- end-to-end: the carrier actually exists ----

// Drives REAL failing tool calls over the streamable-HTTP MCP surface against a host with the
// self-log on, and reads the self-log back over REST (a REST query does NOT pass through the
// CallTool filter, so reading never manufactures the events being asserted).
//
// The ambient Activity is put under TEST control by a startup-filter middleware keyed on a
// request header, so BOTH branches are exercised deterministically on ONE host — instead of
// process-global ActivityListener state shared with every other test class.
// MEASURED, not assumed: on this very host with OpenTelemetry OFF, an unheadered call still comes
// back with a valid 32-hex traceId — ASP.NET Core's hosting layer starts a request Activity as
// soon as any logging provider is enabled, independently of OTel. So "OTel off ⇒ no trace id" is
// NOT the real fallback trigger; "nothing records the exception" (self-log off) is, and that leg
// is covered exhaustively by McpErrorEnvelopeBuildTests. This middleware reproduces the no-Activity
// leg the only way it can be reproduced deterministically here.
public sealed class McpErrorEnvelopeTraceFixture : IAsyncLifetime
{
	// Seeded by M001/M004: $system + a key scoped logs:query — the self-log's own project, so a
	// log_query/REST read over $system/petbox passes the ownership check.
	const string SystemApiKey = "yb_key_system_internal";

	const string TraceHeader = "X-Test-Trace";

	HttpClient _http = null!;
	HttpClient _tracedHttp = null!;
	HttpClient _untracedHttp = null!;

	WebApplicationFactory<Program> Factory { get; }
	public HttpClient Http => _http;

	// An MCP session whose requests run under a valid W3C Activity…
	public McpClient Traced { get; private set; } = null!;

	// …and one whose requests run with NO ambient Activity at all (the OTel-off / untraced case).
	public McpClient Untraced { get; private set; } = null!;

	public McpErrorEnvelopeTraceFixture()
	{
		Environment.SetEnvironmentVariable("PETBOX_MASTER_KEY", "test-key-for-secrets");
		Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Testing");

		Factory = new WebApplicationFactory<Program>()
			.WithWebHostBuilder(b =>
			{
				b.UseEnvironment("Testing");
				// Features:Logging (feature-gated registrations) and Seq:SelfLog:Enabled (the
				// SystemLogger gate) are both read at BUILD time (Program.cs, before
				// builder.Build()). UseSetting IS visible at those pre-Build reads (measured:
				// Architecture/ConfigVisibilityContractTests) — a process-global env var is
				// unnecessary and was leaking into every other test in the process
				// (chore/tests-env-leak).
				b.UseSetting("Features:Logging", "true");
				b.UseSetting("Seq:SelfLog:Enabled", "true");
				b.ConfigureServices(s => s.AddSingleton<IStartupFilter, TraceControlStartupFilter>());
				b.ConfigureAppConfiguration((_, cfg) =>
				{
					cfg.AddInMemoryCollection(new Dictionary<string, string?>
					{
						// Own Core db → own logs/$system/petbox.db self-log, so only THIS host's
						// failures land in the events these tests read.
						["ConnectionStrings:PetBox"] = TestSchema.NewTempConnectionString(),
						["Features:Logging"] = "true",
						["Seq:SelfLog:Enabled"] = "true",
					});
				});
			});
	}

	public async ValueTask InitializeAsync()
	{
		var cs = Factory.Services.GetRequiredService<IConfiguration>().GetConnectionString("PetBox")!;
		TestSchema.Core(cs);
		_http = Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

		using (var scope = Factory.Services.CreateScope())
		{
			var store = scope.ServiceProvider.GetRequiredService<ILogStore>();
			if (!await store.ExistsAsync(LogNames.SystemProject, LogNames.SelfLog))
				await store.CreateAsync(LogNames.SystemProject, LogNames.SelfLog, null);

			// The seeded $system key must be able to run log_query (the tool whose failure we drive).
			using var db = scope.ServiceProvider.GetRequiredService<ICoreDbFactory>().Open();
			(await db.ApiKeys.AnyAsync(k => k.Key == SystemApiKey))
				.Should().BeTrue("the $system internal key is seeded by the core migrations");
		}

		_http.DefaultRequestHeaders.Add("X-Api-Key", SystemApiKey);
		(_tracedHttp, Traced) = await SessionAsync("on");
		(_untracedHttp, Untraced) = await SessionAsync("off");
	}

	// Each MCP session gets its OWN HttpClient so its trace-mode header can never bleed into
	// the other's requests.
	async Task<(HttpClient, McpClient)> SessionAsync(string mode)
	{
		var http = Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
		var transport = new HttpClientTransport(new HttpClientTransportOptions
		{
			Endpoint = new Uri(http.BaseAddress!, "/mcp"),
			AdditionalHeaders = new Dictionary<string, string>
			{
				["X-Api-Key"] = SystemApiKey,
				[TraceHeader] = mode,
			},
		}, http);
		return (http, await McpClient.CreateAsync(transport, cancellationToken: default));
	}

	public async ValueTask DisposeAsync()
	{
		await Untraced.DisposeAsync();
		await Traced.DisposeAsync();
		_untracedHttp.Dispose();
		_tracedHttp.Dispose();
		_http.Dispose();
		await Factory.DisposeAsync();
	}

	// Test-only: puts the ambient Activity under the test's control, per request, by header.
	//   "on"  → guarantee a started W3C Activity (create one if the host made none)
	//   "off" → guarantee NO ambient Activity for the rest of the pipeline
	// Sits in front of the app's own pipeline (an IStartupFilter runs before Startup.Configure),
	// so whatever it decides is what the MCP CallTool filters see.
	sealed class TraceControlStartupFilter : IStartupFilter
	{
		public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
		{
			app.Use(async (ctx, nextMiddleware) =>
			{
				var mode = ctx.Request.Headers[TraceHeader].FirstOrDefault();
				if (mode == "off")
				{
					Activity.Current = null;
					await nextMiddleware(ctx);
					return;
				}

				if (mode == "on" && Activity.Current is null)
				{
					using var activity = new Activity("test.request").Start();
					await nextMiddleware(ctx);
					return;
				}

				await nextMiddleware(ctx);
			});
			next(app);
		};
	}
}

public sealed class McpErrorEnvelopeTraceTests : IClassFixture<McpErrorEnvelopeTraceFixture>
{
	readonly McpErrorEnvelopeTraceFixture _fx;

	public McpErrorEnvelopeTraceTests(McpErrorEnvelopeTraceFixture fx) => _fx = fx;

	// A log that does not exist → LogTools.QueryAsync throws InvalidOperationException from the
	// TOOL BODY (a genuine throw with a genuine stack), which is exactly what the filter catches.
	static Dictionary<string, object?> MissingLogArgs() => new()
	{
		["projectKey"] = LogNames.SystemProject,
		["logName"] = "no-such-log-" + Guid.NewGuid().ToString("N")[..8],
		["kql"] = "events | take 1",
	};

	static async Task<JsonElement> FailAsync(McpClient mcp)
	{
		var tool = (await mcp.ListToolsAsync()).First(t => t.Name == "log_query");
		var result = await tool.CallAsync(MissingLogArgs());
		result.IsError.Should().BeTrue("a missing log must surface as an MCP error result");
		var text = result.Content.OfType<TextContentBlock>().First().Text;
		return JsonDocument.Parse(text).RootElement.GetProperty("error");
	}

	async Task<JsonDocument> QueryAsync(string kql)
	{
		var url = $"/api/logs/{LogNames.SystemProject}/{LogNames.SelfLog}/query?q={Uri.EscapeDataString(kql)}";
		using var resp = await _fx.Http.GetAsync(url);
		resp.StatusCode.Should().Be(HttpStatusCode.OK, "self-log query must succeed");
		return JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
	}

	// The SystemLogFlusher is async (2s / 200-batch) — poll until the event lands.
	async Task<JsonElement> WaitForEventAsync(string kql)
	{
		for (var i = 0; i < 400; i++)
		{
			var doc = await QueryAsync(kql);
			var events = doc.RootElement.GetProperty("events");
			if (events.GetArrayLength() > 0)
				return events[0].Clone();
			await Task.Delay(25);
		}
		throw new Xunit.Sdk.XunitException($"the self-log never produced an event for: {kql}");
	}

	async Task<long> ErrorEventCountAsync()
	{
		var doc = await QueryAsync("events | where MessageTemplate contains 'mcp error' | count");
		return doc.RootElement.GetProperty("rows")[0][0].GetInt64();
	}

	// THE test. A failing tool call under a valid Activity must (a) return a trace id and NO
	// stack, and (b) have put the STACK in the self-log's Exception column under that very id.
	[Fact]
	public async Task FailedCall_UnderActivity_ReturnsTraceId_AndTheStackIsInTheSelfLog()
	{
		var error = await FailAsync(_fx.Traced);

		error.GetProperty("type").GetString().Should().Be(nameof(InvalidOperationException));
		error.GetProperty("message").GetString().Should().NotBeNullOrEmpty();
		error.TryGetProperty("detail", out _).Should().BeFalse("a traceable error must not ship the stack to the agent");

		var traceId = error.GetProperty("traceId").GetString();
		traceId.Should().NotBeNullOrEmpty().And.HaveLength(32);
		traceId.Should().NotBe("00000000000000000000000000000000");

		// The lookup the OWNER performs with that token — and the assertion that the stack is
		// really there, in the Exception COLUMN, not merely "logged somewhere".
		var evt = await WaitForEventAsync(
			$"events | where MessageTemplate contains 'mcp error' | where Properties.TraceId == '{traceId}' | take 1");

		evt.GetProperty("Level").GetString().Should().Be("Error");
		var exception = evt.GetProperty("Exception").GetString();
		exception.Should().NotBeNullOrEmpty("the Exception column IS the stack's storage")
			.And.Contain(nameof(InvalidOperationException))
			.And.Contain("   at ", "…and it must carry stack FRAMES, not just the message");
		exception.Should().Contain("LogTools", "the frames must point at the throwing tool body");

		// The failure is attributed, so the owner can find it without knowing the trace id too.
		evt.GetProperty("Properties").GetProperty("Tool").GetString().Should().Contain("log_query");
		evt.GetProperty("Properties").GetProperty("ErrorType").GetString().Should().Contain(nameof(InvalidOperationException));
	}

	// The fallback, driven for real: with NO ambient Activity there is no token to hand out,
	// so the exception itself must ride the response — otherwise the failure would be invisible
	// to the caller AND unaddressable in the log.
	[Fact]
	public async Task FailedCall_WithoutActivity_FallsBackToTheStackInTheResponse()
	{
		var before = await ErrorEventCountAsync();

		var error = await FailAsync(_fx.Untraced);

		error.TryGetProperty("traceId", out _).Should().BeFalse("there is no Activity, so there is no id to look anything up by");
		error.GetProperty("type").GetString().Should().Be(nameof(InvalidOperationException));
		error.GetProperty("message").GetString().Should().NotBeNullOrEmpty();

		var detail = error.GetProperty("detail").GetString();
		detail.Should().NotBeNullOrEmpty()
			.And.Contain(nameof(InvalidOperationException))
			.And.Contain("   at ", "the response is the only carrier left — it must hold the stack");

		// …and the exception is STILL written to the self-log (just without a TraceId to find it
		// by): losing the trace must not lose the record.
		for (var i = 0; i < 400 && await ErrorEventCountAsync() <= before; i++)
			await Task.Delay(25);
		(await ErrorEventCountAsync()).Should().BeGreaterThan(before,
			"an untraced failure is still recorded — only its ADDRESSABILITY is lost");
	}
}
