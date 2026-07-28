using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;

namespace PetBox.Tests;

// Single physical root for every temp file/dir this test host creates, no matter which of the
// ~197 Path.GetTempPath() call sites asks for it (NewTempConnectionString, TestSchema's template
// builder, ad-hoc TempDir helpers with their own arbitrary prefixes — selflog-route, cfg,
// petbox-test, ...). Measured before this fix (test-temp-single-root card): 92 040 top-level
// entries in a real %TEMP%, ~20k of them petbox-* accumulated in a single day of test runs.
//
// No call site is touched — GetTempPath() itself is redirected process-wide via TMP/TEMP/TMPDIR,
// so every caller's own per-call unique subdirectory (still its own GUID leaf: isolation is
// UNCHANGED, see NewTempConnectionString's comment in TestSchema.cs on why a shared physical
// directory would reintroduce the DDL-race flake / suspected Linux SIGABRT) now nests one level
// down, under Root, instead of landing directly in the OS temp folder. That turns "clean up ~20k
// scattered dirs" into "delete one directory," and gives Defender exactly one path to exclude.
//
// [ModuleInitializer], not an xunit AssemblyFixture (TempDirReaper's sweep is the latter — see
// its own comment on why v3 made that sufficient for A sweep): this redirect has to win the race
// against EVERY static field initializer in the assembly that might call Path.GetTempPath(),
// e.g. TestSchema's `Templated(...)` Lazy<string> fields, not merely against test execution. The
// CLR guarantees a module initializer runs before ANY type in its assembly is first touched,
// which is earlier than anything xunit can orchestrate.
public static class TestTempRoot
{
	// The REAL OS temp directory, captured before Init() overwrites TMP/TEMP/TMPDIR for this
	// process. TempDirReaper needs this — not Path.GetTempPath(), which after Init() answers
	// with Root — to find and sweep SIBLING roots abandoned by killed prior runs.
	public static string RealTempPath { get; private set; } = "";

	// This process's single temp root. Unique per PROCESS (pid + guid), not shared across the
	// machine: two concurrent test hosts (a local run alongside CI, two IDE sessions) must never
	// resolve to the same root, since Cleanup() below deletes it WHOLESALE at process exit — if
	// two hosts shared one, one host's exit would delete the other's still-live temp files.
	public static string Root { get; private set; } = "";

	[ModuleInitializer]
	internal static void Init()
	{
		RealTempPath = Path.GetTempPath();
		Root = Path.Combine(RealTempPath, $"petbox-tests-{Environment.ProcessId}-{Guid.NewGuid():N}");
		Directory.CreateDirectory(Root);

		// GetTempPath() on Windows reads TMP then TEMP; on Unix (where this suite also runs —
		// the Linux CI SIGABRT this fix is partly aimed at) it reads TMPDIR. Setting all three
		// (harmless where not consulted) is what covers every GetTempPath() call site without
		// editing any of them.
		Environment.SetEnvironmentVariable("TMP", Root);
		Environment.SetEnvironmentVariable("TEMP", Root);
		Environment.SetEnvironmentVariable("TMPDIR", Root);

		AppDomain.CurrentDomain.ProcessExit += (_, _) => Cleanup();
	}

	static void Cleanup()
	{
		// The run is over — safe to yank every pooled handle process-wide (same reasoning as
		// TestDirs' own ProcessExit hook in TestDirs.cs).
		SqliteConnection.ClearAllPools();
		try { Directory.Delete(Root, recursive: true); }
		catch
		{
			// A locked file (still-flushing WAL, an antivirus scan mid-run) leaves Root
			// non-empty — TempDirReaper's next-run sweep (age-gated) reaps it, same as any
			// other interrupted run leaves its temp dirs for the next run to collect.
		}
	}
}
