// Shared trace sink for the Class-Б half of the silent-failure taxonomy
// (bug: wire-silent-failures-invisible).
//
// Taxonomy (do not re-derive elsewhere — import from here):
//   Class A — legitimate silence. "This project is not registered" in a global hook is an
//     EXPECTED absence (every hook runs in every project on the machine); it stays fully
//     silent, no trace, nothing written here.
//   Class Б — hidden breakage. The caller may still choose to stay quiet on stdout/stderr
//     (a SessionStart hook must keep its exit-0 fast-path — a hook that crashes or blocks is
//     WORSE than one that silently degrades), but it MUST call wireLog() so the event leaves a
//     footprint `doctor` can surface later. An interactive caller (apply, wire, doctor) is free
//     to ALSO print to stderr/stdout — wireLog() is the one step that must never be skipped.
//   Hard errors (e.g. a corrupt roles.json read by `apply`) are not this module's concern —
//     they throw/exit non-zero on the spot. Logging them here too is harmless but not required.
//
// ~/.petbox/wire.log predates this module: session-budget.ts's logBudgetOverage originated it
// for the startup-banner-truncation bug. This module centralizes the path + mkdir + size-trim
// logic that lived inline there so there is exactly one writer, one path, one trim policy —
// session-budget.ts now delegates to appendWireLogRaw() instead of duplicating it.
//
// Never throws: a failure to write this diagnostic is not allowed to become a second failure.
// Plain TS for native node type-stripping: zero deps.

import { appendFileSync, existsSync, readFileSync, mkdirSync, writeFileSync } from "node:fs";
import { homedir } from "node:os";
import { dirname, join } from "node:path";

export function wireLogPath(homeDir: string = homedir()): string {
  return join(homeDir, ".petbox", "wire.log");
}

// Doctor only ever tails the last few dozen lines; keep the file from growing without bound on
// a machine that accumulates the same recurring Class-Б event over months.
const MAX_LOG_LINES = 500;
const TRIM_TO_LINES = 200;

function trimIfOversized(path: string): void {
  try {
    if (!existsSync(path)) return;
    const raw = readFileSync(path, "utf8");
    const lines = raw.split("\n").filter((l) => l.length > 0);
    if (lines.length <= MAX_LOG_LINES) return;
    writeFileSync(path, lines.slice(-TRIM_TO_LINES).join("\n") + "\n", "utf8");
  } catch {
    // best-effort
  }
}

/**
 * Append one already-formatted line verbatim (no timestamp added here — the caller supplies
 * its own, as session-budget.ts's logBudgetOverage already did before this module existed, so
 * its output stays byte-for-byte the same). Best-effort: never throws, never blocks a hook's
 * fast exit-0 path.
 */
export function appendWireLogRaw(line: string, homeDir: string = homedir()): void {
  try {
    const path = wireLogPath(homeDir);
    mkdirSync(dirname(path), { recursive: true });
    trimIfOversized(path);
    appendFileSync(path, `${line}\n`, "utf8");
  } catch {
    // best-effort — this is already the bottom of the diagnostic chain; nowhere left to report.
  }
}

/**
 * Append one Class-Б trace line: `<ISO timestamp> [source] message`.
 * Best-effort — never throws, never blocks a hook's fast exit-0 path.
 */
export function wireLog(source: string, message: string, homeDir: string = homedir()): void {
  appendWireLogRaw(`${new Date().toISOString()} [${source}] ${message}`, homeDir);
}

/**
 * Read the last `n` trace lines for `doctor`. A missing file is NOT a failure — most machines
 * never trip a Class-Б event, so absence just means nothing has ever been logged (doctor stays
 * offline-safe: this never blocks on network, never treats "no log" as an error).
 */
export function readWireLogTail(n: number = 20, homeDir: string = homedir()): string[] {
  try {
    const path = wireLogPath(homeDir);
    if (!existsSync(path)) return [];
    const raw = readFileSync(path, "utf8");
    const lines = raw.split("\n").filter((l) => l.trim().length > 0);
    return lines.slice(-n);
  } catch {
    return [];
  }
}
