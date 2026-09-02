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
//        - .claude/skills/petbox-methodology/SKILL.md    (thin pointer at the LIVE methodology
//                                                          this project runs — see skill-files.ts)
//        - .factory/skills/petbox-methodology/SKILL.md
//        - .claude/skills/petbox-write-economy/SKILL.md  (bodyRef/fragment write-cost mechanisms)
//        - .factory/skills/petbox-write-economy/SKILL.md
//        - .claude/skills/petbox-node-authoring/SKILL.md (node/comment BODY structure: GFM
//                                                          callouts, the sanitized-SVG diagram
//                                                          convention, when NOT to diagram)
//        - .factory/skills/petbox-node-authoring/SKILL.md
//    8. install the global Claude + Droid hooks + opencode plugin (merge, never clobber live files);
//       all links point at the stable copy (~/.petbox/wire/), and any dead prompt-RAG hook left by
//       an older kit is pruned. Claude Code additionally gets a PreToolUse hook (subagent-model-
//       gate.ts) blocking a petbox-* subagent spawn that also passes an explicit `model` — droid/
//       opencode are not wired for it (the `model` spawn parameter is Claude-Code-only)
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
import { basename, dirname, join, relative, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import {
  AGENT_DEF_OFFLINE_STALE_MARKER,
  DEFAULT_DEFINITION_KEY,
  resolveAgentDefinitionWithLkg,
  type ResolvedAgentDefinition,
} from "./agent-def-fetch.ts";
import { readWireLogTail, wireLog, wireLogPath } from "./wire-log.ts";
import {
  DEFAULT_AGENT_DEFINITION,
  diffAgentDefinitions,
  validateAgentDefinition,
  type AgentDefinition,
} from "./agent-definition.ts";
import { formatApplyBlocked, planApply } from "./apply-artifacts.ts";
import { sweepOrphanArtifacts } from "./apply-orphans.ts";
import { resolveApplyRoot } from "./apply-root.ts";
import { cleanupLegacyArtifact, writeArtifact } from "./apply-write.ts";
import { findDanglingTargets, formatDanglingTargets } from "./definition-integrity.ts";
import { HARNESS_IDS } from "./harness-capabilities.ts";
import { pruneDeadPromptRagHooks } from "./hook-prune.ts";
import {
  cascadeErrors,
  formatCascadeProvenance,
  formatCascadeReport,
  formatCascadeTrace,
  LayerSourceError,
  resolveDefinitionLayers,
  type CascadeResolution,
} from "./layer-cascade.ts";
import { persistKeyForAgentsPosix } from "./posix-env.ts";
import { classifySelfSmokeResponse, finishWireRun } from "./self-smoke.ts";
import {
  buildSkillReports,
  describeWorkspaceProbeFailure,
  formatSkillFile,
  PROJECT_SKILLS,
  probeWorkspace,
  writeSkillFiles,
  type SkillWriteResult,
} from "./skill-files.ts";
import {
  BANNER_BUDGET_WARN_FRACTION,
  bannerBudgetLegsOrUnreachable,
  bannerBudgetWarnThresholdBytes,
  formatBannerBudgetLeg,
  runRegistryStatus,
  runStatus,
} from "./status.ts";
import { SESSION_BANNER_BUDGET_BYTES } from "./session-budget.ts";
import {
  abortRun,
  classifyApplyExit,
  exitWith,
  RunAbort,
  strongestExitCode,
  WIRE_EXIT,
} from "./wire-exit.ts";
import { deriveEnvVar, resolveWorkspace } from "./wire-identity.ts";
import { checkNpmWireDrift, formatNpmWireDrift } from "./npm-wire-drift.ts";
import { readRegistry, registryPath, resolveProject, type RegistryEntry } from "./registry.ts";
import {
  canonicalAgentId,
  DEFAULT_ROLE_MODEL_SEED,
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

// ---- loopback sandbox (petbox-wire's OWN test suite only) ------------------
//
// The full `wire` command's base URL is a constant on purpose: no env var may redirect a real
// wiring run at another host, because that run hands over an API key. But the six exit-code
// regressions this seam exists for (wire-six-remaining-exit-races) live ONLY on the full-wire
// path — validateKey, resolveWorkspace, ensureTelemetryLog — and wire.ts runs main() at import
// time, so its internals cannot be imported by a test (see posix-env.ts / wire-identity.ts on
// why testable logic is extracted instead). A spawn-based test therefore has no other way in,
// and "no way in" is exactly how six live-network exit points went unproven for three fix rounds.
//
// So: honored ONLY when it names an http:// LOOPBACK address. Anything else — a real host, https,
// a DNS name — is ignored with a loud warning, so this can never point a wire at a foreign server.
// It also disables the one machine-GLOBAL write on the path (Windows user-scope env persistence,
// step 4), so running the suite cannot leave junk in the developer's own environment.
const SANDBOX_BASE_URL_ENV = "PETBOX_WIRE_TEST_LOOPBACK_BASE_URL";

function loopbackSandboxBaseUrl(): string | undefined {
  const raw = process.env[SANDBOX_BASE_URL_ENV]?.trim();
  if (!raw) return undefined;
  let parsed: URL;
  try {
    parsed = new URL(raw);
  } catch {
    console.error(`${SANDBOX_BASE_URL_ENV} is not a URL — ignored; using ${DEFAULT_BASE_URL}.`);
    return undefined;
  }
  const isLoopback =
    parsed.protocol === "http:" &&
    (parsed.hostname === "127.0.0.1" || parsed.hostname === "localhost" || parsed.hostname === "[::1]");
  if (!isLoopback) {
    console.error(
      `${SANDBOX_BASE_URL_ENV}=${raw} is not an http:// loopback address — ignored; using ` +
        `${DEFAULT_BASE_URL}. (This seam exists for petbox-wire's own tests and may never point a ` +
        `real wiring run, which hands over an API key, at another host.)`,
    );
    return undefined;
  }
  return raw.replace(/\/+$/, "");
}

// Resolved ONCE, at module load, so the "ignored, not loopback" warning is unconditional: it must
// fire even for an invocation that returns during arg parsing (`--help`), otherwise a mis-set
// override could look accepted. undefined = no override in effect.
const SANDBOX_BASE_URL: string | undefined = loopbackSandboxBaseUrl();

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
    "       npx petbox-wire apply [--definition <key>] [--offline] [--all [--dry-run]]\n" +
    "       npx petbox-wire status [--offline] [--all]\n" +
    "       npx petbox-wire doctor [--offline]\n" +
    "       npx petbox-wire layers [dir...]\n" +
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
    "--key KEY    The API key, passed directly. Prefer setting the env var (--env / above) instead:\n" +
    "             npm logs the full argv — this key included — to ~/.npm/_logs/*.log in plain text\n" +
    "             with no rotation. --key still works (existing automation keeps running) but every\n" +
    "             use prints a warning pointing here; it never prints the key itself.\n" +
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
    "             3 truthfulness partial/block (policy — distinct from usage);\n" +
    "             4 INCOMPLETE — a requested step did not run for a reason you did not ask for (the\n" +
    "             workspace probe failed, so skills were not refreshed). An INTENTIONAL skip stays 0:\n" +
    "             --offline and an unregistered directory are things you asked for. When 1 or 3 also\n" +
    "             apply they win the code; the skip still shows in the printed summary.\n" +
    "             --all runs apply once per registered project (~/.petbox/projects.json) instead of\n" +
    "             cwd only, with a per-project outcome line (written/unchanged/refused/missing-dir/\n" +
    "             error) and an aggregate exit code (the strongest across every project). A registry\n" +
    "             entry whose directory no longer exists is reported and skipped, never aborts the\n" +
    "             rest of the sweep. --dry-run computes and prints every outcome WITHOUT writing or\n" +
    "             deleting anything — use it before a bare `--all`, which writes into every registered\n" +
    "             project's working directory, including ones with uncommitted changes.\n" +
    "status       Print FACT, not a verdict: per declared role x harness, the materialized artifact\n" +
    "             path, its bound model, WHERE that model came from (roster = ~/.petbox/roles.json;\n" +
    "             seed = DEFAULT_ROLE_MODEL_SEED preview, roles.json absent, nothing written; none =\n" +
    "             a PROBLEM — no source at all, apply will hard-refuse on a closed-model-space harness\n" +
    "             or warn-and-inherit on an open one), and the command to change it. Plus a four-pillar\n" +
    "             summary: definition source (server/LKG cache/built-in copy, degradation labelled),\n" +
    "             roster completeness, memory canon (absent/empty/N of 10k chars), and skill files\n" +
    "             (materialized? byte-identical to the current template?). Reads the SAME resolvers\n" +
    "             apply/doctor use; never gates, never writes. --offline skips the definition/canon/\n" +
    "             skill-template network calls (materialization-only facts still print). Always exits\n" +
    "             0 unless status itself crashes — it asserts nothing about correctness. Also prints\n" +
    "             whether npm's published 'latest' kit is behind this checkout's local `main` (best-\n" +
    "             effort — skipped outside a git checkout with a resolvable `main` ref).\n" +
    "             --all: one screen across the WHOLE registry instead of cwd only — one row per\n" +
    "             registered project (skill composition vs. the currently installed kit's templates,\n" +
    "             and what's wrong), plus the same npm-wire tag line once at the top. Read-only\n" +
    "             (never writes), safe to run against every project in the registry.\n" +
    "doctor       Resolve the agent definition the same way apply does (server → LKG cache → built-in\n" +
    "             default), then run the truthfulness gate for every known harness against THAT\n" +
    "             definition, with the harness's local binding fed into the gate — so a roles.json id\n" +
    "             the harness cannot resolve fails here rather than at runtime. Prints OK or each\n" +
    "             violation. Also reports built-in-vs-server definition drift (a built-in that is merely\n" +
    "             poorer than the server is labelled degradation and is normal; real divergence is\n" +
    "             called out separately), skill-file drift against the kit templates, the session-banner\n" +
    "             budget margin, and a tail of ~/.petbox/wire.log. Network checks are skipped with an\n" +
    "             explicit reason when the server is unreachable, never silently. --offline skips them\n" +
    "             itself up front: no live definition fetch (falls straight to LKG cache, then built-in\n" +
    "             default), no skill-file drift check, no banner-budget check — the truthfulness gate\n" +
    "             still runs, against whichever definition that leaves you with.\n" +
    "             Exit 0 all OK; 1 hard fail (invalid default def); 2 usage; 3 truthfulness\n" +
    "             (same taxonomy as apply — policy block is not a hard crash; doctor never reports 4,\n" +
    "             it skips no step of its own).\n" +
    "layers       Diagnose the definition-layer cascade: which layer directories exist on this\n" +
    "             machine, where they physically live, and — by FIELD, never \"the files differ\" —\n" +
    "             what they disagree about. Built on layer-cascade.ts's own resolver (base layer\n" +
    "             excluded: it still ships as a flat JSON inside the package, not a directory, so\n" +
    "             this command says so instead of silently comparing two of three layers). With no\n" +
    "             <dir> arguments, checks this command's own conventional defaults: ~/.petbox/agents\n" +
    "             (user) and <project root>/.petbox/agents (project) — pass explicit directories\n" +
    "             (lowest priority first) to check anything else. Never writes; never touches apply's\n" +
    "             own exit code. Exit 0 clean (2+ layers, zero cascade errors); 1 diverged (a cascade\n" +
    "             ERROR was found — E0-E5/E1); 2 usage; 3 COULD NOT CHECK (fewer than two layers\n" +
    "             present, or a present layer's source is broken) — never confused with 0 or 1.\n" +
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

function isStatusCommand(argv: string[]): boolean {
  return argv[0] === "status";
}

// Local diagnostic subcommand (offline; no project/key) — see runLayers below.
function isLayersCommand(argv: string[]): boolean {
  return argv[0] === "layers";
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
// Also prints a built-in-vs-live definition drift check (bug: builtin-definition-drifts-no-catchup)
// when this run actually reached the server, and a materialized-skill-vs-template drift check
// (same bug, item 3, plus skill-files-clobber-and-apply-skips item 3) when this project is
// registered and its live workspace resolves — both informational only, never changing the exit
// code.
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
  let resolved: ResolvedAgentDefinition;
  try {
    resolved = await resolveApplyDefinition({
      offline,
      definitionKey: DEFAULT_DEFINITION_KEY,
      cwd: process.cwd(),
      label: "doctor",
    });
    definition = resolved.definition;
    validateAgentDefinition(definition);
    // Same referential-integrity gate apply applies, on the same resolution (doctor exists to
    // gate the definition apply would compile — doctor-gates-wrong-definition). A doctor that
    // says OK while apply hard-fails on the very next command is worse than no doctor.
    const dangling = findDanglingTargets(definition);
    if (dangling.length > 0) {
      throw new Error(
        `definition "${definition.name}" names ${dangling.length} role(s) it does not define:\n` +
          formatDanglingTargets(dangling),
      );
    }
  } catch (e) {
    console.error(`doctor: hard failure — ${e instanceof Error ? e.message : String(e)}`);
    // exitWith + return, never a hard process.exit() — see wire-exit.ts's header for why
    // (doctor does a SECOND live fetch, the workspace probe, and two sequential fetches in one
    // process is exactly what turned this from a latent risk into a reproducible crash).
    exitWith(WIRE_EXIT.hard);
    return;
  }

  const roles = loadRoles();
  const bindingNote = isEmptyRoles(roles)
    ? "local binding: (empty — capability gate only; no model ids to check)"
    : `local binding: activeProfile=${roles.activeProfile} (model ids are gated against each harness)`;

  log(`doctor: definition="${definition.name}" (${definition.roles.length} roles)`);
  log(`doctor: ${bindingNote}`);

  // Drift check (bug: builtin-definition-drifts-no-catchup) — informational only, never gates
  // the exit code: nothing here blocks a harness from running correctly (apply already prefers
  // the live server copy over the built-in), it only tells an operator the kit-shipped offline
  // fallback (DEFAULT_AGENT_DEFINITION) has fallen behind what the project's server holds, so the
  // next machine to go offline compiles a stale roster. Only meaningful when this run actually
  // reached the server for the "default" key — an LKG or built-in-default source has nothing
  // live to compare against, so the check is a clean skip, not a failure (doctor is offline by
  // design; this is the one call that leaves that design, and its absence is not an error).
  //
  // Each skip reason below is named EXPLICITLY (bug: doctor-reports-answering-server-unreachable,
  // same class as probe-collapses-http-errors-into-network) — an answered server (404/401/403/5xx)
  // is never folded into "unreachable", and a deliberate `--offline` is never indistinguishable
  // from a genuine failure. Only a true network/timeout miss (resolved carries none of these
  // flags) still says "unreachable", and that word is now honest.
  if (offline) {
    log("doctor: drift check skipped (--offline).");
  } else if (resolved.notFoundOnServer) {
    log(
      "doctor: drift check skipped (server reachable, but this project has no server-side " +
        "definition yet — nothing to compare against).",
    );
  } else if (resolved.forbidden) {
    log(
      "doctor: drift check skipped (server reachable but refused the request — 401/403, check " +
        "the API key's agents:read scope).",
    );
  } else if (resolved.httpError) {
    log(`doctor: drift check skipped (${describeAgentDefHttpError(resolved.httpError)} — nothing to compare against).`);
  } else if (resolved.parseError) {
    log(
      "doctor: drift check skipped (server answered but its response body did not parse — " +
        "nothing to compare against).",
    );
  } else if (resolved.source !== "server") {
    log("doctor: drift check skipped (server unreachable).");
  } else if (definition.name !== DEFAULT_AGENT_DEFINITION.name) {
    log(
      `doctor: drift check skipped (live definition is named "${definition.name}", not ` +
        `"${DEFAULT_AGENT_DEFINITION.name}" — nothing to compare the built-in default against).`,
    );
  } else {
    const { degradations, divergences } = diffAgentDefinitions(DEFAULT_AGENT_DEFINITION, definition);
    if (degradations.length === 0 && divergences.length === 0) {
      log("doctor: built-in default definition matches the live server definition — no drift.");
    } else {
      if (degradations.length > 0) {
        // Info-level, never console.error: the built-in is an offline bootstrap minimum, not a
        // mirror of the live document — a role added server-side is expected to be missing here
        // until the kit's next release, not a defect to chase.
        log(
          `doctor: built-in default is missing ${degradations.length} role(s) present in the live server ` +
            `definition — normal (offline bootstrap minimum, not a mirror):`,
        );
        for (const line of degradations) log(`  - ${line}`);
      }
      if (divergences.length > 0) {
        console.error(
          `doctor: built-in default definition has drifted from the live server definition (${divergences.length}):`,
        );
        for (const line of divergences) console.error(`  - ${line}`);
      }
    }
  }

  // Skill-template drift check (bugs: skill-files-clobber-and-apply-skips item 3,
  // builtin-definition-drifts-no-catchup item 3 — the one item both cards' verdicts named as the
  // last thing left undone) — informational only, same reasoning as the definition drift check
  // just above: doctor is offline by design, and comparing a materialized skill file against its
  // template needs this project's LIVE workspace for the {{WORKSPACE}} placeholder (the registry
  // never stores it — see skill-files.ts's probeWorkspace), so `--offline`, an unregistered
  // directory, or an unreachable server are all a clean skip, never a failure. Reuses the SAME
  // comparison `status` pillar 4 already had (skill-files.ts's buildSkillReports/formatSkillFile)
  // — never a second diff (see that module's header on this consolidation).
  const { root: skillCheckRoot } = resolveApplyRoot(process.cwd());
  const resolvedForSkillCheck = resolveProject(skillCheckRoot);
  if (offline) {
    log("doctor: skill check skipped (--offline).");
  } else if (!resolvedForSkillCheck) {
    log(`doctor: skill check skipped (${skillCheckRoot} is not a registered project; run \`wire\` here first).`);
  } else {
    const probe = await probeWorkspace(resolvedForSkillCheck.baseUrl, resolvedForSkillCheck.apiKey);
    if (!probe.ok) {
      // Shared taxonomy + wording (skill-files.ts's describeWorkspaceProbeFailure) — same helper
      // apply's skill refresh below uses, so a real HTTP error (bug:
      // probe-collapses-http-errors-into-network) is never called "network/timeout" here either.
      const reasonText = describeWorkspaceProbeFailure(probe);
      log(`doctor: skill check skipped (${reasonText}).`);
    } else {
      const reports = buildSkillReports(
        skillCheckRoot,
        join(HERE, "templates"),
        resolvedForSkillCheck.project,
        probe.workspace,
      );
      // Foreign (BLOCKED) and drifted are different defects with different remedies — name them
      // separately, never fold them into one "mismatch" count (task requirement).
      const blocked = reports.filter((r) => r.state === "foreign");
      const drifted = reports.filter((r) => r.state === "ours" && r.matchesTemplate === false);
      if (blocked.length === 0 && drifted.length === 0) {
        log("doctor: skill files — every materialized copy matches its current template, no foreign files.");
      } else {
        if (blocked.length > 0) {
          console.error(`doctor: skill files — ${blocked.length} foreign (BLOCKED) file(s), not ours to fix:`);
          for (const r of blocked) console.error(`  - ${formatSkillFile(r)}`);
        }
        if (drifted.length > 0) {
          console.error(`doctor: skill files — ${drifted.length} file(s) drifted from the current template (run \`petbox-wire apply\` to refresh):`);
          for (const r of drifted) console.error(`  - ${formatSkillFile(r)}`);
        }
      }
    }
  }

  // npm-wire tag drift (task kit-version-lands-everywhere-and-sweeps item 3): a merge to `main`
  // does NOT publish the kit — only a pushed `npm-wire` tag does (.github/workflows/ci.yml). That
  // gap used to be silent: nothing told an operator main had moved on without the tag following.
  // Best-effort/skip-by-default (npm-wire-drift.ts) — only fires when this cwd is a git checkout
  // with a local `main` ref AND the npm registry answers; every other machine gets a clean skip,
  // never a failure, never touching the exit code (informational, like every other doctor drift
  // check). Never gated on the skill/banner checks' `resolvedForSkillCheck` — this one needs no
  // registered project, only local git + a public network call.
  if (offline) {
    log("doctor: npm-wire tag check skipped (--offline).");
  } else {
    const npmDrift = await checkNpmWireDrift(process.cwd());
    if (npmDrift.status === "ahead" || npmDrift.status === "diverged") {
      console.error(`doctor: ${formatNpmWireDrift(npmDrift)}`);
    } else {
      log(`doctor: ${formatNpmWireDrift(npmDrift)}`);
    }
  }

  // Session-banner budget check (card canon-write-gate-banner-budget) — informational only, same
  // skip taxonomy as the drift checks above (--offline / unregistered project / unreachable
  // server all degrade to a named skip, never a failure, never touching the exit code). Runs the
  // SAME assembly SessionStart actually ships (status.ts's computeBannerBudgetLegs: buildProtocol
  // + fetchCanonBlock + assembleSessionBanner against SESSION_BANNER_BUDGET_BYTES), for both
  // `source` values a real session can start with — never a hardcoded canon-size threshold (see
  // that module's doc comment on why one measurably rejects healthy canon on a bad protocol day).
  if (offline) {
    log("doctor: banner-budget check skipped (--offline).");
  } else if (!resolvedForSkillCheck) {
    log(
      `doctor: banner-budget check skipped (${skillCheckRoot} is not a registered project; run \`wire\` here first).`,
    );
  } else {
    const bannerResult = await bannerBudgetLegsOrUnreachable(resolvedForSkillCheck, definition);
    if (!bannerResult.ok) {
      log("doctor: banner-budget check skipped (server did not answer GET /api/memory/{project}/canon).");
    } else {
      const warnThresholdBytes = bannerBudgetWarnThresholdBytes();
      const thin = bannerResult.legs.filter((leg) => leg.marginBytes < warnThresholdBytes);
      const warnPercent = Math.round(BANNER_BUDGET_WARN_FRACTION * 100);
      if (thin.length === 0) {
        log(
          `doctor: banner budget — every source keeps at least ${warnPercent}% margin ` +
            `(${warnThresholdBytes}B) against the ${SESSION_BANNER_BUDGET_BYTES}B session banner budget.`,
        );
      } else {
        console.error(
          `doctor: banner budget — ${thin.length} of ${bannerResult.legs.length} source(s) below the ` +
            `${warnPercent}% margin threshold (${warnThresholdBytes}B):`,
        );
        for (const leg of thin) console.error(`  - ${formatBannerBudgetLeg(leg)}`);
      }
    }
  }

  // Class-Б trace tail (bug: wire-silent-failures-invisible) — informational only, never gates
  // the exit code, same spirit as the drift check above: doctor is offline by design, and most
  // machines will NEVER trip a Class-Б event, so an absent/empty wire.log is not a failure, just
  // "nothing has silently broken yet". This is the one place an operator can see the corrupt
  // roles.json / corrupt registry / corrupt LKG cache / scope-refused fetch events that hooks and
  // best-effort code paths were told to log but not necessarily print loudly.
  const wireLogTail = readWireLogTail(10);
  if (wireLogTail.length === 0) {
    log(`doctor: wire.log — no recorded silent-failure traces (${wireLogPath()}).`);
  } else {
    log(`doctor: wire.log — ${wireLogTail.length} most recent trace line(s) (${wireLogPath()}):`);
    for (const line of wireLogTail) log(`  ${line}`);
  }

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
    // Exit cleanly instead of tearing the process down mid-close (bug surfaced by this task's
    // skill-drift check): doctor can make TWO sequential live fetches in one run (the definition
    // fetch above, then the workspace probe) — a hard process.exit() right after races Windows'
    // async-handle teardown for whichever socket is still closing
    // (`Assertion failed: !(handle->flags & UV_HANDLE_CLOSING), src\win\async.c` — reproduced on
    // this machine against the real server, not merely a local test fixture: `Connection: close`
    // guarantees no keep-alive socket lingers, but does not make its OS-level teardown
    // instantaneous). exitWith (wire-exit.ts) is the one sanctioned spelling of that fix.
    exitWith(WIRE_EXIT.ok);
    return;
  }
  console.error(
    `doctor: FAILED — a role requires a capability a harness does not declare, or is bound to a ` +
      `model a harness cannot resolve (exit ${WIRE_EXIT.truthfulness}).`,
  );
  exitWith(WIRE_EXIT.truthfulness);
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
//   4 — incomplete: a requested step was skipped for a reason the user did not ask for (the
//       workspace probe failed) — an INTENTIONAL skip (--offline, unregistered dir) stays 0
// Best-effort workspace lookup for apply's skill refresh below (bug:
// skill-files-clobber-and-apply-skips). UNLIKE validateKey (the full-wire path, step 3b), a
// failure here must NEVER abort apply — skills are secondary to the agent artifacts apply exists
// to write. probeWorkspace (skill-files.ts) returns a discriminated `ok:false` on ANY failure —
// the caller then skips the skill refresh rather than inventing a workspace apply was never given
// (same "never a hardcoded default" rule as wire-identity.ts) — but distinguishes WHY
// (wire-silent-failures-invisible): a 401/403 (key lacks the scope /api/auth/validate needs) is
// not the same problem as a genuine network/timeout failure, and neither is the same as a 200
// that simply omits `workspace` (older server). doctor's skill-drift check (below) shares this
// SAME probe — moved out of this file so it stopped being a second ~12-line copy alongside
// status.ts's own (see skill-files.ts's header on that dedup).

async function performApply(opts: {
  definitionKey: string;
  offline: boolean;
  label: string;
  /** Directory apply resolves/writes against. Defaults to process.cwd() — the single-project
   * `apply`/`wire` path. `apply --all` (runApplyAll below) passes each registry entry's own
   * directory here instead, so a registry sweep never depends on this process's cwd. */
  cwd?: string;
  /** Compute and print every outcome WITHOUT writing/deleting anything (task:
   * kit-version-lands-everywhere-and-sweeps item 2's "show what would be done first" gate for a
   * registry-wide sweep across OTHER people's project directories). Flows into writeArtifact,
   * removeOwnedArtifact/cleanupLegacyArtifact, sweepOrphanArtifacts and writeSkillFiles — the
   * same primitives the real write path uses, so a preview and a real run can never disagree.
   * Defaults to false; every existing single-project caller is unaffected. */
  dryRun?: boolean;
}): Promise<ApplyRunResult> {
  const cwd = opts.cwd ?? process.cwd();
  const dryRun = opts.dryRun ?? false;
  const { root, via } = resolveApplyRoot(cwd);
  let definition: AgentDefinition;
  let resolved: ResolvedAgentDefinition;
  let rolesData: RolesFile;
  try {
    resolved = await resolveApplyDefinition({
      offline: opts.offline,
      definitionKey: opts.definitionKey,
      cwd,
      label: opts.label,
    });
    definition = resolved.definition;
    validateAgentDefinition(definition);
    // Referential integrity of what we are about to RENDER (bug:
    // artifact-integrity-dangling-and-orphans, spec definition-truthfulness). A role whose
    // artifact names a spawn/escalation target that is not in this definition would ship an
    // instruction to use a `subagent_type` that does not exist on disk. That is a refusal, not
    // a warning: a partially-written set of artifacts where one of them lies is worse than no
    // write at all, so this runs BEFORE the first file is touched.
    const dangling = findDanglingTargets(definition);
    if (dangling.length > 0) {
      throw new Error(
        `definition "${definition.name}" names ${dangling.length} role(s) it does not define:\n` +
          formatDanglingTargets(dangling) +
          `\nNothing was written. Fix the definition (add the role, or drop the reference).`,
      );
    }
    // strict: a corrupt roles.json must hard-fail apply, not silently compile as "no bindings"
    // (wire-silent-failures-invisible — the 2026-07-12 "worker rides on Opus" incident shape).
    rolesData = loadRoles(homedir(), { strict: true });
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

  log(`${opts.label}: root=${root} (via ${via})`);
  // One grep-able line naming WHICH document this run compiled and WHERE it came from. D18
  // makes this load-bearing, not cosmetic: stage 2's confirmation is "apply ran on all three
  // harnesses WITHOUT going to the server for the definition", and that is unprovable unless
  // apply states its own resolution path in its summary rather than only in the narrative lines
  // resolveApplyDefinition prints above.
  const versionSuffix =
    resolved.key !== undefined && resolved.version !== undefined
      ? ` key=${resolved.key} v${resolved.version}`
      : "";
  log(
    `${opts.label}: definition="${definition.name}" source=${resolved.source}${versionSuffix}` +
      `${resolved.stale ? " (stale)" : ""}, harnesses=${HARNESS_IDS.join(",")}`,
  );

  // Orphan sweep gate (bug: artifact-integrity-dangling-and-orphans). Deleting the artifact of
  // a role that left the definition is only safe when the definition is the AUTHORITATIVE one.
  // A degraded resolve — LKG replica, or the kit's offline baseline after a network blip or a
  // 404 — legitimately holds FEWER roles than the project really has, and sweeping against it
  // would delete live roles' artifacts because the network hiccuped. Server-sourced only, for
  // now; when the source of truth moves to file layers (D13/D18 stage 2) that source joins this
  // gate, and the server one leaves with the rest of the server path.
  const orphanSweepSource = resolved.source === "server";
  if (!orphanSweepSource) {
    log(
      `${opts.label}: orphan sweep skipped — the definition came from '${resolved.source}', not the ` +
        `server; a degraded resolve may be missing roles this project really has, and deleting ` +
        `their artifacts on that basis would be destructive.`,
    );
  }

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
      const outcome = writeArtifact(abs, file.content, { dryRun });
      if (outcome.kind === "blocked") {
        clobberBlocked = true;
        clobberedThisHarness = true;
        clobberedPaths.push(abs);
        console.error(
          `${opts.label}: ${dryRun ? "would refuse" : "REFUSED"} to overwrite ${abs} — it exists and does not ` +
            `carry the PetBox origin marker (no \`petbox: managed\` in its frontmatter), so it is a real file, ` +
            `not one apply wrote before. ${dryRun ? "Nothing would be touched." : "Nothing was touched."} Move ` +
            `it aside (or rename the role) and re-run apply.`,
        );
        continue;
      }
      if (outcome.reason !== "unchanged") {
        log(
          `${opts.label}: ${dryRun ? "would write" : "wrote"} ${abs}` +
            (outcome.reason === "own" ? " (updated in place — ours)" : ""),
        );
        written++;
        writtenThisHarness++;
      } else {
        log(`${opts.label}: ${abs} unchanged (already matches)`);
      }

      // Namespacing rename cleanup: remove an OWNED pre-rename unprefixed leftover now that its
      // petbox-<role> replacement exists. Only after a successful write — never orphan a role by
      // deleting the old file when the new one could not be written. Never touches a path that
      // lacks our marker (cleanupLegacyArtifact's own contract — see apply-write.ts).
      if (file.legacyRelativePath !== file.relativePath) {
        const legacyAbs = join(root, file.legacyRelativePath);
        const legacyOutcome = cleanupLegacyArtifact(legacyAbs, { dryRun });
        if (legacyOutcome === "removed") {
          log(
            `${opts.label}: ${dryRun ? "would remove" : "removed"} legacy unprefixed ${legacyAbs} ` +
              `(ours, superseded by ${abs})`,
          );
        } else if (legacyOutcome === "kept-foreign") {
          log(
            `${opts.label}: left ${legacyAbs} in place — not ours (no PetBox origin marker); not renamed or deleted.`,
          );
        }
      }
    }

    // Orphan sweep — a role that is GONE from the definition (apply-orphans.ts). Runs per
    // harness, AFTER its writes, and independently of them: a role skipped by the truthfulness
    // gate is still declared and its file is never a candidate. Removal still requires our
    // origin marker, so a user's own file in the petbox-* namespace is reported and kept.
    if (orphanSweepSource) {
      for (const orphan of sweepOrphanArtifacts(root, harness, definition, { dryRun })) {
        if (orphan.outcome === "removed") {
          log(
            `${opts.label}: ${dryRun ? "would remove" : "removed"} ${orphan.path} — its role is no longer in ` +
              `definition "${definition.name}" (orphan artifact, ours by origin marker)`,
          );
        } else {
          log(
            `${opts.label}: left ${orphan.path} in place — no role by that name in the definition, ` +
              `but the file carries no PetBox origin marker, so it is not ours to delete.`,
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

  // Skills (bug: skill-files-clobber-and-apply-skips): a full `wire` was the ONLY thing that ever
  // wrote these — `apply` skipped them entirely, so a template edit "drifted" until the next full
  // wire (this is exactly what the owner observed for petbox-methodology). `apply` now refreshes
  // them too, using the SAME origin-marker write guard as the agent files above; a blocked skill
  // path folds into the same clobber-refusal exit path. Best-effort project identity: this is a
  // registered project's directory or it is not — `apply` never re-derives one, same as the
  // per-role definition fetch above (resolveApplyDefinition). `--offline` skips the network probe
  // for workspace, same spirit as `--offline` skipping the definition fetch.
  // Skip bookkeeping (bug: probe-collapses-http-errors-into-network / apply's silent-partial
  // side): a skipped skill refresh must never fall out of the final message and structured
  // summary. `intentional` covers the two cases the user asked for themselves — `--offline` and
  // an unregistered project directory — where the prior behavior is correct and untouched. Any
  // other skip (the workspace probe itself failing) is UNINTENTIONAL: the user asked for a full
  // apply and got a partial one, and that must be visible in both the final line and `summary`
  // (WIRE_EXIT is unchanged — this is a visibility fix, not a new exit code, per the card's
  // explicit boundary).
  let skillsSkip: { readonly intentional: boolean; readonly reason: string } | undefined;
  const resolvedForSkills = resolveProject(root);
  if (opts.offline) {
    const reason = "--offline (workspace requires a live /api/auth/validate)";
    skillsSkip = { intentional: true, reason };
    log(`${opts.label}: skills — --offline, skipped (workspace requires a live /api/auth/validate).`);
  } else if (!resolvedForSkills) {
    const reason = `${root} is not a registered project; run \`wire\` here first`;
    skillsSkip = { intentional: true, reason };
    log(`${opts.label}: skills — skipped (${reason}).`);
  } else {
    const probe = await probeWorkspace(resolvedForSkills.baseUrl, resolvedForSkills.apiKey);
    if (!probe.ok) {
      // Shared taxonomy + wording (skill-files.ts's describeWorkspaceProbeFailure) — same helper
      // doctor's skill-drift check uses, so a real HTTP error is never called "network/timeout"
      // here either, and this bucket is genuinely unintentional: the probe was supposed to
      // succeed and didn't.
      const reasonText = describeWorkspaceProbeFailure(probe);
      skillsSkip = { intentional: false, reason: reasonText };
      log(`${opts.label}: skills — skipped (${reasonText}).`);
      if (probe.reason === "forbidden") {
        wireLog(
          "apply",
          `workspace probe for skills refresh got 401/403 from ${resolvedForSkills.baseUrl} — ` +
            `key likely missing a required scope`,
        );
      }
    } else {
      const skillOutcomes = writeSkillFiles(
        root,
        join(HERE, "templates"),
        resolvedForSkills.project,
        probe.workspace,
        PROJECT_SKILLS,
        { dryRun },
      );
      const blockedSkillPaths = reportSkillOutcomes(opts.label, skillOutcomes, dryRun);
      if (blockedSkillPaths.length > 0) {
        clobberBlocked = true;
        clobberedPaths.push(...blockedSkillPaths);
      }
    }
  }

  // Structured summary (machine-readable-ish one line + human detail above). skillsSkipped is
  // always present (never silently dropped) so a machine reader can tell "no skip" from "skip
  // information was omitted" — null means the skills step actually ran (skipped nothing).
  const summary = {
    writtenFiles: written,
    writtenHarnesses,
    partialHarnesses,
    blockedHarnesses,
    clobberBlockedPaths: clobberedPaths,
    skillsSkipped: skillsSkip ?? null,
  };
  log(
    `${opts.label}: result written=${written} ` +
      `ok=[${writtenHarnesses.join(",")}] ` +
      `partial=[${partialHarnesses.join(",")}] ` +
      `blocked=[${blockedHarnesses.join(",")}]` +
      (clobberedPaths.length > 0 ? ` clobber-refused=[${clobberedPaths.join(",")}]` : ""),
  );

  const hadTruthfulnessBlock = partialHarnesses.length > 0 || blockedHarnesses.length > 0;
  const unintendedSkillsSkip = skillsSkip !== undefined && !skillsSkip.intentional;
  const code = classifyApplyExit({
    hardError: clobberBlocked,
    hadTruthfulnessBlock,
    unintendedIncomplete: unintendedSkillsSkip,
  });
  if (code === WIRE_EXIT.incomplete) {
    // wire-exit-incomplete-is-invisible-to-automation: honesty used to live only in this text,
    // so a CI step branching on the exit code could not tell a partial run from a complete one.
    // It now also carries a code of its own (4) — see wire-exit.ts. stderr, not stdout: this is
    // a non-zero outcome, and it must land where the other non-zero outcomes land.
    // Only reached when nothing stronger fired: a clobber refusal (1) or a truthfulness block
    // (3) outranks it, and the skip is still reported inside `summary` on those branches.
    console.error(
      `${opts.label}: done, but INCOMPLETE — every known harness accepted every role, ` +
        `skills were NOT refreshed (${skillsSkip!.reason}). Not a full success (exit ` +
        `${WIRE_EXIT.incomplete}); re-run once resolved. ${JSON.stringify(summary)}`,
    );
  } else if (code === WIRE_EXIT.ok) {
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
// via usage()). Exit codes: 0 full success; 1 hard failure; 2 usage/args; 3 truthfulness;
// 4 incomplete (a step was skipped for a reason the user did not ask for).
async function runApply(argv: string[]): Promise<void> {
  let definitionKey = DEFAULT_DEFINITION_KEY;
  let offline = false;
  let all = false;
  let dryRun = false;
  for (let i = 1; i < argv.length; i++) {
    const a = argv[i];
    if (a === undefined) continue; // unreachable: i < argv.length is the loop condition
    if (a === "--help" || a === "-h") usage(0);
    else if (a === "--offline") offline = true;
    else if (a === "--all") all = true;
    else if (a === "--dry-run") dryRun = true;
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
  // --dry-run outside --all is legal (previews the single cwd project) but --all without
  // --dry-run on the FULL registry is the exact trap the card names: a mass write into other
  // people's working directories, some of which may carry uncommitted work. Never refuse it —
  // the owner may genuinely want that — but never let it happen quietly either: say so loudly
  // before touching anything, once, above the per-project lines.
  if (all && !dryRun) {
    log(
      "apply --all: WRITING to every registered project directory (no --dry-run). " +
        "Re-run with --dry-run first if you have not already previewed this.",
    );
  }

  // Seed a fresh machine's roster BEFORE compiling — apply now refuses any declared role with
  // no local binding (reserve-unbound-inherits-session-model), so a bare `apply` on a machine
  // that never ran full `wire` needs this too, not just wire's own step 11 (see
  // seedDefaultRoleBindingsIfMissing's doc comment). No-op when roles.json already exists.
  seedDefaultRoleBindingsIfMissing("apply");

  if (all) {
    const code = await runApplyAll({ definitionKey, offline, dryRun });
    exitWith(code);
    return;
  }

  const result = await performApply({ definitionKey, offline, dryRun, label: "apply" });
  // Same libuv race doctor/status hit (Assertion failed: !(handle->flags & UV_HANDLE_CLOSING),
  // src\win\async.c): performApply's definition resolve + workspace probe are live network
  // round-trips, and a hard process.exit() right after races Windows' async-handle teardown for
  // whichever socket is still closing — the caller sees exit 127, not the WIRE_EXIT code apply's
  // own message just printed. exitWith (wire-exit.ts) is the one sanctioned spelling of the fix.
  exitWith(result.code);
}

// One row of `apply --all`'s per-project outcome — the "built-in row" for the card's requirement
// that a mass apply reports a understandable per-project verdict, not just an aggregate exit
// code. `outcome` is a closed enum so a caller (and a test) can branch on it without parsing
// prose: "written" — at least one file was written/would be written and nothing was refused;
// "unchanged" — the project was reached and every file already matched (a true no-op, dry or
// not); "refused" — at least one clobber refusal (see ApplyRunResult.hardError); "missing" — the
// registry's directory no longer exists on disk (a stale entry — this must NEVER abort the rest
// of the sweep, per the card); "error" — performApply threw something neither of the above
// covers (a genuinely unexpected failure for THIS project only).
export type RegistryApplyOutcome = "written" | "unchanged" | "refused" | "missing" | "error";

export type RegistryApplyRow = {
  readonly project: string;
  readonly dir: string;
  readonly outcome: RegistryApplyOutcome;
  readonly detail: string;
  readonly code: number;
};

/**
 * Run `performApply` once per entry in the global registry (~/.petbox/projects.json), instead of
 * once against the caller's own cwd. This is the "прогон по всему реестру" the card asks for:
 * one call sweeps every registered project directory, with a per-project outcome line, and a
 * directory that no longer exists on disk is reported and skipped — it never aborts the rest of
 * the run (bug this closes: there was no such command at all; `apply` only ever knew its own
 * cwd).
 *
 * Never throws: an unexpected failure for ONE registry entry is caught and reported as that
 * entry's own "error" row, exactly like the missing-directory case, so one bad entry can never
 * take down the sweep for the other seven. The RETURNED exit code is the strongest across every
 * row (wire-exit.ts's strongestExitCode) — a hard failure on entry 3 of 8 still shows every
 * other project's real outcome, but the process exit code still reflects it.
 */
async function runApplyAll(opts: {
  readonly definitionKey: string;
  readonly offline: boolean;
  readonly dryRun: boolean;
}): Promise<number> {
  const entries = readRegistry();
  const label = opts.dryRun ? "apply --all --dry-run" : "apply --all";
  log(`${label}: ${entries.length} registered project(s) in ${registryPath()}`);
  const rows: RegistryApplyRow[] = [];
  for (const entry of entries) {
    rows.push(await applyToRegistryEntry(entry, opts));
  }

  log("");
  log(`${label}: per-project outcome:`);
  for (const row of rows) {
    log(`${label}:   ${row.project} (${row.dir}) — ${row.outcome}: ${row.detail}`);
  }
  const written = rows.filter((r) => r.outcome === "written").length;
  const unchanged = rows.filter((r) => r.outcome === "unchanged").length;
  const refused = rows.filter((r) => r.outcome === "refused").length;
  const missing = rows.filter((r) => r.outcome === "missing").length;
  const errored = rows.filter((r) => r.outcome === "error").length;
  log(
    `${label}: summary — ${rows.length} project(s): written=${written} unchanged=${unchanged} ` +
      `refused=${refused} missing=${missing} error=${errored}.`,
  );

  const code = strongestExitCode(...rows.map((r) => r.code));
  return code;
}

async function applyToRegistryEntry(
  entry: RegistryEntry,
  opts: { readonly definitionKey: string; readonly offline: boolean; readonly dryRun: boolean },
): Promise<RegistryApplyRow> {
  const dir = entry.prefix;
  if (!existsSync(dir)) {
    // A registry entry whose directory is gone (moved, deleted, a worktree cleaned up) must
    // never fail the whole sweep — the card is explicit about this trap. Reported, skipped, and
    // counted as "ok" for exit-code purposes (WIRE_EXIT.ok): a stale registry row is a cleanup
    // item for the owner, not a defect in THIS run.
    return {
      project: entry.project,
      dir,
      outcome: "missing",
      detail: `directory no longer exists — skipped (registry entry is stale)`,
      code: WIRE_EXIT.ok,
    };
  }
  try {
    const result = await performApply({
      definitionKey: opts.definitionKey,
      offline: opts.offline,
      dryRun: opts.dryRun,
      cwd: dir,
      label: `apply[${entry.project}]`,
    });
    if (result.hardError) {
      return {
        project: entry.project,
        dir,
        outcome: "refused",
        detail:
          result.clobberBlockedPaths.length > 0
            ? `refused to overwrite ${result.clobberBlockedPaths.length} non-PetBox file(s)`
            : `hard failure (see log lines above)`,
        code: result.code,
      };
    }
    if (result.written === 0) {
      return { project: entry.project, dir, outcome: "unchanged", detail: "no changes", code: result.code };
    }
    return {
      project: entry.project,
      dir,
      outcome: "written",
      detail: `${opts.dryRun ? "would write" : "wrote"} ${result.written} file(s)`,
      code: result.code,
    };
  } catch (e) {
    // Genuinely unexpected — performApply itself already catches its own known failure modes and
    // returns a result record; reaching here means something outside that contract threw (e.g. a
    // filesystem permission error on THIS specific directory). One entry's surprise must not cost
    // the rest of the registry its results.
    return {
      project: entry.project,
      dir,
      outcome: "error",
      detail: e instanceof Error ? e.message : String(e),
      code: WIRE_EXIT.hard,
    };
  }
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
}): Promise<ResolvedAgentDefinition> {
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
    if (got.offline) {
      // A deliberate --offline run never attempted a fetch — checked FIRST, never folded into
      // the "unreachable" wording below (bug: doctor-reports-answering-server-unreachable, round
      // 2 — reported live: `doctor --offline` printed this exact line's OLD text, "PetBox
      // unreachable", against a server it had reached moments earlier in the SAME run without
      // the flag; only --offline explains the skip, not connectivity).
      log(`${label}: ${got.staleMarker ?? AGENT_DEF_OFFLINE_STALE_MARKER}`);
    } else if (got.forbidden) {
      // Server was reachable and refused the request — a scope problem, not offline
      // (wire-silent-failures-invisible, evidence 2026-07-26). Say so before the generic stale
      // marker so the operator does not go debug the network for a permissions issue.
      log(
        `${label}: server reachable but refused the request (401/403 — API key likely missing ` +
          `the agents:read scope); ${got.staleMarker ?? "using LKG agent definition cache"}`,
      );
    } else if (got.httpError) {
      // Server ANSWERED with an error status (500, 503, ...) — never "unreachable" (bug:
      // doctor-reports-answering-server-unreachable, same class as
      // probe-collapses-http-errors-into-network).
      log(`${label}: ${describeAgentDefHttpError(got.httpError)}; ${got.staleMarker ?? "using LKG agent definition cache"}`);
    } else {
      log(`${label}: ${got.staleMarker ?? "using LKG agent definition cache"}`);
    }
    log(`${label}: using LKG definition ${got.key} v${got.version} (stale)`);
  } else if (got.offline) {
    // Deliberate --offline, no cache to fall back to — never "no server"/"unreachable", the
    // caller simply asked to skip the network (same reasoning as the lkg branch above).
    log(`${label}: --offline — using kit default baseline (no LKG cache exists)`);
  } else if (got.notFoundOnServer) {
    // Server was reachable; it just has no definition of its own for this project yet
    // (normal for a fresh project) — not an offline/unreachable condition.
    log(`${label}: no server-side definition for this project yet — using kit default baseline`);
  } else if (got.forbidden) {
    // Server was reachable and refused (401/403) AND there is no LKG cache to fall back to —
    // distinct from a genuine network/timeout/5xx failure, which the final else below still
    // covers. Do not say "offline": the fix here is scopes, not connectivity.
    log(
      `${label}: server reachable but refused the request (401/403 — API key likely missing the ` +
        `agents:read scope) and no LKG cache exists — using kit default baseline. This is a ` +
        `permissions problem, not an offline one; check the key's scopes.`,
    );
  } else if (got.httpError) {
    // Server ANSWERED with an error status (500, 503, ...) and there is no LKG cache — distinct
    // from a genuine network/timeout failure, which the final else below still covers.
    log(`${label}: ${describeAgentDefHttpError(got.httpError)} and no LKG cache exists — using kit default baseline.`);
  } else {
    log(`${label}: offline default definition (no server, no LKG cache)`);
  }
  return got;
}

// Shared wording for an agent-def fetch that reached the server but got an error status (500,
// 503, ...) — never "unreachable"/"offline" (bug: doctor-reports-answering-server-unreachable).
// 503 gets its own self-recovering phrasing, same reasoning as skill-files.ts's
// describeWorkspaceProbeFailure for PetBox's own deploy_in_progress window.
function describeAgentDefHttpError(httpError: { status: number; retryAfterSeconds?: number }): string {
  const retryNote =
    httpError.retryAfterSeconds !== undefined ? ` (retry in ~${httpError.retryAfterSeconds}s)` : "";
  const selfRecovering = httpError.status === 503 ? ", self-recovering" : "";
  return `server reachable but answered HTTP ${httpError.status}${selfRecovering}${retryNote}`;
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

// `layers` — diagnostic-only subcommand answering the two questions manual `find` + hashing used
// to answer by hand (card role-definition-cascade-revisit, requirement 1, never covered by the
// accepted idea's spec_plan): which definition LAYERS exist on this machine, where they
// physically live, and — by FIELD, not "the files differ" — what they disagree about. Read-only:
// never writes, never calls resolveApplyDefinition/the server, never gates apply's own exit code.
//
// Built entirely on layer-cascade.ts's resolveDefinitionLayers — this file does not re-derive a
// second comparator. The resolver's own contract (see that module's header) is that the
// directory list is the CALLER's decision; nothing in the codebase has yet picked a fixed
// location for the "user"/"project" layers (the client-side merge itself, P5, has not landed —
// see idea role-definitions-live-in-files), so the defaults below are this command's own,
// documented choice — `~/.petbox/agents` for user, `<project root>/.petbox/agents` for project —
// consistent with the architecture sketch (research/wire-source-of-truth/30-architecture.md §3)
// and with every other `~/.petbox/*` path already in this package. Pass explicit directories on
// the command line (lowest priority first, same order resolveDefinitionLayers takes) to check
// anything else, e.g. a one-off scratch layout while proving this command works.
//
// The "base" layer is deliberately NOT a candidate here: it still ships as a flat JSON file
// inside the package (agent-definition.ts's DEFAULT_AGENT_DEFINITION), not a layer directory —
// that migration step (client-side merge) has not landed. This command says so OUT LOUD instead
// of quietly comparing only two of three layers and looking complete.
//
// The trap this must not repeat (observation doctor-drift-check-silent-skip-unregistered-dir):
// "no divergence" and "could not check" must never look the same. Every early return below prints
// to stderr and uses a DIFFERENT exit code (LAYERS_EXIT.cannotCheck) than both the clean path
// (LAYERS_EXIT.ok) and the found-a-problem path (LAYERS_EXIT.cascadeError) — a script branching on
// exit code, not just prose, cannot confuse the three.
//
// Exit codes (own small taxonomy, not WIRE_EXIT's — this command never touches apply's roster):
//   0  clean       — 2+ layers present, resolved, zero cascade ERRORs (E0-E5/E1; warnings do not
//                    change the exit code — a W3 replica-layer nudge is not a hard problem)
//   1  diverged    — cascade resolved but reported at least one ERROR (dangling target, orphan
//                    tombstone, incomplete new role, replace+append conflict, bad filename/mode)
//   2  usage       — bad arguments
//   3  cannotCheck — fewer than 2 layers present (nothing to diverge from), or a present layer's
//                    source is broken/unreadable (LayerSourceError) — NEVER folded into 0 or 1
const LAYERS_EXIT = { ok: 0, cascadeError: 1, usage: 2, cannotCheck: 3 } as const;

type LayerCandidate = { readonly label: string; readonly dir: string };

function defaultLayerCandidates(cwd: string): LayerCandidate[] {
  const { root } = resolveApplyRoot(cwd);
  return [
    { label: "user", dir: join(homedir(), ".petbox", "agents") },
    { label: "project", dir: join(root, ".petbox", "agents") },
  ];
}

function runLayers(argv: string[]): void {
  const explicitDirs: string[] = [];
  for (let i = 1; i < argv.length; i++) {
    const a = argv[i];
    if (a === undefined) continue; // unreachable: i < argv.length is the loop condition
    if (a === "--help" || a === "-h") usage(0);
    if (a.startsWith("-")) {
      console.error(`layers: unexpected flag: ${a}`);
      usage(LAYERS_EXIT.usage);
    }
    explicitDirs.push(resolve(a));
  }

  const usingDefaults = explicitDirs.length === 0;
  const candidates: LayerCandidate[] = usingDefaults
    ? defaultLayerCandidates(process.cwd())
    : explicitDirs.map((d, i) => ({ label: `arg${i + 1}:${basename(d)}`, dir: d }));

  log(
    `layers: checking ${candidates.length} candidate layer location(s), lowest priority first ` +
      `(${usingDefaults ? "this command's own conventional defaults" : "explicit directories from argv"}):`,
  );
  if (usingDefaults) {
    log(
      `  base                 N/A       bundled inside the package as default-agents.json — NOT ` +
        `yet a layer directory (client-side merge, P5, has not landed); excluded here, not silently skipped.`,
    );
  }

  const present: LayerCandidate[] = [];
  for (const c of candidates) {
    let exists = false;
    try {
      exists = existsSync(c.dir) && statSync(c.dir).isDirectory();
    } catch {
      exists = false;
    }
    log(`  ${c.label.padEnd(20)} ${exists ? "PRESENT  " : "absent   "} ${c.dir}`);
    if (exists) present.push(c);
  }

  if (present.length === 0) {
    console.error(
      "layers: CANNOT CHECK — no layer directory exists on this machine at any candidate " +
        "location above. This is NOT \"no divergence\": nothing was read, nothing was compared.",
    );
    exitWith(LAYERS_EXIT.cannotCheck);
    return;
  }
  if (present.length === 1) {
    console.error(
      `layers: CANNOT CHECK — only one layer is present (${present[0]!.label} at ` +
        `${present[0]!.dir}). Nothing to diverge from. This is NOT "no divergence": divergence ` +
        "needs at least two layers to compare.",
    );
    exitWith(LAYERS_EXIT.cannotCheck);
    return;
  }

  let resolution: CascadeResolution;
  try {
    resolution = resolveDefinitionLayers(present.map((c) => c.dir));
  } catch (e) {
    if (e instanceof LayerSourceError) {
      console.error(
        `layers: CANNOT CHECK — a present layer's source is broken and could not be read: ` +
          `${e.message}`,
      );
      exitWith(LAYERS_EXIT.cannotCheck);
      return;
    }
    throw e;
  }

  log("");
  log("layers: resolved layers (lowest priority first):");
  for (const l of resolution.layers) log(`  ${l.name}  mode=${l.mode}  ${l.dir}`);

  log("");
  log("layers: cascade trace — what each layer did to the roster:");
  log(resolution.trace.length > 0 ? formatCascadeTrace(resolution) : "  (no roles resolved)");

  log("");
  log("layers: per-field provenance — which layer supplied each field of each resolved role:");
  log(
    resolution.definition.roles.length > 0
      ? formatCascadeProvenance(resolution)
      : "  (no roles resolved)",
  );

  log("");
  log("layers: cascade diagnostics — problems, never ordinary field overrides:");
  log(formatCascadeReport(resolution));

  const errors = cascadeErrors(resolution);
  if (errors.length > 0) {
    console.error(
      `layers: DIVERGED — ${errors.length} cascade ERROR(s) found across ${present.length} ` +
        `layer(s); see diagnostics above.`,
    );
    exitWith(LAYERS_EXIT.cascadeError);
    return;
  }
  log(
    `layers: clean — ${present.length} layer(s) compared, zero cascade errors ` +
      `(this IS the "no divergence problem" answer, reached by actually checking).`,
  );
  exitWith(LAYERS_EXIT.ok);
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
  if (SANDBOX_BASE_URL !== undefined) {
    // Loopback sandbox: this is the only step on the full-wire path that writes MACHINE-GLOBAL
    // state (HKCU Environment via PowerShell). Everything else lands under HOME, which the suite
    // already redirects to a temp dir. Skipping it keeps a test run from persisting a junk
    // PETBOX_*_API_KEY into the developer's own user environment.
    log(`[4/10] loopback sandbox — SKIPPED user-scope env persistence for ${envVar}.`);
    return;
  }
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
// asked meaningfully: endpoint missing / non-JSON body). ABORTS THE RUN on a rejected key or a
// project mismatch, so nothing is persisted for a bad key.
//
// Aborts via abortRun (wire-exit.ts), never process.exit: all three failure branches fire
// immediately after a completed live round trip, which is precisely the shape that races
// Windows' socket teardown and surfaces as 127 instead of 1 (wire-six-remaining-exit-races —
// the same defect fixed in doctor, status and apply before it). abortRun still cuts control
// flow dead (it returns `never`), so nothing this function used to skip now runs: main() never
// reaches step 4's persistence, exactly as before.
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
    abortRun(
      WIRE_EXIT.hard,
      `[3/10] validate: could not reach ${uri} — ${(e as Error).message}. Aborting.`,
    );
  }

  if (resp.status === 401) {
    abortRun(WIRE_EXIT.hard, `[3/10] validate: server rejected the API key (401). Aborting.`);
  }
  if (!resp.ok) {
    // Non-standard / endpoint missing → warn and continue. Class-Б: the key still gets
    // persisted below on this ambiguous read, so leave a trace doctor can surface even after
    // this run's stdout has scrolled away (wire-silent-failures-invisible).
    log(`[3/10] validate: unexpected status ${resp.status} (endpoint missing?); continuing with a warning.`);
    wireLog("validate", `unexpected status ${resp.status} from ${uri}; key persisted anyway`);
    return null;
  }
  let body: any = null;
  try {
    body = await resp.json();
  } catch {
    log(`[3/10] validate: 200 but non-JSON body; continuing with a warning.`);
    wireLog("validate", `200 but non-JSON body from ${uri}; key persisted anyway`);
    return null;
  }
  // Contract (AuthApi.cs): 200 => { project, scopes, workspace } (camelCase, ASP.NET web
  // defaults). `workspace` is newer than the other two — an older server omits it.
  const proj = body?.project ?? body?.Project;
  if (typeof proj === "string" && proj.length > 0) {
    if (proj !== projectKey) {
      abortRun(
        WIRE_EXIT.hard,
        `[3/10] validate: key belongs to project '${proj}', not '${projectKey}'. Aborting.`,
      );
    }
    log(`[3/10] validate: OK — key scoped to '${proj}'.`);
  } else {
    log(`[3/10] validate: 200 without a project field; continuing with a warning.`);
    wireLog("validate", `200 from ${uri} without a project field; key persisted anyway`);
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

// Orphan cleanup — STABLE must be an EXACT MIRROR of HERE at EVERY depth, never a UNION. cpSync
// overwrites but never DELETES, so an entry the shipped kit dropped would keep standing next to
// its NEWER peers. This used to compare only the TOP-LEVEL of STABLE against the top level of
// HERE, which caught a whole file/dir vanishing (e.g. the retired prompt-rag.ts) but missed
// anything ONE LEVEL DEEPER — a subdirectory that survives (e.g. `templates/`) while entries
// INSIDE it are renamed or dropped, so the diff never even looked inside.
//
// Bug caught live (task: kit-version-lands-everywhere-and-sweeps, measured 2026-09-02): after
// `templates/analysis-workspace` and `templates/factory-run` were renamed to
// `templates/petbox-analysis-workspace` / `templates/petbox-factory-run`, `update` left the OLD
// directories standing in ~/.petbox/wire/templates/ right next to the new ones — both complets at
// once, forever, because `templates` itself still existed on both sides so the old top-level-only
// diff never recursed into it.
//
// The fix: recurse the same "not on the other side → remove" rule at every directory level, not
// just the root. STABLE holds nothing but a verbatim copy of HERE, so this is still "belongs to
// the kit" by LOCATION, not a name guess — the same reasoning the top-level version already
// relied on, just no longer stopping one level too early.
function pruneStaleMirrorEntries(hereDir: string, stableDir: string, label: string): void {
  if (!existsSync(stableDir)) return;
  const hereEntries = existsSync(hereDir) ? new Set(readdirSync(hereDir)) : new Set<string>();
  for (const name of readdirSync(stableDir)) {
    const stableAbs = join(stableDir, name);
    if (!hereEntries.has(name)) {
      rmSync(stableAbs, { recursive: true, force: true });
      log(
        `${label} orphan cleanup: removed ${relative(STABLE, stableAbs).replace(/\\/g, "/")} ` +
          `from ${STABLE} (not shipped by this kit).`,
      );
      continue;
    }
    const hereAbs = join(hereDir, name);
    let hereIsDir: boolean;
    let stableIsDir: boolean;
    try {
      hereIsDir = statSync(hereAbs).isDirectory();
      stableIsDir = statSync(stableAbs).isDirectory();
    } catch {
      continue; // raced away between readdir and stat — cpSync below will settle it
    }
    // Both sides agree it's a directory → recurse to catch renames/drops nested inside it
    // (this is the templates/ case). A file<->dir type flip is left to cpSync's overwrite.
    if (hereIsDir && stableIsDir) {
      pruneStaleMirrorEntries(hereAbs, stableAbs, label);
    }
  }
}

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
  // (The settings-side half of this removal is pruneLegacyPromptRagHooks — files AND hooks must
  // go.) See pruneStaleMirrorEntries's own comment for why this now recurses.
  pruneStaleMirrorEntries(HERE, STABLE, label);
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

// Log every writeSkillFiles outcome under `label` and return the absolute paths of any
// "blocked" ones (bug: skill-files-clobber-and-apply-skips) — a real, non-PetBox file already
// sat at that path and was left byte-for-byte untouched; the caller decides what a non-empty
// return does to its own exit code.
function reportSkillOutcomes(label: string, result: SkillWriteResult, dryRun: boolean = false): string[] {
  const blocked: string[] = [];
  for (const outcome of result.writes) {
    if (outcome.kind === "blocked") {
      blocked.push(outcome.path);
      console.error(
        `${label}: ${dryRun ? "would refuse" : "REFUSED"} to overwrite skill ${outcome.path} — it exists and ` +
          `does not carry the PetBox origin marker (no \`petbox: managed\` in its frontmatter), so it is a ` +
          `real file, not one wire/apply wrote before. ${dryRun ? "Nothing would be touched." : "Nothing was touched."}`,
      );
    } else if (outcome.kind === "declared-manual") {
      // NOT a refusal and NOT an error (spec: wire-skill-manual-declared-not-error): the project
      // declared this path its own, the kit honoured that. Never enters `blocked`, so it can
      // never reach the exit code — stdout, like every other normal outcome.
      log(
        `${label}: left skill ${outcome.path} alone — declared \`petbox: manual\`, the project ` +
          `owns this path. Nothing was written.`,
      );
    } else if (outcome.reason === "unchanged") {
      log(`${label}: skill ${outcome.path} unchanged (already matches)`);
    } else {
      log(
        `${label}: ${dryRun ? "would write" : "wrote"} ${outcome.path}` +
          (outcome.reason !== "new" ? ` (${outcome.reason})` : ""),
      );
    }
  }
  // Pre-rename sweep (bug: wire-skill-cleanup-on-replace) — same wording and same rules as the
  // agent-role rename cleanup above: an owned leftover is removed, anything not ours is named
  // and kept. Never a blocked path: keeping a file we may not delete is the correct outcome,
  // not a failure of the run.
  for (const cleanup of result.cleanups) {
    if (cleanup.outcome === "removed") {
      log(
        `${label}: ${dryRun ? "would remove" : "removed"} legacy skill ${cleanup.path} ` +
          `(ours, superseded by the current name)` +
          (cleanup.removedDir ? " — and its now-empty directory" : ""),
      );
    } else if (cleanup.outcome === "kept-foreign") {
      log(
        `${label}: left ${cleanup.path} in place — not ours (no \`petbox: managed\` origin marker); ` +
          `not renamed or deleted.`,
      );
    }
  }
  return blocked;
}

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

  // Skill bodies: `petbox` (project-scoped), `petbox-agent-factory` (on-demand, no
  // placeholders), `petbox-methodology` (thin, project-agnostic pointer at the LIVE
  // methodology this project runs — never this repo's own rules; see skill-files.ts),
  // `petbox-write-economy` (bodyRef/fragment write-cost mechanisms) and `petbox-node-authoring`
  // (node/comment BODY structure). Rendered once per skill (see PROJECT_SKILLS in
  // skill-files.ts — the one place a new skill is registered), then dropped into every native
  // skill surface (writeSkillFiles / skill-files.ts).
  reportSkillOutcomes("[7/10]", writeSkillFiles(dir, join(HERE, "templates"), project, workspace));
}

// ---- step 7b: telemetry (opt-in, --telemetry) ------------------------------

// Ensure the target named log exists. PetBox OTLP ingest is project+log-scoped in the PATH
// (`/v1/{metrics,logs}/{project}/{log}`) and returns 404 if the log is absent, so the log MUST
// pre-exist before Claude Code starts exporting. Idempotent: a 409 ("already exists") is success.
//
// Both failure branches abort via abortRun (wire-exit.ts), never process.exit: each fires right
// after a completed live round trip — the socket-teardown race shape (wire-six-remaining-exit-
// races). abortRun returns `never`, so the caller still skips writeTelemetrySettings and every
// later step exactly as the hard exit did.
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
    abortRun(
      WIRE_EXIT.hard,
      `[telemetry] could not reach ${uri} — ${(e as Error).message}. Aborting.`,
    );
  }
  if (resp.ok || resp.status === 409) {
    // 201 Created (fresh) or 409 Conflict (already exists) — both mean the log is ready.
    log(`[telemetry] log '${logName}' ready in project '${project}' (HTTP ${resp.status}).`);
    return;
  }
  const text = await resp.text().catch(() => "");
  abortRun(
    WIRE_EXIT.hard,
    `[telemetry] failed to ensure log '${logName}' — HTTP ${resp.status} ${text}. Aborting.`,
  );
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
  'subagent-model-gate.ts"',
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

// Install the live kit hooks (Stop / SessionStart on both agents, plus a Claude-Code-only
// PreToolUse model-pin gate — see modelGateCmd below) and, on the way through, run the
// retired-prompt-RAG migration on each settings object before it is written back — one read, one
// write per file, so the prune costs nothing extra and cannot be skipped.
function installGlobalHooks(): void {
  const pushCmd = `node "${join(STABLE, "push-session.ts")}"`;
  const pullCmd = `node "${join(STABLE, "pull-memory.ts")}"`;
  const droidPushCmd = `node "${join(STABLE, "droid-push-session.ts")}"`;
  const droidPullCmd = `node "${join(STABLE, "droid-pull-memory.ts")}"`;
  // Claude Code ONLY — subagent-model-enforcement-hook. The Task tool's `model` spawn parameter
  // is the surface being gated, and Claude Code is the only harness where that parameter does
  // anything (Factory Droid ignores it; opencode has no equivalent parameter at all), so this
  // command is never added to the droid settings block below. See subagent-model-gate.ts's own
  // header comment for the rule and why it stops at petbox-* + explicit model.
  const modelGateCmd = `node "${join(STABLE, "subagent-model-gate.ts")}"`;
  // Perf, not correctness: without a matcher this hook's node process would spawn on EVERY
  // PreToolUse event — every Read, Edit, Bash, in every session, on every project on this
  // machine (the settings this writes into are global). Measured cost: ~60ms per invocation: a
  // session with hundreds of tool calls would pay tens of seconds for a branch that only ever
  // fires a handful of times per session. The matcher is scoped to the spawn tool by NAME so the
  // process only starts there; the actual gate (subagent-model-gate.ts's shape check on
  // tool_input) is unchanged and remains the real decision — the matcher is an optimization on
  // top of it, never a substitute for it. Covers both names this kit has seen Claude Code use
  // for the subagent-spawn tool ("Task" and "Agent") in one regex, since neither is a documented
  // stable contract.
  // FAILURE MODE, stated plainly: if a future Claude Code build renames the spawn tool to
  // something outside this set, the matcher stops selecting it and the gate goes SILENT —
  // no crash, no log, just a `model` parameter on a petbox-* spawn that is no longer caught.
  // Nothing here detects that drift; a maintainer who suspects it should check with an actual
  // spawn call, not assume this comment is still accurate.
  const MODEL_GATE_MATCHER = "^(Task|Agent)$";
  // Every kit hook command this run considers current — the prune keeps these, drops the rest.
  const validCmds = new Set([pushCmd, pullCmd, droidPushCmd, droidPullCmd, modelGateCmd]);

  const settingsPath = join(homedir(), ".claude", "settings.json");
  const settings = readJson(settingsPath) ?? {};
  if (!settings.hooks || typeof settings.hooks !== "object") settings.hooks = {};
  const prunedClaude = pruneStaleKitHooks(settings.hooks, validCmds);
  if (prunedClaude > 0) log(`[8/10] pruned ${prunedClaude} stale claude kit hook(s) not pointing at ${STABLE}.`);

  // Claude Code hooks shape: settings.hooks[event] = [{ matcher?, hooks: [{type, command}] }]
  const ensureHook = (event: string, command: string, matcher?: string) => {
    const groups: any[] = Array.isArray(settings.hooks[event]) ? settings.hooks[event] : [];
    const already = groups.some(
      (g) => Array.isArray(g?.hooks) && g.hooks.some((h: any) => h?.command === command),
    );
    if (already) {
      log(`[8/10] claude hook ${event} already present — skipped.`);
      return;
    }
    const group: any = { hooks: [{ type: "command", command }] };
    if (matcher !== undefined) group.matcher = matcher;
    groups.push(group);
    settings.hooks[event] = groups;
    log(`[8/10] claude hook ${event} added.`);
  };

  ensureHook("Stop", pushCmd);
  ensureHook("SessionStart", pullCmd);
  // Matcher-scoped (see MODEL_GATE_MATCHER above) — the shape check inside subagent-model-gate.ts
  // stays tool_name-agnostic and remains the real gate; the matcher only stops the process from
  // spawning on tool calls it would immediately no-op on anyway. Claude Code only (see above).
  ensureHook("PreToolUse", modelGateCmd, MODEL_GATE_MATCHER);
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

// DEFAULT_ROLE_MODEL_SEED now lives in roles.ts (single source of truth shared with status.ts's
// per-role model-source enumeration — see that file's comment on the constant).

// Seed ~/.petbox/roles.json with a default profile ONLY when the file does not exist yet —
// never touches an operator's own bindings. Without this, a brand-new machine's roles.json is
// empty, and apply now REFUSES to write a declared role with no local model binding on any
// CLOSED-model-space harness (reserve-unbound-inherits-session-model; apply-artifacts.ts's
// planApply) — so an unseeded roster on a fresh machine would leave claude-code fully blocked
// (exit WIRE_EXIT.truthfulness), not merely silently tier-drifting the way the 2026-07-26
// incident (and, structurally, the 2026-07-12 one before it) grew from.
// Called from BOTH the full `wire` run (step 11, below) and the standalone `apply` subcommand
// (runApply) — previously only the former, so running a bare `apply` on a fresh machine (no
// roles.json yet) hit the unbound-role refusal with nothing to fix it.
//
// Also seeds `droid` — every role bound to the literal `inherit` — even though droid's model
// space is OPEN (apply would only warn, never block, an unbound droid role). `inherit` is not
// an invented id: it is Factory's own documented frontmatter default
// (https://docs.factory.ai/cli/configuration/custom-droids § Controlling the model), the exact
// value renderDroidMarkdown already wrote for an unbound role before this whole card. Seeding it
// explicitly turns that implicit fallback into a real, visible binding — same output, no warning
// needed, nothing invented. `opencode` has no equivalent safe placeholder (no universal "just
// inherit" keyword in its `provider/model` id space) — it stays genuinely unbound and apply
// warns about it instead of failing (newcomer-equivalent-experience's happy path: exit 0).
function seedDefaultRoleBindingsIfMissing(label: string): void {
  if (existsSync(rolesPath())) {
    log(`${label} roles: ${rolesPath()} already exists — left as-is (existing bindings kept).`);
    return;
  }
  const ccRoles: Record<string, RoleBinding> = {};
  for (const [role, model] of Object.entries(DEFAULT_ROLE_MODEL_SEED)) ccRoles[role] = { model };
  const droidRoles: Record<string, RoleBinding> = {};
  for (const role of Object.keys(DEFAULT_ROLE_MODEL_SEED)) droidRoles[role] = { model: "inherit" };
  const data: RolesFile = {
    activeProfile: "default",
    profiles: {
      default: { agents: { "claude-code": { roles: ccRoles }, droid: { roles: droidRoles } } },
    },
  };
  saveRoles(data);
  log(
    `${label} roles: seeded ${rolesPath()} — profile "default": claude-code aliases ` +
      `(orchestrator=opus, worker=sonnet, worker-highstakes=opus, explore=haiku, ` +
      `reserve=fable), droid=inherit ` +
      `for every role. opencode is intentionally left unbound (its model space is open/unknowable ` +
      `from the kit) — apply will warn about it, not fail; bind it yourself with ` +
      `\`petbox-wire model set <role> <model> --agent opencode\` when you know what to bind it to.`,
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
  if (isStatusCommand(argv)) {
    let offline = false;
    let all = false;
    for (let i = 1; i < argv.length; i++) {
      const a = argv[i];
      if (a === undefined) continue; // unreachable: i < argv.length is the loop condition
      if (a === "--help" || a === "-h") usage(0);
      else if (a === "--offline") offline = true;
      else if (a === "--all") all = true;
      else {
        console.error(`status: unexpected argument: ${a}`);
        usage(WIRE_EXIT.usage);
      }
    }
    if (all) {
      await runRegistryStatus({ offline, cwd: process.cwd() });
      return;
    }
    await runStatus({ offline, cwd: process.cwd() });
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
  if (isLayersCommand(argv)) {
    runLayers(argv);
    return;
  }

  const args = parseArgs(argv);
  const dir = resolve(args.dir);
  const project = args.projectKey;
  if (SANDBOX_BASE_URL !== undefined) {
    log(`[0/10] loopback sandbox base URL in use: ${SANDBOX_BASE_URL} (petbox-wire's own tests).`);
  }
  const baseUrl = SANDBOX_BASE_URL ?? DEFAULT_BASE_URL;

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
    // key-in-argv-npm-log-leak: npm writes the FULL argv (this key included) to its own debug
    // log (~/.npm/_logs/*.log) on every invocation, in plain text, with no rotation — a log from
    // months ago can still carry it. --key stays supported (breaking an already-scripted wiring
    // pipeline is worse), but every use gets a loud, un-suppressable warning pointing at the
    // env-var alternative. Never print the key itself here.
    console.error(
      `[2/10] WARNING: --key puts the API key in argv. npm logs the full command line to\n` +
        `  ~/.npm/_logs/*.log in plain text (no rotation) — that key will sit there readable.\n` +
        `  Prefer setting ${envVar} in the environment instead (see the Connect page for the\n` +
        `  exact command). Already ran with --key? Clean it up: grep -l -F <key> ~/.npm/_logs/*.log\n` +
        `  and remove or scrub the matching file(s).`,
    );
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
    // `ws.exitCode` READS like a child process's status being forwarded; it is not. It is a
    // locally computed WIRE_EXIT.usage from resolveWorkspace (wire-identity.ts), fired the
    // instant after step 3's `await validateKey(...)` finished a live round trip — i.e. exactly
    // the socket-teardown race shape, and it was mis-triaged as safe on first reading twice.
    // Plain early return works here (unlike validateKey's three sites) because this IS main().
    console.error(ws.message);
    exitWith(ws.exitCode);
    return;
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
  // startup artifacts NOW, so the freshly-wired roster is actually usable. NEVER ABORTS THE RUN:
  // the key is already validated and every other file is already written by this point, so a
  // compile hiccup here (e.g. a transient agent-defs fetch failure — resolveApplyDefinition
  // still falls back to LKG/DEFAULT) must not throw away work that already succeeded;
  // re-running `petbox-wire apply` retries just this step (fresh-wire-roster-unusable).
  //
  // "Does not abort" is NOT "does not count" (full-wire-exit-ignores-step-11). Those two were
  // fused in this comment and the code only implemented the first: step 11 could return 1
  // (a clobber refusal), 3 or 4 and the run still ended 0, which made the exit-code table's
  // "0 — every requested step ran" false for the full-wire path and re-opened the very bug this
  // step exists to close — a machine whose agent artifacts were never written is then
  // indistinguishable, to a script, from a fully wired one.
  seedDefaultRoleBindingsIfMissing("[11/10]");
  const applyResult = await performApply({
    definitionKey: DEFAULT_DEFINITION_KEY,
    offline: false,
    label: "[11/10]",
  });
  if (applyResult.code !== WIRE_EXIT.ok) {
    console.error(`[11/10] next: petbox-wire apply`);
  }

  // Fold step 11's outcome into the run's own code, the same way step 10 does it: set
  // process.exitCode and KEEP GOING (selfSmoke, above) — never exitWith/abortRun, which would
  // cut the run off and break the decision this step deliberately made.
  //
  // strongestExitCode, not a bare assignment: step 10 may already have set 1, and
  // `process.exitCode = applyResult.code` would DOWNGRADE that to 3/4 for no better reason than
  // step 11 assigning last. The winner is the taxonomy's declared priority (1 > 3 > 4 > 0).
  // The current process.exitCode is folded in as well, so any earlier non-aborting step (today:
  // only self-smoke) is carried rather than re-derived here; `smokeOk` is passed too so a future
  // refactor of selfSmoke's side effect cannot silently drop it. The result is never weaker than
  // what was already set, which is why assigning it unconditionally is safe.
  const runCodeSoFar = typeof process.exitCode === "number" ? process.exitCode : WIRE_EXIT.ok;
  process.exitCode = strongestExitCode(
    runCodeSoFar,
    smokeOk ? WIRE_EXIT.ok : WIRE_EXIT.hard,
    applyResult.code,
  );

  // Terminal message set depends on the smoke outcome AND on step 11 — a failure must be the LAST
  // line, in red, never followed by "done." (selfsmoke-failure-prints-done). A non-zero step 11 is
  // a failure by exactly that standard: the run now exits non-zero, so its last line must not read
  // like a full success either.
  const finish = finishWireRun({
    smokeOk,
    applyCode: applyResult.code,
    envVar,
    envVarPresentInProcess: !!process.env[envVar],
    platform: process.platform,
  });
  for (const line of finish.lines) {
    if (finish.toStderr) console.error(line);
    else log(line);
  }
}

// The single entrypoint handler, and the place a deliberate deep abort becomes an exit code.
//
// Its fate was left open by wire-six-remaining-exit-races ("решить его судьбу явно"); decided:
// it is NOT safe, so it is converted like the rest. It is the last-resort handler for ANY throw
// out of main(), and main() can throw while a live fetch (validateKey, ensureTelemetryLog,
// selfSmoke, performApply, the definition resolve) has just completed — the same
// completed-round-trip-then-hard-exit shape, only reachable from more places than any single
// call site. exitWith is correct here for the same reason it is correct there.
main().catch((e) => {
  if (e instanceof RunAbort) {
    // A deliberate abort from depth (abortRun) — its message is the operator-facing report, not
    // a crash. No stack: this is an expected outcome (bad key, unreachable server), and a stack
    // would bury the actionable line.
    console.error(e.message);
    exitWith(e.code);
    return;
  }
  console.error(e?.stack ?? String(e));
  exitWith(WIRE_EXIT.hard);
});
