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
//
// The same rule now covers step 11 (apply) too — full-wire-exit-ignores-step-11. Step 11 is the
// other step that fails WITHOUT aborting the run, so it was the other way a non-zero run could
// still sign off with "done.". `applyCode` is a REQUIRED field rather than an optional one on
// purpose: the whole defect class here is a failure that nobody remembered to pass along.

import { WIRE_EXIT } from "./wire-exit.ts";

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
  /**
   * False when the run ended non-zero (self-smoke failed, or step 11 did) — "done." must never be
   * the trailing line of a failed run.
   */
  readonly printDone: boolean;
};

// The step-11 line, shared by both failure branches so the two never drift apart in wording.
function applyFailureLine(applyCode: number): string {
  return (
    `wire: step 11 (apply — the per-harness agent artifacts) FAILED with exit ${applyCode} ` +
    `(see [11/10] above). The roster this machine just got wired with is NOT fully compiled, so ` +
    `this run does NOT exit 0; re-run \`petbox-wire apply\` to retry just that step.`
  );
}

/**
 * Decide wire's terminal message set. "done." is suppressed by EITHER non-aborting failure:
 * a failed self-smoke (steps 1-9 having completed does not make the run "done" when the last
 * barrier failed) or a non-zero step 11 (the artifacts the run exists to produce are missing).
 * Both branches print to stderr and end on a failure line — never on something that reads like
 * success. `applyCode` is step 11's `performApply` result; `WIRE_EXIT.ok` means it was clean.
 *
 * When BOTH failed, both lines print, chronologically (step 10, then step 11): each names a
 * different thing that is wrong with the machine, and dropping either would under-report the run.
 *
 * The NOTE is deliberately NOT printed on either failure branch. It is the "you are finished,
 * here is the one thing left to do in a new terminal" cue, and this run is not finished — the
 * same reason the smoke branch has always withheld it.
 *
 * The NOTE always prints on a successful run (wire-note-idempotent) — it used to be gated on
 * `envVarPresentInProcess`, which made it appear on a first wire run and silently vanish on a
 * re-run once that same terminal had picked up the persisted env var, so it was unreliable as
 * a checklist cue (a re-run in a fresh terminal, the common case, still needs the reminder).
 * The persisted-env-var check only ever reflected the CURRENT process's environment, not
 * whether agents launched from OTHER terminals would see it — printing unconditionally is both
 * simpler and correct for the thing the NOTE is actually about (new agent processes).
 */
export function finishWireRun(opts: {
  readonly smokeOk: boolean;
  /** Step 11's exit code (`performApply`). `WIRE_EXIT.ok` = the artifacts compiled cleanly. */
  readonly applyCode: number;
  readonly envVar: string;
  readonly envVarPresentInProcess: boolean;
  readonly platform: NodeJS.Platform;
}): FinishOutcome {
  const applyFailed = opts.applyCode !== WIRE_EXIT.ok;
  if (!opts.smokeOk) {
    return {
      printDone: false,
      toStderr: true,
      lines: [
        `wire: self-smoke FAILED (see [10/10] above) — steps 1-9 completed but the wiring is ` +
          `UNVERIFIED. Treat this run as failed, not finished; exit code is non-zero.`,
        ...(applyFailed ? [applyFailureLine(opts.applyCode)] : []),
      ],
    };
  }
  if (applyFailed) {
    return {
      printDone: false,
      toStderr: true,
      lines: [applyFailureLine(opts.applyCode)],
    };
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
