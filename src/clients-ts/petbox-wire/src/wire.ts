// Bootstrap CLI for the global agent-wiring kit — shipped as the `petbox-wire` npm package
// (`npx petbox-wire <dir> <projectKey> …`), so a project can be wired without cloning the repo.
//
//   npx petbox-wire <dir> <projectKey> [--env VAR] [--key KEY] [--workspace WS] [--cleanup-legacy]
//                                      [--telemetry] [--telemetry-log <name>]
//   npx petbox-wire update
//   (dev, from a checkout: node <pkg>/src/wire.ts <dir> <projectKey> …)
//
// `update` refreshes only the stable kit copy (~/.petbox/wire/) from this package — protocol,
// scripts, kit-owned templates — with the same mirror/orphan cleanup as a full wire. It does
// NOT touch keys, registry entries, the sticky telemetry flag, per-project MCP/skills, or require
// projectKey/key. It DOES run the prompt-RAG hook migration (below), because a refreshed kit no
// longer ships prompt-rag.ts and a leftover hook pointing at it would fail on every prompt.
//
// prompt-RAG (the opt-in UserPromptSubmit context injector) was REMOVED. Its kit files are gone and
// its flags no longer exist; what remains is a one-way MIGRATION that both `wire` and `update` run
// unconditionally and idempotently: prune any hook targeting prompt-rag.ts from ~/.claude/settings.json
// and ~/.factory/settings.json (see hook-prune.ts).
//
// --telemetry (opt-in, off by default) wires Claude Code to export its loop telemetry (OTLP
// metrics + log-events) into the project's petbox named log (default `cc-telemetry`): it ensures
// the log exists and merges the OTEL_* export env into the project's .claude/settings.json.
// CC-only — opencode/droid OTLP exporters can't carry the project/log path in the endpoint.
//
// Idempotently wires a project to PetBox:
//    1. derive the env-var name for the API key
//    2. obtain the key (--key, else env var / ~/.petbox/keys.json)  — minting keys is OUT OF SCOPE
//    3. validate the key against /api/auth/validate
//    4. persist the key everywhere agents look: ~/.petbox/keys.json (kit hooks) + user-scope
//       env on Windows / ~/.petbox/env.sh sourced from login profiles on POSIX (the per-project
//       MCP configs reference ${ENV_VAR}, so a real environment variable must exist)
//    5. copy the kit to a stable location (~/.petbox/wire/) so global hooks survive npx eviction
//    6. upsert the registry entry (prefix → project, envVar)
//    7. (re)generate per-project config files:
//        - .mcp.json                         (Claude Code MCP)
//        - .opencode/opencode.json           (opencode MCP)
//        - .factory/mcp.json                 (Factory Droid MCP — idempotent merge)
//        - .claude/skills/petbox/SKILL.md    (Claude Code skill; opencode reads it via its
//                                             Claude-compatible skills discovery path)
//        - .factory/skills/petbox/SKILL.md   (Factory Droid skill)
//        - .claude/skills/petbox-agent-factory/SKILL.md  (on-demand factory skill)
//        - .factory/skills/petbox-agent-factory/SKILL.md
//    8. install the global Claude + Droid hooks + opencode plugin (merge, never clobber live files);
//       all links point at the stable copy (~/.petbox/wire/), and any dead prompt-RAG hook left by
//       an older kit is pruned
//    9. (--cleanup-legacy) remove the project's old per-project hook/plugin copies
//   10. self-smoke: POST a tiny session and assert the server applied it
//   11. seed a DEFAULT role→model binding on a fresh machine (~/.petbox/roles.json absent —
//       never overwrites an operator's own bindings), then apply: compile per-harness startup
//       artifacts (.claude/agents/*.md, .opencode/agent/*.md, .factory/droids/*.md) from the
//       roster + local binding (fresh-wire-roster-unusable) — without this, a freshly-wired
//       project's roster stays empty even though the injected protocol tells the agent to
//       spawn workers that do not exist on disk. Non-fatal to the overall wire run: a failure
//       here is reported loudly but does not change the run's exit code — re-run
//       `petbox-wire apply` to retry.
//
// Unlike the hooks, this is a CLI: step failures surface loudly (no silent swallow).

