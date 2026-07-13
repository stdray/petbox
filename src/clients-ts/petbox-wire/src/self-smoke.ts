// Self-smoke response classification + final-line policy for wire's full-wiring path
// (wiring-one-command / selfsmoke-failure-prints-done).
//
// Kept out of wire.ts (whose main() runs at import time, so decision logic that needs to be
// unit-testable in isolation lives in a side module — same pattern as wire-exit.ts,
// wire-identity.ts, apply-write.ts).
//
// The bug: selfSmoke() set process.exitCode = 1 on failure, but main() kept going to the very
// end of the wiring pipeline and printed "done." regardless — a failed self-smoke was visually
// indistinguishable from a clean wire (the LAST line a human sees was always "done."). This
// module makes the terminal message set depend on the smoke outcome, so a failure IS the last
// line, printed to stderr (red), and "done." never follows it.

/** Pure classification of the self-smoke HTTP round trip — no network, no process state. */
export type SelfSmokeResult = {
  readonly ok: boolean;
  /** Human-facing [10/10] line. Goes to stdout when ok, stderr when not. */
  readonly message: string;
};

/**
 * Classify a self-smoke response. `ok`/`status` mirror fetch's Response; `text` is the already
 * -read body (caller owns the fetch/timeout/network-error handling — those are fetch failures,
 * not response classification, and are handled by the caller before this is ever invoked).
 */
export function classifySelfSmokeResponse(
  respOk: boolean,
  status: number,
  text: string,
): SelfSmokeResult {
  if (!respOk) {
    return { ok: false, message: `[10/10] self-smoke: HTTP ${status} — ${text}` };
  }
  let parsed: any = null;
  try {
    parsed = JSON.parse(text);
  } catch {
    /* keep raw */
  }
  if (typeof parsed?.version === "number") {
    return {
      ok: true,
      message:
        `[10/10] self-smoke: OK — sessionId=${parsed.sessionId}, version=${parsed.version}, ` +
        `messages=${parsed.messageCount}`,
    };
  }
  return {
    ok: false,
    message: `[10/10] self-smoke: server did not return a numeric version — ${text}`,
  };
}

/** What main() prints as its LAST lines, and where (stdout vs stderr). */
export type FinishOutcome = {
  readonly lines: readonly string[];
  /** True → every line goes to console.error (red); false → console.log. */
  readonly toStderr: boolean;
  /** False when self-smoke failed — "done." must never be the trailing line of a failed run. */
  readonly printDone: boolean;
};

/**
 * Decide wire's terminal message set. `smokeOk` false is the ONLY branch that suppresses
 * "done." — steps 1-9 having completed does not make the run "done" when the last barrier
 * (self-smoke) failed.
 */
export function finishWireRun(opts: {
  readonly smokeOk: boolean;
  readonly envVar: string;
  readonly envVarPresentInProcess: boolean;
  readonly platform: NodeJS.Platform;
}): FinishOutcome {
  if (!opts.smokeOk) {
    return {
      printDone: false,
      toStderr: true,
      lines: [
        `wire: self-smoke FAILED (see [10/10] above) — steps 1-9 completed but the wiring is ` +
          `UNVERIFIED. Treat this run as failed, not finished; exit code is non-zero.`,
      ],
    };
  }
  if (opts.envVarPresentInProcess) {
    return { printDone: true, toStderr: false, lines: ["done."] };
  }
  return {
    printDone: true,
    toStderr: false,
    lines: [
      `done. NOTE: start a NEW terminal${opts.platform === "win32" ? "" : " (login shell)"} before ` +
        `launching agents — their MCP configs read ${opts.envVar} from the environment. The kit ` +
        `hooks work immediately (keys.json).`,
    ],
  };
}
