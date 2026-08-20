using Microsoft.Extensions.Logging;

namespace PetBox.Core.Observability;

// The self-log "invoker" attribution door (chore background-invoker-not-tagged-in-logs): a
// self-log record produced by a BackgroundService/IHostedService pass carries an explicit
// `Invoker` property, "background:<ServiceName>" — the same "one door every write goes through"
// principle already applied to ApiKeys.CreatedBy (spec access-attribution). Deliberately NOT a
// derivation from EventId ranges or category name on the READING side: a reader must not need to
// memorize which EventId numbers belong to which background pass. The field is stamped by the
// WRITER, at the single call site where each background pass begins, via the standard
// ILogger.BeginScope ambient-scope mechanism (SystemLogger honours IExternalScopeProvider — see
// SystemLoggerProvider). A record produced outside such a scope (e.g. a user request handled by
// RequestLoggingMiddleware) simply has no `Invoker` property — the presence/value of the field is
// itself the distinguishing signal, not a lookup table.
public static class BackgroundInvokerScope
{
	/// <summary>
	/// Opens (and returns, for a `using`) the ambient scope that tags every self-log record
	/// emitted through <paramref name="logger"/> for the scope's lifetime with
	/// <c>Invoker = "background:&lt;serviceName&gt;"</c>. Call this once, at the top of the
	/// method that actually performs one background pass (so a test invoking that same method
	/// directly — bypassing the hosted ExecuteAsync loop — observes the identical attribution).
	/// </summary>
	public static IDisposable? Begin(ILogger logger, string serviceName) =>
		logger.BeginScope(new Dictionary<string, object?>(1, StringComparer.Ordinal)
		{
			["Invoker"] = $"background:{serviceName}",
		});
}
