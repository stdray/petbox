// Skill-template rendering + writing — split out of wire.ts (which runs main() at module top
// level and must never be imported by a test — see posix-env.ts's comment on the identical
// problem) so the per-skill substitution and multi-surface fanout stay testable.
//
// `petbox-methodology` is deliberately a THIN, project-agnostic pointer, unlike `petbox` and
// `petbox-agent-factory`: it must never bake in this repo's own methodology rules (preset
// `quartet`, the `spec_plan` gate, `ideaRef`/`specRef`, …), because a wired project may run a
// different preset, a hand-tuned custom instance, or no methodology at all. The live rules for
// THAT project are always fetched at runtime via `tasks_methodology_guide` — never hardcoded at
// wire time (see the template itself for the reasoning).
//
// Origin marker (bug: skill-files-clobber-and-apply-skips): this module used to `writeFileSync`
// unconditionally — no existence check, no marker, no refusal — the exact class `apply-write.ts`
// already closed for `.claude/agents/*.md`. A user's own hand-edited `SKILL.md` was silently
// destroyed on the next `wire`. All three templates now carry `petbox: managed` in their
// frontmatter (origin-marker.ts), and every write goes through `writeArtifact` (apply-write.ts):
// new path → write; marked existing path → overwrite ("own"); unmarked existing path → refused
// ("blocked"), UNLESS it is byte-for-byte identical to what the PRE-marker template would have
// rendered, in which case it is a leftover from before this fix and gets promoted ("migrated") —
// without that carve-out, the very first `wire`/`apply` after this fix would block on every
// skill file the owner already has on disk.
//
// Rename cleanup (bug: wire-skill-cleanup-on-replace): the write loop below also SWEEPS the
// paths a previous delivery used, per `SkillTemplateSpec.legacyDirs`. `cleanupLegacyArtifact`
// existed for exactly this and had one caller — the agent-role rename in wire.ts — while the
// skill pipeline had none, so renaming a delivered skill's directory left its old SKILL.md on
// disk forever: a standing instruction to use a skill that no longer exists, which no later
// `wire`, `apply`, `doctor` or `status` would ever mention again (they all iterate the CURRENT
// PROJECT_SKILLS and never look at a name that left it). Deletion keeps that function's
// contract untouched — a `petbox: managed` file and nothing else — so a foreign file, or one
// the project declared `petbox: manual`, survives the sweep at an old name just as it survives
// a write at the current one.

