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

// Startup sweep for abandoned %TEMP%\petbox-* directories left by KILLED test runs.
//
// TestDirs.CleanupOrDefer (see TestDirs.cs) defers a locked delete to
// AppDomain.CurrentDomain.ProcessExit — but ProcessExit never fires when the testhost is
// killed, crashes, or the run is cancelled mid-suite (a hung test, a debugger detach, a CI
// job cancel). Every temp dir the interrupted run had created is then abandoned forever: the
// OS temp cleaner does not reliably reclaim these (observed on this machine: 255k leftover
// petbox-* dirs / 24.3 GB, accumulated purely from interrupted runs — a single SUCCESSFUL run
// only ever leaks ~389 dirs / ~118 MB through the same ProcessExit gap). Nothing else ever
// revisits %TEMP% to clean up after a dead process, so the only fix is to sweep at the START
// of the NEXT run.
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

	internal static void Sweep()
	{
		try
		{
			var cutoffUtc = DateTime.UtcNow - MinAge;
			var deleted = 0;
			foreach (var dir in Directory.EnumerateDirectories(Path.GetTempPath(), "petbox-*"))
			{
				if (deleted >= MaxDeletesPerRun) break;

				try
				{
					if (Directory.GetLastWriteTimeUtc(dir) >= cutoffUtc) continue; // may still be live
					Directory.Delete(dir, recursive: true);
					deleted++;
				}
				catch
				{
					// Another process may hold a handle inside it (a concurrent run, or this one
					// racing another reaper sweep) — leave it, a later sweep will retry.
				}
			}
		}
		catch
		{
			// The sweep exists to protect the run, not to gate it — never let it fail the suite.
		}
	}
}
