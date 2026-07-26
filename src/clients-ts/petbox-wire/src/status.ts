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

import { existsSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import {
  DEFAULT_DEFINITION_KEY,
  resolveAgentDefinitionWithLkg,
  type ResolvedAgentDefinition,
} from "./agent-def-fetch.ts";
import { emittedRoleName, type AgentDefinition, type AgentRole } from "./agent-definition.ts";
import { agentFilesDir, sanitizeDroidName } from "./apply-artifacts.ts";
import { resolveApplyRoot } from "./apply-root.ts";
import { CANON_BODY_BUDGET_CHARS, fetchCanonLegs, type CanonLegState } from "./canon.ts";
import { HARNESS_IDS, type HarnessId } from "./harness-capabilities.ts";
import { allowedModels } from "./harness-models.ts";
import { readArtifactState, type ArtifactState } from "./origin-marker.ts";
import { resolveProject, type ResolvedProject } from "./registry.ts";
import {
  DEFAULT_ROLE_MODEL_SEED,
  loadRoles,
  resolveAgentRoles,
  rolesPath,
  type RolesFile,
} from "./roles.ts";
import {
  buildSkillReports,
  checkSkillFile,
  formatSkillFile,
  PROJECT_SKILLS,
  probeWorkspace,
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
    return (
      `LKG CACHE — DEGRADED (server unreachable) — key=${resolved.key} v${resolved.version}, ` +
      `stale`
    );
  }
  // source === "default"
  if (resolved.notFoundOnServer) {
    return "built-in copy — server reachable, no definition for this project yet (normal for a fresh project)";
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

// ---- pillar 3: canon --------------------------------------------------------

export function formatCanonLeg(label: string, state: CanonLegState): string {
  if (state.kind === "absent") return `${label}: absent (never asked, or withheld)`;
  if (state.kind === "empty") {
    return `${label}: empty (0 of ${CANON_BODY_BUDGET_CHARS} chars) — curate with memory_upsert (store canon, key index)`;
  }
  return `${label}: ${state.chars} of ${CANON_BODY_BUDGET_CHARS} chars`;
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

  log("");
  log(
    anyProblem
      ? "status: done — see PROBLEM line(s) above for roles with no model source."
      : "status: done — every declared role has a model source on every known harness.",
  );
  process.exit(WIRE_EXIT.ok);
}
