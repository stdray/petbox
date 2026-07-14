using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PetBox.Log.Core.SelfLogging;

namespace PetBox.Tests.Web;

// spec self-telemetry-log-routing: PetBox's own ILogger output is distributed across named
// self-logs by a simple (SourceContext, EventId) -> destination rule, instead of the previous
// fix (self-log-request-noise, df867885) that downgraded the access line to Debug and threw the
// data away. These tests pin the pure routing/split logic (SelfLogRouter, consumed by
// SystemLogFlusher.cs) independent of the ingestion pipeline or the HTTP host.
public sealed class SelfLogRouterTests
{
	static LogEntryCandidate Candidate(string? sourceContext, int? eventId, string message = "m") => new()
	{
		ServiceKey = "test",
		Timestamp = DateTime.UtcNow,
		Level = PetBox.Log.Core.Models.LogLevel.Information,
		Message = message,
		MessageTemplate = message,
		Properties = BuildProperties(sourceContext, eventId),
	};

	static string BuildProperties(string? sourceContext, int? eventId)
	{
		var parts = new List<string>();
		if (sourceContext is not null) parts.Add($"\"SourceContext\":\"{sourceContext}\"");
		if (eventId is not null) parts.Add($"\"EventId\":{eventId}");
		return "{" + string.Join(",", parts) + "}";
	}

	static readonly IReadOnlyList<SelfLogRoute> DefaultRoutes = new SystemLoggerOptions().Routes;

	[Theory]
	[InlineData("PetBox.Web.Logging.RequestLoggingMiddleware", 500)] // Information (2xx/3xx)
	[InlineData("PetBox.Web.Logging.RequestLoggingMiddleware", 501)] // Warning (4xx)
	[InlineData("PetBox.Web.Logging.RequestLoggingMiddleware", 502)] // Error (5xx)
	[InlineData("PetBox.Web.Logging.RequestLoggingMiddleware", 503)] // Error (exception)
	public void Resolve_AccessLineEventId_RoutesToAccess(string category, int eventId)
	{
		var dest = SelfLogRouter.Resolve(Candidate(category, eventId), DefaultRoutes);
		dest.Should().Be(LogNames.AccessLog);
	}

	[Fact]
	public void Resolve_MatchingCategoryIsAPrefix_NotExactEquality()
	{
		// ILogger<RequestLoggingMiddleware>'s category is the type's FULL name
		// ("PetBox.Web.Logging.RequestLoggingMiddleware"), not the bare namespace the default
		// rule names ("PetBox.Web.Logging") — the rule must match as a prefix for the default
		// rule to ever fire in production.
		var dest = SelfLogRouter.Resolve(Candidate("PetBox.Web.Logging.RequestLoggingMiddleware", 500), DefaultRoutes);
		dest.Should().Be(LogNames.AccessLog);
	}

	[Fact]
	public void Resolve_OrdinaryEvent_UnrelatedCategory_FallsBackToSelfLog()
	{
		var dest = SelfLogRouter.Resolve(Candidate("PetBox.Tasks.SomeService", 500), DefaultRoutes);
		dest.Should().Be(LogNames.SelfLog);
	}

	[Fact]
	public void Resolve_MatchingCategory_UnroutedEventId_FallsBackToSelfLog()
	{
		// Same category prefix as the access rule, but an EventId no rule names.
		var dest = SelfLogRouter.Resolve(Candidate("PetBox.Web.Logging.RequestLoggingMiddleware", 999), DefaultRoutes);
		dest.Should().Be(LogNames.SelfLog);
	}

	[Fact]
	public void Resolve_NoEventIdStamped_FallsBackToSelfLog()
	{
		// SystemLogger only stamps EventId when eventId.Id != 0 (SystemLogger.cs:63) — most
		// ILogger calls in the codebase don't pass one.
		var dest = SelfLogRouter.Resolve(Candidate("PetBox.Web.Logging.RequestLoggingMiddleware", null), DefaultRoutes);
		dest.Should().Be(LogNames.SelfLog);
	}

	[Fact]
	public void Resolve_EmptyRouteList_AlwaysSelfLog()
	{
		var dest = SelfLogRouter.Resolve(Candidate("PetBox.Web.Logging.RequestLoggingMiddleware", 500), []);
		dest.Should().Be(LogNames.SelfLog);
	}

	[Fact]
	public void Split_MixedBatch_EveryEventLandsInExactlyOneGroup_NoneLost()
	{
		var batch = new List<LogEntryCandidate>
		{
			Candidate("PetBox.Web.Logging.RequestLoggingMiddleware", 500, "access-1"),
			Candidate("PetBox.Tasks.SomeService", 0, "ordinary-1"),
			Candidate("PetBox.Web.Logging.RequestLoggingMiddleware", 501, "access-2"),
			Candidate("PetBox.Memory.SomeService", 0, "ordinary-2"),
			Candidate("PetBox.Web.Logging.RequestLoggingMiddleware", 503, "access-3"),
			Candidate("PetBox.Tasks.SomeService", 0, "ordinary-3"),
		};

		var groups = SelfLogRouter.Split(batch, DefaultRoutes).ToDictionary(g => g.Key, g => g.ToList());

		groups.Keys.Should().BeEquivalentTo([LogNames.AccessLog, LogNames.SelfLog]);
		groups[LogNames.AccessLog].Select(c => c.Message).Should().BeEquivalentTo(["access-1", "access-2", "access-3"]);
		groups[LogNames.SelfLog].Select(c => c.Message).Should().BeEquivalentTo(["ordinary-1", "ordinary-2", "ordinary-3"]);

		// Zero events lost across the split: every candidate in the input batch appears in
		// exactly one output group, and the counts sum back to the original batch size.
		groups.Values.Sum(g => g.Count).Should().Be(batch.Count);
		groups.Values.SelectMany(g => g).Should().BeEquivalentTo(batch);
	}