import { execFileSync } from "node:child_process";
import { createHash } from "node:crypto";
import {
  chmodSync,
  cpSync,
  existsSync,
  mkdirSync,
  readdirSync,
  readFileSync,
  rmSync,
  statSync,
  writeFileSync,
} from "node:fs";
import { homedir } from "node:os";
import { dirname, join, relative, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import {
  DEFAULT_DEFINITION_KEY,
  resolveAgentDefinitionWithLkg,
} from "./agent-def-fetch.ts";
import { validateAgentDefinition, type AgentDefinition } from "./agent-definition.ts";
import { formatApplyBlocked, planApply } from "./apply-artifacts.ts";
import { resolveApplyRoot } from "./apply-root.ts";
import { cleanupLegacyArtifact, writeArtifact } from "./apply-write.ts";
import { HARNESS_IDS } from "./harness-capabilities.ts";
import { pruneDeadPromptRagHooks } from "./hook-prune.ts";
import { persistKeyForAgentsPosix } from "./posix-env.ts";
import { classifySelfSmokeResponse, finishWireRun } from "./self-smoke.ts";
import { classifyApplyExit, WIRE_EXIT } from "./wire-exit.ts";
import { deriveEnvVar, resolveWorkspace } from "./wire-identity.ts";
import { resolveProject } from "./registry.ts";
import {
  canonicalAgentId,
  exportRolesBootstrap,
  formatResolvedBinding,
  isEmptyRoles,
  loadRoles,
  resolveAgentRoles,
  rolesPath,
  saveRoles,
  setRoleModel,
  unsetRoleModel,
  useProfile,
  type RoleBinding,
  type RolesFile,
} from "./roles.ts";
import { buildTelemetryOtlpEnv } from "./telemetry-settings.ts";
import { checkTruthfulness, formatViolations } from "./truthfulness.ts";

const DEFAULT_BASE_URL = "https://petbox.3po.su";
// Where THIS run's kit lives (npx cache or a checkout's src dir).
const HERE = dirname(fileURLToPath(import.meta.url));
// Stable install location: the kit is copied here and every global hook/plugin link points at
// it, so wiring survives npx cache eviction and does not depend on any checkout.
const STABLE = join(homedir(), ".petbox", "wire");

// ---- arg parsing -----------------------------------------------------------

type Args = {
  dir: string;
  projectKey: string;
  env?: string;
  key?: string;
  workspace?: string;
  cleanupLegacy: boolean;
  telemetry: boolean;
  telemetryLog: string;
};

const DEFAULT_TELEMETRY_LOG = "cc-telemetry";

// Print the usage banner and exit. `--help`/`-h` → stdout + exit 0; argument errors →
// stderr + exit WIRE_EXIT.usage (2). Same text either way.
function usage(exitCode: number = WIRE_EXIT.usage): never {
  const text =
    "usage: npx petbox-wire <dir> <projectKey> [--env VAR] [--key KEY] [--workspace WS] [--cleanup-legacy]\n" +
    "                       [--telemetry] [--telemetry-log <name>]\n" +
    "       npx petbox-wire update\n" +
    "       npx petbox-wire apply [--definition <key>] [--offline]\n" +
    "       npx petbox-wire doctor\n" +
    "       npx petbox-wire roles\n" +
    "       npx petbox-wire roles export\n" +
    "       npx petbox-wire profile use <name>\n" +
    "       npx petbox-wire model set <role> <model> [--agent <id>] [--profile <name>] [--allow-unknown-model]\n" +
    "       npx petbox-wire model unset <role> [--agent <id>] [--profile <name>]\n" +
    "       npx petbox-wire --help\n" +
    "\n" +
    "Wire a project to PetBox: global hooks, MCP configs and skills. (prompt-RAG was removed; wire and\n" +
    "update now prune any leftover UserPromptSubmit hook that targets the retired prompt-rag.ts.)\n" +
    "\n" +
    "--env VAR    Name of the environment variable holding the project's API key. Default for a fresh\n" +
    "             wire: PETBOX_<PROJECT>_API_KEY (same name the Connect page shows). An already-wired\n" +
    "             directory keeps the name recorded in ~/.petbox/projects.json.\n" +
    "--workspace  Override the workspace the server reports at GET /api/auth/validate (it fills\n" +
    "  WS         {{WORKSPACE}} in the skill template). No hardcoded default: if the server reports\n" +
    "             none and the flag is absent, the wire fails with exit 2 (usage).\n" +
    "\n" +
    "update       Refresh ~/.petbox/wire only (protocol/scripts/templates) from this package. Does not\n" +
    "             touch keys, registry, sticky telemetry, or per-project MCP/skills (it does prune the\n" +
    "             retired prompt-rag hook from the global settings files).\n" +
    "             Kit-copy only — does NOT compile per-harness agent artifacts (use apply).\n" +
    "apply        Compile per-harness startup artifacts from a portable agent definition + local\n" +
    "             role→model binding (~/.petbox/roles.json). Tries GET /api/{project}/agent-defs/{key}\n" +
    "             when cwd resolves via ~/.petbox/projects.json; on miss uses LKG cache\n" +
    "             (~/.petbox/cache/<project>.agent-def.json) with a staleness mark, else built-in\n" +
    "             DEFAULT only when no cache. --offline skips network (cache→DEFAULT). --definition\n" +
    "             <key> selects the server doc (default: default). Writes under the git worktree\n" +
    "             toplevel for cwd (`git rev-parse --show-toplevel`; falls back to cwd when cwd is not\n" +
    "             inside a git working tree) — NEVER the registry's project prefix, so apply run from a\n" +
    "             worktree targets that worktree, not the primary tree it was branched from. Always\n" +
    "             prints which root it resolved and how (git/cwd). Targets:\n" +
    "             claude-code .claude/agents/, opencode .opencode/agent/, droid .factory/droids/.\n" +
    "             Emitted names are namespaced petbox-<role> (frontmatter name: + file basename) —\n" +
    "             role.slug and ~/.petbox/roles.json stay unprefixed; only the render is. Every\n" +
    "             generated file carries a `petbox: managed` origin marker; apply REFUSES (loud,\n" +
    "             non-zero exit) to overwrite an existing file that lacks it — never clobbers a real\n" +
    "             user file. An owned pre-rename unprefixed leftover (e.g. worker.md) is removed once\n" +
    "             its petbox-<role>.md replacement is written; a same-named file without our marker is\n" +
    "             left alone.\n" +
    "             model: frontmatter only when bound (droid unbound → model: inherit) — never invents\n" +
    "             a concrete model id. Clean roles written; dirty skipped and reported.\n" +
    "             Exit codes: 0 full success; 1 hard failure (invalid definition/throw, OR a write was\n" +
    "             refused to avoid clobbering a non-PetBox file); 2 usage/args;\n" +
    "             3 truthfulness partial/block (policy — distinct from usage).\n" +
    "doctor       Run the definition truthfulness gate for every known harness against the default\n" +
    "             definition (+ optional local binding is noted, not required). Prints OK or each\n" +
    "             violation. Exit 0 all OK; 1 hard fail (invalid default def); 2 usage; 3 truthfulness\n" +
    "             (same taxonomy as apply — policy block is not a hard crash). Offline.\n" +
    "roles        Print the local role→model binding for the active profile (~/.petbox/roles.json).\n" +
    "             Offline; empty store exits 0 with a clear message (never invents default models).\n" +
    "roles export Write a bootstrap copy of roles.json to stdout (no secrets; pipe to a file on a\n" +
    "             new machine). Offline.\n" +
    "profile use  Set activeProfile in ~/.petbox/roles.json (creates an empty profile shell if missing).\n" +
    "             Offline. Re-run apply to rebuild artifacts after changing the active profile.\n" +
    "model set    Bind one role to a model for --agent (default: claude-code; aliases: cc/claude,\n" +
    "             factory/factory-droid/droid, opencode). Validated against harness-models.ts's\n" +
    "             three-tier policy — known/unknown write (unknown warns); a recognizably foreign\n" +
    "             harness id (e.g. a droid custom:* id in a claude-code binding — the 2026-07-12\n" +
    "             incident shape) is refused unless --allow-unknown-model forces it through. For\n" +
    "             claude-code, name a TIER ALIAS (sonnet|opus|haiku|fable|inherit) — the Task tool's\n" +
    "             model parameter is a closed enum of exactly those. Offline. Prints `next: petbox-\n" +
    "             wire apply` (this command never compiles artifacts itself).\n" +
    "model unset  Clear one role's binding for --agent (default: claude-code). A fair-empty binding\n" +
    "             a role can hold on purpose (e.g. reserve, when the machine lacks access to the\n" +
    "             tier it would otherwise be bound to) — the role then inherits the session model,\n" +
    "             and apply warns about that honestly. Offline. Prints `next: petbox-wire apply`.";
  (exitCode === 0 ? console.log : console.error)(text);
  process.exit(exitCode);
}

function parseArgs(argv: string[]): Args {
  const positionals: string[] = [];
  let env: string | undefined;
  let key: string | undefined;
  let workspace: string | undefined;
  let cleanupLegacy = false;
  let telemetry = false;
  let telemetryLog = DEFAULT_TELEMETRY_LOG;
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    if (a === undefined) continue; // unreachable: i < argv.length is the loop condition
    if (a === "--help" || a === "-h") usage(0);
    else if (a === "--env") env = argv[++i];
    else if (a === "--key") key = argv[++i];
    else if (a === "--workspace") workspace = argv[++i];
    else if (a === "--cleanup-legacy") cleanupLegacy = true;
    else if (a === "--telemetry") telemetry = true;
    // Missing value falls through to "" so the empty-log check below reports it as usage
    // error, same as every other required-value flag here.
    else if (a === "--telemetry-log") telemetryLog = argv[++i] ?? "";
    else if (a.startsWith("--")) {
      console.error(`unknown flag: ${a}`);
      usage();
    } else positionals.push(a);
  }
  if (!telemetryLog || !telemetryLog.trim()) {
    console.error("--telemetry-log requires a non-empty log name");
    usage();
  }
  const dir = positionals[0];
  const projectKey = positionals[1];
  if (dir === undefined || projectKey === undefined) {
    console.error("usage: <dir> and <projectKey> are both required");
    usage();
  }
  return {
    dir,
    projectKey,
    ...(env !== undefined ? { env } : {}),
    ...(key !== undefined ? { key } : {}),
    ...(workspace !== undefined ? { workspace } : {}),
    cleanupLegacy,
    telemetry,
    telemetryLog: telemetryLog.trim(),
  };
}

// True when argv is the safe kit-refresh subcommand (no project/key required).
function isUpdateCommand(argv: string[]): boolean {
  return argv[0] === "update";
}

function isDoctorCommand(argv: string[]): boolean {
  return argv[0] === "doctor";
}

function isApplyCommand(argv: string[]): boolean {
  return argv[0] === "apply";
}

// Local role/profile subcommands (offline; no project/key).
function isRolesCommand(argv: string[]): boolean {
  return argv[0] === "roles";
}

function isProfileCommand(argv: string[]): boolean {
  return argv[0] === "profile";
}

function isModelCommand(argv: string[]): boolean {
  return argv[0] === "model";
}

// doctor — truthfulness gate for each known harness vs the SAME definition apply would compile
// (doctor-gates-wrong-definition): server → LKG cache → built-in DEFAULT, exactly like apply
// (resolveApplyDefinition, shared with runApply below), not the hard-coded built-in default.
// Exit codes match apply (WIRE_EXIT): 0 OK; 1 hard (invalid def); 2 usage; 3 truthfulness policy.
async function runDoctor(argv: string[]): Promise<void> {
  let offline = false;
  for (let i = 1; i < argv.length; i++) {
    const a = argv[i];
    if (a === undefined) continue; // unreachable: i < argv.length is the loop condition
    if (a === "--help" || a === "-h") usage(0);
    else if (a === "--offline") offline = true;
    else {
      console.error(`doctor: unexpected argument: ${a}`);
      usage(WIRE_EXIT.usage);
    }
  }

  let definition: AgentDefinition;
  try {
    definition = await resolveApplyDefinition({
      offline,
      definitionKey: DEFAULT_DEFINITION_KEY,
      cwd: process.cwd(),
      label: "doctor",
    });
    validateAgentDefinition(definition);
  } catch (e) {
    console.error(`doctor: hard failure — ${e instanceof Error ? e.message : String(e)}`);
    process.exit(WIRE_EXIT.hard);
  }

  const roles = loadRoles();
  const bindingNote = isEmptyRoles(roles)
    ? "local binding: (empty — capability gate only; no model ids to check)"
    : `local binding: activeProfile=${roles.activeProfile} (model ids are gated against each harness)`;

  log(`doctor: definition="${definition.name}" (${definition.roles.length} roles)`);
  log(`doctor: ${bindingNote}`);

  let hadTruthfulnessBlock = false;
  for (const harness of HARNESS_IDS) {
    // Same gate apply uses: capabilities + the LOCAL model binding for this harness, so a
    // roles.json holding an id this harness cannot resolve fails here too (not at runtime).
    const violations = checkTruthfulness(
      definition,
      harness,
      resolveAgentRoles(roles, harness),
    );
    if (violations.length === 0) {
      log(`doctor: ${harness} — OK`);
    } else {
      hadTruthfulnessBlock = true;
      console.error(`doctor: ${harness} — ${violations.length} violation(s):`);
      console.error(formatViolations(violations));
    }
  }

  const code = classifyApplyExit({ hadTruthfulnessBlock });
  if (code === WIRE_EXIT.ok) {
    log("doctor: all known harnesses OK.");
    process.exit(WIRE_EXIT.ok);
  }
  console.error(
    `doctor: FAILED — a role requires a capability a harness does not declare, or is bound to a ` +
      `model a harness cannot resolve (exit ${WIRE_EXIT.truthfulness}).`,
  );
  process.exit(WIRE_EXIT.truthfulness);
}

// Result of one apply compile pass — a plain data record so a caller can decide what to do
// with it (exit with the code, or just log and continue — see performApply below).
type ApplyRunResult = {
  readonly code: number;
  readonly written: number;
  readonly writtenHarnesses: readonly string[];
  readonly partialHarnesses: readonly string[];
  readonly blockedHarnesses: readonly string[];
  readonly clobberBlockedPaths: readonly string[];
  readonly hardError: boolean;
};

