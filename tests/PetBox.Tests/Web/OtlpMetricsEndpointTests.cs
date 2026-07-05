using System.Net;
using System.Net.Http.Headers;
using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Proto.Collector.Metrics.V1;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Metrics.V1;
using OpenTelemetry.Proto.Resource.V1;
using PetBox.Log.Core.Data;

namespace PetBox.Tests.Web;

// End-to-end for the OTLP metrics ingest endpoints (/v1/metrics/{project}/{log} path-based +
// bare /v1/metrics self-export), mirroring the traces routes they were modelled on. Reuses the
// LogPipelineFixture host — which creates $system/default and the $system/petbox self-log — and
// asserts landed points by reading the per-log MetricPoints table straight through ILogStore.
// Unlike CLEF logs (async channel), metric points are BulkCopyAsync'd inside the request, so the
// rows are queryable the moment the 200 returns — no WaitForIngest poll needed.
public sealed class OtlpMetricsEndpointTests : IClassFixture<LogPipelineFixture>
{
	readonly LogPipelineFixture _fx;

	const string ApiKey = "yb_key_system_internal";

	public OtlpMetricsEndpointTests(LogPipelineFixture fx) => _fx = fx;

	// Build an ExportMetricsServiceRequest carrying one Gauge point with a unique metric name so
	// the landed-row assertion pins to THIS test even under the shared per-class host.
	static byte[] EncodeGauge(string metricName, double value)
	{
		var dp = new NumberDataPoint { TimeUnixNano = 1_700_000_000_000_000_000UL, AsDouble = value };
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

	static string UniqueName(string marker) => $"test.metric.{marker}.{Guid.NewGuid():N}";

	static ByteArrayContent Protobuf(byte[] body)
	{
		var content = new ByteArrayContent(body);
		content.Headers.ContentType = new MediaTypeHeaderValue("application/x-protobuf");
		return content;
	}

	int MetricPointCount(string projectKey, string logName, string metricName)
	{
		using var scope = _fx.Factory.Services.CreateScope();
		var store = scope.ServiceProvider.GetRequiredService<ILogStore>();
		using var ctx = store.NewContext(projectKey, logName);
		return ctx.MetricPoints.Count(p => p.MetricName == metricName);
	}

	[Fact]
	public async Task IngestMetrics_ValidBody_Returns200_AndLandsInMetricPoints()
	{
		var name = UniqueName("valid");
		var req = new HttpRequestMessage(HttpMethod.Post, "/v1/metrics/$system/default")
		{
			Content = Protobuf(EncodeGauge(name, 0.42)),
		};
		req.Headers.Add("X-Api-Key", ApiKey);

		using var resp = await _fx.Client.SendAsync(req);
		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		var doc = System.Text.Json.JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
		doc.RootElement.GetProperty("ingested").GetInt32().Should().Be(1);
		doc.RootElement.GetProperty("errors").GetInt32().Should().Be(0);

		MetricPointCount("$system", "default", name).Should().Be(1);
	}

	[Fact]
	public async Task IngestMetrics_MalformedBody_Returns400()
	{
		var req = new HttpRequestMessage(HttpMethod.Post, "/v1/metrics/$system/default")
		{
			Content = Protobuf(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF }),
		};
		req.Headers.Add("X-Api-Key", ApiKey);

		using var resp = await _fx.Client.SendAsync(req);
		resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
		(await resp.Content.ReadAsStringAsync()).Should().Contain("malformed");
	}

	[Fact]
	public async Task IngestMetrics_MissingLog_Returns404()
	{
		var req = new HttpRequestMessage(HttpMethod.Post, "/v1/metrics/$system/no-such-log")
		{
			Content = Protobuf(EncodeGauge(UniqueName("missing"), 1.0)),
		};
		req.Headers.Add("X-Api-Key", ApiKey);

		using var resp = await _fx.Client.SendAsync(req);
		resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
		(await resp.Content.ReadAsStringAsync()).Should().Contain("create it first");
	}

	[Fact]
	public async Task IngestMetrics_WithoutApiKey_Returns401()
	{
		var req = new HttpRequestMessage(HttpMethod.Post, "/v1/metrics/$system/default")
		{
			Content = Protobuf(EncodeGauge(UniqueName("noauth"), 1.0)),
		};

		using var resp = await _fx.Client.SendAsync(req);
		resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
	}

	[Fact]
	public async Task SelfIngestMetrics_ValidKey_Returns200_AndLandsInSelfLog()
	{
		var name = UniqueName("self");
		var req = new HttpRequestMessage(HttpMethod.Post, "/v1/metrics")
		{
			Content = Protobuf(EncodeGauge(name, 3.14)),
		};
		req.Headers.Add("X-Seq-ApiKey", ApiKey);

		using var resp = await _fx.Client.SendAsync(req);
		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		System.Text.Json.JsonDocument.Parse(await resp.Content.ReadAsStringAsync())
			.RootElement.GetProperty("ingested").GetInt32().Should().Be(1);

		MetricPointCount(LogNames.SystemProject, LogNames.SelfLog, name).Should().Be(1);
	}

	[Fact]
	public async Task SelfIngestMetrics_BadKey_Returns401()
	{
		var req = new HttpRequestMessage(HttpMethod.Post, "/v1/metrics")
		{
			Content = Protobuf(EncodeGauge(UniqueName("selfbad"), 1.0)),
		};
		req.Headers.Add("X-Seq-ApiKey", "bad_key");

		using var resp = await _fx.Client.SendAsync(req);
		resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
	}
}
