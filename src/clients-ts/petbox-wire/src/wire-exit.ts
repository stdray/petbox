// THE exit module for every petbox-wire CLI: the code taxonomy, and the two — only two —
// sanctioned ways a run may end.
//
// Kept in a tiny module (not wire.ts) so unit tests can import it without running wire main().
//
// ---- exit codes ------------------------------------------------------------
//
//   0 — full success: every requested step ran and every known harness wrote every role
//   1 — hard failure: invalid definition, unexpected throw, a refused clobbering write,
//       a rejected/unreachable API key
//   2 — usage / bad arguments (convention)
//   3 — truthfulness: POLICY blocked some roles/harnesses (partial write possible)
//   4 — incomplete: a requested step did NOT run for a reason the user did not ask for
//       (e.g. the workspace probe that gates the skills refresh failed), so the run is
//       partial even though nothing was refused and no policy fired
//
// Code 4 is NEW (wire-exit-incomplete-is-invisible-to-automation) and is a BREAKING change:
// a path that used to exit 0 while quietly skipping a step now exits 4. That is the point —
// `if apply; then …` could not see a partial run before, because honesty lived only in stdout.
//
// Why 4 and not "reuse 3": 3 means "policy blocked this on purpose". A step that failed for a
// reason outside the user's control is not policy. Merging them would make 3 mean two things,
// which is the exact disease this taxonomy exists to prevent.
//
// What is NOT code 4 — an INTENTIONAL skip stays 0:
//   - `--offline` (the user asked for no network)
//   - an unregistered project directory (nothing to probe against)
// Alarming on a skip the user requested trains people to ignore the alarm.
//
// PRIORITY when several conditions hold at once (classifyApplyExit, locked by wire-exit.test.ts):
//   hard (1)  >  truthfulness (3)  >  incomplete (4)  >  ok (0)
// A refusal to write and a policy block are both statements about what the run REFUSED to do;
// "incomplete" only says a step did not get to run. The stronger claim wins, and the weaker one
// is never lost — it stays in the `summary` JSON both failure branches print.
//
// ---- how a run ends --------------------------------------------------------
//
// NEVER a hard `process.exit()`. It tears the process down while libuv may still be
// closing a socket left by a just-completed fetch, and the caller then observes 127
// (`Assertion failed: !(handle->flags & UV_HANDLE_CLOSING), src\win\async.c`) instead of the
// code the call site named. That defect was fixed one site at a time in doctor, then status,
// then apply, then in six more places at once — which is why the mechanism now lives here and
// a structural guard (wire-process-exit-whitelist.test.ts) forbids new raw exits package-wide.
//
// Use exactly one of:
//   exitWith(code)          — the run is over, right here, and control already is where it
//                             needs to be (top of a command function, end of main()).
//   abortRun(code, message) — the run must end from somewhere DEEP (inside a helper that
//                             cannot simply `return` its way out). Throws; the entrypoint's
//                             `.catch` turns it back into exitWith(code). It aborts control
//                             flow exactly like process.exit() did, so converting a hard exit
//                             to abortRun never lets work continue that used to be cut off.

import { unrefLingeringHandles } from "./hook-drain.ts";

export const WIRE_EXIT = {
  ok: 0,
  hard: 1,
  usage: 2,
  truthfulness: 3,
  incomplete: 4,
} as const;

/**
 * End the run with `code`. The ONLY sanctioned terminal statement in this package.
 *
 * Sets `process.exitCode` (Node exits with it once the loop drains) instead of calling
 * a hard `process.exit()` (which would race the teardown of any socket still closing), then
 * unrefs whatever handles are still mid-close so they cannot hold the process open either.
 *
 * Does NOT abort control flow — the caller must `return` after it. When there is no clean
 * `return` available, use `abortRun` instead.
 */
export function exitWith(code: number): void {
  process.exitCode = code;
  unrefLingeringHandles();
}

/**
 * Thrown by `abortRun`. Carries the exit code the run should end with, so the entrypoint's
 * `.catch` can tell a deliberate abort ("the key was rejected") from a genuine crash.
 */
export class RunAbort extends Error {
  readonly code: number;
  constructor(code: number, message: string) {
    super(message);
    this.name = "RunAbort";
    this.code = code;
  }
}

/**
 * Abort the whole run from arbitrary depth with `code`, reporting `message`.
 *
 * Returns `never` — control does not continue past it, exactly as `process.exit()` did — but
 * it unwinds through the call stack instead of tearing libuv down mid-close. The entrypoint's
 * `.catch` is responsible for printing `message` and calling `exitWith(code)`.
 */
export function abortRun(code: number, message: string): never {
  throw new RunAbort(code, message);
}

/** Pure classifier for apply's process exit (testable without spawning). */
export function classifyApplyExit(opts: {
  hardError?: boolean;
  hadTruthfulnessBlock?: boolean;
  /**
   * A requested step was skipped for a reason the user did not ask for (see WIRE_EXIT.incomplete).
   * Deliberately the LAST thing checked: a refusal or a policy block is the stronger statement
   * about the run and must win the exit code, with the skip still reported in the summary.
   */
  unintendedIncomplete?: boolean;
}): number {
  if (opts.hardError) return WIRE_EXIT.hard;
  if (opts.hadTruthfulnessBlock) return WIRE_EXIT.truthfulness;
  if (opts.unintendedIncomplete) return WIRE_EXIT.incomplete;
  return WIRE_EXIT.ok;
}
