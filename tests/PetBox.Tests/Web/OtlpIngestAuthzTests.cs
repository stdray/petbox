using System.Net;
using System.Net.Http.Headers;
using Google.Protobuf;
using LinqToDB;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Proto.Collector.Logs.V1;
using OpenTelemetry.Proto.Collector.Metrics.V1;
using OpenTelemetry.Proto.Collector.Trace.V1;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Log.Core.Data;

namespace PetBox.Tests.Web;

// authz-cleanup-phase2-rest: OtlpEndpoints.IngestLogs/IngestTraces/IngestMetrics
// (POST /v1/{signal}/{projectKey}/{logName}) carried ONLY the "ApiKey" policy — proves some api
// key authenticated, with NO check that the caller's project claim authorizes the route's
// {projectKey}, unlike LogApi.cs's equivalent CLEF/Seq handlers (AuthorizeProject). Any api key
// could inject OTLP logs/traces/metrics into ANY project. Fixed by adding the same
// ProjectScope.Authorizes check (OtlpEndpoints.AuthorizeProject, modelled on LogApi's helper of the
// same name/shape) before any body parsing or log lookup. The by-design AllowAnonymous bare
// self-export routes (/v1/logs, /v1/traces, /v1/metrics — shared-secret X-Seq-ApiKey) are untouched
// and not exercised here. Reuses LogPipelineFixture (shared host, $system/default already seeded),
// mirroring OtlpMetricsEndpointTests' style.
public sealed class OtlpIngestAuthzTests : IClassFixture<LogPipelineFixture>
{
	readonly LogPipelineFixture _fx;
	readonly HttpClient _client;

	public OtlpIngestAuthzTests(LogPipelineFixture fx)
	{
		_fx = fx;
		_client = fx.Client;
	}

	async Task<(string Key, string Project)> SeedProjectKeyAsync(string scopes = "logs:ingest")
	{
		var proj = $"otlpauthz{Guid.NewGuid():N}"[..16];
		var key = $"yb_key_{Guid.NewGuid():N}";
		using var scope = _fx.Factory.Services.CreateScope();
		var db = scope.ServiceProvider.GetRequiredService<PetBoxDb>();
		await db.InsertAsync(new Project { Key = proj, WorkspaceKey = LogNames.SystemProject, Name = proj });
		await db.InsertAsync(new ApiKey { Key = key, ProjectKey = proj, Scopes = scopes, Name = key, CreatedAt = DateTime.UtcNow });
		var store = scope.ServiceProvider.GetRequiredService<ILogStore>();
		if (!await store.ExistsAsync(proj, LogNames.Default))
			await store.CreateAsync(proj, LogNames.Default, null);
		return (key, proj);
	}

	static ByteArrayContent Protobuf(byte[] body)
	{
		var content = new ByteArrayContent(body);
		content.Headers.ContentType = new MediaTypeHeaderValue("application/x-protobuf");
		return content;
	}

	static HttpRequestMessage Req(string path, string apiKey, byte[] body, string? serviceKey = null)
	{
		var req = new HttpRequestMessage(HttpMethod.Post, path) { Content = Protobuf(body) };
		req.Headers.Add("X-Api-Key", apiKey);
		if (serviceKey is not null) req.Headers.Add("X-Service-Key", serviceKey);
		return req;
	}

	// Empty (but well-formed) OTLP envelopes: AuthorizeProject runs before any parsing, so an empty
	// body suffices for the FORBIDDEN-path assertions, and also parses cleanly (0 records, 0 errors)
	// for the success-path assertions — the point under test is the auth gate, not payload shape.
	static readonly byte[] EmptyLogs = new ExportLogsServiceRequest().ToByteArray();
	static readonly byte[] EmptyTraces = new ExportTraceServiceRequest().ToByteArray();
	static readonly byte[] EmptyMetrics = new ExportMetricsServiceRequest().ToByteArray();

	[Fact]
	public async Task IngestLogs_OwnProject_Succeeds()
	{
		var (key, proj) = await SeedProjectKeyAsync();
		using var resp = await _client.SendAsync(
			Req($"/v1/logs/{proj}/default", key, EmptyLogs, serviceKey: "svc"));
		resp.StatusCode.Should().Be(HttpStatusCode.OK, "an api key authorized for its own project must be able to OTLP-ingest logs into it");
	}

	[Fact]
	public async Task IngestLogs_ForeignProject_Returns403()
	{
		var (key, _) = await SeedProjectKeyAsync();
		using var resp = await _client.SendAsync(
			Req("/v1/logs/$system/default", key, EmptyLogs, serviceKey: "svc"));
		resp.StatusCode.Should().Be(HttpStatusCode.Forbidden,
			"an api key authorized only for its own project must not OTLP-ingest logs into a foreign project ($system)");
	}

	[Fact]
	public async Task IngestTraces_OwnProject_Succeeds()
	{
		var (key, proj) = await SeedProjectKeyAsync();
		using var resp = await _client.SendAsync(Req($"/v1/traces/{proj}/default", key, EmptyTraces));
		resp.StatusCode.Should().Be(HttpStatusCode.OK, "an api key authorized for its own project must be able to OTLP-ingest traces into it");
	}

	[Fact]
	public async Task IngestTraces_ForeignProject_Returns403()
	{
		var (key, _) = await SeedProjectKeyAsync();
		using var resp = await _client.SendAsync(Req("/v1/traces/$system/default", key, EmptyTraces));
		resp.StatusCode.Should().Be(HttpStatusCode.Forbidden,
			"an api key authorized only for its own project must not OTLP-ingest traces into a foreign project ($system)");
	}

	[Fact]
	public async Task IngestMetrics_OwnProject_Succeeds()
	{
		var (key, proj) = await SeedProjectKeyAsync();
		using var resp = await _client.SendAsync(Req($"/v1/metrics/{proj}/default", key, EmptyMetrics));
		resp.StatusCode.Should().Be(HttpStatusCode.OK, "an api key authorized for its own project must be able to OTLP-ingest metrics into it");
	}

	[Fact]
	public async Task IngestMetrics_ForeignProject_Returns403()
	{
		var (key, _) = await SeedProjectKeyAsync();
		using var resp = await _client.SendAsync(Req("/v1/metrics/$system/default", key, EmptyMetrics));
		resp.StatusCode.Should().Be(HttpStatusCode.Forbidden,
			"an api key authorized only for its own project must not OTLP-ingest metrics into a foreign project ($system)");
	}
}
