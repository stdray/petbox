using System.Data.Common;
using LinqToDB;

namespace PetBox.Core.Data;

// PRAGMA synchronous — how hard SQLite fsyncs before it calls a commit done.
//
// PRODUCTION NEVER SETS THIS. `Relaxed` is null in every deployed process, and while it is null
// we emit no PRAGMA at all, so every connection keeps SQLite's OWN default (FULL: an fsync per
// commit). Production durability is therefore not merely "unchanged by this file" — this file
// cannot weaken it without somebody assigning the property, and the only assignment anywhere in
// the repository is tests/TestDurability.cs, compiled into the test assemblies alone.
// SqliteDurabilityTests reads `PRAGMA synchronous` back through the production factory in both
// configurations and asserts 2 (FULL) for the production one.
//
// Why the test host relaxes it: the suite is disk-bound, not CPU-bound — 6 % CPU with ~40
// requests queued at the disk on average — and essentially all of that queue is fsync barriers on
// commits to throwaway per-test databases. A test host killed mid-run leaves an unusable temp dir
// no matter what synchronous said, so there is no durability to lose.
//
// The pragma is PER-CONNECTION state: unlike journal_mode=WAL it is NOT written into the file
// header, so setting it once at create would cover exactly one throwaway connection. (That is the
// same trap that made DataDbFactory's max_page_count quota silently not exist — see the note
// there.) Hence the hook below, which fires on every open linq2db performs, plus the explicit
// calls on the raw-connection paths.
public static class SqliteDurability
{
	// null = production: emit nothing, keep SQLite's FULL. Written once, before any test runs.
	public static string? Relaxed { get; set; }

	// The statement a freshly-opened connection should run, or null when there is nothing to do.
	public static string? Statement(string? relaxed) =>
		relaxed is null ? null : $"PRAGMA synchronous = {relaxed};";

	public static void ApplyTo(DbConnection connection)
	{
		var sql = Statement(Relaxed);
		if (sql is null) return;
		using var cmd = connection.CreateCommand();
		cmd.CommandText = sql;
		cmd.ExecuteNonQuery();
	}

	// Every linq2db context in the repo builds its DataOptions through this, so the hook reaches
	// every connection linq2db opens — including the ones the pool hands back, which carry
	// whatever pragma state their previous user left.
	public static DataOptions WithDurability(this DataOptions options) =>
		options.UseAfterConnectionOpened(ApplyTo);
}