// apply's core — compile per-harness artifacts (distinct from update kit-copy). Never calls
// process.exit: the `apply` subcommand (runApply, below) exits with the returned code; full
// wire's step 11 logs the result and keeps going regardless — a compile failure there must not
// abort a wiring run that already validated the key and wrote every other file (see this file's
// top doc comment on step 11 / fresh-wire-roster-unusable).
// Definition source: server fetch when registry resolves cwd; else offline default.
//
// Per role × harness (definition-truthfulness + wiring-startup-symmetry):
//   - dirty roles → skip + report (never silent); clean roles still written
// Result codes (see WIRE_EXIT / classifyApplyExit):
//   0 — full success: every known harness wrote all its roles, no skips
//   1 — hard failure: invalid definition / unexpected throw, or a clobber refusal
//   3 — truthfulness: policy blocked some roles/harnesses (partial write possible)
async function performApply(opts: {
  definitionKey: string;
  offline: boolean;
  label: string;
}): Promise<ApplyRunResult> {
  const { root, via } = resolveApplyRoot(process.cwd());
  let definition: AgentDefinition;
  try {
    definition = await resolveApplyDefinition({
      offline: opts.offline,
      definitionKey: opts.definitionKey,
      cwd: process.cwd(),
      label: opts.label,
    });
    validateAgentDefinition(definition);
  } catch (e) {
    console.error(`${opts.label}: hard failure — ${e instanceof Error ? e.message : String(e)}`);
    return {
      code: WIRE_EXIT.hard,
      written: 0,
      writtenHarnesses: [],
      partialHarnesses: [],
      blockedHarnesses: [],
      clobberBlockedPaths: [],
      hardError: true,
    };
  }

  const rolesData = loadRoles();
  log(`${opts.label}: root=${root} (via ${via})`);
  log(`${opts.label}: definition="${definition.name}", harnesses=${HARNESS_IDS.join(",")}`);

  let written = 0;
  const writtenHarnesses: string[] = [];
  const partialHarnesses: string[] = [];
  const blockedHarnesses: string[] = [];
  // Any writeArtifact refusal (bug: apply-clobbers-user-agent-files) — a real file that is not
  // ours sat where we needed to write. Distinct from the truthfulness gate: it can happen even
  // when every role is capability/model-clean, so it needs its own signal into the exit code.
  let clobberBlocked = false;
  const clobberedPaths: string[] = [];
  for (const harness of HARNESS_IDS) {
    const roleModels = resolveAgentRoles(rolesData, harness);
    const plan = planApply(definition, harness, roleModels);

    let writtenThisHarness = 0;
    let clobberedThisHarness = false;
    for (const file of plan.files) {
      const abs = join(root, file.relativePath);
      const outcome = writeArtifact(abs, file.content);
      if (outcome.kind === "blocked") {
        clobberBlocked = true;
        clobberedThisHarness = true;
        clobberedPaths.push(abs);
        console.error(
          `${opts.label}: REFUSED to overwrite ${abs} — it exists and does not carry the PetBox ` +
            `origin marker (no \`petbox: managed\` in its frontmatter), so it is a real file, not ` +
            `one apply wrote before. Nothing was touched. Move it aside (or rename the role) and ` +
            `re-run apply.`,
        );
        continue;
      }
      log(
        `${opts.label}: wrote ${abs}` +
          (outcome.reason === "own" ? " (updated in place — ours)" : ""),
      );
      written++;
      writtenThisHarness++;

      // Namespacing rename cleanup: remove an OWNED pre-rename unprefixed leftover now that its
      // petbox-<role> replacement exists. Only after a successful write — never orphan a role by
      // deleting the old file when the new one could not be written. Never touches a path that
      // lacks our marker (cleanupLegacyArtifact's own contract — see apply-write.ts).
      if (file.legacyRelativePath !== file.relativePath) {
        const legacyAbs = join(root, file.legacyRelativePath);
        const legacyOutcome = cleanupLegacyArtifact(legacyAbs);
        if (legacyOutcome === "removed") {
          log(`${opts.label}: removed legacy unprefixed ${legacyAbs} (ours, superseded by ${abs})`);
        } else if (legacyOutcome === "kept-foreign") {
          log(
            `${opts.label}: left ${legacyAbs} in place — not ours (no PetBox origin marker); not renamed or deleted.`,
          );
        }
      }
    }

    for (const w of plan.warnings) {
      console.error(`${opts.label}: warn — ${w}`);
    }

    if (plan.violations.length > 0 || clobberedThisHarness) {
      if (plan.violations.length > 0) {
        console.error(formatApplyBlocked(plan.violations, plan.harness, plan.skippedRoles));
      }
      if (writtenThisHarness > 0) partialHarnesses.push(plan.harness);
      else blockedHarnesses.push(plan.harness);
    } else if (writtenThisHarness > 0) {
      writtenHarnesses.push(plan.harness);
    }
  }

  // Structured summary (machine-readable-ish one line + human detail above).
  const summary = {
    writtenFiles: written,
    writtenHarnesses,
    partialHarnesses,
    blockedHarnesses,
    clobberBlockedPaths: clobberedPaths,
  };
  log(
    `${opts.label}: result written=${written} ` +
      `ok=[${writtenHarnesses.join(",")}] ` +
      `partial=[${partialHarnesses.join(",")}] ` +
      `blocked=[${blockedHarnesses.join(",")}]` +
      (clobberedPaths.length > 0 ? ` clobber-refused=[${clobberedPaths.join(",")}]` : ""),
  );

  const hadTruthfulnessBlock = partialHarnesses.length > 0 || blockedHarnesses.length > 0;
  const code = classifyApplyExit({ hardError: clobberBlocked, hadTruthfulnessBlock });
  if (code === WIRE_EXIT.ok) {
    log(`${opts.label}: done — all known harnesses accepted every role.`);
  } else if (clobberBlocked) {
    console.error(
      `${opts.label}: hard failure — refused to overwrite ${clobberedPaths.length} non-PetBox ` +
        `file(s) (exit ${WIRE_EXIT.hard}). ${JSON.stringify(summary)}`,
    );
  } else {
    console.error(
      `${opts.label}: truthfulness partial — some roles/harnesses blocked (exit ${WIRE_EXIT.truthfulness}). ${JSON.stringify(summary)}`,
    );
  }

  return {
    code,
    written,
    writtenHarnesses,
    partialHarnesses,
    blockedHarnesses,
    clobberBlockedPaths: clobberedPaths,
    hardError: clobberBlocked,
  };
}

// `apply` subcommand — parses CLI args, runs performApply, exits with its code (2 on bad args,
// via usage()). Exit codes: 0 full success; 1 hard failure; 2 usage/args; 3 truthfulness.
async function runApply(argv: string[]): Promise<void> {
  let definitionKey = DEFAULT_DEFINITION_KEY;
  let offline = false;
  for (let i = 1; i < argv.length; i++) {
    const a = argv[i];
    if (a === undefined) continue; // unreachable: i < argv.length is the loop condition
    if (a === "--help" || a === "-h") usage(0);
    else if (a === "--offline") offline = true;
    else if (a === "--definition") {
      const v = argv[++i];
      if (!v || v.startsWith("--")) {
        console.error("apply: --definition requires a non-empty key");
        usage(WIRE_EXIT.usage);
      }
      definitionKey = v.trim();
      if (!definitionKey) {
        console.error("apply: --definition requires a non-empty key");
        usage(WIRE_EXIT.usage);
      }
    } else if (a.startsWith("--")) {
      console.error(`apply: unexpected argument: ${a}`);
      usage(WIRE_EXIT.usage);
    } else {
      console.error(`apply: unexpected argument: ${a}`);
      usage(WIRE_EXIT.usage);
    }
  }

  const result = await performApply({ definitionKey, offline, label: "apply" });
  process.exit(result.code);
}

// Server → LKG cache → built-in DEFAULT (definition-offline-lkg).
// Server is authoritative; disk is LKG replica. roles.json polarity is separate (not here).
// `label` prefixes the log lines: apply and doctor share this resolution so that doctor gates
// the definition apply would actually compile, and each says so under its own name.
async function resolveApplyDefinition(opts: {
  offline: boolean;
  definitionKey: string;
  cwd: string;
  label?: string;
}): Promise<AgentDefinition> {
  const label = opts.label ?? "apply";
  const resolved = resolveProject(opts.cwd);
  const got = await resolveAgentDefinitionWithLkg({
    offline: opts.offline,
    definitionKey: opts.definitionKey,
    ...(resolved?.project !== undefined ? { projectKey: resolved.project } : {}),
    ...(resolved?.baseUrl !== undefined ? { baseUrl: resolved.baseUrl } : {}),
    ...(resolved?.apiKey !== undefined ? { apiKey: resolved.apiKey } : {}),
  });

  if (got.source === "server") {
    log(`${label}: using server definition ${got.key} v${got.version}`);
  } else if (got.source === "lkg") {
    log(`${label}: ${got.staleMarker ?? "using LKG agent definition cache"}`);
    log(`${label}: using LKG definition ${got.key} v${got.version} (stale)`);
  } else {
    log(`${label}: offline default definition (no server, no LKG cache)`);
  }
  return got.definition;
}

