// `petbox-wire status` — prints FACT, never a verdict (task: wire-status-command).
//
// Distinct from `doctor`: doctor answers "is it broken" (a gate, non-zero exit on policy
// violation). status answers "what do I have right now and how do I change it" for someone who
// just ran `wire`/`apply` and wants to see what landed. It reads the SAME resolvers doctor/apply
// already use (resolveAgentDefinitionWithLkg, resolveAgentRoles, allowedModels, …) — never a
// second implementation of any of those checks — and always exits 0 unless it itself throws
// (an actual bug), because it asserts nothing about correctness.
//
// Per role x harness, one line: role -> materialized file (path) -> model -> WHERE the model
// came from -> how to change it. The model source is a strict ENUMERATION, not prose:
//   roster — bound in ~/.petbox/roles.json (the file exists and carries a value for this role).
//   seed   — the file does not exist yet; this is what DEFAULT_ROLE_MODEL_SEED (roles.ts) would
//            write on the next `wire`/`apply` (a preview, nothing is written by `status` itself).
//   none   — no value from either roster or seed. This is a PROBLEM, not a blank: on a CLOSED
//            model-space harness (claude-code) `apply` hard-refuses the role; on an OPEN one
//            (opencode; droid only when its seed also fails to cover a role) `apply` still
//            writes it, warning that it inherits the session model. Either way status names the
//            fix.
//
// Plus a four-pillar summary: definition source (server / LKG cache / built-in copy — each
// degradation labelled explicitly), roster (file present? every declared role bound?), canon
// (absent / empty / N of the 10k-char budget, via canon.ts's version-based classification, never
// a string compare against the server's marker text), skills (materialized? byte-identical to
// the current template?).
//
// wire.ts runs main() at module top level and must NEVER be imported by a side module (see its
// own file header) — this file does not import it. wire.ts's dispatch only parses `status`'s argv
// (usage/help text stays there, per this task's scope) and calls runStatus with already-parsed
// options.
//
// Plain TS for native node type-stripping: zero deps.

