namespace PetBox.Log.Core.Query;

// The translation SEAM for the KQL engine: which backend dialect the pipeline compiles against, plus the
// place future semantic-deviation knobs (join null-key semantics, parse RE2, percentile method, make_*
// behavior) will hang off. Defaults reproduce today's behavior EXACTLY — the SQLite dialect, no
// deviations — so threading it through changes nothing until a knob is set. No knobs are exercised yet;
// this is purely the seam, so later waves can flip behavior at ONE well-defined place instead of
// re-plumbing the transformer.
public sealed class KqlTranslationOptions
{
	// The backend the pipeline compiles to. SQLite is the only live log backend today.
	public KqlDialect Dialect { get; init; } = KqlDialect.Sqlite;

	// The behavior-preserving default, used wherever no explicit options are supplied.
	public static KqlTranslationOptions Default { get; } = new();
}