// Print active profile + agent/role/model tree from ~/.petbox/roles.json. Exit 0 when empty.
function runRoles(argv: string[]): void {
  // roles | roles export  (+ optional --help)
  const sub = argv[1];
  if (sub === "--help" || sub === "-h") usage(0);
  if (sub === "export") {
    for (let i = 2; i < argv.length; i++) {
      const a = argv[i];
      if (a === undefined) continue; // unreachable: i < argv.length is the loop condition
      if (a === "--help" || a === "-h") usage(0);
      console.error(`roles export: unexpected argument: ${a}`);
      usage();
    }
    const data = loadRoles();
    // stdout only — bootstrap for a new machine (document in usage).
    console.log(JSON.stringify(exportRolesBootstrap(data), null, 2));
    return;
  }
  if (sub !== undefined) {
    console.error(`roles: unexpected argument: ${sub}`);
    usage();
  }
  const data = loadRoles();
  if (isEmptyRoles(data) && !data.profiles[data.activeProfile]) {
    log(
      `roles: no bindings in ${join(homedir(), ".petbox", "roles.json")} (activeProfile would be "default").\n` +
        `  Bindings are local — set models in that file or via a future apply path; nothing is invented.`,
    );
    return;
  }
  log(formatResolvedBinding(data));
}

// profile use <name> — set activeProfile; create empty shell if missing.
function runProfile(argv: string[]): void {
  const sub = argv[1];
  if (sub === "--help" || sub === "-h") usage(0);
  if (sub !== "use") {
    console.error(`profile: expected "use <name>"${sub ? `, got "${sub}"` : ""}`);
    usage();
  }
  const name = argv[2];
  if (!name || name.startsWith("-")) {
    console.error("profile use: requires a non-empty <name>");
    usage();
  }
  for (let i = 3; i < argv.length; i++) {
    const a = argv[i];
    if (a === undefined) continue; // unreachable: i < argv.length is the loop condition
    if (a === "--help" || a === "-h") usage(0);
    console.error(`profile use: unexpected argument: ${a}`);
    usage();
  }
  const before = loadRoles();
  const created = !before.profiles[name];
  const next = useProfile(before, name);
  saveRoles(next);
  log(
    `profile: activeProfile = "${next.activeProfile}"` +
      (created ? " (created empty profile shell)" : "") +
      `\n  wrote ${join(homedir(), ".petbox", "roles.json")}` +
      `\n  re-run apply to rebuild artifacts (profile use does not compile).`,
  );
}

// model set/unset — the tool verb for a role→model binding (spec binding-set-by-tool): before
// this, ~/.petbox/roles.json could ONLY be written by hand-editing an undocumented JSON format,
// and hand-editing it wrong is exactly how the 2026-07-12 incident (a droid id in the claude-code
// block) happened. Validation reuses harness-models.ts's classifyModel via roles.ts's
// setRoleModel — this file does not re-derive the policy.
function runModel(argv: string[]): void {
  const sub = argv[1];
  if (sub === "--help" || sub === "-h") usage(0);
  if (sub === "set") {
    runModelSet(argv);
    return;
  }
  if (sub === "unset") {
    runModelUnset(argv);
    return;
  }
  console.error(`model: expected "set <role> <model>" or "unset <role>"${sub ? `, got "${sub}"` : ""}`);
  usage();
}

// model set <role> <model> [--agent <id>] [--profile <name>] [--allow-unknown-model]
function runModelSet(argv: string[]): void {
  const role = argv[2];
  const model = argv[3];
  if (!role || role.startsWith("-")) {
    console.error("model set: requires a non-empty <role>");
    usage();
  }
  if (!model || model.startsWith("-")) {
    console.error("model set: requires a non-empty <model> (use `model unset <role>` to clear a binding)");
    usage();
  }
  let agent = "claude-code";
  let profile: string | undefined;
  let allowUnknownModel = false;
  for (let i = 4; i < argv.length; i++) {
    const a = argv[i];
    if (a === undefined) continue; // unreachable: i < argv.length is the loop condition
    if (a === "--help" || a === "-h") usage(0);
    else if (a === "--agent") agent = argv[++i] ?? "";
    else if (a === "--profile") profile = argv[++i];
    else if (a === "--allow-unknown-model") allowUnknownModel = true;
    else {
      console.error(`model set: unexpected argument: ${a}`);
      usage();
    }
  }
  if (!agent.trim()) {
    console.error("model set: --agent requires a non-empty value");
    usage();
  }

  const before = loadRoles();
  const result = setRoleModel(before, {
    agent,
    role,
    model,
    ...(profile !== undefined ? { profile } : {}),
    allowUnknownModel,
  });
  const canon = canonicalAgentId(agent);
  const profileName = (profile ?? "").trim() || before.activeProfile;
  if (!result.ok) {
    console.error(`model set: REFUSED — ${result.reason}`);
    process.exit(WIRE_EXIT.truthfulness);
  }
  saveRoles(result.data);
  log(`model: set ${canon}/${role} = ${model} (profile "${profileName}")`);
  if (result.warning) log(`model: warn — ${result.warning}`);
  log(`  wrote ${rolesPath()}`);
  log(`next: petbox-wire apply`);
}

// model unset <role> [--agent <id>] [--profile <name>]
function runModelUnset(argv: string[]): void {
  const role = argv[2];
  if (!role || role.startsWith("-")) {
    console.error("model unset: requires a non-empty <role>");
    usage();
  }
  let agent = "claude-code";
  let profile: string | undefined;
  for (let i = 3; i < argv.length; i++) {
    const a = argv[i];
    if (a === undefined) continue; // unreachable: i < argv.length is the loop condition
    if (a === "--help" || a === "-h") usage(0);
    else if (a === "--agent") agent = argv[++i] ?? "";
    else if (a === "--profile") profile = argv[++i];
    else {
      console.error(`model unset: unexpected argument: ${a}`);
      usage();
    }
  }
  if (!agent.trim()) {
    console.error("model unset: --agent requires a non-empty value");
    usage();
  }

  const before = loadRoles();
  const result = unsetRoleModel(before, {
    agent,
    role,
    ...(profile !== undefined ? { profile } : {}),
  });
  saveRoles(result.data);
  const canon = canonicalAgentId(agent);
  const profileName = (profile ?? "").trim() || before.activeProfile;
  if (result.removed) {
    log(`model: unset ${canon}/${role} (profile "${profileName}") — binding removed.`);
  } else {
    log(`model: ${canon}/${role} had no binding in profile "${profileName}" — nothing to remove.`);
  }
  log(`  wrote ${rolesPath()}`);
  log(`next: petbox-wire apply`);
}

// ---- small helpers ---------------------------------------------------------

const log = (msg: string) => console.log(msg);

// deriveEnvVar / resolveWorkspace live in wire-identity.ts (importable by unit tests; wire.ts
// itself runs main() on import and cannot be imported).

// Cross-platform key store (~/.petbox/keys.json): a flat JSON map { "<ENV_VAR>": "<key>" }.
// The kit's own hooks read it (via registry.ts) with no env var required. The per-project MCP
// configs still reference ${ENV_VAR}, so persistKeyForAgents() additionally materializes a real
// environment variable per platform.
function keysStorePath(): string {
  return join(homedir(), ".petbox", "keys.json");
}

// Read a key from the store. Returns "" if the file/entry is missing.
function readKeyFromStore(name: string): string {
  const store = readJson(keysStorePath());
  const v = store && typeof store === "object" ? store[name] : undefined;
  return typeof v === "string" ? v : "";
}

// Merge (never clobber) a key into the store. On POSIX tighten the file to 0600 (best-effort;
// skipped on Windows, where chmod is a no-op / can throw).
function writeKeyToStore(name: string, value: string): void {
  const path = keysStorePath();
  const store = readJson(path) ?? {};
  store[name] = value;
  writeJson(path, store);
  if (process.platform !== "win32") {
    try {
      chmodSync(path, 0o600);
    } catch {
      /* best-effort */
    }
  }
}

// The agent MCP configs (.mcp.json `${VAR}`, opencode `{env:VAR}`, droid `${VAR}`) resolve the
// key from a REAL environment variable — keys.json alone only covers the kit hooks. Persist it:
//  - Windows: user-scope env via PowerShell (visible to NEW terminals);
//  - POSIX: regenerate ~/.petbox/env.sh from the whole key store and make sure the login
//    profiles source it (marker-guarded, idempotent).
function persistKeyForAgents(envVar: string): void {
  if (process.platform === "win32") {
    const value = readKeyFromStore(envVar);
    try {
      execFileSync(
        "powershell",
        [
          "-NoProfile",
          "-NonInteractive",
          "-Command",
          `[Environment]::SetEnvironmentVariable('${envVar}', $env:WIRE_KEY_VALUE, 'User')`,
        ],
        { encoding: "utf8", env: { ...process.env, WIRE_KEY_VALUE: value } },
      );
      log(`[4/10] persisted ${envVar} to user-scope env (MCP configs read it; NEW terminals see it).`);
    } catch (e) {
      console.error(`[4/10] failed to persist ${envVar} to user-scope env — ${(e as Error).message}`);
      process.exit(1);
    }
    return;
  }

  // The actual file-writing logic lives in posix-env.ts — a side-effect-free module (no
  // top-level main()) so it stays importable by tests, unlike wire.ts itself.
  const envShPath = persistKeyForAgentsPosix(homedir());
  log(`[4/10] wrote ${envShPath} and ensured login profiles source it (MCP configs read ${envVar}; new login shells see it).`);
}

function readJson(path: string): any {
  try {
    return JSON.parse(readFileSync(path, "utf8"));
  } catch {
    return null;
  }
}

function writeJson(path: string, obj: unknown): void {
  mkdirSync(dirname(path), { recursive: true });
  writeFileSync(path, JSON.stringify(obj, null, 2) + "\n", "utf8");
}

