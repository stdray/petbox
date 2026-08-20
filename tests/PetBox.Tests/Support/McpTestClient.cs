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
// READ THIS BEFORE CHANGING THE TIMEOUT — it does not do what the work item first assumed
// (gate-flake-parallel-builds). The flake being chased is: several agents each run
// `./build.ps1 -Target Test` from their own worktree at once, and whole test CLASSES die in their
// fixture's InitializeAsync with no assert text at all. The working theory was that the SDK's
// default 60-second InitializationTimeout was what gave out under that load. It is not. Measured
// on this repo, under three competing suite runs plus a saturated 32-core CPU:
//
//   ~880 handshakes sampled; median 48 ms, p95 3.6 s, and the slowest SUCCESSFUL one 4.6 s.
//   The failures, reproduced twice with this timeout explicitly raised to 180 s, died after
//   7.6 s and 5.5 s — nowhere near any budget this option sets.
//
// And the exception is not a timeout at all. An InitializationTimeout expiry surfaces as
// `TimeoutException("Initialization timed out")`; what actually arrives is
// `ClientTransportClosedException: The transport was closed`, thrown out of
// McpSessionHandler.GetCompletionDetailsAsync — the transport's message channel COMPLETING
// mid-connect, which no timeout value prevents. See work item mcp-fixture-load-bottleneck for the
// live lead (every success lands under 5 s, every failure just past it, and
// McpClientOptions.DiscoverProbeTimeout defaults to exactly 5 s).
static class McpTestClient
{
	// Pinned, not tuned. 60 seconds is what the SDK was already applying by default; making it
	// explicit changes no behaviour and is not claimed to fix anything. It is here so the value is
	// a visible decision in one file instead of an invisible default in 25, and so that whoever
	// fixes the real cause has one place to put the option.
	//
	// Raising it was tried and measured (180 s) — it changed nothing, because nothing was ever
	// reaching the budget. Do not raise it again without a measurement showing a handshake that
	// actually spends that long: a bigger number buys no reliability here and costs diagnosability,
	// since a genuinely wedged connect would then out-last the whole suite (a solo gate run is
	// ~1.5 min, and xunit.runner.json already flags a test as long-running at 30 s).
	public static readonly TimeSpan InitializationTimeout = TimeSpan.FromSeconds(60);

	// The elapsed time is in the message on purpose, and it is the part of this file that has
	// already paid for itself. The failure it describes arrives with NO assert text — just a
	// fixture that threw — and the single most useful fact for whoever reads that log next is how
	// long the connect actually took. That number is what falsified the original theory in one
	// run; without it the next agent re-derives the whole thing from scratch, which has now
	// happened to three of them.
	public static async Task<McpClient> ConnectAsync(
		IClientTransport transport,
		CancellationToken cancellationToken = default)
	{
		var options = new McpClientOptions { InitializationTimeout = InitializationTimeout };
		var sw = Stopwatch.StartNew();
		try
		{
			return await McpClient.CreateAsync(transport, options, cancellationToken: cancellationToken);
		}
		catch (Exception ex)
		{
			throw new InvalidOperationException(
				$"MCP handshake failed after {sw.Elapsed.TotalSeconds:F1}s "
				+ $"(InitializationTimeout {InitializationTimeout.TotalSeconds:F0}s): {ex.Message} "
				+ "A few seconds here, with 'The transport was closed', is the known parallel-load "
				+ "flake — see work items gate-flake-parallel-builds and mcp-fixture-load-bottleneck — "
				+ "NOT a regression in the code under test. An elapsed at or near the budget would be a "
				+ "genuinely wedged handshake and IS worth investigating.",
				ex);
		}
	}
}
