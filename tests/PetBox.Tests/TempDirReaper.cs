using PetBox.Tests;

// An xunit v3 assembly fixture is constructed once per test assembly, before any test in it
// runs — which is exactly the guarantee this sweep needs. Under xunit v2 there was no such
// mechanism (IAssemblyFixture / ITestPipelineStartup are v3-only, and the community
// Xunit.Extensions.AssemblyFixture package was not referenced), so this used to be a
// [ModuleInitializer]: a CLR-level hook chosen only because it was the one thing certain to fire
// once per assembly regardless of runner. The v3 migration removes that constraint, so the sweep
// is now declared as what it actually is — assembly-level test setup.
[assembly: AssemblyFixture(typeof(TempDirReaper))]

namespace PetBox.Tests;

// Startup sweep for abandoned test-temp ROOTS left by KILLED test runs.
//
// TestTempRoot.Cleanup (see TestTempRoot.cs) deletes its whole per-process root at
// AppDomain.CurrentDomain.ProcessExit — but ProcessExit never fires when the testhost is
// killed, crashes, or the run is cancelled mid-suite (a hung test, a debugger detach, a CI
// job cancel). The interrupted run's root is then abandoned forever: the OS temp cleaner does
// not reliably reclaim these (observed on this machine, pre-single-root: 255k leftover
// petbox-* dirs / 24.3 GB, accumulated purely from interrupted runs — a single SUCCESSFUL run
// only ever leaks ~389 dirs / ~118 MB through the same ProcessExit gap; post-single-root that
// same leak is ONE abandoned root instead of hundreds of scattered leaves). Nothing else ever
// revisits %TEMP% to clean up after a dead process, so the only fix is to sweep at the START
// of the NEXT run.
//
// Two sweep targets, since post-container-dir a per-process root can be abandoned in either
// place depending on which build created it:
//  - TestTempRoot.Container's children — the CURRENT layout. A killed run's root now lives at
//    RealTempPath\petbox-tests\{pid}-{guid}, one level under the fixed container, not directly
//    in RealTempPath.
//  - RealTempPath itself, filtered to "petbox-*" — LEGACY layout, still produced by any worktree
//    on this machine still running pre-container code (this checkout is one of several worktrees
//    active side by side; a sibling worktree without this change keeps dropping bare
//    "petbox-tests-{pid}-{guid}" roots straight into RealTempPath until it too picks up the
//    change). Dropping this half would leave that class of leak permanently unswept, not just
//    temporarily, since nothing else ever revisits RealTempPath either.
//    TestTempRoot.Container ("petbox-tests", no trailing pid/guid) also matches "petbox-*" here
//    and MUST be excluded from this loop, never treated as a leaf: it is the live shared
//    container, and deleting it wholesale (should it ever go quiet past MinAge, e.g. no run
//    starts a fresh root inside it for a day) would take down every concurrent host's still-live
//    root nested inside it, not just one interrupted run.
//
// Both loops sweep off TestTempRoot.RealTempPath / .Container, NOT Path.GetTempPath(): by the
// time this fixture runs, TestTempRoot's [ModuleInitializer] has already redirected
// TMP/TEMP/TMPDIR to point at THIS process's own fresh (empty) root, so Path.GetTempPath() no
// longer answers with the shared OS temp folder where a previous, killed process's root would
// actually be sitting.
public sealed class TempDirReaper
{
	// Never touch a directory younger than this: another, still-running test process may own
	// it. LastWriteTimeUtc is the same signal used to verify this sweep (see the fake-old-dir
	// check in the verification for this change) — a live run's dirs get created and written to
	// continuously, so their top-level LastWriteTimeUtc stays within seconds of "now".
	static readonly TimeSpan MinAge = TimeSpan.FromHours(24);

	// Bound the work so a neglected machine can never turn this into a slow startup: enumerating
	// 255k dirs measured ~2s, and each delete measured ~1ms, so capping deletes at 2000/run bounds
	// the worst case to roughly enumeration (~2s, proportional to how many petbox-* dirs exist at
	// all, stale or not) plus ~2s of deletes — a few seconds, not the minutes a full 255k-dir sweep
	// would cost. Whatever is left past the cap is simply picked up by the next run's sweep.
	const int MaxDeletesPerRun = 2000;

	public TempDirReaper() => Sweep();

	static void Sweep()
	{
		try
		{
			var cutoffUtc = DateTime.UtcNow - MinAge;
			var deleted = 0;

			// Legacy layout: bare petbox-* roots dropped straight into RealTempPath by a
			// sibling worktree still running pre-container code. Container itself also matches
			// "petbox-*" here and is excluded — it is the live shared container, never a leaf.
			foreach (var dir in Directory.EnumerateDirectories(TestTempRoot.RealTempPath, "petbox-*"))
			{
				if (deleted >= MaxDeletesPerRun) return;
				if (string.Equals(dir, TestTempRoot.Container, StringComparison.OrdinalIgnoreCase)) continue;

				if (TryReap(dir, cutoffUtc)) deleted++;
			}

			// Current layout: abandoned per-process roots nested one level under Container.
			if (Directory.Exists(TestTempRoot.Container))
			{
				foreach (var dir in Directory.EnumerateDirectories(TestTempRoot.Container))
				{
					if (deleted >= MaxDeletesPerRun) return;

					if (TryReap(dir, cutoffUtc)) deleted++;
				}
			}
		}
		catch
		{
			// The sweep exists to protect the run, not to gate it — never let it fail the suite.
		}
	}

	// Deletes dir if it is older than cutoffUtc (i.e. abandoned, not a still-live concurrent
	// run's root); returns whether it actually deleted something.
	static bool TryReap(string dir, DateTime cutoffUtc)
	{
		try
		{
			if (Directory.GetLastWriteTimeUtc(dir) >= cutoffUtc) return false; // may still be live
			Directory.Delete(dir, recursive: true);
			return true;
		}
		catch
		{
			// Another process may hold a handle inside it (a concurrent run, or this one racing
			// another reaper sweep) — leave it, a later sweep will retry.
			return false;
		}
	}
}