function toFileUrl(absPath: string): string {
  // Build a file:/// URL the way Node does (handles Windows drive letters / backslashes).
  return new URL("file://" + (process.platform === "win32" ? "/" : "") + absPath.replace(/\\/g, "/")).href;
}

// ---- step 4: validate ------------------------------------------------------

// What GET /api/auth/validate reports back (AuthValidResponse). `workspace` is only present on
// servers new enough to report it — absent on an older deployment, which is a supported case
// (the caller then requires --workspace instead of inventing a default).
type ValidatedKey = {
  project?: string;
  scopes?: unknown;
  workspace?: string;
};

// Validate the key and RETURN what the server said about it (null when the server could not be
// asked meaningfully: endpoint missing / non-JSON body). Hard-exits on a rejected key or a
// project mismatch, so nothing is persisted for a bad key.
async function validateKey(
  baseUrl: string,
  key: string,
  projectKey: string,
): Promise<ValidatedKey | null> {
  const uri = `${baseUrl}/api/auth/validate`;
  let resp: Response;
  try {
    resp = await fetch(uri, {
      method: "GET",
      headers: { "X-Api-Key": key },
      signal: AbortSignal.timeout(12000),
    });
  } catch (e) {
    console.error(`[3/10] validate: could not reach ${uri} — ${(e as Error).message}. Aborting.`);
    process.exit(1);
  }

  if (resp.status === 401) {
    console.error(`[3/10] validate: server rejected the API key (401). Aborting.`);
    process.exit(1);
  }
  if (!resp.ok) {
    // Non-standard / endpoint missing → warn and continue.
    log(`[3/10] validate: unexpected status ${resp.status} (endpoint missing?); continuing with a warning.`);
    return null;
  }
  let body: any = null;
  try {
    body = await resp.json();
  } catch {
    log(`[3/10] validate: 200 but non-JSON body; continuing with a warning.`);
    return null;
  }
  // Contract (AuthApi.cs): 200 => { project, scopes, workspace } (camelCase, ASP.NET web
  // defaults). `workspace` is newer than the other two — an older server omits it.
  const proj = body?.project ?? body?.Project;
  if (typeof proj === "string" && proj.length > 0) {
    if (proj !== projectKey) {
      console.error(
        `[3/10] validate: key belongs to project '${proj}', not '${projectKey}'. Aborting.`,
      );
      process.exit(1);
    }
    log(`[3/10] validate: OK — key scoped to '${proj}'.`);
  } else {
    log(`[3/10] validate: 200 without a project field; continuing with a warning.`);
  }
  const ws = body?.workspace ?? body?.Workspace;
  const projectValue = typeof proj === "string" ? proj : undefined;
  const workspaceValue = typeof ws === "string" && ws.trim().length > 0 ? ws.trim() : undefined;
  return {
    ...(projectValue !== undefined ? { project: projectValue } : {}),
    scopes: body?.scopes ?? body?.Scopes,
    ...(workspaceValue !== undefined ? { workspace: workspaceValue } : {}),
  };
}

// ---- step 5: stable kit copy -----------------------------------------------

// Short content fingerprint of every regular file under root (path + bytes, sorted). Used by
// `update` (and full wire's stable copy) so operators can see before/after kit identity without
// a package version (published package.json is often 0.0.0 until CI stamps it).
function kitFingerprint(root: string): string {
  if (!existsSync(root)) return "(absent)";
  const files: string[] = [];
  const walk = (dir: string): void => {
    for (const name of readdirSync(dir).sort()) {
      const abs = join(dir, name);
      const st = statSync(abs);
      if (st.isDirectory()) walk(abs);
      else if (st.isFile()) files.push(abs);
    }
  };
  walk(root);
  const h = createHash("sha256");
  for (const abs of files) {
    const rel = relative(root, abs).replace(/\\/g, "/");
    h.update(rel);
    h.update("\0");
    h.update(readFileSync(abs));
    h.update("\0");
  }
  return h.digest("hex").slice(0, 12);
}

type CopyKitResult = { before: string; after: string; skipped: boolean };

// Copy the running kit (HERE — an npx cache dir or a checkout's src/) into the stable location
// (~/.petbox/wire/), overwriting. Every global hook/plugin link is computed from STABLE, so the
// wiring keeps working after npx evicts its cache or a checkout moves. Copies the whole src dir
// (all .ts files + templates/). No-op when already running the installed copy.
// `label` prefixes log lines (full wire uses "[5/10]"; `update` uses "update").
function copyKitToStable(label: string = "[5/10]"): CopyKitResult {
  const before = kitFingerprint(STABLE);
  if (resolve(HERE) === resolve(STABLE)) {
    log(`${label} stable copy: already running the installed kit at ${STABLE} — skipped.`);
    return { before, after: before, skipped: true };
  }
  mkdirSync(STABLE, { recursive: true });
  // Orphan cleanup — STABLE must be an EXACT MIRROR of HERE, never a UNION. cpSync overwrites but
  // never DELETES, so a file the shipped kit dropped (e.g. the retired prompt-rag.ts / mcp-client.ts)
  // would keep standing next to its NEWER peers → version skew: a leftover module importing a symbol
  // the current registry.ts no longer exports → SyntaxError at hook time. Remove every top-level
  // STABLE entry absent from HERE before copying, so the install can only ever match the shipped kit.
  // (The settings-side half of that removal is pruneLegacyPromptRagHooks — files AND hooks must go.)
  const hereEntries = new Set(readdirSync(HERE));
  for (const name of readdirSync(STABLE)) {
    if (!hereEntries.has(name)) {
      rmSync(join(STABLE, name), { recursive: true, force: true });
      log(`${label} orphan cleanup: removed ${name} from ${STABLE} (not shipped by this kit).`);
    }
  }
  cpSync(HERE, STABLE, { recursive: true, force: true });
  const after = kitFingerprint(STABLE);
  log(`${label} stable copy: kit installed to ${STABLE} (from ${HERE}); hash ${before} → ${after}.`);
  return { before, after, skipped: false };
}

// ---- migration: the retired prompt-RAG hook --------------------------------
//
// prompt-RAG is gone from the kit, but a machine that once ran `--prompt-rag` still has the hook
// command sitting in ~/.claude/settings.json and ~/.factory/settings.json, pointing at a
// prompt-rag.ts the kit no longer ships — which would fail on EVERY prompt. So: prune it
// UNCONDITIONALLY (no flag gates it any more) on every wire/update run. Idempotent by construction:
// the file is only rewritten when something was actually removed, so a second run is a byte-identical
// no-op. Other hooks in those files are never touched (see hook-prune.ts).
function pruneLegacyPromptRagHooks(label: string): void {
  const targets: Array<[string, string]> = [
    ["claude", join(homedir(), ".claude", "settings.json")],
    ["droid", join(homedir(), ".factory", "settings.json")],
  ];
  for (const [agent, path] of targets) {
    const settings = readJson(path);
    if (!settings || typeof settings !== "object") continue;
    if (!settings.hooks || typeof settings.hooks !== "object") continue;
    const pruned = pruneDeadPromptRagHooks(settings.hooks);
    if (pruned === 0) continue; // nothing to do → do not touch the file at all
    writeJson(path, settings);
    log(
      `${label} migration: pruned ${pruned} dead ${agent} prompt-rag UserPromptSubmit hook(s) from ${path} ` +
        `(the feature was removed; the hook pointed at a file the kit no longer ships).`,
    );
  }
}

// Safe kit-text refresh only: mirror THIS package into ~/.petbox/wire with orphan cleanup, plus the
// prompt-RAG hook migration (a refreshed kit drops prompt-rag.ts, so the dead hook must go with it).
// Intentionally does NOT: rotate/require API keys, touch ~/.petbox/keys.json or projects.json,
// (re)install any live hook, rewrite per-project MCP/skills, or flip the sticky telemetry flag.
// v1: STABLE kit only — re-run full wire to regenerate per-project skill bodies / MCP configs.
function runUpdate(argv: string[]): void {
  // `update` takes no flags other than help; reject extras so typos don't silently no-op.
  for (let i = 1; i < argv.length; i++) {
    const a = argv[i];
    if (a === undefined) continue; // unreachable: i < argv.length is the loop condition
    if (a === "--help" || a === "-h") usage(0);
    console.error(`update: unexpected argument: ${a}`);
    usage();
  }
  log(`update: refreshing stable kit ${STABLE} from ${HERE}`);
  log(`update: source hash ${kitFingerprint(HERE)}`);
  const result = copyKitToStable("update:");
  pruneLegacyPromptRagHooks("update:");
  if (result.skipped) {
    log(`update: done — kit already at ${STABLE} (hash ${result.after}).`);
  } else if (result.before === result.after) {
    log(`update: done — kit unchanged (hash ${result.after}).`);
  } else {
    log(`update: done — kit hash ${result.before} → ${result.after}.`);
  }
  log(
    "update: skipped keys, registry, sticky telemetry, global hooks reinstall, " +
      "and per-project MCP/skills (re-run full wire to refresh those).",
  );
}

// ---- step 6: registry ------------------------------------------------------

