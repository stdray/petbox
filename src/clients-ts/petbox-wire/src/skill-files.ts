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

import { existsSync, mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { writeArtifact } from "./apply-write.ts";
import { hasPetboxMarker, PETBOX_MARKER_LINE, readArtifactState, type ArtifactState } from "./origin-marker.ts";

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
};

// Every skill wire.ts renders into a freshly-wired project (see writeSkillFiles / wire.ts step 7).
export const PROJECT_SKILLS: SkillTemplateSpec[] = [
  { dir: "petbox", needsWorkspace: true },
  { dir: "petbox-agent-factory", needsWorkspace: false },
  { dir: "petbox-methodology", needsWorkspace: false },
  { dir: "petbox-write-economy", needsWorkspace: false },
];

// Substitute {{PROJECT}} and {{WORKSPACE}}. Safe to call uniformly even for a template that has
// no {{WORKSPACE}} placeholder — replace() on a pattern with zero matches is a no-op.
export function renderSkillTemplate(tpl: string, project: string, workspace: string): string {
  return tpl.replace(/\{\{PROJECT\}\}/g, project).replace(/\{\{WORKSPACE\}\}/g, workspace);
}

// What the PRE-marker template used to render, byte-for-byte, for the migration carve-out below.
// The marker is the ONLY thing added to the templates by this fix (see module comment), so
// stripping its exact line back out of a freshly rendered body reconstructs the legacy output —
// no separate "old template" copy to keep in sync.
const MARKER_LINE_WITH_EOL = new RegExp(`^${PETBOX_MARKER_LINE}\\r?\\n`, "m");
function stripMarkerLine(rendered: string): string {
  return rendered.replace(MARKER_LINE_WITH_EOL, "");
}

export type SkillWriteOutcome =
  | { readonly kind: "written"; readonly path: string; readonly reason: "new" | "own" | "migrated" }
  | { readonly kind: "blocked"; readonly path: string };

// Write one rendered skill body to `absPath`, same clobber contract as apply-write.ts's
// writeArtifact PLUS the one-time migration carve-out: an existing file with no origin marker
// that is byte-for-byte equal to `legacyRendered` (what the pre-marker template produced) is a
// leftover from before this fix, not a foreign file — it is promoted in place (reason
// "migrated"). Anything else unmarked is a real user file and is refused, untouched, same as
// writeArtifact.
function writeSkillArtifact(absPath: string, rendered: string, legacyRendered: string): SkillWriteOutcome {
  if (existsSync(absPath)) {
    let existing: string | undefined;
    try {
      existing = readFileSync(absPath, "utf8");
    } catch {
      existing = undefined; // unreadable — let writeArtifact's own guard classify it (blocked)
    }
    if (existing !== undefined && !hasPetboxMarker(existing) && existing === legacyRendered) {
      mkdirSync(dirname(absPath), { recursive: true });
      writeFileSync(absPath, rendered, "utf8");
      return { kind: "written", path: absPath, reason: "migrated" };
    }
  }
  const outcome = writeArtifact(absPath, rendered);
  return outcome.kind === "blocked"
    ? { kind: "blocked", path: absPath }
    : { kind: "written", path: absPath, reason: outcome.reason };
}

// Render every PROJECT_SKILLS entry from templatesRoot and write it into every SKILL_SURFACES
// root under dir. Returns one outcome per (skill × surface), in write order, for the caller's
// log lines — a "blocked" outcome means a real, non-PetBox file already sat at that path and was
// left byte-for-byte untouched (see writeSkillArtifact above).
export function writeSkillFiles(
  dir: string,
  templatesRoot: string,
  project: string,
  workspace: string,
): SkillWriteOutcome[] {
  const outcomes: SkillWriteOutcome[] = [];
  for (const spec of PROJECT_SKILLS) {
    const tpl = readFileSync(join(templatesRoot, spec.dir, "SKILL.md"), "utf8");
    const rendered = renderSkillTemplate(tpl, project, workspace);
    const legacyRendered = stripMarkerLine(rendered);
    for (const surface of SKILL_SURFACES) {
      const skillPath = join(dir, ...surface, spec.dir, "SKILL.md");
      outcomes.push(writeSkillArtifact(skillPath, rendered, legacyRendered));
    }
  }
  return outcomes;
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
        : "materialized (ours)";
  const match =
    report.matchesTemplate === "unknown"
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
