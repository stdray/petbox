using System.Net;
using System.Net.Http.Headers;
using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Proto.Collector.Metrics.V1;
using OpenTelemetry.Proto.Collector.Trace.V1;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Metrics.V1;
using OpenTelemetry.Proto.Resource.V1;
using OpenTelemetry.Proto.Trace.V1;
using PetBox.Log.Core.Data;

namespace PetBox.Tests.Web;

// RETRY IDEMPOTENCY of the OTLP ingest endpoints. A stock OTLP exporter re-sends the identical batch
// on any timeout/5xx; before the fix that replay hit the span PK (→ 500, and the exporter kept
// retrying) and silently doubled every metric point (MetricPoints had no natural key).
//
// The acceptance is literal: POST the SAME batch TWICE → 200 both times, row count unchanged.
// Same harness as OtlpMetricsEndpointTests (LogPipelineFixture host, rows read back through
// ILogStore); spans and metric points are written inside the request, so they are queryable the
// moment the 200 returns.
[Collection(LogPipelineCollectionDef.Name)]
public sealed class OtlpIngestIdempotencyTests
{
	readonly LogPipelineFixture _fx;

	const string ApiKey = "yb_key_system_internal";

	public OtlpIngestIdempotencyTests(LogPipelineFixture fx) => _fx = fx;

	static ByteArrayContent Protobuf(byte[] body)
	{
		var content = new ByteArrayContent(body);
		content.Headers.ContentType = new MediaTypeHeaderValue("application/x-protobuf");
		return content;
	}