// Reuse the envVar of an existing registry entry for this exact prefix, so a plain re-run
// stays idempotent even when the var name was customized via --env in the past.
function registryEnvVar(prefix: string): string | undefined {
  const data = readJson(join(homedir(), ".petbox", "projects.json"));
  const entries: any[] = Array.isArray(data?.entries) ? data.entries : [];
  const norm = (p: string) => p.replace(/[\\/]+/g, "/").replace(/\/+$/, "").toLowerCase();
  const hit = entries.find((e) => norm(String(e?.prefix ?? "")) === norm(prefix));
  const v = hit?.envVar;
  return typeof v === "string" && v.length > 0 ? v : undefined;
}

// Upsert the registry entry for `prefix` — prefix/project/envVar (+ baseUrl when non-default).
// The entry is rewritten whole, so a retired key from an older kit (the removed `promptRag` gate)
// is dropped on the next wire rather than lingering as dead config.
function upsertRegistry(prefix: string, project: string, envVar: string, baseUrl: string): void {
  const path = join(homedir(), ".petbox", "projects.json");
  const data = readJson(path) ?? {};
  const entries: any[] = Array.isArray(data.entries) ? data.entries : [];
  const norm = (p: string) => p.replace(/[\\/]+/g, "/").replace(/\/+$/, "").toLowerCase();
  const np = norm(prefix);
  const next = entries.filter((e) => norm(String(e?.prefix ?? "")) !== np);
  const entry: any = { prefix, project, envVar };
  if (baseUrl !== DEFAULT_BASE_URL) entry.baseUrl = baseUrl;
  next.push(entry);
  writeJson(path, { entries: next });
  log(`[6/10] registry: upserted ${prefix} → ${project} (${envVar}) in ${path}`);
}

// ---- step 7: per-project files --------------------------------------------

// Merge one MCP server into a possibly-shared JSON config (Droid's .factory/mcp.json can hold
// team servers), preserving every other server and top-level key. Idempotent: re-running with
// the same inputs yields byte-identical output. Only the `petbox` entry is (re)generated.
function mergeMcpServer(path: string, name: string, server: unknown): void {
  const data = readJson(path) ?? {};
  if (!data.mcpServers || typeof data.mcpServers !== "object") data.mcpServers = {};
  data.mcpServers[name] = server;
  writeJson(path, data);
}

// Skill surfaces wire.ts writes the rendered petbox SKILL.md into. opencode is intentionally
// absent: it discovers the skill through its Claude-compatible path (`.claude/skills/…`), and a
// second same-name copy under `.opencode/skills/` would be a duplicate whose resolution opencode
// does not document. Droid reads its own `.factory/skills/` root (its compat path is
// `.agent/skills/`, NOT `.claude/skills/`), so it needs a dedicated copy.
const SKILL_SURFACES: string[][] = [
  [".claude", "skills"], // Claude Code (native) + opencode (Claude-compatible discovery)
  [".factory", "skills"], // Factory Droid (native)
];

function writeProjectFiles(dir: string, project: string, envVar: string, workspace: string): void {
  // .mcp.json (Claude Code) — petbox-only file owned by wire.ts, regenerated whole.
  const mcp = {
    mcpServers: {
      petbox: {
        type: "http",
        url: `${DEFAULT_BASE_URL}/mcp`,
        headers: { "X-Api-Key": `\${${envVar}}` },
      },
    },
  };
  writeJson(join(dir, ".mcp.json"), mcp);
  log(`[7/10] wrote ${join(dir, ".mcp.json")}`);

  // .opencode/opencode.json (opencode) — petbox-only file owned by wire.ts, regenerated whole.
  const oc = {
    $schema: "https://opencode.ai/config.json",
    mcp: {
      petbox: {
        type: "remote",
        url: `${DEFAULT_BASE_URL}/mcp`,
        enabled: true,
        headers: { "X-Api-Key": `{env:${envVar}}` },
      },
    },
  };
  writeJson(join(dir, ".opencode", "opencode.json"), oc);
  log(`[7/10] wrote ${join(dir, ".opencode", "opencode.json")}`);

  // .factory/mcp.json (Factory Droid) — a project-level MCP config that may be shared with team
  // servers, so merge (never clobber) rather than regenerate whole. Droid supports `${VAR}`
  // env-var expansion in header values, so the key stays out of the file (no secret committed).
  const droidMcpPath = join(dir, ".factory", "mcp.json");
  mergeMcpServer(droidMcpPath, "petbox", {
    type: "http",
    url: `${DEFAULT_BASE_URL}/mcp`,
    headers: { "X-Api-Key": `\${${envVar}}` },
    disabled: false,
  });
  log(`[7/10] merged petbox MCP server into ${droidMcpPath}`);

  // SKILL.md — render once from the template, then drop a copy into every native skill surface.
  const tpl = readFileSync(join(HERE, "templates", "SKILL.md"), "utf8");
  const skill = tpl
    .replace(/\{\{PROJECT\}\}/g, project)
    .replace(/\{\{WORKSPACE\}\}/g, workspace);
  for (const surface of SKILL_SURFACES) {
    const skillPath = join(dir, ...surface, "petbox", "SKILL.md");
    mkdirSync(dirname(skillPath), { recursive: true });
    writeFileSync(skillPath, skill, "utf8");
    log(`[7/10] wrote ${skillPath}`);
  }

  // Agent-factory skill — on-demand procedure (no project placeholders). Same surfaces as petbox.
  const factoryTpl = readFileSync(join(HERE, "templates", "agent-factory", "SKILL.md"), "utf8");
  for (const surface of SKILL_SURFACES) {
    const skillPath = join(dir, ...surface, "petbox-agent-factory", "SKILL.md");
    mkdirSync(dirname(skillPath), { recursive: true });
    writeFileSync(skillPath, factoryTpl, "utf8");
    log(`[7/10] wrote ${skillPath}`);
  }
}

// ---- step 7b: telemetry (opt-in, --telemetry) ------------------------------

// Ensure the target named log exists. PetBox OTLP ingest is project+log-scoped in the PATH
// (`/v1/{metrics,logs}/{project}/{log}`) and returns 404 if the log is absent, so the log MUST
// pre-exist before Claude Code starts exporting. Idempotent: a 409 ("already exists") is success.
async function ensureTelemetryLog(
  baseUrl: string,
  project: string,
  key: string,
  logName: string,
): Promise<void> {
  const uri = `${baseUrl}/api/logs/${project}/logs`;
  let resp: Response;
  try {
    resp = await fetch(uri, {
      method: "POST",
      headers: { "X-Api-Key": key, "Content-Type": "application/json" },
      body: JSON.stringify({ name: logName }),
      signal: AbortSignal.timeout(12000),
    });
  } catch (e) {
    console.error(`[telemetry] could not reach ${uri} — ${(e as Error).message}. Aborting.`);
    process.exit(1);
  }
  if (resp.ok || resp.status === 409) {
    // 201 Created (fresh) or 409 Conflict (already exists) — both mean the log is ready.
    log(`[telemetry] log '${logName}' ready in project '${project}' (HTTP ${resp.status}).`);
    return;
  }
  const text = await resp.text().catch(() => "");
  console.error(`[telemetry] failed to ensure log '${logName}' — HTTP ${resp.status} ${text}. Aborting.`);
  process.exit(1);
}

// Persist the OTLP export env for Claude Code, SPLIT by secrecy (per-project, NOT machine-scope:
// machine env would make EVERY CC session on the box export):
//  - non-secret vars (endpoints, protocol, exporters, interval) → .claude/settings.json `env`;
//  - the API-key-bearing OTEL_EXPORTER_OTLP_HEADERS → .claude/settings.local.json `env` (the CC
//    local-override file, conventionally gitignored) — the raw key lands there, never in the
//    shareable settings.json.
// Why the raw key and not `${envVar}`: Claude Code does NOT expand `${VAR}` inside settings.json
// `env` values (unlike `.mcp.json`) — empirically verified 2026-07-06 — so a reference form sends
// the literal string and the ingest returns 401. The key already lives plaintext in
// ~/.petbox/keys.json; settings.local.json (gitignored) is the same trust boundary, per-project.
// A literal key PINS the value: if the project api key rotates the header goes stale — re-run wire
// (--telemetry) to re-provision. The header shape/name is built in buildTelemetryOtlpEnv (which the
// unit test covers); this function only merges the result into the two files, preserving other
// keys/env entries — only our OTEL_* / CLAUDE_* keys change.
function writeTelemetrySettings(
  dir: string,
  project: string,
  key: string,
  logName: string,
): void {
  const { publicEnv, secretEnv } = buildTelemetryOtlpEnv(DEFAULT_BASE_URL, project, key, logName);
  // Non-secret export config → committable settings.json.
  mergeEnvIntoSettings(join(dir, ".claude", "settings.json"), publicEnv);
  log(`[telemetry] merged OTLP export config into .claude/settings.json (log '${logName}').`);

  // Secret header (carries the API key) → gitignored settings.local.json.
  mergeEnvIntoSettings(join(dir, ".claude", "settings.local.json"), secretEnv);
  log(`[telemetry] wrote OTLP auth header into .claude/settings.local.json (gitignored — keep it out of git).`);
}

// Merge an env map into a Claude Code settings file's `env` block, preserving all other keys/entries.
function mergeEnvIntoSettings(settingsPath: string, envMap: Record<string, string>): void {
  const settings = readJson(settingsPath) ?? {};
  if (!settings.env || typeof settings.env !== "object") settings.env = {};
  for (const [k, v] of Object.entries(envMap)) settings.env[k] = v;
  writeJson(settingsPath, settings);
}

