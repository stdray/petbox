using Kusto.Language;

namespace PetBox.Log.Core.Query;

// Response-size caps for the log_query surfaces (REST + MCP). A query with no explicit
// take/top gets DefaultTake; one WITH an explicit limit may go up to MaxTake — so a plain
// `events` can never materialize a whole large table (the OOM vector on the 1GB prod VM),
// while a deliberate `take 5000` still works. Mirrors the reference engine: ADX applies its
// own query truncation (500k rows / 64MB) with a truncated flag, so a default cap does not
// break KQL parity. The caller learns about a cut via the Truncated flag on the result.
public static class KqlLimits
{
	public const int DefaultTake = 1000;
	public const int MaxTake = 100_000;

	public static int EffectiveRowLimit(KustoCode code) =>
		KqlTransformer.HasExplicitRowLimit(code) ? MaxTake : DefaultTake;
}
