// SessionStart-hook stdout byte budget — Claude Code specific (work
// startup-banner-truncated-86-percent, spec wiring-startup-budget).
//
// MEASURED, not assumed. Claude Code decides whether to inline a hook's stdout in full, or
// collapse it to a small preview (persisting the rest to a side file the agent only reaches if
// it GUESSES to open it), based on the RAW BYTE LENGTH of that hook's stdout — nothing to do
// with markdown structure, token count, or where inside the text the important content sits.
//
// Method: a throwaway hook printed exactly N bytes (ASCII, byte-marker-stamped every 64 bytes),
// invoked via `claude -p [--resume <session>] --disallowedTools=<all-read-ish-tools> "<probe>"`
// in an isolated scratch project (not this repo), and the GROUND TRUTH was read from the
// session transcript's `attachment.content` field (hook_success.content vs hook_success.stdout
// byte length) — not the model's self-report, which was observed to be unreliable exactly at
// the boundary (it answered "TRUNCATED=yes" once for content the transcript proved was NOT
// truncated). Binary search against claude-code 2.1.209 (2026-07-14):
//   N = 10 000 bytes → hook_success.content.length === hook_success.stdout.length (10000,
//                       byte-identical — full inline, no truncation, no persisted file)
//   N = 10 001 bytes → hook_success.content.length collapses to ~2 374 (a
//                       "<persisted-output>\nOutput too large (N.NKB). Full output saved to:
//                       ...\n\nPreview (first 2KB):\n<first ~2000 bytes>" wrapper); the
//                       marker-sweep confirmed the preview itself cuts at byte ~2000 (last
//                       fully-visible marker at offset 1984, 8 bytes long, then silence).
// So the hard edge is EXACTLY 10 000 bytes of stdout — not ~2048 as the "Preview (first 2KB)"
// wording might suggest (that "2KB" is the preview length once truncation has already
// triggered, not the inline threshold). Confirmed identical on both "SessionStart:startup" and
// "SessionStart:resume" hook names — the gate is on stdout size, not the hook event.
export const HARNESS_INLINE_HARD_LIMIT_BYTES = 10_000;

// The budget sits below the hard edge, but not by so much that it throws away context that
// would physically fit. Measured 2026-07-14 against the live $system server: protocol block
// 6 276 B + canon payload (project + workspace) 2 893 B = 9 169 B. An 8 000 B budget dropped
// the canon every session even though it fit inside the harness's 10 000 B edge — the margin
// cost more than it protected. 9 400 B keeps a 600 B cushion for drift (agent-def notes,
// project name, the resume/compact suffix), and drift no longer hides: assembleSessionBanner
// drops the canon and LOGS loudly (stderr + ~/.petbox/wire.log) instead of letting the harness
// cut mid-sentence. If you need more room, shrink the canon — it is an index of pointers, not
// a document.
export const SESSION_BANNER_BUDGET_BYTES = 9_400;

export type SessionBannerResult = {
  /** What actually goes to stdout — always the mandatory protocol block, plus canon iff it fit. */
  text: string;
  /** Byte length of `text` (what the harness will actually see). */
  totalBytes: number;
  /** Byte length of the mandatory protocol block alone. */
  protocolBytes: number;
  /** Byte length of the canon block that was CONSIDERED, 0 when no canon was available at all. */
  canonBytes: number;
  /** True iff the canon block is present in `text`. */
  canonIncluded: boolean;
  /**
   * True iff assembling protocol+canon together would have exceeded `budgetBytes` — i.e. this
   * session's banner is a degraded case (canon dropped, or — the rarer, worse case — the
   * protocol block alone is already over budget and had nowhere left to cut). Callers should
   * log this loudly: a silent 14KB-into-a-2KB-window truncation is exactly the bug this module
   * exists to prevent from recurring.
   */
  overBudget: boolean;
};

// Assemble the final SessionStart banner from the MANDATORY protocol block (gates, self-intro,
// search-before-rework — must always survive) and the OPTIONAL canon block (best-effort; can be
// large, can grow independently of this kit). If both together fit the budget, ship both. If
// not, drop the canon rather than ship a byte-stream the harness itself will guillotine at an
// arbitrary offset — that arbitrary cut does not respect section boundaries, so an oversized
// canon appended after the protocol block risks slicing INTO the protocol block's own tail
// (this is exactly how rules 4-7 went missing in the original bug: the harness's cut lands
// wherever the cumulative byte count crosses its own line, not at a markdown heading). Protocol
// always wins the budget; canon is what degrades.
export function assembleSessionBanner(
  protocol: string,
  canon: string | null,
  budgetBytes: number = SESSION_BANNER_BUDGET_BYTES,
): SessionBannerResult {
  const protocolBytes = Buffer.byteLength(protocol, "utf8");
  if (!canon) {
    return {
      text: protocol,
      totalBytes: protocolBytes,
      protocolBytes,
      canonBytes: 0,
      canonIncluded: false,
      overBudget: protocolBytes > budgetBytes,
    };
  }
  const canonBytes = Buffer.byteLength(canon, "utf8");
  const combined = `${protocol}\n\n${canon}`;
  const combinedBytes = Buffer.byteLength(combined, "utf8");
  if (combinedBytes <= budgetBytes) {
    return {
      text: combined,
      totalBytes: combinedBytes,
      protocolBytes,
      canonBytes,
      canonIncluded: true,
      overBudget: false,
    };
  }
  return {
    text: protocol,
    totalBytes: protocolBytes,
    protocolBytes,
    canonBytes,
    canonIncluded: false,
    overBudget: true,
  };
}

// Loud-failure channel for a budget overage — per the wire-silent-failures-invisible taxonomy,
// an expected absence (no canon configured, server unreachable) degrades silently by design,
// but a BREAKAGE (content existed and had to be cut to fit) must leave a trace: stderr, so it's
// visible in whatever the caller's environment surfaces, AND an append to ~/.petbox/wire.log,
// so it survives even when stderr is swallowed (Claude Code hook stderr is not always shown to
// the human) and is checkable after the fact (`cat ~/.petbox/wire.log`). Best-effort: a failure
// to write the log file must never affect the hook's own best-effort contract — never throws.
export async function logBudgetOverage(message: string): Promise<void> {
  const line = `${new Date().toISOString()} ${message}`;
  console.error(line);
  try {
    const { appendFile, mkdir } = await import("node:fs/promises");
    const { homedir } = await import("node:os");
    const { join } = await import("node:path");
    const dir = join(homedir(), ".petbox");
    await mkdir(dir, { recursive: true });
    await appendFile(join(dir, "wire.log"), `${line}\n`, "utf8");
  } catch {
    // best-effort: a failed log write must not affect the hook's own best-effort contract
  }
}