	async Task<HttpResponseMessage> PostAsync(string url, byte[] body)
	{
		var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = Protobuf(body) };
		req.Headers.Add("X-Api-Key", ApiKey);
		return await _fx.Client.SendAsync(req);
	}

	// ── traces ─────────────────────────────────────────────────────────────────

	static ByteString Hex(string hex) => ByteString.CopyFrom(Convert.FromHexString(hex));

	// Two spans of one trace — the second one exists to prove a multi-row statement replays too
	// (the OR IGNORE is per row, not per batch).
	static byte[] EncodeTrace(string traceId, params string[] spanIds)
	{
		var scopeSpans = new ScopeSpans { Scope = new InstrumentationScope { Name = "test" } };
		foreach (var spanId in spanIds)
			scopeSpans.Spans.Add(new Span
			{
				TraceId = Hex(traceId),
				SpanId = Hex(spanId),
				Name = "GET /x",
				Kind = Span.Types.SpanKind.Server,
				StartTimeUnixNano = 1_700_000_000_000_000_000UL,
				EndTimeUnixNano = 1_700_000_000_100_000_000UL,
			});

		var resourceSpans = new ResourceSpans { Resource = new Resource() };
		resourceSpans.ScopeSpans.Add(scopeSpans);
		var request = new ExportTraceServiceRequest();
		request.ResourceSpans.Add(resourceSpans);
		return request.ToByteArray();
	}

	static string UniqueHex(int bytes) => Guid.NewGuid().ToString("N")[..(bytes * 2)];

	int SpanCount(string projectKey, string logName, string traceId)
	{
		using var scope = _fx.Factory.Services.CreateScope();
		var store = scope.ServiceProvider.GetRequiredService<ILogStore>();
		using var ctx = store.NewEnsuredContext(projectKey, logName);
		return ctx.Spans.Count(s => s.TraceId == traceId);
	}

	[Fact]
	public async Task IngestTraces_SameBatchTwice_Returns200Twice_AndDoesNotGrow()
	{
		var traceId = UniqueHex(16);
		var body = EncodeTrace(traceId, UniqueHex(8), UniqueHex(8));

		using var first = await PostAsync("/v1/traces/$system/default", body);
		first.StatusCode.Should().Be(HttpStatusCode.OK);
		SpanCount("$system", "default", traceId).Should().Be(2);

		// The retry: byte-identical batch, the exact thing an exporter re-sends after a timeout.
		using var replay = await PostAsync("/v1/traces/$system/default", body);
		replay.StatusCode.Should().Be(HttpStatusCode.OK, "a replayed batch is accepted, not a PK conflict → 500");

		SpanCount("$system", "default", traceId).Should().Be(2, "the replay must not add rows");
	}

	// ── metrics ────────────────────────────────────────────────────────────────

	const ulong Time = 1_700_000_000_000_000_000UL;

	static KeyValue Attr(string key, string value) =>
		new() { Key = key, Value = new AnyValue { StringValue = value } };

	// One gauge point, with its identifying attributes. `value` is a parameter so a test can replay
	// the SAME point with a DIFFERENT value — the natural key deliberately excludes the value.
	static byte[] EncodeGauge(string metricName, double value, params KeyValue[] pointAttrs)
	{
		var dp = new NumberDataPoint { TimeUnixNano = Time, AsDouble = value };
		dp.Attributes.AddRange(pointAttrs);
		var metric = new Metric { Name = metricName, Unit = "1", Gauge = new Gauge() };
		metric.Gauge.DataPoints.Add(dp);

		var scopeMetrics = new ScopeMetrics { Scope = new InstrumentationScope { Name = "test" } };
		scopeMetrics.Metrics.Add(metric);
		var resourceMetrics = new ResourceMetrics { Resource = new Resource() };
		resourceMetrics.ScopeMetrics.Add(scopeMetrics);
		var request = new ExportMetricsServiceRequest();
		request.ResourceMetrics.Add(resourceMetrics);
		return request.ToByteArray();
	}

	static string UniqueMetric(string marker) => $"test.metric.{marker}.{Guid.NewGuid():N}";

	int MetricPointCount(string projectKey, string logName, string metricName)
	{
		using var scope = _fx.Factory.Services.CreateScope();
		var store = scope.ServiceProvider.GetRequiredService<ILogStore>();
		using var ctx = store.NewEnsuredContext(projectKey, logName);
		return ctx.MetricPoints.Count(p => p.MetricName == metricName);
	}

	[Fact]
	public async Task IngestMetrics_SameBatchTwice_Returns200Twice_AndDoesNotGrow()
	{
		var name = UniqueMetric("replay");
		var body = EncodeGauge(name, 0.42, Attr("host", "h1"));

		using var first = await PostAsync("/v1/metrics/$system/default", body);
		first.StatusCode.Should().Be(HttpStatusCode.OK);
		MetricPointCount("$system", "default", name).Should().Be(1);

		using var replay = await PostAsync("/v1/metrics/$system/default", body);
		replay.StatusCode.Should().Be(HttpStatusCode.OK);

		MetricPointCount("$system", "default", name).Should().Be(1, "a replayed point must not double-count");
	}

	// The natural key excludes the VALUE on purpose: an exporter that recomputed a cumulative
	// counter between the send and the retry re-sends the SAME point (same name, attrs, timestamp)
	// with a different number. Keying on the value would let exactly that double-count.
	[Fact]
	public async Task IngestMetrics_SamePointDifferentValue_StillOneRow()
	{
		var name = UniqueMetric("revalue");

		using var first = await PostAsync("/v1/metrics/$system/default", EncodeGauge(name, 0.42, Attr("host", "h1")));
		first.StatusCode.Should().Be(HttpStatusCode.OK);

		using var replay = await PostAsync("/v1/metrics/$system/default", EncodeGauge(name, 0.99, Attr("host", "h1")));
		replay.StatusCode.Should().Be(HttpStatusCode.OK);

		MetricPointCount("$system", "default", name).Should().Be(1);
	}

	// …and the key is not over-broad: the attribute set IS part of the identity, so two points of the
	// same metric at the same instant from different hosts are two distinct points, not a duplicate.
	[Fact]
	public async Task IngestMetrics_SameNameAndTime_DifferentAttributes_AreDistinctPoints()
	{
		var name = UniqueMetric("attrs");

		using var a = await PostAsync("/v1/metrics/$system/default", EncodeGauge(name, 1.0, Attr("host", "h1")));
		a.StatusCode.Should().Be(HttpStatusCode.OK);
		using var b = await PostAsync("/v1/metrics/$system/default", EncodeGauge(name, 1.0, Attr("host", "h2")));
		b.StatusCode.Should().Be(HttpStatusCode.OK);

		MetricPointCount("$system", "default", name).Should().Be(2);
	}
}
