namespace PetBox.Web;

// The stop budget, in ONE place, because it is split across two systems that cannot see each
// other: the .NET host owns `ShutdownTimeout`, docker owns `stop_grace_period`, and whichever is
// smaller decides. Before this file both were framework defaults — 30 s host vs 10 s docker — so
// the drains below were written against a budget prod never actually granted them: docker sent
// SIGKILL at 10 s while the host still believed it had 30.
//
// THE ORDER OF A STOP (this is what the numbers are budgeting):
//   1. SIGTERM reaches PID1 — ENTRYPOINT is exec-form `./PetBox.Web`, no shell wrapper, so the
//      signal lands on the host itself and ConsoleLifetime turns it into a graceful stop.
//   2. IHostApplicationLifetime.ApplicationStopping fires; Kestrel stops accepting and lets
//      in-flight requests finish.
//   3. Hosted services stop, in REVERSE registration order, all sharing ONE token cancelled after
//      HostShutdownTimeout. This is where the real drains live: ChannelIngestionPipeline (log
//      channel → SQLite), KeyStatFlusher (ApiKey.LastUsedAt marks).
//   4. host.Dispose() → the DI container disposes its singletons. This phase is NOT covered by
//      ShutdownTimeout — it is why DisposalTail exists below.
//
// Raising HostShutdownTimeout means raising MinimumStopGracePeriod with it, or docker goes back to
// deciding the outcome.
public static class ShutdownBudget
{
	// What the host gets for phases 2-3. The framework default (30 s), now written down rather than
	// inherited, so the compose file has something to be checked against.
	public static readonly TimeSpan HostShutdownTimeout = TimeSpan.FromSeconds(30);

	// Phase 4. MemoryUsageRecorder.DisposeAsync waits up to 5 s for its usage-telemetry channel to
	// drain, and it is a plain IAsyncDisposable singleton, not a hosted service — so it runs AFTER
	// every StopAsync has returned and outside the token above. Nothing else in the container has a
	// blocking DisposeAsync, so 5 s is the whole tail.
	public static readonly TimeSpan DisposalTail = TimeSpan.FromSeconds(5);

	// The floor for `stop_grace_period` in deploy/compose.yaml, asserted by ComposeStopGraceTests.
	// The extra 5 s is slack for process teardown itself, not for any drain.
	// Costs nothing in the happy path: docker waits for the process to EXIT and only kills at the
	// deadline, so a clean stop (well under a second in practice) is unaffected by the ceiling.
	public static readonly TimeSpan MinimumStopGracePeriod =
		HostShutdownTimeout + DisposalTail + TimeSpan.FromSeconds(5);
}
