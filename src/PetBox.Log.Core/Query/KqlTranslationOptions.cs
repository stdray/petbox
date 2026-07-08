namespace PetBox.Log.Core.Query;

// The join-default knob: which kind a bare `join` (no explicit kind=) resolves to. Kusto's own default
// is innerunique (dedup the left side by key); PetBox deliberately defaults to Inner instead (a plain
// ANSI equi-join, no left dedup) per spec kql-join-default-inner. innerunique remains requestable, both
// explicitly (kind=innerunique) and by setting this knob back to InnerUnique.
public enum KqlJoinDefault { Inner, InnerUnique }

// How dcount() is evaluated. Exact (the default) preserves today's behavior EXACTLY — an exact
// COUNT(DISTINCT …) on every backend. Approx requests the backend's approximate distinct-count
// (DuckDB's approx_count_distinct); a backend with no approximate primitive (SQLite) silently
// DEGRADES back to Exact rather than erroring (spec kql-semantic-options: degrade, not error).
public enum KqlDCountMode { Exact, Approx }

// The translation SEAM for the KQL engine: which backend dialect the pipeline compiles against, plus the
// place future semantic-deviation knobs (join null-key semantics, parse RE2, percentile method, make_*
// behavior) will hang off. Most defaults reproduce today's behavior EXACTLY — the SQLite dialect, no
// deviations — so threading it through changes nothing until a knob is set. The ONE deliberate deviation
// is DefaultJoinKind, which defaults to Inner (spec kql-join-default-inner), NOT Kusto's innerunique.
// DCountMode defaults to Exact, which preserves today's exact COUNT(DISTINCT) dcount on every backend.
public sealed class KqlTranslationOptions
{
	// The backend the pipeline compiles to. SQLite is the only live log backend today.
	public KqlDialect Dialect { get; init; } = KqlDialect.Sqlite;

	// The kind a bare `join` (no explicit kind=) resolves to. Defaults to Inner per spec
	// kql-join-default-inner — a DELIBERATE deviation from Kusto's innerunique default. Set to
	// InnerUnique to restore Kusto's Kusto-default left-dedup semantics.
	public KqlJoinDefault DefaultJoinKind { get; init; } = KqlJoinDefault.Inner;

	// How dcount() evaluates. Defaults to Exact (today's exact COUNT(DISTINCT) on every backend). Approx
	// asks for the backend's approximate distinct count (DuckDB approx_count_distinct); SQLite has no
	// approximate primitive and DEGRADES to Exact under Approx (spec kql-semantic-options).
	public KqlDCountMode DCountMode { get; init; } = KqlDCountMode.Exact;

	// The behavior-preserving default, used wherever no explicit options are supplied.
	public static KqlTranslationOptions Default { get; } = new();
}