// ---- step 8: global install ------------------------------------------------

// Hook commands are `node "<STABLE>/<file>.ts"`. Older wirings (this repo's own owner box
// included) left commands pointing at a checkout — e.g. `node "D:\…\agents\wiring\push-session.ts"`.
// Recognize a kit hook by these command suffixes so we can prune the stale ones (any that don't
// equal one of this run's stable commands).
const KIT_HOOK_SUFFIXES = [
  'push-session.ts"',
  'pull-memory.ts"',
  'droid-push-session.ts"',
  'droid-pull-memory.ts"',
];

// Remove kit hook entries whose command is NOT one of the current stable commands (validCmds),
// then drop any now-empty groups. Mutates hooksObj in place; returns the count pruned.
function pruneStaleKitHooks(hooksObj: any, validCmds: Set<string>): number {
  let removed = 0;
  for (const event of Object.keys(hooksObj)) {
    const groups: any[] = Array.isArray(hooksObj[event]) ? hooksObj[event] : [];
    for (const g of groups) {
      if (!g || !Array.isArray(g.hooks)) continue;
      const before = g.hooks.length;
      g.hooks = g.hooks.filter((h: any) => {
        const c = typeof h?.command === "string" ? h.command : "";
        const isKit = KIT_HOOK_SUFFIXES.some((s) => c.endsWith(s));
        return !(isKit && !validCmds.has(c));
      });
      removed += before - g.hooks.length;
    }
    hooksObj[event] = groups.filter((g) => !(g && Array.isArray(g.hooks) && g.hooks.length === 0));
  }
  return removed;
}

// Install the live kit hooks (Stop / SessionStart on both agents) and, on the way through, run the
// retired-prompt-RAG migration on each settings object before it is written back — one read, one
// write per file, so the prune costs nothing extra and cannot be skipped.
function installGlobalHooks(): void {
  const pushCmd = `node "${join(STABLE, "push-session.ts")}"`;
  const pullCmd = `node "${join(STABLE, "pull-memory.ts")}"`;
  const droidPushCmd = `node "${join(STABLE, "droid-push-session.ts")}"`;
  const droidPullCmd = `node "${join(STABLE, "droid-pull-memory.ts")}"`;
  // Every kit hook command this run considers current — the prune keeps these, drops the rest.
  const validCmds = new Set([pushCmd, pullCmd, droidPushCmd, droidPullCmd]);

  const settingsPath = join(homedir(), ".claude", "settings.json");
  const settings = readJson(settingsPath) ?? {};
  if (!settings.hooks || typeof settings.hooks !== "object") settings.hooks = {};
  const prunedClaude = pruneStaleKitHooks(settings.hooks, validCmds);
  if (prunedClaude > 0) log(`[8/10] pruned ${prunedClaude} stale claude kit hook(s) not pointing at ${STABLE}.`);

  // Claude Code hooks shape: settings.hooks[event] = [{ matcher?, hooks: [{type, command}] }]
  const ensureHook = (event: string, command: string) => {
    const groups: any[] = Array.isArray(settings.hooks[event]) ? settings.hooks[event] : [];
    const already = groups.some(
      (g) => Array.isArray(g?.hooks) && g.hooks.some((h: any) => h?.command === command),
    );
    if (already) {
      log(`[8/10] claude hook ${event} already present — skipped.`);
      return;
    }
    groups.push({ hooks: [{ type: "command", command }] });
    settings.hooks[event] = groups;
    log(`[8/10] claude hook ${event} added.`);
  };

  ensureHook("Stop", pushCmd);
  ensureHook("SessionStart", pullCmd);
  // Migration (unconditional): drop any leftover prompt-rag UserPromptSubmit hook — the feature is
  // gone and the kit no longer ships the file its command points at.
  const ragPrunedClaude = pruneDeadPromptRagHooks(settings.hooks);
  if (ragPrunedClaude > 0) {
    log(`[8/10] pruned ${ragPrunedClaude} dead claude prompt-rag UserPromptSubmit hook(s) (feature removed).`);
  }
  writeJson(settingsPath, settings);
  log(`[8/10] merged hooks into ${settingsPath}`);

  // Factory Droid hooks: same JSON shape as Claude Code, merged into ~/.factory/settings.json
  // under the `hooks` key (a documented fallback location). Droid exposes petbox tools as
  // `mcp__petbox__*` and delivers Claude-Code-compatible snake_case payloads, so it reuses the
  // shared protocol/append flow via its own thin hooks. No `enableHooks` flag is set: the droid
  // hooks reference does not document one gating hook execution.
  const droidSettingsPath = join(homedir(), ".factory", "settings.json");
  const droidSettings = readJson(droidSettingsPath) ?? {};
  if (!droidSettings.hooks || typeof droidSettings.hooks !== "object") droidSettings.hooks = {};
  const prunedDroid = pruneStaleKitHooks(droidSettings.hooks, validCmds);
  if (prunedDroid > 0) log(`[8/10] pruned ${prunedDroid} stale droid kit hook(s) not pointing at ${STABLE}.`);

  const ensureDroidHook = (event: string, command: string) => {
    const groups: any[] = Array.isArray(droidSettings.hooks[event]) ? droidSettings.hooks[event] : [];
    const already = groups.some(
      (g) => Array.isArray(g?.hooks) && g.hooks.some((h: any) => h?.command === command),
    );
    if (already) {
      log(`[8/10] droid hook ${event} already present — skipped.`);
      return;
    }
    groups.push({ hooks: [{ type: "command", command }] });
    droidSettings.hooks[event] = groups;
    log(`[8/10] droid hook ${event} added.`);
  };

  ensureDroidHook("Stop", droidPushCmd);
  ensureDroidHook("SessionStart", droidPullCmd);
  // Same migration on the Droid side. Its legacy command carried an `--agent droid` suffix, which is
  // why the prune matches the QUOTED BASENAME (`prompt-rag.ts"`) and catches both variants.
  const ragPrunedDroid = pruneDeadPromptRagHooks(droidSettings.hooks);
  if (ragPrunedDroid > 0) {
    log(`[8/10] pruned ${ragPrunedDroid} dead droid prompt-rag UserPromptSubmit hook(s) (feature removed).`);
  }
  writeJson(droidSettingsPath, droidSettings);
  log(`[8/10] merged droid hooks into ${droidSettingsPath}`);

  // Global opencode plugin: thin shim re-exporting the kit plugin from the stable copy's file
  // URL (overwritten each run, so an old shim pointing at a checkout is replaced).
  const pluginAbs = join(STABLE, "opencode-plugin.ts");
  const pluginUrl = toFileUrl(pluginAbs);
  const shimDir = join(homedir(), ".config", "opencode", "plugins");
  mkdirSync(shimDir, { recursive: true });
  const shimPath = join(shimDir, "petbox.ts");
  const shim = `// Auto-generated by wire.ts — global PetBox opencode plugin shim.
// Re-exports the kit plugin from its absolute path so a single source of truth serves
// every project (the active project is resolved from cwd via the shared registry).
export { PetboxPlugin, default } from "${pluginUrl}";
`;
  writeFileSync(shimPath, shim, "utf8");
  log(`[8/10] wrote global opencode plugin shim ${shimPath} → ${pluginUrl}`);
}

// ---- step 9: cleanup legacy ------------------------------------------------

function cleanupLegacy(dir: string): void {
  // .claude/hooks/ — drop the whole per-project hooks folder.
  const hooksDir = join(dir, ".claude", "hooks");
  if (existsSync(hooksDir)) {
    rmSync(hooksDir, { recursive: true, force: true });
    log(`[9/10] removed ${hooksDir}`);
  }

  // .claude/settings.local.json — drop ONLY the hooks key, keep permissions etc.
  const localPath = join(dir, ".claude", "settings.local.json");
  const local = readJson(localPath);
  if (local && typeof local === "object" && "hooks" in local) {
    delete local.hooks;
    writeJson(localPath, local);
    log(`[9/10] removed 'hooks' key from ${localPath}`);
  }

  // .opencode/plugin/ — drop the per-project plugin folder.
  const pluginDir = join(dir, ".opencode", "plugin");
  if (existsSync(pluginDir)) {
    rmSync(pluginDir, { recursive: true, force: true });
    log(`[9/10] removed ${pluginDir}`);
  }

  // .opencode node deps — only if package.json depends solely on @opencode-ai/plugin.
  const ocPkgPath = join(dir, ".opencode", "package.json");
  const ocPkg = readJson(ocPkgPath);
  if (ocPkg) {
    const deps = { ...(ocPkg.dependencies ?? {}), ...(ocPkg.devDependencies ?? {}) };
    const keys = Object.keys(deps);
    const onlyPlugin = keys.length > 0 && keys.every((k) => k === "@opencode-ai/plugin");
    const noDeps = keys.length === 0;
    if (onlyPlugin || noDeps) {
      for (const f of ["package.json", "bun.lock", "node_modules"]) {
        const p = join(dir, ".opencode", f);
        if (existsSync(p)) {
          rmSync(p, { recursive: true, force: true });
          log(`[9/10] removed ${p}`);
        }
      }
    } else {
      log(`[9/10] kept .opencode deps — package.json has non-plugin deps: ${keys.join(", ")}`);
    }
  }
}

// ---- step 10: self-smoke ---------------------------------------------------

