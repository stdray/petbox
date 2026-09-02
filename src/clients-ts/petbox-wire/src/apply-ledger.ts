// The single record of what one `apply` pass did — or, under `--dry-run`, would do.
//
// Why this module exists (observation: apply-all-summary-undercounts-writes). `apply --all`'s
// per-project verdict was derived from a counter that only ever incremented in the ROLE write
// loop; skill writes went through a different function that returned nothing but its blocked
// paths. A project where apply wrote twelve skill files and no role files therefore reported
// "unchanged: no changes" — the summary and the log lines above it described two different runs,
// and the preview a `--dry-run` printed was not the preview of the thing that would execute.
//
// The fix is structural, not a second counter: every outcome apply produces is APPENDED here as
// one action, the human log line is RENDERED from that action, and the summary is COMPUTED from
// the same array. There is no path by which a line can be printed without being counted, or
// counted without being printed. The test that pins it (apply-ledger.test.ts, and the end-to-end
// one in apply-all-registry.test.ts) asserts exactly that: the number of "would write" lines in
// the captured output equals the summary's own `writes`.
//
// The old field name `written` is GONE rather than quietly repaired — it counted roles and was
// read as "files". Callers now take `filesWritten` (everything) or `roleFilesWritten` /
// `skillFilesWritten` (the split), so a reader who meant the old meaning gets a compile error
// instead of a wrong number.
//
// Plain TS for native node type-stripping: zero deps.

/** What kind of thing an action touched — only for the wording of the rendered line. */
export type ApplySubject = "role" | "skill" | "legacy" | "orphan" | "gitignore";

export type ApplyActionKind =
  /** A file was (or would be) written: created, updated in place, migrated, or adopted. */
  | "write"
  /** The file already matched byte for byte — nothing to do. */
  | "unchanged"
  /** An owned file was (or would be) deleted. */
  | "remove"
  /** A foreign file sat where we wanted to write: refused, untouched. Drives the exit code. */
  | "refuse"
  /** A foreign file sat where we wanted to DELETE: kept, untouched. Never an error. */
  | "kept"
  /** The project declared the path `petbox: manual`. Left alone on purpose. Never an error. */
  | "manual";

export type ApplyAction = {
  readonly kind: ApplyActionKind;
  readonly subject: ApplySubject;
  readonly path: string;
  /** Short parenthetical for the log line ("adopted", "updated in place — ours", ...). */
  readonly note?: string;
};

export type ApplyLedger = {
  readonly record: (action: ApplyAction) => void;
  readonly actions: readonly ApplyAction[];
};

export function createLedger(): ApplyLedger {
  const actions: ApplyAction[] = [];
  return {
    record: (action: ApplyAction) => {
      actions.push(action);
    },
    get actions() {
      return actions;
    },
  };
}

export type ApplySummary = {
  /** Every "write"/"would write" line, roles and skills and the gitignore block together. */
  readonly filesWritten: number;
  readonly roleFilesWritten: number;
  readonly skillFilesWritten: number;
  readonly unchanged: number;
  readonly removed: number;
  readonly refused: number;
  readonly refusedPaths: readonly string[];
  readonly keptForeign: number;
  readonly declaredManual: number;
};

/** Fold the actions into the numbers the summary line and the per-project verdict both print. */
export function summarize(actions: readonly ApplyAction[]): ApplySummary {
  const writes = actions.filter((a) => a.kind === "write");
  const refused = actions.filter((a) => a.kind === "refuse");
  return {
    filesWritten: writes.length,
    roleFilesWritten: writes.filter((a) => a.subject === "role").length,
    skillFilesWritten: writes.filter((a) => a.subject === "skill").length,
    unchanged: actions.filter((a) => a.kind === "unchanged").length,
    removed: actions.filter((a) => a.kind === "remove").length,
    refused: refused.length,
    refusedPaths: refused.map((a) => a.path),
    keptForeign: actions.filter((a) => a.kind === "kept").length,
    declaredManual: actions.filter((a) => a.kind === "manual").length,
  };
}

export type RenderedLine = {
  readonly text: string;
  /** Refusals go to stderr; everything else is a normal outcome on stdout. */
  readonly stderr: boolean;
};

const SUBJECT_WORD: Readonly<Record<ApplySubject, string>> = {
  role: "",
  skill: "skill ",
  legacy: "legacy ",
  orphan: "",
  gitignore: "",
};

/**
 * The operator-facing line for one action. The VERB is what the counting test keys on: a "write"
 * action always renders "would write " (dry) or "wrote " (real) followed by the path, and nothing
 * else ever renders those two verbs. Keep it that way — the equality between the log and the
 * summary is only as strong as this function's exclusivity.
 */
export function formatAction(label: string, action: ApplyAction, dryRun: boolean): RenderedLine {
  const subject = SUBJECT_WORD[action.subject];
  const note = action.note ? ` (${action.note})` : "";
  switch (action.kind) {
    case "write":
      return { stderr: false, text: `${label}: ${dryRun ? "would write" : "wrote"} ${subject}${action.path}${note}` };
    case "unchanged":
      return { stderr: false, text: `${label}: ${subject}${action.path} unchanged (already matches)` };
    case "remove":
      // Em-dash, not parentheses: a removal's note is the JUSTIFICATION for deleting a file, and
      // it reads as part of the sentence rather than as an aside ("removed X — its role is no
      // longer in definition Y"). Writes keep the parenthetical form, which is what they had.
      return {
        stderr: false,
        text: `${label}: ${dryRun ? "would remove" : "removed"} ${subject}${action.path}${action.note ? ` — ${action.note}` : ""}`,
      };
    case "refuse":
      return {
        stderr: true,
        text:
          `${label}: ${dryRun ? "would refuse" : "REFUSED"} to overwrite ${subject}${action.path} — it exists and ` +
          `does not carry the PetBox origin marker (no \`petbox: managed\` in its frontmatter), so it is a real ` +
          `file, not one apply wrote before. ${dryRun ? "Nothing would be touched." : "Nothing was touched."} ` +
          `Adopt it deliberately with \`--adopt ${action.path}\` if it IS an old PetBox render, or move it aside.`,
      };
    case "kept":
      return {
        stderr: false,
        text:
          `${label}: left ${subject}${action.path} in place${action.note ? ` — ${action.note}` : ""} — ` +
          `no \`petbox: managed\` origin marker, so it is not ours to delete.`,
      };
    case "manual":
      return {
        stderr: false,
        text: `${label}: left ${subject}${action.path} alone — declared \`petbox: manual\`, the project owns this path.`,
      };
  }
}

/**
 * The one-line tail every apply pass prints, computed from the SAME actions the lines came from.
 *
 * Deliberately uses `writes=` / `removals=` rather than echoing the "would write" / "would remove"
 * verbs: the counting test greps the output for those verbs, and a summary that spelled them too
 * would count itself and pass while genuinely disagreeing with the lines above it.
 */
export function formatSummaryLine(label: string, summary: ApplySummary, dryRun: boolean): string {
  return (
    `${label}: summary (${dryRun ? "dry run — nothing touched" : "applied"}) — ` +
    `writes=${summary.filesWritten} (roles=${summary.roleFilesWritten} skills=${summary.skillFilesWritten}) ` +
    `unchanged=${summary.unchanged} removals=${summary.removed} ` +
    `refused=${summary.refused} kept-foreign=${summary.keptForeign} manual=${summary.declaredManual}`
  );
}
