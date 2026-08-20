using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using PetBox.Core.Observability;
using PetBox.Log.Core.SelfLogging;
using MelLogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace PetBox.Tests.Web;

// chore background-invoker-not-tagged-in-logs: a self-log record produced by a
// BackgroundService/IHostedService must carry an explicit "Invoker" property distinguishing it
// from a record produced by ordinary (user-request) logging — WITHOUT the reader needing to know
// per-service EventId ranges. SystemLogger gets this via the standard MEL ISupportExternalScope
// contract (BeginScope/ForEachScope); BackgroundInvokerScope (PetBox.Core.Observability) is the
// one door every background pass opens to assert it. This is the write-boundary unit test —
// SystemLogger constructed directly, exactly like KqlPropertyKeysTests.SystemLogger_* does — for
// the mechanism itself. BackgroundInvokerHostTests covers the same claim end-to-end through a
// real BackgroundService and the queryable self-log.
public sealed class BackgroundInvokerScopeTests
{
	static SystemLogger MakeLogger(out ChannelReader<LogEntryCandidate> reader) =>
		MakeLogger(out reader, out _);

	static SystemLogger MakeLogger(out ChannelReader<LogEntryCandidate> reader, out LoggerExternalScopeProvider scopeProvider)
	{
		var channel = Channel.CreateUnbounded<LogEntryCandidate>();
		reader = channel.Reader;
		// The real host wires this identically: LoggerFactory detects SystemLoggerProvider
		// implements ISupportExternalScope and calls SetScopeProvider with exactly this type.
		scopeProvider = new LoggerExternalScopeProvider();
		return new SystemLogger("PetBox.Test", new SystemLoggerOptions(), channel.Writer, TimeProvider.System, scopeProvider);
	}

	static void LogOnce(SystemLogger logger, string message)
	{
		var state = new List<KeyValuePair<string, object?>> { new("{OriginalFormat}", message) };
		logger.Log(MelLogLevel.Information, default, (IReadOnlyList<KeyValuePair<string, object?>>)state, null, (_, _) => message);
	}

	[Fact]
	public void RecordLoggedInsideBackgroundInvokerScope_CarriesExplicitInvokerProperty()
	{
		var logger = MakeLogger(out var reader);

		using (BackgroundInvokerScope.Begin(logger, "RetentionService"))
			LogOnce(logger, "swept 3 entries");

		reader.TryRead(out var candidate).Should().BeTrue();
		using var doc = JsonDocument.Parse(candidate!.Properties);
		doc.RootElement.GetProperty("Invoker").GetString().Should().Be("background:RetentionService");
	}

	[Fact]
	public void RecordLoggedOutsideAnyScope_HasNoInvokerProperty_DistinguishableFromBackgroundRecord()
	{
		// Stands in for a record produced while handling a user request (same logger, same
		// process, no BackgroundInvokerScope active) — the acceptance criterion: this record must
		// differ from the background-tagged one above, and the difference must be readable from
		// the record itself, not from knowing an EventId range.
		var logger = MakeLogger(out var reader);

		LogOnce(logger, "ordinary request-driven event");

		reader.TryRead(out var candidate).Should().BeTrue();
		using var doc = JsonDocument.Parse(candidate!.Properties);
		doc.RootElement.TryGetProperty("Invoker", out _).Should().BeFalse(
			"a record produced outside a BackgroundInvokerScope must not carry background attribution");
	}

	[Fact]
	public void ScopeDisposal_StopsTaggingRecordsLoggedAfterIt()
	{
		var logger = MakeLogger(out var reader);

		using (BackgroundInvokerScope.Begin(logger, "RetentionService"))
			LogOnce(logger, "inside scope");
		LogOnce(logger, "after scope disposed");

		reader.TryRead(out var first).Should().BeTrue();
		using (var doc = JsonDocument.Parse(first!.Properties))
			doc.RootElement.GetProperty("Invoker").GetString().Should().Be("background:RetentionService");

		reader.TryRead(out var second).Should().BeTrue();
		using (var doc = JsonDocument.Parse(second!.Properties))
			doc.RootElement.TryGetProperty("Invoker", out _).Should().BeFalse();
	}

	[Fact]
	public void DifferentServiceNames_ProduceDistinctInvokerValues()
	{
		var logger = MakeLogger(out var reader);

		using (BackgroundInvokerScope.Begin(logger, "WalCheckpointService"))
			LogOnce(logger, "checkpoint pass");

		reader.TryRead(out var candidate).Should().BeTrue();
		using var doc = JsonDocument.Parse(candidate!.Properties);
		doc.RootElement.GetProperty("Invoker").GetString().Should().Be("background:WalCheckpointService");
	}
}