import { existsSync, mkdirSync, readdirSync, readFileSync, rmdirSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { cleanupLegacyArtifact, writeArtifact, type LegacyCleanupOutcome } from "./apply-write.ts";
import {
  hasPetboxMarker,
  isDeclaredManual,
  PETBOX_DIGEST_KEY,
  PETBOX_MARKER_LINE,
  readArtifactState,
  readDigestMode,
  type ArtifactState,
  type SkillDigestMode,
} from "./origin-marker.ts";

// Skill surfaces wire.ts writes rendered skill bodies into. opencode is intentionally absent: it
// discovers skills through its Claude-compatible path (`.claude/skills/…`), and a second
// same-name copy under `.opencode/skills/` would be a duplicate whose resolution opencode does
// not document. Droid reads its own `.factory/skills/` root (its compat path is
// `.agent/skills/`, NOT `.claude/skills/`), so it needs a dedicated copy.
export const SKILL_SURFACES: string[][] = [
  [".claude", "skills"], // Claude Code (native) + opencode (Claude-compatible discovery)
  [".factory", "skills"], // Factory Droid (native)
];

export type SkillTemplateSpec = {
  // Directory name — used BOTH as the template's subdir under templatesRoot AND as the target
  // skill folder name under every SKILL_SURFACES root (e.g. "petbox-methodology" reads
  // <templatesRoot>/petbox-methodology/SKILL.md and writes .claude/skills/petbox-methodology/SKILL.md).
  dir: string;
  // Whether the template uses the {{WORKSPACE}} placeholder (only `petbox` does, for its UI URL).
  needsWorkspace: boolean;
  // Invocation mode (spec: wire-skill-invocation-mode). "auto" = the agent is told about this
  // skill unprompted, through the salience digest (buildAutoSkillsIndex below); "manual" = it is
  // reachable only by an explicit `skill(name)` call. The registry value here is the DECLARED
  // intent; the template's own frontmatter carries `petbox-digest: <mode>` and is what the
  // digest actually reads on disk — the two are pinned together by a parity test
  // (skill-files.test.ts), the same discipline PROJECT_SKILLS<->templates/ already has.
  digestMode: SkillDigestMode;
  // Directory names this skill was delivered under BEFORE (bug: wire-skill-cleanup-on-replace).
  // After the current path is successfully written, each of these is swept: the kit was the only
  // source of truth for what it put there, so leaving it behind is a standing instruction to
  // read a skill that no longer exists. Removal still requires the `petbox: managed` marker —
  // a foreign or declared-manual file at an old name is reported and kept, never deleted.
  // Empty/absent for a skill that has never moved.
  legacyDirs?: readonly string[];
};

// Every skill wire.ts renders into a freshly-wired project (see writeSkillFiles / wire.ts step 7).
export const PROJECT_SKILLS: SkillTemplateSpec[] = [
  { dir: "petbox", needsWorkspace: true, digestMode: "auto" },
  { dir: "petbox-agent-factory", needsWorkspace: false, digestMode: "manual" },
  { dir: "petbox-methodology", needsWorkspace: false, digestMode: "auto" },
  { dir: "petbox-write-economy", needsWorkspace: false, digestMode: "auto" },
  { dir: "petbox-node-authoring", needsWorkspace: false, digestMode: "auto" },
  { dir: "petbox-analysis-workspace", needsWorkspace: false, digestMode: "manual", legacyDirs: ["analysis-workspace"] },
  { dir: "petbox-factory-run", needsWorkspace: false, digestMode: "manual", legacyDirs: ["factory-run"] },
  { dir: "petbox-card-check", needsWorkspace: false, digestMode: "manual" },
];

// Substitute {{PROJECT}} and {{WORKSPACE}}. Safe to call uniformly even for a template that has
// no {{WORKSPACE}} placeholder — replace() on a pattern with zero matches is a no-op.
export function renderSkillTemplate(tpl: string, project: string, workspace: string): string {
  return tpl.replace(/\{\{PROJECT\}\}/g, project).replace(/\{\{WORKSPACE\}\}/g, workspace);
}

// What the PRE-declaration template used to render, byte-for-byte, for the migration carve-out
// below. The two declaration lines (`petbox: managed` from the clobber fix, `petbox-digest: …`
// from the invocation-mode contract) are the ONLY things those two changes added to the
// templates, so stripping both back out of a freshly rendered body reconstructs the legacy
// output — no separate "old template" copy to keep in sync. Both must be stripped: a file
// materialized by a pre-fix wire carries NEITHER, so leaving the digest line in would make the
// comparison miss and a legitimate migration candidate would be refused instead.
const MARKER_LINE_WITH_EOL = new RegExp(`^${PETBOX_MARKER_LINE}\\r?\\n`, "m");
const DIGEST_LINE_WITH_EOL = new RegExp(`^${PETBOX_DIGEST_KEY}:[ \\t]*\\S+[ \\t]*\\r?\\n`, "m");
function stripMarkerLine(rendered: string): string {
  return rendered.replace(MARKER_LINE_WITH_EOL, "").replace(DIGEST_LINE_WITH_EOL, "");
}

export type SkillWriteOutcome =
  | { readonly kind: "written"; readonly path: string; readonly reason: "new" | "own" | "unchanged" | "migrated" }
  // The project declared this path its own (`petbox: manual`). Left untouched — and this is a
  // LEGAL outcome, not a conflict: it must never reach an exit code (spec:
  // wire-skill-manual-declared-not-error). Distinct from "blocked", which is the undeclared
  // foreign file the operator still has to sort out by hand.
  | { readonly kind: "declared-manual"; readonly path: string }
  | { readonly kind: "blocked"; readonly path: string };

// Write one rendered skill body to `absPath`, same clobber contract as apply-write.ts's
// writeArtifact PLUS the one-time migration carve-out: an existing file with no origin marker
// that is byte-for-byte equal to `legacyRendered` (what the pre-marker template produced) is a
// leftover from before this fix, not a foreign file — it is promoted in place (reason
// "migrated"). Anything else unmarked is a real user file and is refused, untouched, same as
// writeArtifact.
function writeSkillArtifact(
  absPath: string,
  rendered: string,
  legacyRendered: string,
  opts: { readonly dryRun?: boolean } = {},
): SkillWriteOutcome {
  if (existsSync(absPath)) {
    let existing: string | undefined;
    try {
      existing = readFileSync(absPath, "utf8");
    } catch {
      existing = undefined; // unreadable — let writeArtifact's own guard classify it (blocked)
    }
    // Declared manual — the project owns this path. Checked BEFORE the migration carve-out and
    // before writeArtifact, because both of those would otherwise write: nothing here may touch
    // the file, and the caller must not treat the skip as a failure.
    if (existing !== undefined && isDeclaredManual(existing)) {
      return { kind: "declared-manual", path: absPath };
    }
    if (existing !== undefined && !hasPetboxMarker(existing) && existing === legacyRendered) {
      if (!opts.dryRun) {
        mkdirSync(dirname(absPath), { recursive: true });
        writeFileSync(absPath, rendered, "utf8");
      }
      return { kind: "written", path: absPath, reason: "migrated" };
    }
  }
  const outcome = writeArtifact(absPath, rendered, opts);
  return outcome.kind === "blocked"
    ? { kind: "blocked", path: absPath }
    : { kind: "written", path: absPath, reason: outcome.reason };
}

/** One swept pre-rename path. `outcome` is cleanupLegacyArtifact's own verdict, unchanged. */
export type SkillCleanupOutcome = {
  readonly path: string;
  readonly outcome: LegacyCleanupOutcome;
  /** True when the emptied skill directory was removed too (no orphan folder left behind). */
  readonly removedDir: boolean;
};

export type SkillWriteResult = {
  /** One per (skill × surface), in write order. */
  readonly writes: SkillWriteOutcome[];
  /** One per swept pre-rename path — only for specs that declare `legacyDirs`. */
  readonly cleanups: SkillCleanupOutcome[];
};

/**
 * Remove the SKILL.md a previous delivery left at `legacyDir`, and the directory with it once it
 * is empty (bug: wire-skill-cleanup-on-replace — a leftover folder is an orphan even when the
 * only file in it is gone). Deletion goes through cleanupLegacyArtifact, so its contract holds
 * unchanged: ONLY a `petbox: managed` file is ever unlinked. A foreign file, a file the project
 * declared `petbox: manual`, or one we could not even read is reported and left exactly where it
 * is. The directory is removed only when it is empty, which means a legacy skill folder holding
 * anything else the kit did not write (a `references/`, the project's own notes) survives whole.
 */
function cleanupLegacySkillDir(
  dir: string,
  surface: string[],
  legacyDir: string,
  opts: { readonly dryRun?: boolean } = {},
): SkillCleanupOutcome {
  const legacySkillDir = join(dir, ...surface, legacyDir);
  const legacyPath = join(legacySkillDir, "SKILL.md");
  const outcome = cleanupLegacyArtifact(legacyPath, opts);
  let removedDir = false;
  if (outcome === "removed" && !opts.dryRun) {
    try {
      rmdirSync(legacySkillDir); // throws ENOTEMPTY when anything else lives there — then keep it
      removedDir = true;
    } catch {
      // not empty, or already gone — either way there is nothing of ours left to remove
    }
  }
  return { path: legacyPath, outcome, removedDir };
}

// Render every `specs` entry from templatesRoot and write it into every SKILL_SURFACES root
// under dir. Returns one write outcome per (skill × surface), in write order, for the caller's
// log lines — a "blocked" outcome means a real, non-PetBox file already sat at that path and was
// left byte-for-byte untouched, and "declared-manual" means the project owns that path (see
// writeSkillArtifact above) — plus one cleanup outcome per swept pre-rename path.
//
// `specs` defaults to PROJECT_SKILLS and exists so the rename/cleanup behaviour can be exercised
// against a fixture registry: the delivered set has no legacy names of its own yet (the renames
// land in petbox-skill-naming), and a mechanism that DELETES FILES must not go untested until
// the day its first real caller appears.
export function writeSkillFiles(
  dir: string,
  templatesRoot: string,
  project: string,
  workspace: string,
  specs: readonly SkillTemplateSpec[] = PROJECT_SKILLS,
  opts: { readonly dryRun?: boolean } = {},
): SkillWriteResult {
  const writes: SkillWriteOutcome[] = [];
  const cleanups: SkillCleanupOutcome[] = [];
  for (const spec of specs) {
    const tpl = readFileSync(join(templatesRoot, spec.dir, "SKILL.md"), "utf8");
    const rendered = renderSkillTemplate(tpl, project, workspace);
    const legacyRendered = stripMarkerLine(rendered);
    for (const surface of SKILL_SURFACES) {
      const skillPath = join(dir, ...surface, spec.dir, "SKILL.md");
      const outcome = writeSkillArtifact(skillPath, rendered, legacyRendered, opts);
      writes.push(outcome);
      // Sweep the pre-rename copies ONLY after the replacement actually landed — never orphan a
      // skill by deleting the old file when the new one could not be written (identical rule to
      // the agent-role rename cleanup in wire.ts, which this pipeline had no equivalent of).
      // "blocked" and "declared-manual" are both non-writes, so neither triggers a sweep. In a
      // dry run the "write" was only simulated, but the SAME would-sweep preview still applies —
      // opts.dryRun flows into cleanupLegacySkillDir below, same as the write above.
      if (outcome.kind !== "written") continue;
      for (const legacyDir of spec.legacyDirs ?? []) {
        if (legacyDir === spec.dir) continue;
        cleanups.push(cleanupLegacySkillDir(dir, surface, legacyDir, opts));
      }
    }
  }
  return { writes, cleanups };
}

// ---- template-drift comparison (bugs: skill-files-clobber-and-apply-skips [item 3],
// builtin-definition-drifts-no-catchup [item 3]) -----------------------------------------------
//
// Both bugs' bodies named the SAME remaining gap: `status` grew the "is this skill file
// materialized, and does it still match the current template" comparison, but `doctor` never
// looked at skills at all. This is the ONE place that comparison lives — `status.ts` and
// `wire.ts`'s `runDoctor` both call `checkSkillFile`/`formatSkillFile`/`buildSkillReports`
// instead of each re-deriving it. `ArtifactState`/`readArtifactState` live in origin-marker.ts
// (not skill-specific — status.ts's per-role lines use the same classifier).

export type SkillFileReport = {
  readonly path: string;
  readonly state: ArtifactState;
  /** "unknown" when the expected render could not be computed (workspace unresolved, offline). */
  readonly matchesTemplate: boolean | "unknown";
};

/** Compare one materialized path against `rendered` (undefined when the expected render is
 * unavailable — offline, or a workspace-needing template with no probed workspace). */
export function checkSkillFile(absPath: string, rendered: string | undefined): SkillFileReport {
  const state = readArtifactState(absPath);
  if (state === "absent") return { path: absPath, state, matchesTemplate: false };
  // Declared manual: the kit does not render this path, so "does it match the template" is not a
  // question that has an answer — never a drift report, never a foreign report.
  if (state === "manual") return { path: absPath, state, matchesTemplate: "unknown" };
  if (rendered === undefined) return { path: absPath, state, matchesTemplate: "unknown" };
  if (state === "foreign") return { path: absPath, state, matchesTemplate: false };
  let content: string;
  try {
    content = readFileSync(absPath, "utf8");
  } catch {
    return { path: absPath, state, matchesTemplate: "unknown" };
  }
  return { path: absPath, state, matchesTemplate: content === rendered };
}

/** Human-readable line for one report — distinguishes a foreign (BLOCKED) file, whose remedy is
 * "sort it out yourself" (never touched, never diffed), from an owned file that has DRIFTED from
 * the current template, whose remedy is "re-run apply/wire". These are different defects and
 * every caller (status, doctor) must say so distinctly, not fold them into one "mismatch". */
export function formatSkillFile(report: SkillFileReport): string {
  const base =
    report.state === "absent"
      ? "not materialized"
      : report.state === "foreign"
        ? "BLOCKED — a foreign (non-PetBox) file sits here"
        : report.state === "manual"
          ? "declared manual (`petbox: manual`) — the project owns this path"
          : "materialized (ours)";
  const match =
    report.state === "manual"
      ? " — left alone on purpose, not compared"
      : report.matchesTemplate === "unknown"
        ? " — template match unknown (workspace not resolved; run online to verify)"
        : report.matchesTemplate
        ? " — matches the current template"
        : report.state === "ours"
          ? " — DRIFTED from the current template (re-run apply/wire to refresh)"
          : "";
  return `${report.path}: ${base}${match}`;
}

// One report per (PROJECT_SKILLS spec x SKILL_SURFACES surface) under `root`, comparing each
// materialized file against what the CURRENT template renders for `project`/`workspace`.
// `workspace` undefined means unresolved (offline, unregistered project, or a failed probe) — a
// spec needing {{WORKSPACE}} (only `petbox`) then gets `matchesTemplate: "unknown"` rather than a
// false mismatch; a spec that doesn't need it still renders and compares normally, same as
// status.ts always did. Shared by `status` (pillar 4) and `doctor` (skill-drift check).
export function buildSkillReports(
  root: string,
  templatesRoot: string,
  project: string,
  workspace: string | undefined,
): SkillFileReport[] {
  const reports: SkillFileReport[] = [];
  for (const spec of PROJECT_SKILLS) {
    let rendered: string | undefined;
    if (workspace !== undefined || !spec.needsWorkspace) {
      try {
        const tpl = readFileSync(join(templatesRoot, spec.dir, "SKILL.md"), "utf8");
        rendered = renderSkillTemplate(tpl, project, workspace ?? "");
      } catch {
        rendered = undefined;
      }
    }
    for (const surface of SKILL_SURFACES) {
      const absPath = join(root, ...surface, spec.dir, "SKILL.md");
      reports.push(checkSkillFile(absPath, rendered));
    }
  }
  return reports;
}

// ---- workspace probe (dedup: status.ts and wire.ts each carried their own ~12-line copy of the
// SAME GET /api/auth/validate probe for the `petbox` skill template's {{WORKSPACE}} placeholder —
// the registry never stores workspace, so every caller that renders or compares that template
// needs to ask the server) --------------------------------------------------------------------

export type WorkspaceProbeResult =
  | { readonly ok: true; readonly workspace: string }
  | { readonly ok: false; readonly reason: "network" }
  | { readonly ok: false; readonly reason: "forbidden" }
  // The server DID answer — a live fact, never "unreachable" (bug:
  // probe-collapses-http-errors-into-network). `status` is always carried so a caller can name
  // the code; `retryAfterSeconds` is populated best-effort when the error body carries one (this
  // is exactly PetBox's own 503 deploy_in_progress shape — see the module doc comment below —
  // but the field is read generically, not gated on status === 503, in case another endpoint ever
  // reuses the same convention).
  | { readonly ok: false; readonly reason: "http-error"; readonly status: number; readonly retryAfterSeconds?: number }
  | { readonly ok: false; readonly reason: "parse-error" }
  | { readonly ok: false; readonly reason: "no-workspace-field" };

/**
 * Resolve the live workspace for a project. Distinguishes WHY it failed
 * (wire-silent-failures-invisible / probe-collapses-http-errors-into-network) instead of folding
 * every non-2xx response into "network": that used to claim the server was unreachable even when
 * it had just answered with e.g. a 500 or a 503 — a direct lie the caller then repeated verbatim
 * ("could not reach ... (network/timeout)"). The taxonomy now separates:
 *   - "network"  — fetch itself threw (offline/timeout/abort) — the only case "could not reach"
 *                  is honest;
 *   - "forbidden" — 401/403, a scope problem, not connectivity;
 *   - "http-error" — any OTHER non-2xx status; the server responded, full stop. Carries `status`
 *                  so the caller can name it, and best-effort `retryAfterSeconds` for a body like
 *                  PetBox's own 503 deploy-in-progress shape
 *                  (`{"error":"service_unavailable","reason":"deploy_in_progress","retryAfterSeconds":60}`,
 *                  with a matching `Retry-After` header) — that state is self-recovering during a
 *                  redeploy, not a defect to chase, and callers should say so rather than send the
 *                  operator off to debug their network;
 *   - "parse-error" — 2xx but the body did not parse as JSON;
 *   - "no-workspace-field" — 2xx, parsed, but no usable `workspace`/`Workspace` string (older
 *                  server).
 * A caller that only needs a yes/no can still check `.ok`.
 */
export async function probeWorkspace(baseUrl: string, apiKey: string, timeoutMs = 8000): Promise<WorkspaceProbeResult> {
  const ctrl = new AbortController();
  const timer = setTimeout(() => ctrl.abort(), timeoutMs);
  try {
    let resp: Response;
    try {
      resp = await fetch(`${baseUrl}/api/auth/validate`, {
        method: "GET",
        // Connection: close so this socket doesn't linger keep-alive after the response — apply
        // /doctor/status are all short-lived CLI processes, and a kept-alive socket is a libuv
        // handle that either stalls natural process exit or races a forced process.exit() against
        // the handle's own close teardown (same crash class canon.ts's fetchCanon documents; the
        // original two copies of this probe used the bare AbortSignal.timeout() shorthand
        // instead, which leaves the exact same handle behind — reproduced empirically as a
        // `UV_HANDLE_CLOSING` assertion crash on Windows the first time a test exercised doctor's
        // new skill-drift check against a real server).
        headers: { "X-Api-Key": apiKey, Connection: "close" },
        signal: ctrl.signal,
      });
    } catch {
      return { ok: false, reason: "network" };
    }
    if (resp.status === 401 || resp.status === 403) {
      return { ok: false, reason: "forbidden" };
    }
    if (!resp.ok) {
      // The server answered with an error status — never "network". Best-effort peek at the body
      // for a retryAfterSeconds field (PetBox's 503 deploy_in_progress shape); an unparseable or
      // absent body still carries a meaningful status code, so this never turns into a "network"
      // classification either.
      let retryAfterSeconds: number | undefined;
      try {
        const errBody: any = await resp.json();
        if (typeof errBody?.retryAfterSeconds === "number") retryAfterSeconds = errBody.retryAfterSeconds;
      } catch {
        // best effort only
      }
      return {
        ok: false,
        reason: "http-error",
        status: resp.status,
        ...(retryAfterSeconds !== undefined ? { retryAfterSeconds } : {}),
      };
    }
    let body: any;
    try {
      body = await resp.json();
    } catch {
      return { ok: false, reason: "parse-error" };
    }
    const ws = body?.workspace ?? body?.Workspace;
    if (typeof ws === "string" && ws.trim().length > 0) return { ok: true, workspace: ws.trim() };
    return { ok: false, reason: "no-workspace-field" };
  } finally {
    clearTimeout(timer);
  }
}

/**
 * Shared human text for a failed probe — every caller (doctor's skill-drift check, apply's skill
 * refresh) renders this SAME wording so the reason taxonomy above and its prose never drift apart
 * (previously wire.ts carried two near-identical ternary chains). An HTTP error is never described
 * as unreachable; 503 gets its own retry-after phrasing since PetBox's own redeploy window answers
 * 503 by design (self-recovering, not a defect to chase — see probeWorkspace's doc comment).
 */
export function describeWorkspaceProbeFailure(probe: Extract<WorkspaceProbeResult, { readonly ok: false }>): string {
  switch (probe.reason) {
    case "forbidden":
      return "the API key was rejected (401/403 — check its scopes), not merely offline";
    case "no-workspace-field":
      return "the server responded but did not report a workspace (older server?)";
    case "parse-error":
      return "the server responded 200 but the body did not parse as JSON";
    case "network":
      return "could not reach /api/auth/validate (network/timeout)";
    case "http-error":
      if (probe.status === 503) {
        const retry = probe.retryAfterSeconds !== undefined ? ` — retry in ~${probe.retryAfterSeconds}s` : "";
        return `the server is deploying (HTTP 503, service_unavailable) — this is self-recovering, not a network problem${retry}`;
      }
      return `the server responded with HTTP ${probe.status} (reachable — not a transport/connectivity failure)`;
  }
}

// ---- opencode salience index (bug: opencode-skills-not-autoinjected) --------------------------
//
// opencode ALREADY resolves a skill's body lazily, on demand, via its native `skill` tool — the
// exact same progressive-disclosure shape Claude Code and Droid use (confirmed against
// https://opencode.ai/docs/skills/ and https://docs.factory.ai/harness/skills: cheap name+
// description always listed, full body fetched only on an explicit call). That mechanism is not
// missing and must not be duplicated (project rule m-9a5acb03389d4337bef2407131e59e19: "don't
// duplicate a cheap surface into an expensive one" — an earlier version of this fix injected full
// bodies, ~47.5KB across the six petbox-* skills in this repo, into EVERY session's system
// prompt; reserve returned it for exactly this reason). What was actually missing was salience:
// the agent has to notice a skill exists, pick the right one, and decide to call it, with nothing
// forcing that noticing to happen. So this module builds a SALIENCE INDEX, not a body copy: one
// short line per petbox-* skill naming WHEN to call it, derived from that skill's own
// `description:` frontmatter (the "Use ..." sentence — the house convention every current
// petbox-* skill already follows) so it can never drift into a second copy of the skill's
// content. The actual body still arrives lazily, through the untouched native `skill` tool.
//
// Scoped by DECLARATION, not by directory name: a skill enters the digest iff its materialized
// frontmatter says `petbox-digest: auto` (spec: wire-skill-invocation-mode). The earlier rule
// — "directory name starts with petbox" — is gone, and it had to go: every delivered skill is
// heading for a `petbox-*` name (work: petbox-skill-naming), after which the prefix separates
// nothing. It was already wrong today — `petbox-methodology-system` is `petbox-` prefixed,
// repo-native, and not in the kit's delivery at all, yet the prefix rule put it in every
// opencode session's system prompt; a skill that exists to be called deliberately
// (`petbox-card-check`, `petbox-factory-run`) is `petbox-`/deliberate and would have joined it. Reads the
// MATERIALIZED file (post `{{PROJECT}}`/`{{WORKSPACE}}` substitution, post any user edits),
// never re-renders a template — so a project can take a delivered skill out of its own digest
// by editing one frontmatter line, without the kit knowing anything about it.

const FRONTMATTER_RE = /^---\r?\n[\s\S]*?\r?\n---\r?\n/;

/**
 * Extract the frontmatter `description:` value from a raw SKILL.md — either a single-line
 * scalar (`description: text`) or a folded block scalar (`description: >-\n  line one\n  line
 * two`), the two forms every current petbox-* skill uses. `null` when there is no frontmatter or
 * no description field (caller degrades to "no trigger for this skill" rather than guessing).
 */
export function extractSkillDescription(raw: string): string | null {
  const fm = raw.match(FRONTMATTER_RE);
  if (!fm) return null;
  const yaml = fm[0];
  const block = yaml.match(/^description:\s*[|>][-+]?[ \t]*\r?\n((?:[ \t]+\S.*\r?\n?)+)/m);
  const blockText = block?.[1];
  if (blockText !== undefined) {
    return blockText
      .split(/\r?\n/)
      .map((l) => l.trim())
      .filter(Boolean)
      .join(" ");
  }
  const single = yaml.match(/^description:[ \t]*(.+?)\r?$/m);
  const singleText = single?.[1];
  return singleText !== undefined ? singleText.trim() : null;
}

/**
 * The one sentence a description names as WHEN to reach for the skill — every current petbox-*
 * skill's description states this as a sentence starting "Use ..." (see the petbox-* SKILL.md
 * files under .claude/skills). Falls back to the description's first sentence when no such
 * sentence is found, so a future skill that doesn't follow the convention still gets some
 * one-line trigger, not none.
 */
export function extractSkillTrigger(description: string): string {
  const sentences = description
    .replace(/\s+/g, " ")
    .trim()
    .split(/(?<=\.)\s+/);
  const useSentence = sentences.find((s) => /^Use\b/.test(s.trim()));
  return (useSentence ?? sentences[0] ?? "").trim();
}

export type PetboxSkillTrigger = { readonly name: string; readonly trigger: string };

/**
 * One trigger line per skill materialized under `<root>/.claude/skills/` whose frontmatter
 * DECLARES `petbox-digest: auto`, sorted by directory name for a stable order. Every other
 * skill on disk — declared `manual`, or carrying no declaration at all (a project's own skill,
 * whatever it is named) — is out. A skill whose description can't be parsed is skipped too
 * (never injects a blank line). `[]` when the skills directory is absent (wire apply not run
 * yet) or empty — never throws (best-effort, same contract as every other opencode-plugin.ts
 * injector).
 */
export function readAutoDigestSkillTriggers(root: string): PetboxSkillTrigger[] {
  const dir = join(root, ".claude", "skills");
  let dirNames: string[];
  try {
    dirNames = readdirSync(dir, { withFileTypes: true })
      .filter((e) => e.isDirectory())
      .map((e) => e.name)
      .sort();
  } catch {
    return [];
  }
  const out: PetboxSkillTrigger[] = [];
  for (const name of dirNames) {
    try {
      const raw = readFileSync(join(dir, name, "SKILL.md"), "utf8");
      if (readDigestMode(raw) !== "auto") continue; // declaration, never the directory name
      const description = extractSkillDescription(raw);
      if (!description) continue;
      out.push({ name, trigger: extractSkillTrigger(description) });
    } catch {
      // missing/unreadable SKILL.md under a skill dir — skip it, best-effort
    }
  }
  return out;
}

/**
 * Render the salience index for opencode's system prompt, or `null` when there is nothing to
 * index (mirrors fetchCanonBlock's "best-effort, degrades to nothing" shape so the caller can
 * `if (block) output.system.push(block)` uniformly). Each line names the trigger condition and
 * the exact skill name to call — the body itself is never inlined; `skill(name)` still fetches
 * it, lazily, same as always.
 */
export function buildAutoSkillsIndex(root: string): string | null {
  const triggers = readAutoDigestSkillTriggers(root);
  if (triggers.length === 0) return null;
  const lines = triggers.map((t) => `- ${t.trigger} → \`${t.name}\``);
  return ["## PetBox skills — call `skill(name)` on match, don't browse first", "", ...lines].join("\n");
}

// NOTE: a `shouldInjectOnce` per-session gate used to live here, used by opencode-plugin.ts to
// push the salience index only on a session's first turn. It was removed, not fixed: the
// assumption under it — that a block pushed into one request "stays in the model's context" —
// is false for opencode, which rebuilds the system prompt from scratch for every request (and
// whose FIRST request per session is the small-model title generation, sharing the session id).
// The gate therefore delivered the index to the title request and to nothing else. See the
// injection site in opencode-plugin.ts for the live measurement, and
// opencode-plugin-system-transform.test.ts for the regression that pins it.
