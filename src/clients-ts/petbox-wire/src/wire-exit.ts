// CLI exit taxonomy for petbox-wire apply / usage.
//
// Kept in a tiny module (not wire.ts) so unit tests can import without running wire main().
//
//   0 — full success (every known harness wrote every role)
//   1 — hard failure (invalid definition, unexpected throw)
//   2 — usage / bad arguments (convention)
//   3 — truthfulness: policy blocked some roles/harnesses (partial write possible)

export const WIRE_EXIT = {
  ok: 0,
  hard: 1,
  usage: 2,
  truthfulness: 3,
} as const;

/** Pure classifier for apply's process exit (testable without spawning). */
export function classifyApplyExit(opts: {
  hardError?: boolean;
  hadTruthfulnessBlock?: boolean;
}): number {
  if (opts.hardError) return WIRE_EXIT.hard;
  if (opts.hadTruthfulnessBlock) return WIRE_EXIT.truthfulness;
  return WIRE_EXIT.ok;
}