import { existsSync, readFileSync } from "node:fs";
import { homedir } from "node:os";
import { basename, dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import {
  DEFAULT_DEFINITION_KEY,
  resolveAgentDefinitionWithLkg,
  type ResolvedAgentDefinition,
} from "./agent-def-fetch.ts";
import {
  DEFAULT_AGENT_DEFINITION,
  KIT_VERSION,
  emittedRoleName,
  type AgentDefinition,
  type AgentRole,
} from "./agent-definition.ts";
import { agentFilesDir, planApply, sanitizeDroidName } from "./apply-artifacts.ts";
import { resolveApplyRoot } from "./apply-root.ts";
import { classifyManagedPaths, formatGitState, type GitStateReport } from "./git-state.ts";
import { managedPathsForGitState, projectRoleFiles } from "./managed-paths.ts";
import { loadWireConfig, userAgentFilesRoot, type RoleScope } from "./role-scope.ts";
import { CANON_BODY_BUDGET_CHARS, fetchCanonBlock, fetchCanonLegs, type CanonLegState } from "./canon.ts";
import { HARNESS_IDS, type HarnessId } from "./harness-capabilities.ts";
import { allowedModels } from "./harness-models.ts";
import { unrefLingeringHandles } from "./hook-drain.ts";
import { checkNpmWireDrift, formatNpmWireDrift } from "./npm-wire-drift.ts";
import { readArtifactState, type ArtifactState } from "./origin-marker.ts";
import { buildProtocol, mcpPetboxTool } from "./protocol.ts";
import { readRegistry, resolveProject, type RegistryEntry, type ResolvedProject } from "./registry.ts";
import {
  DEFAULT_ROLE_MODEL_SEED,
  loadRoles,
  resolveAgentRoles,
  rolesPath,
  type RolesFile,
} from "./roles.ts";
import {
  assembleSessionBanner,
  HARNESS_INLINE_HARD_LIMIT_BYTES,
  SESSION_BANNER_BUDGET_BYTES,
  type SessionBannerResult,
} from "./session-budget.ts";
import {
  buildSkillReports,
  checkSkillFile,
  formatSkillFile,
  PROJECT_SKILLS,
  probeWorkspace,
  renderSkillTemplate,
  SKILL_SURFACES,
} from "./skill-files.ts";
import { WIRE_EXIT } from "./wire-exit.ts";

const HERE = dirname(fileURLToPath(import.meta.url));
const TEMPLATES_ROOT = join(HERE, "templates");

const log = (msg: string) => console.log(msg);

// ---- pillar 1: definition --------------------------------------------------

export function formatDefinitionSource(resolved: ResolvedAgentDefinition): string {
  if (resolved.source === "server") {
    return `server (live) — key=${resolved.key} v${resolved.version}`;
  }
  if (resolved.source === "lkg") {
    // An answered server (401/403, or any other HTTP error) is never "unreachable" (bug:
    // doctor-reports-answering-server-unreachable, same class as
    // probe-collapses-http-errors-into-network) — only a genuine network/timeout miss is.
    // A deliberate --offline run never attempted a fetch at all — checked FIRST, round 2 of the
    // same bug: this branch used to say "server unreachable" for --offline too.
    if (resolved.offline) {
      return (
        `LKG CACHE — --offline (no live fetch attempted) — key=${resolved.key} v${resolved.version}, ` +
        `stale`
      );
    }
    if (resolved.forbidden) {
      return (
        `LKG CACHE — DEGRADED (server reachable but refused the request, 401/403) — ` +
        `key=${resolved.key} v${resolved.version}, stale`
      );
    }
    if (resolved.httpError) {
      return (
        `LKG CACHE — DEGRADED (server answered HTTP ${resolved.httpError.status}, not unreachable) — ` +
        `key=${resolved.key} v${resolved.version}, stale`
      );
    }
    return (
      `LKG CACHE — DEGRADED (server unreachable) — key=${resolved.key} v${resolved.version}, ` +
      `stale`
    );
  }
  // source === "default"
  if (resolved.offline) {
    return "built-in copy — --offline, no LKG cache on disk (no live fetch attempted)";
  }
  if (resolved.notFoundOnServer) {
    return "built-in copy — server reachable, no definition for this project yet (normal for a fresh project)";
  }
  if (resolved.forbidden) {
    return "built-in copy — DEGRADED (server reachable but refused the request, 401/403 — check API key scopes)";
  }
  if (resolved.httpError) {
    return `built-in copy — DEGRADED (server answered HTTP ${resolved.httpError.status}, not unreachable — no LKG cache on disk)`;
  }
  return "built-in copy — DEGRADED (no server reachable, no LKG cache on disk)";
}

// ---- pillar 2: roster -------------------------------------------------------

export type RosterState =
  | { readonly kind: "absent" }
  | { readonly kind: "empty-shell"; readonly activeProfile: string }
  | { readonly kind: "partial"; readonly activeProfile: string; readonly missing: readonly string[] }
  | { readonly kind: "complete"; readonly activeProfile: string };

/** claude-code is the reference closed harness — see roles.ts's DEFAULT_ROLE_MODEL_SEED. */
export function computeRosterState(
  definition: AgentDefinition,
  rolesData: RolesFile,
  fileExists: boolean,
): RosterState {
  if (!fileExists) return { kind: "absent" };
  const ccRoles = resolveAgentRoles(rolesData, "claude-code");
  if (Object.keys(ccRoles).length === 0) {
    return { kind: "empty-shell", activeProfile: rolesData.activeProfile };
  }
  const missing = definition.roles
    .map((r) => r.slug)
    .filter((slug) => !(ccRoles[slug] ?? "").trim());
  if (missing.length > 0) {
    return { kind: "partial", activeProfile: rolesData.activeProfile, missing };
  }
  return { kind: "complete", activeProfile: rolesData.activeProfile };
}

export function formatRosterState(state: RosterState): string {
  const path = rolesPath();
  switch (state.kind) {
    case "absent":
      return (
        `absent (${path}) — a bare \`wire\`/\`apply\` seeds it (claude-code aliases + droid ` +
        `inherit; opencode stays unbound — see the per-role lines below)`
      );
    case "empty-shell":
      return `present but EMPTY (${path}, activeProfile="${state.activeProfile}") — no bindings for any agent`;
    case "partial":
      return (
        `present but PARTIAL (${path}, activeProfile="${state.activeProfile}") — claude-code ` +
        `missing: ${state.missing.join(", ")}`
      );
    case "complete":
      return (
        `present and COMPLETE (${path}, activeProfile="${state.activeProfile}") — every ` +
        `declared role is bound for claude-code`
      );
  }
}

// ---- per-role x harness: file path -----------------------------------------

export function roleRelativePath(harness: HarnessId, role: AgentRole): string {
  const dir = agentFilesDir(harness);
  const fileName =
    harness === "droid" ? `${sanitizeDroidName(emittedRoleName(role))}.md` : `${emittedRoleName(role)}.md`;
  return join(dir, fileName).replace(/\\/g, "/");
}

// ArtifactState/readArtifactState moved to origin-marker.ts (task
// builtin-definition-drifts-no-catchup item 3, alongside skill-files.ts's checkSkillFile): the
// marker classifier is not role- or skill-specific — both need the SAME function, imported
// above, never a second copy.

export function formatArtifactState(relPath: string, state: ArtifactState): string {
  if (state === "absent") return `${relPath} (not materialized yet)`;
  if (state === "ours") return `${relPath} (materialized, ours)`;
  // `petbox: manual` — declared by the project as its own. Left alone on purpose, so it must not
  // read as BLOCKED: that word tells the operator to go fix something, and there is nothing here
  // to fix (spec: wire-skill-manual-declared-not-error).
  if (state === "manual") return `${relPath} (declared manual — the project owns this path)`;
  return `${relPath} (BLOCKED — a foreign file sits here, not ours)`;
}

// ---- per-role x harness: model source enumeration --------------------------

export type ModelSource =
  | { readonly kind: "roster"; readonly model: string }
  | { readonly kind: "seed"; readonly model: string }
  | { readonly kind: "none" };

/**
 * Where a role's model for `harness` would come from RIGHT NOW, without writing anything —
 * mirrors wire.ts's seedDefaultRoleBindingsIfMissing (droid always gets `inherit` for every
 * seeded role; opencode is never seeded — see roles.ts's DEFAULT_ROLE_MODEL_SEED doc comment) so
 * the "seed" preview never drifts from what a real `wire`/`apply` run would actually write.
 */
export function resolveRoleModelSource(
  role: string,
  harness: HarnessId,
  rolesFileExists: boolean,
  roleModels: Readonly<Record<string, string>>,
): ModelSource {
  const bound = (roleModels[role] ?? "").trim();
  if (bound) return { kind: "roster", model: bound };
  if (!rolesFileExists) {
    if (harness === "claude-code") {
      const seeded = DEFAULT_ROLE_MODEL_SEED[role];
      if (seeded) return { kind: "seed", model: seeded };
    } else if (harness === "droid") {
      // Every role DEFAULT_ROLE_MODEL_SEED knows gets the literal `inherit` seed for droid
      // (wire.ts's seedDefaultRoleBindingsIfMissing) — a real, documented Factory default, not
      // an invented id.
      if (DEFAULT_ROLE_MODEL_SEED[role]) return { kind: "seed", model: "inherit" };
    }
    // opencode is never seeded (open, unknowable id space from the kit) — falls through to
    // "none" below even on a totally fresh machine, same as wire.ts's own seeding decision.
  }
  return { kind: "none" };
}

export type RoleModelSourceLine = {
  readonly line: string;
  /** True for "none" — a role with no model source at all (see file header). */
  readonly problem: boolean;
};

export function formatRoleModelSource(
  role: string,
  harness: HarnessId,
  source: ModelSource,
): RoleModelSourceLine {
  if (source.kind === "roster") {
    return {
      problem: false,
      line:
        `model=${source.model} (source: roster) — change: ` +
        `\`petbox-wire model set ${role} <model> --agent ${harness}\` (or edit ${rolesPath()} directly)`,
    };
  }
  if (source.kind === "seed") {
    return {
      problem: false,
      line:
        `model=${source.model} (source: seed — default, not yet written to ${rolesPath()}) — ` +
        `change: \`petbox-wire model set ${role} <model> --agent ${harness}\` (writes the file now); ` +
        `or run \`petbox-wire apply\`/\`wire\` to materialize this default as-is`,
    };
  }
  // "none" — always a problem, never a blank (task requirement).
  const closed = allowedModels(harness) !== null;
  const consequence = closed
    ? `apply HARD-REFUSES this role on '${harness}' (closed model space) until it is bound`
    : `apply WARNS and writes it inheriting the session model on '${harness}' (open model space)`;
  return {
    problem: true,
    line:
      `model=(none) (source: none → inherits session model) — PROBLEM: ${consequence}. ` +
      `change: \`petbox-wire model set ${role} <model> --agent ${harness}\``,
  };
}

// ---- roles as FILES, per scope (card: normalize-all-environments-to-default item 3) ----------
//
// The gap this closes, measured 2026-09-02: `status` looked at SKILLS only. Roles — 90 files
// across eight projects — were never checked by anything, and they had silently split into two
// generations (`petbox-orchestrator.md` carried the current prose in two projects and an old one
// in four; droid's `petbox-worker-highstakes` was bound to a concrete model id in some and
// `inherit` in others). A default nobody measures is not a default, so this counts them: present
// and matching, present and DRIFTED from what apply would render right now, absent, or foreign.
//
// Read-only, and it must stay so — `status --all` runs this against seven other people's working
// directories.

export type RoleFilesReport = {
  readonly harness: HarnessId;
  readonly dir: string;
  /** Ours (origin marker) and byte-identical to what apply would render right now. */
  readonly current: number;
  /** Ours, but different from the current render — the invisible axis this card is about. */
  readonly drifted: readonly string[];
  /** Declared by the definition, nothing on disk. */
  readonly missing: readonly string[];
  /** Something else's file sits on a path apply would want — apply refuses these. */
  readonly foreign: readonly string[];
};

/**
 * Classify the role artifacts for ONE harness in ONE directory against the definition. `agentDir`
 * is the harness's agent directory itself — the project's (`<root>/.claude/agents`) or the user
 * profile's (`~/.claude/agents`) — because the two layouts differ and this function must not
 * re-derive either (role-scope.ts owns the user side, apply-artifacts.ts the project side).
 */
export function computeRoleFilesReport(
  agentDir: string,
  harness: HarnessId,
  definition: AgentDefinition,
  rolesData: RolesFile,
): RoleFilesReport {
  const drifted: string[] = [];
  const missing: string[] = [];
  const foreign: string[] = [];
  let current = 0;
  let plan;
  try {
    plan = planApply(definition, harness, resolveAgentRoles(rolesData, harness));
  } catch {
    // A definition this harness cannot plan at all is a fact for doctor, not a crash for status.
    return { harness, dir: agentDir, current: 0, drifted: [], missing: [], foreign: [] };
  }
  for (const file of plan.files) {
    const abs = join(agentDir, basename(file.relativePath));
    const state = readArtifactState(abs);
    if (state === "absent") {
      missing.push(abs);
      continue;
    }
    if (state === "foreign") {
      foreign.push(abs);
      continue;
    }
    if (state === "manual") continue; // the owner declared this path theirs — not ours to judge
    let content: string;
    try {
      content = readFileSync(abs, "utf8");
    } catch {
      drifted.push(abs); // ours by marker but unreadable now — that is a discrepancy, not a match
      continue;
    }
    if (content === file.content) current++;
    else drifted.push(abs);
  }
  return { harness, dir: agentDir, current, drifted, missing, foreign };
}

export function formatRoleFilesReport(report: RoleFilesReport): string {
  const problems: string[] = [];
  if (report.drifted.length > 0) problems.push(`drifted: ${report.drifted.length}`);
  if (report.missing.length > 0) problems.push(`missing: ${report.missing.length}`);
  if (report.foreign.length > 0) problems.push(`foreign: ${report.foreign.length}`);
  return (
    `${report.harness} (${report.dir}) — ${report.current} current` +
    (problems.length > 0 ? `, ${problems.join(", ")}` : ", nothing else")
  );
}

/** The user-profile role reports for all three harnesses — a MACHINE fact, printed once. */
export function computeUserRoleReports(
  definition: AgentDefinition,
  rolesData: RolesFile,
  homeDir: string = homedir(),
): RoleFilesReport[] {
  return HARNESS_IDS.map((h) => computeRoleFilesReport(userAgentFilesRoot(h, homeDir), h, definition, rolesData));
}

// ---- pillar 3: canon --------------------------------------------------------

export function formatCanonLeg(label: string, state: CanonLegState): string {
  if (state.kind === "absent") return `${label}: absent (never asked, or withheld)`;
  if (state.kind === "empty") {
    return `${label}: empty (0 of ${CANON_BODY_BUDGET_CHARS} chars) — curate with memory_upsert (store canon, key index)`;
  }
  return `${label}: ${state.chars} of ${CANON_BODY_BUDGET_CHARS} chars`;
}

// ---- session banner budget (card canon-write-gate-banner-budget) ----------
//
// Downgraded from the card's original ask (a WRITE gate on canon curation) to a READ-time
// instrument: canon curation is server-side, but the 9 400B session-banner budget is a
// Claude-Code-specific client fact (session-budget.ts) the server cannot see — and a
// canon-only threshold sized to survive the worst-observed PROTOCOL size proved actively wrong
// (measured: a "conservative" 2 971B ceiling derived from the worst-seen 6 427B protocol would
// have rejected a perfectly healthy 3 053B canon the very next day, once the protocol happened
// to shrink back down). So this measures the REAL thing SessionStart actually assembles instead
// of inventing a second, static proxy for it.
//
// Runs the SAME assembly pull-memory.ts's SessionStart hook runs — buildProtocol, then
// fetchCanonBlock, then assembleSessionBanner compared against SESSION_BANNER_BUDGET_BYTES —
// never a reimplemented formula and never a re-slice of the rendered banner TEXT: buildProtocol's
// own prose quotes the literal heading "## PetBox memory canon" (its canon-entry-point
// paragraph), so a first-occurrence text split misattributes part of the protocol's own tail to
// canon (measured, not hypothetical — this is the exact mistake a prior pass on this card made).
// Byte counts here come only from assembleSessionBanner's own return fields.
//
// Measured for BOTH `source` values a real session can start with, not just the default: a
// card-caught regression was specific to `source=resume` (its protocol carries a few dozen extra
// bytes for the recall-nudge suffix — protocol.ts — enough to tip it over budget on a day
// `source=startup` still fit comfortably). Canon does not vary by source, so it is fetched once
// and reused for both legs below.
const BANNER_BUDGET_SOURCES = ["startup", "resume"] as const;

export type BannerBudgetLeg = {
  readonly source: (typeof BANNER_BUDGET_SOURCES)[number];
  readonly banner: SessionBannerResult;
  /** protocol + the "\n\n" join + canon (canon term dropped when canon is entirely absent) —
   * what assembleSessionBanner itself compares against the budget, independent of whether canon
   * ultimately survived into the shipped text. */
  readonly combinedBytes: number;
  /** budget - combinedBytes; negative means canon (or, worse, the bare protocol) is over. */
  readonly marginBytes: number;
};

export async function computeBannerBudgetLegs(
  resolvedProject: ResolvedProject,
  definition: AgentDefinition,
  opts?: { readonly canonFetch?: (p: ResolvedProject) => Promise<string | null> },
): Promise<BannerBudgetLeg[]> {
  const canonFetch = opts?.canonFetch ?? fetchCanonBlock;
  const canon = await canonFetch(resolvedProject);
  return BANNER_BUDGET_SOURCES.map((source) => {
    const protocol = buildProtocol(resolvedProject.project, mcpPetboxTool, {
      source,
      harness: "claude-code",
      definition,
    });
    const banner = assembleSessionBanner(protocol, canon);
    const combinedBytes =
      banner.canonBytes > 0 ? banner.protocolBytes + 2 + banner.canonBytes : banner.protocolBytes;
    return {
      source,
      banner,
      combinedBytes,
      marginBytes: SESSION_BANNER_BUDGET_BYTES - combinedBytes,
    };
  });
}

export function formatBannerBudgetLeg(leg: BannerBudgetLeg): string {
  const { banner, combinedBytes, marginBytes, source } = leg;
  const sizeText =
    banner.canonBytes > 0
      ? `protocol=${banner.protocolBytes}B + canon=${banner.canonBytes}B = ${combinedBytes}B`
      : `protocol=${banner.protocolBytes}B (no canon) = ${combinedBytes}B`;
  let verdict: string;
  if (banner.overBudget) {
    if (banner.canonBytes === 0) {
      verdict = `PROTOCOL ALONE over budget by ${-marginBytes}B (nothing left to drop)`;
    } else if (banner.canonLegs === "project-only") {
      // Degraded but not lost: the ladder shed the workspace leg and the project leg still
      // shipped (canon-degrade-by-legs-not-all-or-nothing). Reading this as a flat "canon
      // DROPPED" would overstate the loss and hide which leg actually went.
      verdict = `canon WORKSPACE LEG DROPPED (project leg kept, ${banner.canonIncludedBytes}B) — over budget by ${-marginBytes}B`;
    } else {
      verdict = `canon DROPPED — over budget by ${-marginBytes}B`;
    }
  } else if (banner.canonBytes === 0) {
    verdict = `no canon available, margin ${marginBytes}B`;
  } else {
    verdict = `canon INCLUDED, margin ${marginBytes}B`;
  }
  return `source=${source}: ${sizeText} — ${verdict}`;
}

/**
 * Warn fraction for doctor's banner-budget check: 5% of the 9 400B budget = 470B. Chosen because
 * the card's own measured "healthy" case (92B margin, ~1%) was in fact one bad day of protocol
 * drift away from silently dropping canon (and did drop it, days later) — a threshold that only
 * fires once margin is already negative would have said nothing until the exact incident this
 * card exists to catch had already happened again. 5% gives a visible amber zone before the
 * red one.
 */
export const BANNER_BUDGET_WARN_FRACTION = 0.05;

export function bannerBudgetWarnThresholdBytes(): number {
  return Math.round(SESSION_BANNER_BUDGET_BYTES * BANNER_BUDGET_WARN_FRACTION);
}

/**
 * Reachability-checked wrapper: `status`'s canon pillar (3/4, above) already distinguishes a
 * genuinely UNREACHABLE server from "queried, nothing curated" via fetchCanonLegs's `ok` flag —
 * this reuses that SAME signal so the banner-budget section (and doctor's check, which calls
 * this too) report "server unreachable" honestly instead of silently computing a no-canon banner
 * that looks deceptively healthy.
 */
export async function bannerBudgetLegsOrUnreachable(
  resolvedProject: ResolvedProject,
  definition: AgentDefinition,
): Promise<{ readonly ok: true; readonly legs: BannerBudgetLeg[] } | { readonly ok: false }> {
  const reach = await fetchCanonLegs(resolvedProject);
  if (!reach.ok) return { ok: false };
  return { ok: true, legs: await computeBannerBudgetLegs(resolvedProject, definition) };
}

// ---- pillar 4: skills -------------------------------------------------------
//
// checkSkillFile/formatSkillFile/buildSkillReports/probeWorkspace all moved to skill-files.ts
// (task builtin-definition-drifts-no-catchup item 3 / skill-files-clobber-and-apply-skips item
// 3): `doctor` needed the SAME materialized-vs-template comparison this pillar already had, so
// it now lives in one place (next to the templates and the origin marker it renders) and both
// `status` and `doctor` call it — never a second copy of the diff.

function printSkillsMaterializationOnly(root: string): void {
  for (const spec of PROJECT_SKILLS) {
    for (const surface of SKILL_SURFACES) {
      const absPath = join(root, ...surface, spec.dir, "SKILL.md");
      log(`status:   ${formatSkillFile(checkSkillFile(absPath, undefined))}`);
    }
  }
}

// ---- orchestration -----------------------------------------------------------

/**
 * `status`'s whole run: resolve root/project/definition/roles (the SAME resolvers apply/doctor
 * use), print facts, exit 0. Options are pre-parsed by wire.ts's dispatch (argv/usage stay
 * there) — this function takes no argv and never calls usage().
 */
export async function runStatus(opts: { readonly offline: boolean; readonly cwd: string }): Promise<void> {
  const { root, via } = resolveApplyRoot(opts.cwd);
  log(`status: root=${root} (via ${via})`);

  const resolvedProject: ResolvedProject | null = resolveProject(root);

  // npm-wire tag drift (task kit-version-lands-everywhere-and-sweeps item 3): best-effort/skip
  // outside a git checkout with a local `main` ref — see npm-wire-drift.ts's header. Printed
  // once, up front, next to `root=` — it is a machine-wide fact (which kit version npm ships),
  // not a per-project one.
  if (opts.offline) {
    log("status: npm-wire tag check skipped (--offline).");
  } else {
    const npmDrift = await checkNpmWireDrift(root);
    log(`status: ${formatNpmWireDrift(npmDrift)}`);
  }

  // ---- pillar 1: definition ----
  const resolvedDef = await resolveAgentDefinitionWithLkg({
    offline: opts.offline,
    definitionKey: DEFAULT_DEFINITION_KEY,
    ...(resolvedProject?.project !== undefined ? { projectKey: resolvedProject.project } : {}),
    ...(resolvedProject?.baseUrl !== undefined ? { baseUrl: resolvedProject.baseUrl } : {}),
    ...(resolvedProject?.apiKey !== undefined ? { apiKey: resolvedProject.apiKey } : {}),
  });
  const definition = resolvedDef.definition;
  log("");
  log(`status: pillar 1/4 — definition: ${formatDefinitionSource(resolvedDef)}`);
  log(`status:   name="${definition.name}", roles=${definition.roles.map((r) => r.slug).join(",")}`);

  // ---- pillar 2: roster ----
  const rolesFileExists = existsSync(rolesPath());
  const rolesData = loadRoles();
  log("");
  log(`status: pillar 2/4 — roster: ${formatRosterState(computeRosterState(definition, rolesData, rolesFileExists))}`);

  // ---- per role x harness: file -> model -> source -> change ----
  log("");
  log("status: role -> file -> model -> source -> change (one line per role x harness):");
  let anyProblem = false;
  for (const harness of HARNESS_IDS) {
    log(`status: [${harness}]`);
    const roleModels = resolveAgentRoles(rolesData, harness);
    for (const role of definition.roles) {
      const relPath = roleRelativePath(harness, role);
      const absPath = join(root, relPath);
      const artifactState = readArtifactState(absPath);
      const source = resolveRoleModelSource(role.slug, harness, rolesFileExists, roleModels);
      const { line, problem } = formatRoleModelSource(role.slug, harness, source);
      if (problem) anyProblem = true;
      log(`status:   ${role.slug} -> ${formatArtifactState(relPath, artifactState)} -> ${line}`);
    }
  }

  // ---- roles as files, per scope (card item 3) ----
  // The per-role lines above answer "what model, from where"; this answers "where do the FILES
  // live and are they the same generation", which nothing used to ask at all.
  //
  // [user] is compared against DEFAULT_AGENT_DEFINITION (the kit's own bundled baseline), NEVER
  // against `definition` (pillar 1's per-project, cwd-resolved document) — that was the exact bug
  // (card user-scope-roles-rendered-from-cwd-project-definition): comparing machine-wide files
  // against a per-cwd definition made `status` say "current" or "drifted" depending on which
  // directory the operator happened to run it from. computeUserRoleReports' own `drifted` count
  // against the baseline IS the staleness check the card asks for — a file that no longer matches
  // what THIS kit build would render is stale, whether that is because someone hand-edited it or
  // because the kit was upgraded since the last apply; there is no separate "version" to compare,
  // because the baseline has no version axis other than the kit build itself.
  const machineScope: RoleScope = loadWireConfig().roleScope;
  log("");
  log(`status: role FILES by scope (machine policy: roles → ${machineScope}):`);
  log(`status:   [user] source: kit baseline (default-agents.json), kit v${KIT_VERSION} — deterministic, independent of cwd`);
  for (const report of computeUserRoleReports(DEFAULT_AGENT_DEFINITION, rolesData)) {
    log(`status:   [user]    ${formatRoleFilesReport(report)}`);
  }
  for (const harness of HARNESS_IDS) {
    log(
      `status:   [project] ${formatRoleFilesReport(
        computeRoleFilesReport(join(root, agentFilesDir(harness)), harness, definition, rolesData),
      )}`,
    );
  }
  const projectRoles = projectRoleFiles(root);
  log(
    machineScope === "user"
      ? `status:   project role copies present: ${projectRoles.length} (target under this policy: 0)`
      : `status:   project role copies present: ${projectRoles.length}`,
  );

  // ---- git state of managed paths (card item 5) ----
  log("");
  log(`status: ${formatGitState(classifyManagedPaths(root, managedPathsForGitState(root)))}`);

  // ---- pillar 3: canon ----
  log("");
  if (opts.offline) {
    log("status: pillar 3/4 — canon: skipped (--offline)");
  } else if (!resolvedProject) {
    log(`status: pillar 3/4 — canon: n/a — ${root} is not a registered project (run \`wire\` here first)`);
  } else {
    const legs = await fetchCanonLegs(resolvedProject);
    if (!legs.ok) {
      log("status: pillar 3/4 — canon: UNREACHABLE (server did not answer GET /api/memory/{project}/canon)");
    } else {
      log(
        `status: pillar 3/4 — canon: ${formatCanonLeg("project", legs.project)}; ` +
          `${formatCanonLeg("workspace", legs.workspace)}`,
      );
    }
  }

  // ---- pillar 4: skills ----
  log("");
  if (opts.offline) {
    log("status: pillar 4/4 — skills: template-match check skipped (--offline); materialization only:");
    printSkillsMaterializationOnly(root);
  } else if (!resolvedProject) {
    log(`status: pillar 4/4 — skills: workspace unknown — ${root} is not a registered project; materialization only:`);
    printSkillsMaterializationOnly(root);
  } else {
    const probe = await probeWorkspace(resolvedProject.baseUrl, resolvedProject.apiKey);
    const workspace = probe.ok ? probe.workspace : undefined;
    log(`status: pillar 4/4 — skills (project=${resolvedProject.project}, workspace=${workspace ?? "unknown"}):`);
    for (const report of buildSkillReports(root, TEMPLATES_ROOT, resolvedProject.project, workspace)) {
      log(`status:   ${formatSkillFile(report)}`);
    }
  }

  // ---- session banner budget (card canon-write-gate-banner-budget) ----
  log("");
  if (opts.offline) {
    log("status: session banner budget: skipped (--offline).");
  } else if (!resolvedProject) {
    log(`status: session banner budget: n/a — ${root} is not a registered project (run \`wire\` here first)`);
  } else {
    const result = await bannerBudgetLegsOrUnreachable(resolvedProject, definition);
    if (!result.ok) {
      log("status: session banner budget: UNREACHABLE (server did not answer GET /api/memory/{project}/canon)");
    } else {
      log(
        `status: session banner budget (harness inline hard limit ${HARNESS_INLINE_HARD_LIMIT_BYTES}B, ` +
          `budget ${SESSION_BANNER_BUDGET_BYTES}B):`,
      );
      for (const leg of result.legs) log(`status:   ${formatBannerBudgetLeg(leg)}`);
    }
  }

  log("");
  log(
    anyProblem
      ? "status: done — see PROBLEM line(s) above for roles with no model source."
      : "status: done — every declared role has a model source on every known harness.",
  );
  // Same libuv race doctor hit (Assertion failed: !(handle->flags & UV_HANDLE_CLOSING)):
  // runStatus does TWO live network requests (definition resolve, then the workspace probe), and
  // two sequential fetches in one process is exactly what turns this from a latent risk into a
  // reproducible crash on a hard process.exit(). Set exitCode + return, letting Node drain the
  // event loop naturally, after unref'ing whatever handle is still mid-close (see wire.ts's
  // doctor exit points / hook-drain.ts for the identical fix).
  process.exitCode = WIRE_EXIT.ok;
  unrefLingeringHandles();
}

// ---- registry-wide status (task kit-version-lands-everywhere-and-sweeps item 4) --------------
//
// The owner's actual question — "is everything on the same, latest scheme" — is about the WHOLE
// registry, not one project someone happens to be sitting in. `status` (above) answers that one
// project deeply; this answers every registered project SHALLOWLY, one screen, one row per
// project: skill composition vs. the CURRENTLY installed kit's templates, and what's wrong if
// anything. Deliberately network-free (buildSkillReports/checkSkillFile with `workspace:
// undefined` — every PROJECT_SKILLS template except `petbox` renders fully from `project` alone,
// so a byte-compare works without a live probe; `petbox` itself is skipped from the byte compare
// and just checked for presence) — this has to be safe to run against SIX OTHER PEOPLE'S working
// directories without touching their network or their disk (read-only, always).

export type RegistryStatusRow = {
  readonly project: string;
  readonly dir: string;
  readonly verdict: "ok" | "stale" | "missing-dir";
  readonly presentSkills: number;
  readonly totalSkills: number;
  readonly missingSkills: readonly string[];
  readonly driftedSkills: readonly string[];
  readonly foreignPaths: readonly string[];
  readonly legacyLeftovers: readonly string[];
  /**
   * Generated ROLE artifacts still sitting in this project tree (card item 3). Under the "user"
   * role policy the target is zero — the same five roles live once in the harness profiles — so a
   * non-empty list here makes the row stale. Under the "project" policy they are expected and do
   * not, on their own, make anything stale.
   */
  readonly projectRoleFiles: readonly string[];
  /** git classification of every managed path (card item 5). */
  readonly git: GitStateReport;
};

/** One row's read-only verdict for `entry`. Never throws, never writes — an unreadable template
 * or a missing directory degrades the row, it never aborts the caller's loop over the rest of
 * the registry (same "one bad entry can't sink the sweep" rule `apply --all` follows). */
export function computeRegistryStatusRow(
  entry: RegistryEntry,
  templatesRoot: string = TEMPLATES_ROOT,
  roleScope: RoleScope = loadWireConfig().roleScope,
): RegistryStatusRow {
  const dir = entry.prefix;
  const totalSkills = PROJECT_SKILLS.length;
  if (!existsSync(dir)) {
    return {
      project: entry.project,
      dir,
      verdict: "missing-dir",
      presentSkills: 0,
      totalSkills,
      missingSkills: PROJECT_SKILLS.map((s) => s.dir),
      driftedSkills: [],
      foreignPaths: [],
      legacyLeftovers: [],
      projectRoleFiles: [],
      git: { repo: false, states: [], tracked: 0, ignored: 0, untracked: 0, absent: 0 },
    };
  }
  const missingSkills: string[] = [];
  const driftedSkills: string[] = [];
  const foreignPaths: string[] = [];
  const legacyLeftovers: string[] = [];
  let presentSkills = 0;
  for (const spec of PROJECT_SKILLS) {
    let anyPresent = false;
    let anyDrift = false;
    let rendered: string | undefined;
    if (!spec.needsWorkspace) {
      try {
        const tpl = readFileSync(join(templatesRoot, spec.dir, "SKILL.md"), "utf8");
        rendered = renderSkillTemplate(tpl, entry.project, "");
      } catch {
        rendered = undefined; // this kit build no longer ships this template — treat as unknown
      }
    }
    for (const surface of SKILL_SURFACES) {
      const absPath = join(dir, ...surface, spec.dir, "SKILL.md");
      const report = checkSkillFile(absPath, rendered);
      if (report.state === "ours" || report.state === "manual") anyPresent = true;
      if (report.state === "foreign") {
        foreignPaths.push(absPath);
        anyPresent = true; // materialized, just not something we can vouch for
      }
      if (report.state === "ours" && report.matchesTemplate === false) anyDrift = true;
      for (const legacyDir of spec.legacyDirs ?? []) {
        if (existsSync(join(dir, ...surface, legacyDir, "SKILL.md"))) {
          legacyLeftovers.push(join(dir, ...surface, legacyDir, "SKILL.md"));
        }
      }
    }
    if (anyPresent) presentSkills++;
    else missingSkills.push(spec.dir);
    if (anyDrift) driftedSkills.push(spec.dir);
  }
  const roleFiles = projectRoleFiles(dir);
  const git = classifyManagedPaths(dir, managedPathsForGitState(dir));
  // "on the single policy" means every managed path is IGNORED or absent — never committed
  // (`one-c`: 25 tracked) and never loose (`infra`, `petsonde`: 25 untracked with no .gitignore
  // at all). A directory that is not a git repository has no policy to be off, so it never
  // counts against the row.
  const gitOffPolicy = git.repo && (git.tracked > 0 || git.untracked > 0);
  const roleCopiesOffPolicy = roleScope === "user" && roleFiles.length > 0;
  const verdict: "ok" | "stale" =
    missingSkills.length === 0 &&
    driftedSkills.length === 0 &&
    foreignPaths.length === 0 &&
    legacyLeftovers.length === 0 &&
    !roleCopiesOffPolicy &&
    !gitOffPolicy
      ? "ok"
      : "stale";
  return {
    project: entry.project,
    dir,
    verdict,
    presentSkills,
    totalSkills,
    missingSkills,
    driftedSkills,
    foreignPaths,
    legacyLeftovers,
    projectRoleFiles: roleFiles,
    git,
  };
}

export function formatRegistryStatusRow(row: RegistryStatusRow): string {
  if (row.verdict === "missing-dir") {
    return `${row.project} (${row.dir}) — MISSING DIRECTORY (stale registry entry)`;
  }
  const skillsCol = `${row.presentSkills}/${row.totalSkills} skills`;
  const rolesCol = `${row.projectRoleFiles.length} project role file(s)`;
  const gitCol = row.git.repo
    ? `git tracked=${row.git.tracked} ignored=${row.git.ignored} untracked=${row.git.untracked}`
    : "not a git repo";
  if (row.verdict === "ok") {
    return `${row.project} (${row.dir}) — OK, ${skillsCol}, ${rolesCol}, ${gitCol}`;
  }
  const parts: string[] = [];
  if (row.missingSkills.length > 0) parts.push(`missing: ${row.missingSkills.join(",")}`);
  if (row.driftedSkills.length > 0) parts.push(`drifted: ${row.driftedSkills.join(",")}`);
  if (row.foreignPaths.length > 0) parts.push(`foreign: ${row.foreignPaths.length} file(s)`);
  if (row.legacyLeftovers.length > 0) parts.push(`legacy leftovers: ${row.legacyLeftovers.length} file(s)`);
  if (row.projectRoleFiles.length > 0) parts.push(`role copies still in the project: ${row.projectRoleFiles.length}`);
  if (row.git.repo && row.git.tracked > 0) parts.push(`managed paths COMMITTED: ${row.git.tracked}`);
  if (row.git.repo && row.git.untracked > 0) parts.push(`managed paths untracked (not ignored): ${row.git.untracked}`);
  return `${row.project} (${row.dir}) — STALE, ${skillsCol}, ${rolesCol}, ${gitCol} (${parts.join("; ")})`;
}

/**
 * `status --all`: one screen answering "is everything on the same, latest scheme" across the
 * WHOLE registry (~/.petbox/projects.json), not just cwd. Prints the npm-wire tag check once
 * (machine-wide fact, same as runStatus's own line), then one row per registered project. Never
 * writes anything, never gates an exit code beyond WIRE_EXIT.ok — same "status asserts nothing,
 * it reports" contract runStatus already carries; a stale/missing row is visible in the table,
 * not in the process exit code (that would make `status --all` a gate, which is `doctor`'s job,
 * not this command's — kept deliberately out of scope here per the card).
 */
export async function runRegistryStatus(opts: { readonly offline: boolean; readonly cwd: string }): Promise<void> {
  const entries = readRegistry();
  log(`status --all: ${entries.length} registered project(s) in ~/.petbox/projects.json`);

  if (opts.offline) {
    log("status --all: npm-wire tag check skipped (--offline).");
  } else {
    const npmDrift = await checkNpmWireDrift(opts.cwd);
    log(`status --all: ${formatNpmWireDrift(npmDrift)}`);
  }

  // Roles under the "user" policy are a MACHINE fact, identical for every project — printed once,
  // above the table, never repeated per row.
  //
  // Source is DEFAULT_AGENT_DEFINITION (the kit's own bundled baseline), never a per-project
  // server resolve (card user-scope-roles-rendered-from-cwd-project-definition — this used to call
  // resolveAgentDefinitionWithLkg with no projectKey at all, which can never hit a live server or
  // find any project's LKG cache and so always silently fell back to the SAME baseline anyway,
  // just via a network-shaped code path with a pointless try/catch around it). Naming the source
  // explicitly, plus the kit version, is what lets an operator answer "is this stale" without a
  // second resolve: computeUserRoleReports' own `drifted` count against this SAME baseline below
  // already says so.
  const roleScope: RoleScope = loadWireConfig().roleScope;
  log("");
  log(`status --all: role policy: roles → ${roleScope} scope (~/.petbox/wire.json)`);
  log(`status --all: user-scope role source: kit baseline (default-agents.json), kit v${KIT_VERSION} — deterministic, independent of cwd`);
  {
    const rolesData = loadRoles();
    log(
      `status --all: user-scope role files (machine-wide, ${DEFAULT_AGENT_DEFINITION.roles.length} declared role(s)):`,
    );
    for (const report of computeUserRoleReports(DEFAULT_AGENT_DEFINITION, rolesData)) {
      log(`status --all:   ${formatRoleFilesReport(report)}`);
    }
  }

  log("");
  log("status --all: project / skills / role copies / git (one row per registered project):");
  const rows = entries.map((e) => computeRegistryStatusRow(e, TEMPLATES_ROOT, roleScope));
  for (const row of rows) {
    log(`status --all:   ${formatRegistryStatusRow(row)}`);
  }

  const ok = rows.filter((r) => r.verdict === "ok").length;
  const stale = rows.filter((r) => r.verdict === "stale").length;
  const missingDir = rows.filter((r) => r.verdict === "missing-dir").length;
  log("");
  log(
    `status --all: summary — ${rows.length} project(s): ok=${ok} stale=${stale} missing-dir=${missingDir}.` +
      (stale > 0 ? " Run `petbox-wire apply --all` (preview first with --dry-run) to bring stale projects current." : ""),
  );
  process.exitCode = WIRE_EXIT.ok;
  unrefLingeringHandles();
}
