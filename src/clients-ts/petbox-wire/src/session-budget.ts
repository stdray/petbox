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
import { CANON_PROJECT_SECTION_MARKER, CANON_WORKSPACE_SECTION_MARKER } from "./canon.ts";
import { appendWireLogRaw } from "./wire-log.ts";

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

/**
 * Which canon legs survived the budget (canon-degrade-by-legs-not-all-or-nothing):
 * `both` — the block went in whole; `project-only` — the workspace leg was shed to make room;
 * `none` — no canon at all (either none was offered, or shedding workspace was not enough).
 */
export type CanonLegsIncluded = "both" | "project-only" | "none";

export type SessionBannerResult = {
  /** What actually goes to stdout — always the mandatory protocol block, plus canon iff it fit. */
  text: string;
  /** Byte length of `text` (what the harness will actually see). */
  totalBytes: number;
  /** Byte length of the mandatory protocol block alone. */
  protocolBytes: number;
  /** Byte length of the canon block that was CONSIDERED, 0 when no canon was available at all. */
  canonBytes: number;
  /** True iff ANY canon leg is present in `text`. See `canonLegs` for which ones. */
  canonIncluded: boolean;
  /** Which canon legs actually made it into `text`. */
  canonLegs: CanonLegsIncluded;
  /** Byte length of the canon text actually SHIPPED in `text` (0 when none survived). */
  canonIncludedBytes: number;
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
//
// Canon degrades LEG BY LEG, not all-or-nothing (work canon-degrade-by-legs-not-all-or-nothing).
// The block carries two independent legs — the project canon and the workspace canon — and the
// project one is the more specific, more expensive-to-re-derive of the two, so a single byte of
// overage used to cost the agent BOTH. The ladder is now: whole block → project leg only →
// nothing, re-checking the budget at each rung and stopping at the first that fits. This is
// insurance against any future drift (protocol, canon or wrapper), not a fix for one incident.
export function assembleSessionBanner(
  protocol: string,
  canon: string | null,
  budgetBytes: number = SESSION_BANNER_BUDGET_BYTES,
): SessionBannerResult {
  const protocolBytes = Buffer.byteLength(protocol, "utf8");
  const bare = (canonBytes: number): SessionBannerResult => ({
    text: protocol,
    totalBytes: protocolBytes,
    protocolBytes,
    canonBytes,
    canonIncluded: false,
    canonLegs: "none",
    canonIncludedBytes: 0,
    overBudget: canonBytes > 0 || protocolBytes > budgetBytes,
  });
  if (!canon) return bare(0);

  const canonBytes = Buffer.byteLength(canon, "utf8");
  const withCanon = (kept: string, legs: Exclude<CanonLegsIncluded, "none">): SessionBannerResult => {
    const text = `${protocol}\n\n${kept}`;
    return {
      text,
      totalBytes: Buffer.byteLength(text, "utf8"),
      protocolBytes,
      canonBytes,
      canonIncluded: true,
      canonLegs: legs,
      canonIncludedBytes: Buffer.byteLength(kept, "utf8"),
      overBudget: legs !== "both",
    };
  };

  const whole = withCanon(canon, "both");
  if (whole.totalBytes <= budgetBytes) return whole;

  // Rung two: shed the workspace leg. `null` when there is no workspace leg to shed, or when
  // shedding it would leave nothing but the block's own heading — in either case there is no
  // intermediate rung and the ladder goes straight to the bottom.
  const projectOnly = dropWorkspaceLeg(canon);
  if (projectOnly !== null) {
    const degraded = withCanon(projectOnly, "project-only");
    if (degraded.totalBytes <= budgetBytes) return degraded;
  }
  return bare(canonBytes);
}

/**
 * Cut the workspace leg off a rendered canon block, keeping everything before it. Returns null
 * when the cut is not worth making: no workspace section at all, or nothing but the block
 * heading would survive it (a workspace-only canon).
 *
 * `lastIndexOf` on purpose: the project leg's body is owner-authored markdown that may itself
 * contain a `### Workspace` heading, and the workspace section is always rendered last (canon.ts
 * buildBlock), so the LAST occurrence is the section boundary while the first may not be.
 */
function dropWorkspaceLeg(canon: string): string | null {
  const at = canon.lastIndexOf(CANON_WORKSPACE_SECTION_MARKER);
  if (at < 0) return null;
  const kept = canon.slice(0, at).trimEnd();
  return kept.includes(CANON_PROJECT_SECTION_MARKER) ? kept : null;
}

/**
 * One clause naming WHICH canon legs the budget cost this session — the part the pre-existing
 * overage log could not say (it only reported the fact of a drop, so "canon DROPPED" covered
 * both "we lost the workspace index" and "we lost everything"). Lives here, next to the ladder
 * that produces the outcome, so pull-memory's wire.log line and status/doctor's report cannot
 * word the same state differently.
 */
export function describeCanonDegradation(result: SessionBannerResult): string {
  if (result.canonLegs === "both") return "KEPT (still risks harness truncation)";
  if (result.canonLegs === "project-only") {
    return `WORKSPACE LEG DROPPED, project leg KEPT (${result.canonIncludedBytes}B of ${result.canonBytes}B)`;
  }
  if (result.canonBytes === 0) return "not available at all";
  return `DROPPED ENTIRELY, both legs (${result.canonBytes}B) — shedding the workspace leg alone was not enough`;
}

// Loud-failure channel for a budget overage — per the wire-silent-failures-invisible taxonomy,
// an expected absence (no canon configured, server unreachable) degrades silently by design,
// but a BREAKAGE (content existed and had to be cut to fit) must leave a trace: stderr, so it's
// visible in whatever the caller's environment surfaces, AND an append to ~/.petbox/wire.log,
// so it survives even when stderr is swallowed (Claude Code hook stderr is not always shown to
// the human) and is checkable after the fact (`cat ~/.petbox/wire.log`, or `petbox-wire doctor`,
// which now tails this same file). The append itself is delegated to wire-log.ts — the single
// shared writer for every Class-Б trace in the kit — so path/mkdir/size-trim logic lives in one
// place; this function keeps its own pre-existing (non-namespaced) line format for backward
// compatibility. Best-effort: a failure to write the log file must never affect the hook's own
// best-effort contract — never throws.
export async function logBudgetOverage(message: string): Promise<void> {
  const line = `${new Date().toISOString()} ${message}`;
  console.error(line);
  appendWireLogRaw(line);
}
