using System.Diagnostics;
using ModelContextProtocol.Client;

namespace PetBox.Tests.Support;

// THE way a test opens an MCP session. Every fixture and every ad-hoc reconnect in this project
// goes through here; a bare `McpClient.CreateAsync` in tests/ is a gate failure
// (McpTestClientUsageTests enforces it).
//
// Why one helper instead of 25 call sites: before this, 25 fixtures each spelled out
// `McpClient.CreateAsync(transport, cancellationToken: default)` by hand, so McpClientOptions was
// never passed anywhere and every knob on it was whatever the SDK defaulted to — not a decision,
// an accident repeated 25 times. That shape is what everyone copies from the fixture next door, so
// the shape itself has to carry the options. This is the one place any of them can now be set.
//
// ── What was actually wrong (work item gate-flake-parallel-builds) ──────────────────────────────
//
// Symptom: several agents each run `./build.ps1 -Target Test` from their own worktree at once, and
// whole test CLASSES die in their fixture's InitializeAsync with no assert text at all —
// `ClientTransportClosedException: The transport was closed`, out of McpClient.CreateAsync.
//
// The first theory was the SDK's default 60-second InitializationTimeout. That was measured and
// FALSIFIED: under three competing suite runs plus a saturated 32-core CPU, ~880 sampled handshakes
// ran at a median of 48 ms, p95 3.6 s, slowest SUCCESSFUL 4.6 s — and the flake still reproduced
// twice with InitializationTimeout explicitly raised to 180 s, dying after 7.6 s and 5.5 s. An
// InitializationTimeout expiry also surfaces as `TimeoutException("Initialization timed out")`,
// which is not the exception anyone was seeing.
//
// The real budget is DiscoverProbeTimeout, and the numbers name it exactly: every success landed
// under 5 s, every failure just past it, and the SDK default for that option is 5 s on the nose.
// Per its XML doc, when ProtocolVersion is null (our case) the client first probes the server with
// `server/discover` and falls back to `initialize` when the probe times out. Under contention the
// probe misses that 5 s deadline, and the auto-detect teardown completes the transport's message
// channel instead of leaving it open for the fallback — so the fallback then runs on a closed
// channel and the whole connect dies. The 5 s deadline was never protecting us; it was the trigger.
static class McpTestClient
{
	// Timeout.InfiniteTimeSpan is the value the SDK's own doc names for this: "Use
	// Timeout.InfiniteTimeSpan to disable the separate probe timeout and rely solely on
	// InitializationTimeout."
	//
	// Why disable it outright rather than just raise it. The probe deadline exists for ONE purpose:
	// to notice a server that predates the 2026-07-28 revision and silently drops `server/discover`,
	// so the client can fall back to `initialize`. That case cannot occur here, and we have the
	// measurement to say so — the tests only ever talk to this solution's own in-process server, and
	// if it were dropping the probe then EVERY handshake would sit out the full probe deadline
	// before falling back. The measured median is 48 ms. The server answers. So for this suite the
	// fallback path is unreachable except when the deadline misfires under load, which is precisely
	// the bug: a finite value here is not a safety net, it is the only way in.
	//
	// Does this trade a fast failure for a minute of silence? No — the worst case is unchanged.
	// InitializationTimeout still bounds the ENTIRE connect (the SDK applies it as a linked
	// CancelAfter around the probe AND any fallback), so a wedged server still fails at 60 s, and it
	// now fails as `TimeoutException("Initialization timed out")` instead of the far more confusing
	// `ClientTransportClosedException`. Nothing waits longer than it did; one misleading way to fail
	// early has been removed. The residual risk is the mirror image: if a future upgrade ever does
	// leave the server unable to answer `server/discover`, every MCP fixture fails loudly at the 60 s
	// budget with that message, rather than limping along 5 s at a time.
	public static readonly TimeSpan DiscoverProbeTimeout = Timeout.InfiniteTimeSpan;

	// Deliberately explicit, and with the probe deadline disabled above this is now the ONE budget
	// that bounds a connect — which is what finally makes pinning it worth doing. 60 seconds is
	// unchanged from what the SDK was already applying, so it introduces no behaviour of its own.
	//
	// Raising it was tried and measured (180 s) and changed nothing, because nothing was reaching
	// the budget. Do not raise it without a measurement showing a handshake that actually spends
	// that long: a bigger number buys no reliability here and costs diagnosability, since a wedged
	// connect would then out-last the whole suite (a solo gate run is ~1.5 min, and
	// xunit.runner.json already flags a test as long-running at 30 s).
	public static readonly TimeSpan InitializationTimeout = TimeSpan.FromSeconds(60);

	// Opt-in handshake log: set PETBOX_MCP_HANDSHAKE_LOG to a file path and every connect appends
	// "<elapsed ms> <outcome>". Off by default, and deliberately kept rather than deleted after the
	// investigation — this is the instrument that falsified the first theory and then proved the
	// second, and the next person to touch load behaviour here will want the same distribution
	// rather than a fresh guess. The max over a run is the useful statistic: it says whether the
	// load that breaks things actually occurred, which a green run on its own never shows.
	const string HandshakeLogVariable = "PETBOX_MCP_HANDSHAKE_LOG";

	static readonly object HandshakeLogLock = new();

	// The elapsed time is in the failure message on purpose. This failure arrives with NO assert
	// text — just a fixture that threw — and the single most useful fact for whoever reads that log
	// next is how long the connect actually took. That number is what settled this work item;
	// without it the next agent re-derives everything from scratch, which happened to three of them.
	public static async Task<McpClient> ConnectAsync(
		IClientTransport transport,
		CancellationToken cancellationToken = default)
	{
		var options = new McpClientOptions
		{
			InitializationTimeout = InitializationTimeout,
			DiscoverProbeTimeout = DiscoverProbeTimeout,
		};

		var sw = Stopwatch.StartNew();
		try
		{
			var client = await McpClient.CreateAsync(transport, options, cancellationToken: cancellationToken);
			Record(sw.Elapsed, "ok");
			return client;
		}
		catch (Exception ex)
		{
			Record(sw.Elapsed, "FAIL " + ex.GetType().Name);
			throw new InvalidOperationException(
				$"MCP handshake failed after {sw.Elapsed.TotalSeconds:F1}s "
				+ $"(InitializationTimeout {InitializationTimeout.TotalSeconds:F0}s, DiscoverProbeTimeout disabled): "
				+ $"{ex.Message} A few seconds here, with 'The transport was closed', would be the "
				+ "parallel-load flake this helper exists to close — see work items "
				+ "gate-flake-parallel-builds and mcp-fixture-load-bottleneck — and would mean the probe "
				+ "deadline is back from somewhere, NOT a regression in the code under test. An elapsed "
				+ "at or near the budget is a genuinely wedged handshake and IS worth investigating.",
				ex);
		}
	}

	static void Record(TimeSpan elapsed, string outcome)
	{
		var path = Environment.GetEnvironmentVariable(HandshakeLogVariable);
		if (string.IsNullOrEmpty(path)) return;

		// Best-effort telemetry: a diagnostic that can fail a test run is worse than no diagnostic.
		try
		{
			lock (HandshakeLogLock)
				File.AppendAllText(path, $"{elapsed.TotalMilliseconds:F1} {outcome}" + Environment.NewLine);
		}
		catch (IOException)
		{
		}
		catch (UnauthorizedAccessException)
		{
		}
	}
}