	[Fact]
	public void Split_PreservesOrderWithinEachDestination()
	{
		var batch = new List<LogEntryCandidate>
		{
			Candidate("PetBox.Web.Logging.RequestLoggingMiddleware", 500, "a1"),
			Candidate("PetBox.Web.Logging.RequestLoggingMiddleware", 501, "a2"),
			Candidate("PetBox.Tasks.SomeService", 0, "o1"),
			Candidate("PetBox.Web.Logging.RequestLoggingMiddleware", 502, "a3"),
		};

		var groups = SelfLogRouter.Split(batch, DefaultRoutes).ToDictionary(g => g.Key, g => g.ToList());
		groups[LogNames.AccessLog].Select(c => c.Message).Should().ContainInOrder("a1", "a2", "a3");
	}

	// --- SystemLoggerOptions.Routes: the default rule's shape ---

	[Fact]
	public void DefaultRoutes_CoverEventIds500Through503_ForRequestLoggingMiddleware()
	{
		var routes = new SystemLoggerOptions().Routes;
		routes.Select(r => r.EventId).Should().BeEquivalentTo([500, 501, 502, 503]);
		routes.Should().OnlyContain(r => r.Category == "PetBox.Web.Logging");
		routes.Should().OnlyContain(r => r.Destination == LogNames.AccessLog);
	}
}

// SystemLogFlusher-level: proves the flusher itself (not just the pure router function) performs
// the split against a REAL SystemLoggerProvider channel, on a single flush pass, with an isolated
// fake IIngestionPipeline recording what each destination received.
public sealed class SystemLogFlusherTests
{
	sealed class RecordingPipeline : IIngestionPipeline
	{
		public readonly List<(string Project, string Log, IReadOnlyList<LogEntryCandidate> Batch)> Calls = [];
		readonly Lock _gate = new();

		public ValueTask IngestAsync(string projectKey, string logName, IReadOnlyList<LogEntryCandidate> batch, CancellationToken ct)
		{
			lock (_gate) Calls.Add((projectKey, logName, batch));
			return ValueTask.CompletedTask;
		}
	}

	[Fact]
	public async Task ExecuteAsync_MixedBatch_IngestsBothDestinations_ZeroEventsLost()
	{
		var options = new SystemLoggerOptions { BatchSize = 50, QueueCapacity = 100 };
		using var provider = new SystemLoggerProvider(Options.Create(options));
		var pipeline = new RecordingPipeline();
		var flusher = new SystemLogFlusher(pipeline, provider);

		// Everything is written to the provider's channel BEFORE the flusher starts, so its
		// first read drains all four in ONE batch — the exact "mixed batch" shape the spec cares
		// about, not a coincidence of two separate flush passes.
		var accessLogger = provider.CreateLogger("PetBox.Web.Logging.RequestLoggingMiddleware");
		var otherLogger = provider.CreateLogger("PetBox.Tasks.SomeService");
		accessLogger.LogInformation(new EventId(500), "GET /a -> 200 (1 ms)");
		accessLogger.LogWarning(new EventId(501), "GET /b -> 404 (1 ms)");
		otherLogger.LogInformation("ordinary event 1");
		otherLogger.LogError("ordinary event 2");

		await flusher.StartAsync(CancellationToken.None);
		try
		{
			for (var i = 0; i < 400 && pipeline.Calls.Count < 2; i++)
				await Task.Delay(25);
		}
		finally
		{
			await flusher.StopAsync(CancellationToken.None);
		}

		pipeline.Calls.Should().HaveCount(2, "one IngestAsync call per destination in the mixed batch");

		var accessCall = pipeline.Calls.Single(c => c.Log == LogNames.AccessLog);
		var selfCall = pipeline.Calls.Single(c => c.Log == LogNames.SelfLog);
		accessCall.Project.Should().Be(LogNames.SystemProject);
		selfCall.Project.Should().Be(LogNames.SystemProject);

		accessCall.Batch.Select(c => c.Message).Should().BeEquivalentTo(["GET /a -> 200 (1 ms)", "GET /b -> 404 (1 ms)"]);
		selfCall.Batch.Select(c => c.Message).Should().BeEquivalentTo(["ordinary event 1", "ordinary event 2"]);

		// Zero lost: the two destinations' batches sum back to everything written.
		(accessCall.Batch.Count + selfCall.Batch.Count).Should().Be(4);
	}
}
