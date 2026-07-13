using LinqToDB;
using LinqToDB.Data;
using PetBox.Core.Data;

namespace PetBox.Tests.Web;

// A counting ICoreDbFactory over a real core.db — the instrument that measures what the code under
// test actually does to core.db: how many connections it opens (factory Open() calls) and how many
// SQL statements it executes (linq2db tracing, BeforeExecute — one per command). This is what
// produced the measured "N opens / M statements per request" numbers (db-cache-behind-services): a
// cache assertion in this suite is "the second read cost 0 opens", never a timing guess.
//
// Built OVER options rather than decorating an inner factory's connections because linq2db's
// per-instance OnTraceConnection setter is obsolete (removal in v7) — tracing is attached the
// supported way, via DataOptions.UseTracing. CreateOptions/the clone preserves the shared
// MappingSchema (see CoreDbFactory — a per-connection schema was the ~290 MB prod OOM).
public sealed class CountingCoreDbFactory : ICoreDbFactory
{
	readonly CoreDbFactory _inner;
	int _opens;
	int _statements;

	public CountingCoreDbFactory(string connectionString)
		: this(PetBoxDb.CreateOptions(connectionString)) { }

	public CountingCoreDbFactory(DataOptions<PetBoxDb> options) =>
		_inner = new CoreDbFactory(new DataOptions<PetBoxDb>(options.Options.UseTracing(
			System.Diagnostics.TraceLevel.Info,
			info =>
			{
				if (info.TraceInfoStep == TraceInfoStep.BeforeExecute)
					Interlocked.Increment(ref _statements);
			})));

	public int Opens => Volatile.Read(ref _opens);
	public int Statements => Volatile.Read(ref _statements);

	public PetBoxDb Open()
	{
		Interlocked.Increment(ref _opens);
		return _inner.Open();
	}

	public void Reset()
	{
		Volatile.Write(ref _opens, 0);
		Volatile.Write(ref _statements, 0);
	}
}
