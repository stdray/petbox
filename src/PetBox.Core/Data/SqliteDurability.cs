using System.Data.Common;
using LinqToDB;

namespace PetBox.Core.Data;

// What a PetBox SQLite file loses when the MACHINE dies mid-write — the axis `PRAGMA synchronous`
// controls. Every connection factory names one of these explicitly; there is no default overload
// and no unassigned write path, because "nobody chose it" is the state this type exists to end.
public enum SqliteTier
{
	// User data and durable state: core.db, config, tasks, memory, sessions, deploy, and the
	// per-pet data files. A write these tiers acknowledged — an MCP tool that returned success, a
	// deploy that reported itself started — must still be there after a power cut. Losing the tail
	// of one of these is not "a bit of lag", it is petbox having lied about a completed write.
	Durable,

	// Telemetry: logs, spans, metric points. Already lossy by construction (sampled, batched,
	// dropped when a queue fills, and transported over the network) and never read back as an
	// authority for anything — the audit trail that matters lives in the Durable tiers. The tail of
	// this stream is worth less than the guarantee that would protect it.
	Telemetry,

	// Derived: the disk cache. Every byte is reconstructible from the source it was computed from,
	// so losing the tail on a power cut costs a cache MISS, not data — and the file's correct
	// repair has always been to delete it (see CacheDb on why it is not part of core.db).
	//
	// Same VALUE as Telemetry, its own NAME on purpose. This enum's job is to record WHY a database
	// got its durability, and "a cache is telemetry" is simply untrue — a future reader who sees
	// SqliteTier.Telemetry on a cache learns something false and stops trusting the labels. Two
	// reasons landing on one value is the normal case for a reason-carrying enum, not a redundancy
	// to collapse.
	Derived,
}

// PRAGMA synchronous — how hard SQLite fsyncs before it calls a commit done.
//
// PER-CONNECTION, NOT PERSISTENT. Unlike journal_mode=WAL, which is written into the file header
// and survives every reopen, synchronous is connection state and resets on every fresh open. Set
// once on a bootstrap connection it would cover exactly that one connection and silently nothing
// else — no error, no symptom, no failing test. That is not a hypothetical: it is precisely how
// DataDbFactory's max_page_count quota came to not exist in production for months (work
// flaky-quota-exceeded-507). Hence everything below is shaped around one rule: THE VALUE IS
// ASSERTED ON EVERY OPEN. The linq2db hook fires per open (including pooled reuse), and each
// raw-connection path calls ApplyTo itself.
//
// WHY THE DURABLE TIER ASSERTS `FULL` INSTEAD OF LEAVING IT ALONE.
// FULL is also SQLite's own default, so "emit nothing" would usually land on the same value —
// usually is the problem. A pooled handle carries whatever the PREVIOUS user of that handle set,
// and on the per-pet data files the previous user is a pet running arbitrary SQL, which may
// include `PRAGMA synchronous = OFF` (deliberately NOT deny-listed — see DataSqlService). Silence
// also makes the value depend on a compile-time default (SQLITE_DEFAULT_SYNCHRONOUS) that is not
// ours to pin. Asserting the tier's choice at the start of every connection's life makes it true
// by construction rather than true by coincidence, and it is what makes that pooled leak a
// non-issue instead of a hole. Cost is a flag assignment in SQLite's memory — no I/O.
//
// WHY THE NON-DURABLE TIERS CHOOSE `NORMAL`, AND WHAT IS NOT BEING CLAIMED.
// Under journal_mode=WAL — which every tier's *Schema.Ensure applies, and which is load-bearing
// here — synchronous=NORMAL cannot corrupt the file. The exposure is narrower and specific: a power
// loss or kernel panic can roll back transactions committed since the last WAL checkpoint. A crash
// of the petbox PROCESS loses nothing at all, because the WAL pages are already in the OS page
// cache. So the whole cost of this choice is: some log lines, spans and cache entries written
// shortly before a hard machine failure may be missing.
// The choice rests on that cost being ~zero, NOT on a speedup. THERE ARE NO PRODUCTION MEASUREMENTS
// of what fsync-per-commit costs these tiers, and none are claimed here. Nor is the test suite
// evidence for it, in either direction: the "fsync-bound test suite" figures this comment used to
// cite (6 % CPU, ~40 average disk queue) were SUPERSEDED by the measurement on work
// test-dbs-in-memory — ~0.1 disk queue at 15–30 % CPU, and synchronous=OFF made the suite 13 %
// SLOWER rather than faster. Do not reintroduce them as live context, and do not reach for a test-
// host number to justify a production setting. The honest statement stands on its own: FULL buys
// these tiers a guarantee nobody would ever use, so it is not worth any price, measured or not.
// THIS IS ALSO WHY `NORMAL` MUST NOT BE COPIED TO ANOTHER TIER WITHOUT CHECKING ITS JOURNAL MODE.
// Everything above depends on WAL: under journal_mode=DELETE, synchronous=NORMAL risks actual
// CORRUPTION on power loss, not merely lost commits. Every internal tier applies
// SqlitePragmas.ApplyWal from its own *Schema.Ensure (core.db from MigrationRunner.Run), so that
// precondition currently holds everywhere — config was the last exception and no longer is. A NEW
// tier is not covered by that sentence until it applies WAL too.
//
// PATHS DELIBERATELY LEFT UNASSIGNED — the rest of the sweep's ledger. synchronous governs writes
// only, so a read-only connection has nothing to decide: DataDbCatalog.DescribeAsync,
// Pages/ProjectHome/Database, Pages/ProjectHome/Table (which opens Mode=ReadOnly outright) and
// Pages/Admin/ProjectDataDb introspect and page through user data without writing. They inherit
// whatever their handle carries, which cannot affect durability because they never commit.
// ScopedDbFactory.EvictAsync constructs a SqliteConnection purely as a key for ClearPool and never
// opens it.
public static class SqliteDurability
{
	// The decisions, in SQLite's own PRAGMA keywords. Changing either of these changes what a
	// deployed PetBox promises about acknowledged writes — read the two blocks above first.
	const string DurableSynchronous = "FULL";
	const string TelemetrySynchronous = "NORMAL";
	const string DerivedSynchronous = "NORMAL";

