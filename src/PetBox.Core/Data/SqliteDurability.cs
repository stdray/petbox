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
// WHY THE TELEMETRY TIER CHOOSES `NORMAL`, AND WHAT IS NOT BEING CLAIMED.
// Under journal_mode=WAL — which LogSchema.Ensure applies, and which is load-bearing here —
// synchronous=NORMAL cannot corrupt the file. The exposure is narrower and specific: a power loss
// or kernel panic can roll back transactions committed since the last WAL checkpoint. A crash of
// the petbox PROCESS loses nothing at all, because the WAL pages are already in the OS page cache.
// So the whole cost of this choice is: some log lines and spans written shortly before a hard
// machine failure may be missing.
// The choice rests on that cost being ~zero, NOT on a measured speedup. THERE ARE NO PRODUCTION
// MEASUREMENTS of what fsync-per-commit costs this tier, and none are claimed here. The test suite
// is known to be fsync-bound (6 % CPU, ~40 average disk queue), but a test host is not a
// production host and that number must not be spent as if it were one. The honest statement is:
// FULL buys the telemetry tier a guarantee nobody would ever use, so it is not worth any price at
// all, measured or not.
// THIS IS ALSO WHY `NORMAL` MUST NOT BE COPIED TO ANOTHER TIER WITHOUT CHECKING ITS JOURNAL MODE.
// Everything above depends on WAL. Under journal_mode=DELETE (config.db still runs there —
// ConfigSchema.Ensure does not call SqlitePragmas.ApplyWal) synchronous=NORMAL risks actual
// CORRUPTION on power loss, not merely lost commits.
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

	// TEST-HOST OVERRIDE, null in every deployed process. Not a tier and not a policy: when set it
	// replaces EVERY tier's value at once, which is only ever appropriate for a host whose data
	// dies with it. The single assignment in the repository is tests/TestDurability.cs, compiled
	// into the test assemblies alone; SqliteDurabilityGuardTests fails the build if one appears
	// under src/, because SqliteDurabilityTests structurally cannot catch that (it nulls this
	// property itself to model a deployed process).
	public static string? Relaxed { get; set; }

	// The value this tier's connections must carry. Total by construction: a tier with no decision
	// throws at the switch rather than falling through to something plausible.
	public static string Synchronous(SqliteTier tier) =>
		Relaxed ?? tier switch
		{
			SqliteTier.Durable => DurableSynchronous,
			SqliteTier.Telemetry => TelemetrySynchronous,
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
	static readonly Action<DbConnection> ApplyDurable = c => ApplyTo(c, SqliteTier.Durable);
	static readonly Action<DbConnection> ApplyTelemetry = c => ApplyTo(c, SqliteTier.Telemetry);

	// Every linq2db context in the repo builds its DataOptions through this, so the hook reaches
	// every connection linq2db opens — including the ones the pool hands back, which carry whatever
	// pragma state their previous user left.
	public static DataOptions WithDurability(this DataOptions options, SqliteTier tier) =>
		options.UseAfterConnectionOpened(tier is SqliteTier.Telemetry ? ApplyTelemetry : ApplyDurable);
}
