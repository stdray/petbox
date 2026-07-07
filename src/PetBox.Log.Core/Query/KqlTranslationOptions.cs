namespace PetBox.Log.Core.Query;

// The join-default knob: which kind a bare `join` (no explicit kind=) resolves to. Kusto's own default
// is innerunique (dedup the left side by key); PetBox deliberately defaults to Inner instead (a plain
// ANSI equi-join, no left dedup) per spec kql-join-default-inner. innerunique remains requestable, both
// explicitly (kind=innerunique) and by setting this knob back to InnerUnique.
public enum KqlJoinDefault { Inner, InnerUnique }

// The translation SEAM for the KQL engine: which backend dialect the pipeline compiles against, plus the
// place future semantic-deviation knobs (join null-key semantics, parse RE2, percentile method, make_*
// behavior) will hang off. Most defaults reproduce today's behavior EXACTLY — the SQLite dialect, no
// deviations — so threading it through changes nothing until a knob is set. The ONE deliberate deviation
// is DefaultJoinKind, which defaults to Inner (spec kql-join-default-inner), NOT Kusto's innerunique.
public sealed class KqlTranslationOptions
{
	// The backend the pipeline compiles to. SQLite is the only live log backend today.
	public KqlDialect Dialect { get; init; } = KqlDialect.Sqlite;

	// The kind a bare `join` (no explicit kind=) resolves to. Defaults to Inner per spec
	// kql-join-default-inner — a DELIBERATE deviation from Kusto's innerunique default. Set to
	// InnerUnique to restore Kusto's Kusto-default left-dedup semantics.
	public KqlJoinDefault DefaultJoinKind { get; init; } = KqlJoinDefault.Inner;

	// The behavior-preserving default, used wherever no explicit options are supplied.
	public static KqlTranslationOptions Default { get; } = new();
}