// Returns whether the smoke succeeded — the caller (main()) uses this to decide whether "done."
// is allowed to print (selfsmoke-failure-prints-done: a failed smoke must never be followed by
// a line that reads like success). Response classification itself lives in self-smoke.ts so it
// is unit-testable without a network call; this wrapper only owns the fetch + exit-code side effect.
async function selfSmoke(baseUrl: string, project: string, key: string): Promise<boolean> {
  const uri = `${baseUrl}/api/sessions/${project}/wire-smoke?agent=wire`;
  const body = JSON.stringify({ role: "user", content: "wire.ts self-smoke — verifying the session push pipeline." });
  let resp: Response;
  try {
    resp = await fetch(uri, {
      method: "POST",
      headers: { "X-Api-Key": key, "Content-Type": "application/x-ndjson; charset=utf-8" },
      body,
      signal: AbortSignal.timeout(12000),
    });
  } catch (e) {
    console.error(`[10/10] self-smoke: POST failed — ${(e as Error).message}`);
    process.exitCode = 1;
    return false;
  }
  const text = await resp.text();
  const result = classifySelfSmokeResponse(resp.ok, resp.status, text);
  if (result.ok) {
    log(result.message);
  } else {
    console.error(result.message);
    process.exitCode = 1;
  }
  return result.ok;
}

// ---- step 11: seed a default role binding + apply --------------------------

// Default claude-code role→model seed for a BRAND-NEW machine (fresh-wire-roster-unusable):
// aliases only (never a concrete id — see harness-models.ts / the claude-api skill's live
// finding that the Task tool's `model` param is a closed enum of exactly these four tiers).
// `reserve` is deliberately ABSENT: the tester's machine may not have access to the `fable`
// tier, and binding it there would repeat the 2026-07-12 incident shape (a binding the harness
// cannot actually resolve) — an unbound role inherits the session model instead, and apply
// already warns about that honestly (see planApply's warnings).
const DEFAULT_ROLE_MODEL_SEED: Readonly<Record<string, string>> = {
  orchestrator: "opus",
  worker: "sonnet",
  utility: "haiku",
  explore: "haiku",
};

// Seed ~/.petbox/roles.json with a default profile ONLY when the file does not exist yet —
// never touches an operator's own bindings. Without this, a brand-new machine's roles.json is
// empty, every generated .claude/agents/*.md ships with no `model:` key at all, and every role
// silently rides the session model — the exact silent tier-drift class the 2026-07-12 incident
// ("worker on Opus for a whole session, nobody noticed") grew from.
function seedDefaultRoleBindingsIfMissing(label: string): void {
  if (existsSync(rolesPath())) {
    log(`${label} roles: ${rolesPath()} already exists — left as-is (existing bindings kept).`);
    return;
  }
  const roles: Record<string, RoleBinding> = {};
  for (const [role, model] of Object.entries(DEFAULT_ROLE_MODEL_SEED)) roles[role] = { model };
  const data: RolesFile = {
    activeProfile: "default",
    profiles: { default: { agents: { "claude-code": { roles } } } },
  };
  saveRoles(data);
  log(
    `${label} roles: seeded ${rolesPath()} — profile "default", claude-code aliases ` +
      `(orchestrator=opus, worker=sonnet, utility=haiku, explore=haiku; reserve left unbound — ` +
      `bind it yourself with \`petbox-wire model set reserve <tier>\` if this machine has access).`,
  );
}

// ---- main ------------------------------------------------------------------

async function main(): Promise<void> {
  const argv = process.argv.slice(2);
  // Subcommands that need no project/key. Must run before parseArgs, which requires
  // <dir> <projectKey> positionals for the full wire path.
  if (isUpdateCommand(argv)) {
    runUpdate(argv);
    return;
  }
  if (isDoctorCommand(argv)) {
    await runDoctor(argv);
    return;
  }
  if (isApplyCommand(argv)) {
    await runApply(argv);
    return;
  }
  if (isRolesCommand(argv)) {
    runRoles(argv);
    return;
  }
  if (isProfileCommand(argv)) {
    runProfile(argv);
    return;
  }
  if (isModelCommand(argv)) {
    runModel(argv);
    return;
  }

  const args = parseArgs(argv);
  const dir = resolve(args.dir);
  const project = args.projectKey;
  const baseUrl = DEFAULT_BASE_URL;

  if (!existsSync(dir)) {
    console.error(`directory does not exist: ${dir}`);
    process.exit(1);
  }

  // 1. env var — explicit --env wins; else reuse the existing registry entry (idempotent
  // re-run with a customized var name); else derive from the project key.
  const envVar = args.env ?? registryEnvVar(dir) ?? deriveEnvVar(project);
  log(`[1/10] envVar = ${envVar}`);

  // 2. key — --key wins, else process env (owner's inherited user-scope var still works),
  // else the cross-platform key store (~/.petbox/keys.json).
  let key = args.key;
  if (key) {
    log(`[2/10] using --key from the command line.`);
  } else {
    key = process.env[envVar] || readKeyFromStore(envVar) || "";
    if (!key) {
      console.error(
        `[2/10] no API key found.\n` +
          `  Provide one with --key <KEY>, or set ${envVar} (env or ~/.petbox/keys.json) first.\n` +
          `  A key for a NEW project has no agent on it yet, so it can't mint its own key —\n` +
          `  ask a workspace admin to mint one on the project's Connect page:\n` +
          `    /ui/admin/ws/<workspace>/projects/${project}/connect (mint only happens there)\n` +
          `  Then re-run with --key <KEY>. (Minting keys is out of scope for wire.ts.)`,
      );
      process.exit(1);
    }
    log(`[2/10] using existing ${envVar} (env or key store).`);
  }

  // 3. validate — BEFORE persisting anything, so a bad key never lands in the stores.
  const validated = await validateKey(baseUrl, key, project);

  // 3b. workspace for the skill template ({{WORKSPACE}}): --workspace overrides the workspace the
  // server reports at /api/auth/validate; there is NO hardcoded default. Resolved BEFORE any
  // persistence so an unresolvable workspace leaves the machine untouched.
  const ws = resolveWorkspace(args.workspace, validated?.workspace);
  if (!ws.ok) {
    console.error(ws.message);
    process.exit(ws.exitCode);
  }
  const workspace = ws.workspace;
  log(
    `[3/10] workspace = ${workspace} (${ws.source === "flag" ? "--workspace" : "reported by /api/auth/validate"}).`,
  );

  // 4. persist everywhere agents look: keys.json (kit hooks read it immediately) + a real
  // env var per platform (the per-project MCP configs reference ${envVar}). Idempotent, so
  // re-runs self-heal a machine where only one of the two exists.
  writeKeyToStore(envVar, key);
  log(`[4/10] persisted ${envVar} to ${keysStorePath()}.`);
  persistKeyForAgents(envVar);

  // 5. stable kit copy
  copyKitToStable();

  // 6. registry
  upsertRegistry(dir, project, envVar, baseUrl);

  // 7. project files
  writeProjectFiles(dir, project, envVar, workspace);

  // 7b. telemetry (opt-in): ensure the target log exists, then persist the OTLP export env into
  // the project's .claude/settings.json. Off by default — only when --telemetry is passed.
  // opencode/droid are intentionally NOT wired: their OTLP exporters append `/v1/{signal}` to a
  // base endpoint and cannot carry the project/log path PetBox's ingest requires — CC-only.
  if (args.telemetry) {
    await ensureTelemetryLog(baseUrl, project, key, args.telemetryLog);
    writeTelemetrySettings(dir, project, key, args.telemetryLog);
  } else {
    log(`[telemetry] not requested — skipped (pass --telemetry to enable Claude Code OTLP export).`);
  }

  // 8. global install — installs the live Stop/SessionStart hooks and, unconditionally, prunes the
  // dead prompt-rag UserPromptSubmit hook left behind by a kit that still had the feature.
  installGlobalHooks();

  // 9. cleanup legacy
  if (args.cleanupLegacy) cleanupLegacy(dir);
  else log(`[9/10] cleanup-legacy not requested — skipped.`);

  // 10. self-smoke
  const smokeOk = await selfSmoke(baseUrl, project, key);

  // 11. seed a default role→model binding (fresh machine only) + apply — compile per-harness
  // startup artifacts NOW, so the freshly-wired roster is actually usable. Never aborts the run:
  // the key is already validated and every other file is already written by this point, so a
  // compile hiccup here (e.g. a transient agent-defs fetch failure — resolveApplyDefinition
  // still falls back to LKG/DEFAULT) is reported loudly but does not flip wire's own exit code;
  // re-running `petbox-wire apply` retries just this step (fresh-wire-roster-unusable).
  seedDefaultRoleBindingsIfMissing("[11/10]");
  const applyResult = await performApply({
    definitionKey: DEFAULT_DEFINITION_KEY,
    offline: false,
    label: "[11/10]",
  });
  if (applyResult.code !== WIRE_EXIT.ok) {
    console.error(`[11/10] next: petbox-wire apply`);
  }

  // Terminal message set depends on the smoke outcome — a failure must be the LAST line, in
  // red, never followed by "done." (selfsmoke-failure-prints-done).
  const finish = finishWireRun({
    smokeOk,
    envVar,
    envVarPresentInProcess: !!process.env[envVar],
    platform: process.platform,
  });
  for (const line of finish.lines) {
    if (finish.toStderr) console.error(line);
    else log(line);
  }
}

main().catch((e) => {
  console.error(e?.stack ?? String(e));
  process.exit(1);
});