	// The value this tier's connections must carry — a PURE FUNCTION OF THE TIER, with no
	// process-wide state behind it. Total by construction: a tier with no decision throws at the
	// switch rather than falling through to something plausible.
	//
	// There used to be a settable `Relaxed` here: a blanket override that replaced every tier's
	// value at once, assigned only by a test-host module initializer to run the suite on
	// synchronous=OFF. It is gone, and both halves of that are deliberate. The suite never needed
	// it to be CORRECT, only (supposedly) to be fast — and the measurement recorded on work
	// test-dbs-in-memory found OFF made the suite 13 % SLOWER, not faster (three runs against two,
	// non-overlapping ranges), because the suite is bound by latency rather than write throughput.
	// With its one writer gone the property had no users at all, and a mutable public static that
	// can silently outrank every durability decision in the product is not worth keeping for a
	// hypothetical one. Its guard test (SqliteDurabilityGuardTests, a source scan forbidding any
	// assignment under src/) went with it: a back door that does not exist needs no guard.
	public static string Synchronous(SqliteTier tier) =>
		tier switch
		{
			SqliteTier.Durable => DurableSynchronous,
			SqliteTier.Telemetry => TelemetrySynchronous,
			SqliteTier.Derived => DerivedSynchronous,
			_ => throw new ArgumentOutOfRangeException(
				nameof(tier), tier, "No synchronous value has been chosen for this SQLite tier."),
		};

	// The statement a freshly-opened connection of this tier runs. Always non-null: unlike the
	// previous shape, production is no longer defined by emitting nothing.
	public static string Statement(SqliteTier tier) => $"PRAGMA synchronous = {Synchronous(tier)};";

	public static void ApplyTo(DbConnection connection, SqliteTier tier)
	{
		using var cmd = connection.CreateCommand();
		cmd.CommandText = Statement(tier);
		cmd.ExecuteNonQuery();
	}

	// Cached per tier so the hook does not allocate a closure on every connection open, and so the
	// options object CoreDbFactory builds ONCE (see the note there — it is load-bearing for memory,
	// not just speed) keeps holding a single shared delegate.
	//
	// Selected by an EXHAUSTIVE switch, never a ternary. This started life as
	// `tier is Telemetry ? ApplyTelemetry : ApplyDurable`, which was correct for exactly two members
	// and silently wrong the moment a third arrived: the cache tier would have been handed
	// ApplyDurable and fsynced on every commit while the enum said otherwise. A switch expression
	// makes that a compile-time hole instead of a quiet one.
	static readonly Action<DbConnection> ApplyDurable = c => ApplyTo(c, SqliteTier.Durable);
	static readonly Action<DbConnection> ApplyTelemetry = c => ApplyTo(c, SqliteTier.Telemetry);
	static readonly Action<DbConnection> ApplyDerived = c => ApplyTo(c, SqliteTier.Derived);

	static Action<DbConnection> HookFor(SqliteTier tier) => tier switch
	{
		SqliteTier.Durable => ApplyDurable,
		SqliteTier.Telemetry => ApplyTelemetry,
		SqliteTier.Derived => ApplyDerived,
		_ => throw new ArgumentOutOfRangeException(
			nameof(tier), tier, "No durability hook has been chosen for this SQLite tier."),
	};

	// Every linq2db context in the repo builds its DataOptions through this, so the hook reaches
	// every connection linq2db opens — including the ones the pool hands back, which carry whatever
	// pragma state their previous user left.
	public static DataOptions WithDurability(this DataOptions options, SqliteTier tier) =>
		options.UseAfterConnectionOpened(HookFor(tier));
}
