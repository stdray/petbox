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
import { basename, dirname, isAbsolute, join, relative, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { readWireLogTail, wireLog, wireLogPath } from "./wire-log.ts";
import {
  DEFAULT_AGENT_DEFINITION_PATH,
  KIT_VERSION,
  validateAgentDefinition,
  type AgentDefinition,
} from "./agent-definition.ts";
import {
  baseLayer,
  definitionLayerCandidates,
  formatDefinitionErrors,
  formatDefinitionLayersLine,
  formatDefinitionProvenance,
  resolveLocalDefinition,
  resolveUserScopeDefinition,
  type LocalDefinition,
} from "./definition-source.ts";
import { formatApplyBlocked, planApply } from "./apply-artifacts.ts";
import { sweepOrphanArtifacts, sweepOrphanArtifactsIn, sweepProjectRoleArtifacts } from "./apply-orphans.ts";
import { createAdoptSet, NO_ADOPT, type AdoptSet } from "./adopt-paths.ts";
import {
  createLedger,
  formatAction,
  formatSummaryLine,
  summarize,
  type ApplyAction,
  type ApplyLedger,
  type ApplySummary,
} from "./apply-ledger.ts";
import { resolveApplyRoot } from "./apply-root.ts";
import { userProfileCollision } from "./role-dir-collision.ts";
import { cleanupLegacyArtifact, writeArtifact } from "./apply-write.ts";
import { upsertGitignoreBlock } from "./gitignore-block.ts";
import { managedGitignoreEntries } from "./managed-paths.ts";
import {
  isRoleScope,
  loadWireConfig,
  resolveRoleScope,
  ROLE_SCOPES,
  saveWireConfig,
  userAgentFilesRoot,
  wireConfigPath,
  type RoleScope,
} from "./role-scope.ts";
import { findDanglingTargets, formatDanglingTargets } from "./definition-integrity.ts";
import { HARNESS_IDS } from "./harness-capabilities.ts";
import { pruneDeadPromptRagHooks } from "./hook-prune.ts";
import {
  cascadeErrors,
  formatCascadeProvenance,
  formatCascadeReport,
  formatCascadeTrace,
  isLayerDirectory,
  LayerSourceError,
  resolveDefinitionLayers,
  type CascadeResolution,
  type ResolveLayersOptions,
} from "./layer-cascade.ts";
import { persistKeyForAgentsPosix } from "./posix-env.ts";
import { petboxDir, petboxKeysJsonPath, petboxWireMirrorDir } from "./petbox-dir.ts";
import { classifySelfSmokeResponse, finishWireRun } from "./self-smoke.ts";
import {
  buildSkillReports,
  claudeSkillsDir,
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
import {
  detectKeysStoreDrift,
  formatKeyDrift,
  inspectKeyStoreEntry,
  readRegistry,
  registryPath,
  resolveProject,
  toEnvRef,
  UnresolvedEnvRefError,
  type RegistryEntry,
} from "./registry.ts";
import {
  agentLookupKeys,
  CANONICAL_AGENT_IDS,
  canonicalAgentId,
  exportRolesBootstrap,
  findBindingProviderInconsistencies,
  formatResolvedBinding,
  HARNESS_ROLE_MODEL_SEEDS,
  isEmptyRoles,
  loadRoles,
  loadRolesMigrated,
  resetRoleModelSlice,
  resetRoleModelToKitDefault,
  resolveAgentRoles,
  rolesPath,
  ROLES_FORMAT_VERSION,
  rewrittenByMigration,
  saveRoles,
  seedMissingRoleBindings,
  setRoleModel,
  setRoleModelSlice,
  unsetRoleModel,
  unsetRoleModelSlice,
  useProfile,
  type RoleBindingMigration,
  type RoleBindingReattribution,
  type RoleBindingRefresh,
  type RolesFile,
  type SliceCell,
} from "./roles.ts";
import {
  checkModelValidity,
  checkRolesModelValidity,
  createModelSourceCache,
  formatModelValidity,
  type ModelValidity,
} from "./model-validity.ts";
import { buildTelemetryOtlpEnv } from "./telemetry-settings.ts";
import { checkTruthfulness, formatViolations } from "./truthfulness.ts";
import { codexHomeDir } from "./codex-paths.ts";
import {
  CODEX_CATALOG_FILE_NAME,
  findCodexConfigDivergence,
  readCodexCatalogPathFromConfig,
  renderCodexConfigFragmentText,
} from "./codex-config-fragment.ts";
import { findUnregisteredRoleBindings } from "./model-registration-check.ts";
import { findQwenConfigDivergence, renderQwenConfigFragmentText } from "./qwen-config-fragment.ts";
import { buildQwenMcpServerEntry } from "./qwen-mcp-entry.ts";
import { qwenHomeDir } from "./qwen-paths.ts";
import { mergeQwenProjectSettings, type QwenProjectSettingsOutcome } from "./qwen-project-settings.ts";
import {
  applyCodexProjectTrust,
  buildHookStateBlock,
  buildMcpServerBlock,
  findBlock,
  parseTomlBlocks,
  serializeTomlBlocks,
  tomlString,
  type TomlBlock,
  upsertBlock,
} from "./codex-toml.ts";
import { codexHookStateKey, computeCodexHookTrustHash } from "./codex-hook-trust.ts";

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
const STABLE = petboxWireMirrorDir();

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
    "       npx petbox-wire apply [--offline] [--all [--dry-run]]\n" +
    "                             [--roles=project|user] [--adopt <abs path>]...\n" +
    "       npx petbox-wire status [--offline] [--all]\n" +
    "       npx petbox-wire doctor [--offline]\n" +
    "       npx petbox-wire layers [dir...]\n" +
    "       npx petbox-wire roles\n" +
    "       npx petbox-wire roles --check-models\n" +
    "       npx petbox-wire roles export\n" +
    "       npx petbox-wire profile use <name>\n" +
    "       npx petbox-wire model set <role|--all-roles> <model> [--agent <id>|--all-agents]\n" +
    "                                 [--profile <name>|--all-profiles] [--allow-unknown-model]\n" +
    "       npx petbox-wire model unset <role|--all-roles> [--agent <id>|--all-agents]\n" +
    "                                   [--profile <name>|--all-profiles]\n" +
    "       npx petbox-wire model reset <role|--all-roles> [--agent <id>|--all-agents]\n" +
    "                                   [--profile <name>|--all-profiles]\n" +
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
    "             role→model binding (~/.petbox/roles.json). The definition is built FROM FILES, by\n" +
    "             laying layers over each other in a declared order — base (this package's\n" +
    "             default-agents.json, always present) < user (~/.petbox/agents) < project\n" +
    "             (<root>/.petbox/agents) — and never fetched from anywhere. A layer directory that\n" +
    "             does not exist is a layer with no opinion; a layer that IS there and cannot be\n" +
    "             read/parsed hard-refuses the run, naming the file, having written nothing. Every\n" +
    "             run prints its layers and, per role and field, WHICH layer supplied it. Writes\n" +
    "             under the git worktree\n" +
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
    "             --offline and an unregistered directory are things you asked for. --offline no\n" +
    "             longer has anything to do with the definition (that resolve is file-only and always\n" +
    "             runs); it now skips exactly the network this command still does — the workspace\n" +
    "             probe behind the skill refresh. When 1 or 3 also\n" +
    "             apply they win the code; the skip still shows in the printed summary.\n" +
    "             --all runs apply once per registered project (~/.petbox/projects.json) instead of\n" +
    "             cwd only, with a per-project outcome line (written/unchanged/refused/missing-dir/\n" +
    "             error) and an aggregate exit code (the strongest across every project). A registry\n" +
    "             entry whose directory no longer exists is reported and skipped, never aborts the\n" +
    "             rest of the sweep. --dry-run computes and prints every outcome WITHOUT writing or\n" +
    "             deleting anything — use it before a bare `--all`, which writes into every registered\n" +
    "             project's working directory, including ones with uncommitted changes. Its per-file\n" +
    "             lines and its summary come from ONE ledger, so the preview's counts are the counts\n" +
    "             of the run that would execute (skill writes used to be invisible to the old\n" +
    "             role-only counter, printing a 12-write project as 'unchanged').\n" +
    "             --roles=project|user picks WHERE role artifacts are rendered. project (historical):\n" +
    "             into each project tree. user (default on a fresh machine — no ~/.petbox/wire.json\n" +
    "             yet, since 2026-09-09): ONCE into the three harness profiles —\n" +
    "             ~/.claude/agents, ~/.config/opencode/agents (plural; the singular `agent` is\n" +
    "             opencode legacy), ~/.factory/droids — 15 files instead of 90, and each project's own\n" +
    "             role copies are then swept (marker-gated: a file without `petbox: managed` is\n" +
    "             reported and kept, never deleted). Skills stay per-project either way — their\n" +
    "             bodies carry {{PROJECT}} substitution. The choice is REMEMBERED in\n" +
    "             ~/.petbox/wire.json, so a later plain `apply`, `wire` step 11 or hook does not\n" +
    "             silently re-create the project copies. --dry-run never persists it.\n" +
    "             Every apply also writes the kit's managed paths into the project's .gitignore, in a\n" +
    "             delimited block that is the only region it ever reads or replaces.\n" +
    "             --adopt <absolute path> (repeatable): treat the file at THAT EXACT PATH as an old\n" +
    "             PetBox render and overwrite it even though it carries no origin marker — the escape\n" +
    "             hatch for the pre-marker `petbox/SKILL.md` copies. There is deliberately no --force\n" +
    "             and no bulk variant: a path you did not name is still refused and still exits 1, and\n" +
    "             a `petbox: manual` declaration outranks --adopt. A named path apply never considered\n" +
    "             is reported and exits 1 rather than passing silently.\n" +
    "status       Print FACT, not a verdict: per declared role x harness, the materialized artifact\n" +
    "             path, its bound model, WHERE that model came from (roster = ~/.petbox/roles.json;\n" +
    "             seed = DEFAULT_ROLE_MODEL_SEED preview, roles.json absent, nothing written; none =\n" +
    "             a PROBLEM — no source at all, apply will hard-refuse on a closed-model-space harness\n" +
    "             or warn-and-inherit on an open one), and the command to change it. Plus a four-pillar\n" +
    "             summary: definition layers (which of base/user/project are present, and which layer\n" +
    "             gave each field), roster completeness, memory canon (absent/empty/N of 10k chars),\n" +
    "             and skill files (materialized? byte-identical to the current template?). Reads the\n" +
    "             SAME resolvers apply/doctor use; never gates, never writes. --offline skips the\n" +
    "             canon/skill-template network calls (the definition never needed one). Always exits\n" +
    "             0 unless status itself crashes — it asserts nothing about correctness. Also prints\n" +
    "             whether npm's published 'latest' kit is behind this checkout's local `main` (best-\n" +
    "             effort — skipped outside a git checkout with a resolvable `main` ref).\n" +
    "             --all: one screen across the WHOLE registry instead of cwd only — one row per\n" +
    "             registered project (skill composition vs. the currently installed kit's templates,\n" +
    "             and what's wrong), plus the same npm-wire tag line once at the top. Read-only\n" +
    "             (never writes), safe to run against every project in the registry.\n" +
    "doctor       Resolve the agent definition the same way apply does (the file cascade base < user <\n" +
    "             project), then run the truthfulness gate for every known harness against THAT\n" +
    "             definition, with the harness's local binding fed into the gate — so a roles.json id\n" +
    "             the harness cannot resolve fails here rather than at runtime. Prints OK or each\n" +
    "             violation, plus the layers it resolved and their per-field provenance. Also reports\n" +
    "             skill-file drift against the kit templates, the session-banner\n" +
    "             budget margin, and a tail of ~/.petbox/wire.log. Network checks are skipped with an\n" +
    "             explicit reason when the server is unreachable, never silently. --offline skips them\n" +
    "             itself up front: no skill-file drift check, no banner-budget check — the definition\n" +
    "             resolve and the truthfulness gate still run, because neither one touches a network.\n" +
    "             A broken definition layer is a HARD failure here, same as in apply: doctor exists to\n" +
    "             gate the definition apply would compile.\n" +
    "             Exit 0 all OK; 1 hard fail (invalid/unreadable definition layer); 2 usage; 3 truthfulness\n" +
    "             (same taxonomy as apply — policy block is not a hard crash; doctor never reports 4,\n" +
    "             it skips no step of its own).\n" +
    "layers       Diagnose the definition-layer cascade: which layer directories exist on this\n" +
    "             machine, where they physically live, and — by FIELD, never \"the files differ\" —\n" +
    "             what they disagree about. Built on layer-cascade.ts's own resolver, the same one\n" +
    "             apply/doctor/status resolve with. With no <dir> arguments it checks exactly what\n" +
    "             apply would: the kit base (default-agents.json, always the floor) under\n" +
    "             ~/.petbox/agents (user) and <project root>/.petbox/agents (project). Pass explicit\n" +
    "             directories (lowest priority first) to compare an arbitrary set instead — that mode\n" +
    "             takes your list literally and adds no base. A directory that exists but declares\n" +
    "             nothing (empty, or only .DS_Store/Thumbs.db/a README) counts as absent, not broken.\n" +
    "             Never writes; never touches apply's own exit code. Exit 0 clean (the cascade\n" +
    "             resolved, zero cascade errors — including the ordinary fresh-machine case where the\n" +
    "             kit base is the only layer); 1 diverged (a cascade ERROR was found — E0-E5/E1);\n" +
    "             2 usage; 3 COULD NOT CHECK (a present layer's source is broken, or fewer than two\n" +
    "             explicit directories in explicit mode) — never confused with 0 or 1.\n" +
    "roles        Print the local role→model binding for the active profile (~/.petbox/roles.json).\n" +
    "             Offline; empty store exits 0 with a clear message (never invents default models).\n" +
    "roles --check-models\n" +
    "             Check EVERY binding in roles.json against its harness's LIVE model source and\n" +
    "             print the three-way tally. READ-ONLY: writes nothing, gates nothing, always exits\n" +
    "             0. Costs one network round trip (codex) and one `opencode models` spawn, once for\n" +
    "             the whole sweep — which is why it is opt-in rather than part of plain `roles`.\n" +
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
    "             model parameter is a closed enum of exactly those. A SECOND gate then asks the\n" +
    "             harness's LIVE source whether the identifier is known at all (qwen: settings.json\n" +
    "             modelProviders; droid: built-in catalog + customModels; codex: /models of the\n" +
    "             active model_provider; opencode: `opencode models`), with THREE outcomes: valid;\n" +
    "             invalid (warns and writes — that source describes THIS machine, not the model's\n" +
    "             existence); and COULD NOT VERIFY (source unreachable: no key, not on PATH, no\n" +
    "             config yet), which never masquerades as invalid and never blocks. Not offline for\n" +
    "             codex/opencode. --all-roles/--all-agents/--all-profiles write the SAME model across\n" +
    "             a SLICE of the matrix instead of one cell — the live gate runs ONCE per distinct\n" +
    "             agent in the slice (shared cache), and the write is all-or-nothing: any agent the\n" +
    "             live gate blocks refuses the WHOLE slice, nothing partial is ever written; a\n" +
    "             non-blocking outcome (invalid-but-warn, unverified) never blocks any of it. Printed\n" +
    "             cell by cell so it is visible what changed. Right after writing, refreshes the\n" +
    "             codex/qwen printed fragments and (when this machine's roleScope is `user`) the\n" +
    "             per-harness role files themselves — no separate `wire`/`apply` needed for that; a\n" +
    "             `project`-scope machine still needs `apply`. Prints `next: petbox-wire apply` as a\n" +
    "             safety net either way.\n" +
    "model unset  Clear one role's binding for --agent (default: claude-code), or a whole slice with\n" +
    "             --all-roles/--all-agents/--all-profiles. A fair-empty binding a role can hold on\n" +
    "             purpose (e.g. reserve, when the machine lacks access to the tier it would\n" +
    "             otherwise be bound to) — the role then inherits the session model, and apply warns\n" +
    "             about that honestly. No live gate (nothing is being validated). Same post-write\n" +
    "             refresh as `model set`. Offline. Prints `next: petbox-wire apply`.\n" +
    "model reset  Give one role's binding (or a whole slice) back to the kit's own current default —\n" +
    "             stamps origin `kit`, UNCONDITIONALLY overwriting even an `owner` binding (unlike\n" +
    "             the passive reseed on `apply`/`wire`, which only ever refreshes a cell already\n" +
    "             labelled `kit`). A cell the kit has never seeded (e.g. opencode) is reported, never\n" +
    "             invented. No live gate (the value is the kit's own known-good default, not\n" +
    "             user-supplied). Same post-write refresh as `model set`. Offline. Prints `next:\n" +
    "             petbox-wire apply`.";
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
// (doctor-gates-wrong-definition): the file cascade base < user < project, resolved through the
// one shared resolver (definition-source.ts's resolveLocalDefinition), exactly like apply.
// Exit codes match apply (WIRE_EXIT): 0 OK; 1 hard (invalid/unreadable definition layer);
// 2 usage; 3 truthfulness policy.
//
// The built-in-vs-live definition drift check that used to live here is GONE, and not because it
// was noisy: there is no longer a second document to drift FROM. The kit's baseline stopped being
// "an offline bootstrap minimum that lags the server" and became the base LAYER every resolve is
// built on (card wire-stops-fetching-definition). A check comparing it against itself would
// report "no drift" forever and mean nothing.
//
// The materialized-skill-vs-template drift check (bug builtin-definition-drifts-no-catchup item 3,
// plus skill-files-clobber-and-apply-skips item 3) stays: skills DO still come from the server's
// workspace identity, so it remains informational and network-gated.
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
  let local: LocalDefinition;
  try {
    local = resolveLocalDefinition({ root: resolveApplyRoot(process.cwd()).root });
    if (local.errors.length > 0) {
      throw new Error(
        `the definition layer cascade reported ${local.errors.length} error(s):\n` +
          formatDefinitionErrors(local.errors) +
          `\nNothing was gated. Fix the layer files named above, or remove them.`,
      );
    }
    definition = local.definition;
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
  log(`doctor: ${formatDefinitionLayersLine(local)}`);
  log("doctor: per-field provenance — which layer supplied each field of each resolved role:");
  log(formatDefinitionProvenance(local));
  log(`doctor: ${bindingNote}`);

  // Skill-template drift check (bugs: skill-files-clobber-and-apply-skips item 3,
  // builtin-definition-drifts-no-catchup item 3 — the one item both cards' verdicts named as the
  // last thing left undone) — informational only, never gating the exit code: doctor's own
  // gate (the truthfulness pass below) is offline by design, and comparing a materialized skill
  // file against its
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
  // roles.json / corrupt registry / broken definition layer / scope-refused fetch events that hooks and
  // best-effort code paths were told to log but not necessarily print loudly.
  const wireLogTail = readWireLogTail(10);
  if (wireLogTail.length === 0) {
    log(`doctor: wire.log — no recorded silent-failure traces (${wireLogPath()}).`);
  } else {
    log(`doctor: wire.log — ${wireLogTail.length} most recent trace line(s) (${wireLogPath()}):`);
    for (const line of wireLogTail) log(`  ${line}`);
  }

  // keys.json vs environment drift (card keys-json-doctor-drift-check): local-only, no network —
  // runs unconditionally, --offline included. Bypasses resolveProject's env-first fallback (see
  // registry.ts's comment on detectKeysStoreDrift) so a healthy `doctor` actually proves the FILE
  // is in sync — not just that THIS process's env resolved fine, which is all the old truthfulness
  // loop below ever checked. Silent when nothing has drifted: a positive "in sync" line would be
  // noise on every wired machine on every run, whether or not any project's key ever changed —
  // never gates the exit code, same taxonomy as every other doctor drift check above. Auto-syncs
  // whatever it found stale (requirement: apply AND doctor refresh the file now, not only `wire`),
  // so the same key does not keep re-warning on every later run.
  const keyDrifts = detectKeysStoreDrift();
  if (keyDrifts.length > 0) {
    console.error(
      `doctor: keys.json — ${keyDrifts.length} key(s) were out of sync with the environment ` +
        `(values never printed; comparison is by hash only; now auto-synced from the environment):`,
    );
    for (const d of keyDrifts) console.error(`  - ${formatKeyDrift(d)}`);
    syncKeysStoreFromEnv();
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
  /**
   * Every outcome this pass produced, folded from the SAME ledger the log lines were rendered
   * from (apply-ledger.ts). Replaces the old `written` field, which counted ROLE writes only and
   * was read everywhere as "files": a project where apply wrote twelve skill files and no role
   * files reported "unchanged — no changes" (observation apply-all-summary-undercounts-writes).
   * Renamed rather than repaired so no caller can keep the old meaning by accident.
   */
  readonly summary: ApplySummary;
  readonly writtenHarnesses: readonly string[];
  readonly partialHarnesses: readonly string[];
  readonly blockedHarnesses: readonly string[];
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
  /**
   * WHERE role artifacts go (card: normalize-all-environments-to-default item 1). "project" is
   * the historical behavior, unchanged. "user" means the roles were already rendered ONCE into
   * the harness profiles by applyUserRoles — so this pass renders NO roles here and instead
   * sweeps the project's own copies, which are now duplicates that can only drift.
   * Skills are unaffected by this axis: their bodies carry {{PROJECT}} substitution, so they are
   * per-project by construction (skill-files.ts).
   */
  roleScope?: RoleScope;
  /** `--adopt` paths. Defaults to the empty set — no path is ever adoptable unless named. */
  adopt?: AdoptSet;
}): Promise<ApplyRunResult> {
  const cwd = opts.cwd ?? process.cwd();
  const dryRun = opts.dryRun ?? false;
  const roleScope = opts.roleScope ?? "project";
  const adopt = opts.adopt ?? NO_ADOPT;
  const ledger = createLedger();
  // Every outcome goes through here: recorded first, THEN rendered from the recorded action.
  // There is deliberately no way to print an outcome line without counting it (apply-ledger.ts).
  const emit = (action: ApplyAction): void => {
    ledger.record(action);
    const rendered = formatAction(opts.label, action, dryRun);
    if (rendered.stderr) console.error(rendered.text);
    else log(rendered.text);
  };
  const { root, via } = resolveApplyRoot(cwd);
  // SCATTER GUARD (card: wire-apply-guard-registered-dir, second scenario). `via === "cwd"` means
  // resolveApplyRoot found NO git working tree and fell back to "wherever this process happened to
  // be started" — it is a fallback, not an answer. Under roleScope=project that fallback is the
  // instruction to render 5 roles × 3 harness layouts into that directory, so a friend who ran
  // `petbox-wire apply` from their home directory (or a downloads folder, or a shell that had not
  // cd'd anywhere) got 15 files scattered where nothing will ever maintain them — and one of the
  // three trees, `~/.opencode/agent`, is not even a path any harness reads any more.
  //
  // The distinction that makes this safe is `via` itself, and it is exactly the right axis:
  //   - via="git" — a real checkout, including a FRESH CLONE that is not registered yet. That
  //     case is documented, intentional and unchanged: root = the clone's top, skills are skipped
  //     with "run `wire` here first", exit 0 (apply-skills-skip.test.ts). Never refused here.
  //   - via="cwd" — there is no project. Refuse rather than guess one.
  // NOT keyed on the registry: HOME could be a registered prefix and the scatter would be just as
  // wrong, so "is this path in ~/.petbox/projects.json" answers a different question entirely.
  //
  // roleScope=user is untouched by this: it renders into the harness profiles and needs no project
  // at all, which is precisely why it is the hint below.
  if (roleScope === "project" && via === "cwd") {
    console.error(
      `${opts.label}: REFUSED — ${root} is not a git working tree, so it is not a project; apply ` +
        `fell back to the current directory only because it had nothing better. Rendering ` +
        `roleScope=project here would scatter ${HARNESS_IDS.length} harness layouts of role files ` +
        `into a directory nothing maintains. Nothing was written by this step (${opts.label}).\n` +
        `  To install the roles for this MACHINE (they belong in the harness profiles, not in a ` +
        `directory): petbox-wire apply --roles=user\n` +
        `  To wire a PROJECT: run this from inside its checkout (any directory under its git ` +
        `working tree will do).`,
    );
    return {
      code: WIRE_EXIT.hard,
      summary: summarize(ledger.actions),
      writtenHarnesses: [],
      partialHarnesses: [],
      blockedHarnesses: [],
      hardError: true,
    };
  }
  let definition: AgentDefinition;
  let local: LocalDefinition;
  let rolesData: RolesFile;
  try {
    // The ONE resolve path: base < user < project, from files, no network anywhere on it
    // (definition-source.ts). A PRESENT-but-unreadable layer throws LayerSourceError here — with
    // the absolute path and the parser's own position already in its message — and lands in the
    // catch below BEFORE the first artifact is touched, which is the whole contract of
    // broken-layer-fails-loudly for a BUILD command. A layer directory that is simply absent is
    // not an error and is never mentioned as one.
    local = resolveLocalDefinition({ root });
    if (local.errors.length > 0) {
      throw new Error(
        `the definition layer cascade reported ${local.errors.length} error(s):\n` +
          formatDefinitionErrors(local.errors) +
          `\nNothing was written by this step (${opts.label}). Fix the layer files named above, or remove them.`,
      );
    }
    definition = local.definition;
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
          `\nNothing was written by this step (${opts.label}). Fix the definition (add the role, or drop the reference).`,
      );
    }
    // strict: a corrupt roles.json must hard-fail apply, not silently compile as "no bindings"
    // (wire-silent-failures-invisible — the 2026-07-12 "worker rides on Opus" incident shape).
    rolesData = loadRoles(homedir(), { strict: true });
  } catch (e) {
    console.error(`${opts.label}: hard failure — ${e instanceof Error ? e.message : String(e)}`);
    return {
      code: WIRE_EXIT.hard,
      summary: summarize(ledger.actions),
      writtenHarnesses: [],
      partialHarnesses: [],
      blockedHarnesses: [],
      hardError: true,
    };
  }

  log(`${opts.label}: root=${root} (via ${via})`);
  // The grep-able lines naming WHICH layers this run compiled and, per role and field, which one
  // supplied it. D18 makes this load-bearing, not cosmetic: stage 2's confirmation is "apply ran
  // on all three harnesses WITHOUT going to the server for the definition", and that is
  // unprovable unless apply states its own resolution path in its own output. A source= label
  // ("server"/"lkg"/"default") cannot state it — there is no single source any more, only an
  // ordered set of layers, and the interesting fact is which of them won each field.
  log(
    `${opts.label}: definition="${definition.name}" ${formatDefinitionLayersLine(local)}, ` +
      `harnesses=${HARNESS_IDS.join(",")}`,
  );
  log(`${opts.label}: per-field provenance — which layer supplied each field of each resolved role:`);
  log(formatDefinitionProvenance(local));

  // Orphan sweep (bug: artifact-integrity-dangling-and-orphans) — UNCONDITIONAL, matching the
  // user-scope path (applyUserRoles) that always swept.
  //
  // It used to be gated on `source === "server"`, and that gate was right for what it guarded: a
  // degraded network resolve (an LKG replica, or the kit baseline after a blip or a 404)
  // legitimately holds FEWER roles than the project really has, and sweeping against it would
  // delete live roles' artifacts because a socket hiccuped. There is no such resolve left. The
  // cascade is built from local files that either read or hard-refuse the whole run above, so
  // "this definition might be an accidental subset" is no longer a state the code can be in —
  // and leaving the gate would have silently disabled the sweep forever, since its condition
  // became permanently false the moment the server path went away.

  const writtenHarnesses: string[] = [];
  const partialHarnesses: string[] = [];
  const blockedHarnesses: string[] = [];
  // Any writeArtifact refusal (bug: apply-clobbers-user-agent-files) — a real file that is not
  // ours sat where we needed to write. Distinct from the truthfulness gate: it can happen even
  // when every role is capability/model-clean, so it needs its own signal into the exit code.
  let clobberBlocked = false;

  // ---- roles --------------------------------------------------------------------------------
  // Under roleScope "user" the roles were already rendered ONCE into the harness profiles
  // (applyUserRoles, called by the caller before this sweep) — so this project gets no role
  // render at all, only the removal of the copies it still holds. See the option's doc comment.
  if (roleScope === "user") {
    for (const harness of HARNESS_IDS) {
      // COLLISION GUARD (card: wire-apply-guard-registered-dir). When `root` IS the home
      // directory — which is exactly what resolveApplyRoot hands back for an `apply` run from
      // $HOME, since HOME is not a git working tree and the cwd fallback takes over — then
      // `join(root, agentFilesDir(h))` and `userAgentFilesRoot(h)` are the SAME directory for
      // claude-code (~/.claude/agents) and droid (~/.factory/droids). The sweep below would then
      // delete, as "project copies", the very files applyUserRoles rendered moments earlier in
      // this same command: measured 10 of 15, silently, exit 0. Asked per harness because
      // opencode does NOT collide (project `.opencode/agent` vs user `.config/opencode/agents`)
      // and a guard resting on that accident would be no guard at all. See role-dir-collision.ts
      // for why the comparison is not string equality.
      const collision = userProfileCollision(root, harness, homedir());
      if (collision) {
        log(
          `${opts.label}: sweep SKIPPED for ${harness} — the project role directory is the user ` +
            `profile itself (${collision.projectDir}). Nothing here is a project copy: these are ` +
            `the user-scope roles this run just rendered. Root ${root} was resolved from cwd, not ` +
            `from a git working tree.`,
        );
        continue;
      }
      // opencode's project directory is the singular `.opencode/agent`, which the target layout
      // drops entirely (the user-scope name is the plural `agents`) — so its now-empty directory
      // goes too. rmdirSync refuses a non-empty directory, so a project keeping its own files
      // there is never touched.
      for (const swept of sweepProjectRoleArtifacts(root, harness, {
        dryRun,
        pruneEmptyDir: harness === "opencode",
      })) {
        emit(
          swept.outcome === "removed"
            ? {
                kind: "remove",
                subject: "role",
                path: swept.path,
                note: `project copy — roles now render into the ${harness} user profile`,
              }
            : { kind: "kept", subject: "role", path: swept.path },
        );
      }
    }
  } else {
    for (const harness of HARNESS_IDS) {
      const roleModels = resolveAgentRoles(rolesData, harness);
      const plan = planApply(definition, harness, roleModels);

      let writtenThisHarness = 0;
      let clobberedThisHarness = false;
      for (const file of plan.files) {
        const abs = join(root, file.relativePath);
        adopt.consider(abs);
        const outcome = writeArtifact(abs, file.content, { dryRun, adopt: adopt.has(abs) });
        if (outcome.kind === "blocked") {
          clobberBlocked = true;
          clobberedThisHarness = true;
          emit({ kind: "refuse", subject: "role", path: abs });
          continue;
        }
        if (outcome.reason !== "unchanged") {
          emit({
            kind: "write",
            subject: "role",
            path: abs,
            ...(outcome.reason === "own"
              ? { note: "updated in place — ours" }
              : outcome.reason === "adopted"
                ? { note: "ADOPTED — unmarked file overwritten because --adopt named this exact path" }
                : {}),
          });
          writtenThisHarness++;
        } else {
          emit({ kind: "unchanged", subject: "role", path: abs });
        }

        // Namespacing rename cleanup: remove an OWNED pre-rename unprefixed leftover now that its
        // petbox-<role> replacement exists. Only after a successful write — never orphan a role by
        // deleting the old file when the new one could not be written. Never touches a path that
        // lacks our marker (cleanupLegacyArtifact's own contract — see apply-write.ts).
        if (file.legacyRelativePath !== file.relativePath) {
          const legacyAbs = join(root, file.legacyRelativePath);
          const legacyOutcome = cleanupLegacyArtifact(legacyAbs, { dryRun });
          if (legacyOutcome === "removed") {
            emit({
              kind: "remove",
              subject: "legacy",
              path: legacyAbs,
              note: `unprefixed, ours, superseded by ${abs}`,
            });
          } else if (legacyOutcome === "kept-foreign") {
            emit({ kind: "kept", subject: "legacy", path: legacyAbs });
          }
        }
      }

      // Orphan sweep — a role that is GONE from the definition (apply-orphans.ts). Runs per
      // harness, AFTER its writes, and independently of them: a role skipped by the truthfulness
      // gate is still declared and its file is never a candidate. Removal still requires our
      // origin marker, so a user's own file in the petbox-* namespace is reported and kept.
      for (const orphan of sweepOrphanArtifacts(root, harness, definition, { dryRun })) {
        emit(
          orphan.outcome === "removed"
            ? {
                kind: "remove",
                subject: "orphan",
                path: orphan.path,
                note: `its role is no longer in definition "${definition.name}"`,
              }
            : {
                kind: "kept",
                subject: "orphan",
                path: orphan.path,
                note: "no role by that name in the definition",
              },
        );
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
  }

  // Skills (bug: skill-files-clobber-and-apply-skips): a full `wire` was the ONLY thing that ever
  // wrote these — `apply` skipped them entirely, so a template edit "drifted" until the next full
  // wire (this is exactly what the owner observed for petbox-methodology). `apply` now refreshes
  // them too, using the SAME origin-marker write guard as the agent files above; a blocked skill
  // path folds into the same clobber-refusal exit path. Best-effort project identity: this is a
  // registered project's directory or it is not — `apply` never re-derives one. `--offline` skips
  // the network probe for workspace, and after stage 2 that probe is the ONLY network call apply
  // still makes: the definition resolve above reads files and runs either way.
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
        { dryRun, adopt: (p: string) => (adopt.consider(p), adopt.has(p)) },
      );
      if (reportSkillOutcomes(emit, skillOutcomes)) clobberBlocked = true;
    }
  }

  // ---- qwen project settings (card wire-qwen-project-settings-mcp-and-skills) ----------------
  // The gap this closes: `wire` wrote `<project>/.qwen/settings.json` and `apply` did not, so a
  // project wired by an older kit and kept current with `apply` alone never grew the file. Caught
  // live in D:\my\prj\petsonde: no `.qwen` directory at all, qwen fell back to `.mcp.json`
  // (claude-code's format, which qwen does not env-var-resolve) and reported `needs
  // authentication` with zero tools, on a key that was verified healthy against the live server.
  //
  // Preconditions, both load-bearing:
  //   - `via === "git"` — the SAME reason the scatter guard above exists, and it matters more
  //     here than anywhere else in this function. Under roleScope=user apply legitimately runs
  //     with root = the HOME directory (HOME is not a git working tree, so resolveApplyRoot falls
  //     back to cwd); writing `.qwen/settings.json` there would create a USER-scope settings file
  //     carrying `skills.directories`, and qwen loads every directory from that key at `user`
  //     level — i.e. exactly the one-project's-skills-in-every-project spill this card names as
  //     its second measured trap. `via` is the axis that tells a project from a fallback.
  //   - a registry hit — the envVar is the registry's to state, and `apply` never invents a
  //     project identity (same rule the skills refresh above follows).
  // Deliberately NOT gated on `--offline`: this write is pure filesystem, unlike the workspace
  // probe, so an offline apply is entitled to a complete one.
  //
  // DEFAULT_BASE_URL, not `resolvedForSkills.baseUrl`, and that is not an oversight: every one of
  // the five project MCP configs writeProjectFiles emits is built from this same constant, so
  // taking the registry's value here would make `wire` and `apply` write DIFFERENT urls into the
  // same file — each run overwriting the other's, flip-flopping forever and warning about a name
  // conflict every time. (In production the two agree by construction: upsertRegistry stores
  // `baseUrl` only when it DIFFERS from this constant, and nothing but the test loopback seam
  // ever makes it differ.) If per-project base urls are ever really wanted, that is one change
  // across all five sites, not a sixth site quietly disagreeing.
  if (via === "git" && resolvedForSkills) {
    const qwenReport = wireQwenProjectSettings(root, resolvedForSkills.envVar, DEFAULT_BASE_URL, opts.label, {
      dryRun,
    });
    emit(
      qwenReport.outcome.reason === "unchanged"
        ? { kind: "unchanged", subject: "qwen", path: qwenReport.path }
        : {
            kind: "write",
            subject: "qwen",
            path: qwenReport.path,
            note:
              qwenReport.outcome.reason === "new"
                ? "created — petbox MCP server + skills.directories"
                : "petbox MCP server + skills.directories merged (other keys untouched)",
          },
    );
  }

  // ---- the single git policy for managed paths (card item 5) ---------------------------------
  // Owner decision 2026-09-02: managed paths belong in the PROJECT's `.gitignore`, not in
  // `.git/info/exclude`. Only the kit's own delimited block is ever read or replaced
  // (gitignore-block.ts); every other line in the file is copied through byte for byte. Skipped
  // entirely when the root is not a git working tree — there is nothing for a `.gitignore` to
  // mean there, and writing one into a plain directory would be litter.
  if (via === "git") {
    const gitignorePath = join(root, ".gitignore");
    const outcome = upsertGitignoreBlock(gitignorePath, managedGitignoreEntries(), { dryRun });
    emit(
      outcome === "unchanged"
        ? { kind: "unchanged", subject: "gitignore", path: gitignorePath }
        : {
            kind: "write",
            subject: "gitignore",
            path: gitignorePath,
            note:
              outcome === "new"
                ? "created — managed-path block"
                : "managed-path block updated (other lines untouched)",
          },
    );
  }

  const summaryCounts = summarize(ledger.actions);
  // Structured summary (machine-readable-ish one line + human detail above). skillsSkipped is
  // always present (never silently dropped) so a machine reader can tell "no skip" from "skip
  // information was omitted" — null means the skills step actually ran (skipped nothing).
  const summary = {
    ...summaryCounts,
    roleScope,
    writtenHarnesses,
    partialHarnesses,
    blockedHarnesses,
    skillsSkipped: skillsSkip ?? null,
  };
  // The counted summary comes from the SAME ledger the per-file lines above were rendered from —
  // that identity is the whole point of the module, and it is what the "would write" counting
  // test asserts (apply-ledger.test.ts / apply-all-registry.test.ts).
  log(formatSummaryLine(opts.label, summaryCounts, dryRun));
  log(
    `${opts.label}: result roleScope=${roleScope} ` +
      `ok=[${writtenHarnesses.join(",")}] ` +
      `partial=[${partialHarnesses.join(",")}] ` +
      `blocked=[${blockedHarnesses.join(",")}]` +
      (summaryCounts.refusedPaths.length > 0
        ? ` clobber-refused=[${summaryCounts.refusedPaths.join(",")}]`
        : ""),
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
      `${opts.label}: hard failure — refused to overwrite ${summaryCounts.refused} non-PetBox ` +
        `file(s) (exit ${WIRE_EXIT.hard}). ${JSON.stringify(summary)}`,
    );
  } else {
    console.error(
      `${opts.label}: truthfulness partial — some roles/harnesses blocked (exit ${WIRE_EXIT.truthfulness}). ${JSON.stringify(summary)}`,
    );
  }

  return {
    code,
    summary: summaryCounts,
    writtenHarnesses,
    partialHarnesses,
    blockedHarnesses,
    hardError: clobberBlocked,
  };
}

// `apply` subcommand — parses CLI args, runs performApply, exits with its code (2 on bad args,
// via usage()). Exit codes: 0 full success; 1 hard failure; 2 usage/args; 3 truthfulness;
// 4 incomplete (a step was skipped for a reason the user did not ask for).
async function runApply(argv: string[]): Promise<void> {
  let offline = false;
  let all = false;
  let dryRun = false;
  let roleScopeFlag: RoleScope | undefined;
  const adoptPaths: string[] = [];
  for (let i = 1; i < argv.length; i++) {
    const a = argv[i];
    if (a === undefined) continue; // unreachable: i < argv.length is the loop condition
    if (a === "--help" || a === "-h") usage(0);
    else if (a === "--offline") offline = true;
    else if (a === "--all") all = true;
    else if (a === "--dry-run") dryRun = true;
    else if (a.startsWith("--roles=")) {
      const v = a.slice("--roles=".length).trim();
      if (!isRoleScope(v)) {
        console.error(`apply: --roles must be one of ${ROLE_SCOPES.join("|")} (got '${v}')`);
        usage(WIRE_EXIT.usage);
      }
      roleScopeFlag = v;
    } else if (a === "--roles") {
      const v = argv[++i];
      if (!v || !isRoleScope(v.trim())) {
        console.error(`apply: --roles requires one of ${ROLE_SCOPES.join("|")}`);
        usage(WIRE_EXIT.usage);
      }
      roleScopeFlag = v.trim() as RoleScope;
    } else if (a === "--adopt") {
      // Repeatable, and ABSOLUTE only. A relative path would be resolved against whatever cwd
      // apply happens to run in, which under `--all` is not the directory the file lives in —
      // the operator would name one path and adopt another. Refuse instead of guessing.
      const v = argv[++i];
      if (!v || v.startsWith("--")) {
        console.error("apply: --adopt requires an absolute path (repeat the flag for more than one)");
        usage(WIRE_EXIT.usage);
      }
      if (!isAbsolute(v)) {
        console.error(
          `apply: --adopt needs an ABSOLUTE path; got '${v}'. A relative path would resolve against ` +
            `this process's cwd, which under --all is not the directory the file lives in.`,
        );
        usage(WIRE_EXIT.usage);
      }
      adoptPaths.push(v);
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

  // keys.json auto-sync (card keys-json-doctor-drift-check): before this, only a full `wire` run
  // ever wrote the file, so a plain env-var rotation left it stale until the next full wire. A
  // --dry-run must leave the machine exactly as it found it (same rule the roleScope persistence
  // below follows) — skip the write there, but still say what WOULD have synced so a preview is
  // not silently different from the real run that follows it.
  if (dryRun) {
    const wouldSync = detectKeysStoreDrift();
    if (wouldSync.length > 0) {
      log(
        `apply: --dry-run — keys.json has ${wouldSync.length} key(s) out of sync with the ` +
          `environment (values never printed); a real run would sync them now.`,
      );
    }
  } else {
    const synced = syncKeysStoreFromEnv();
    if (synced.length > 0) {
      log(`apply: keys.json — synced ${synced.length} key(s) from the environment (values never printed).`);
    }
  }

  // Seed a fresh machine's roster BEFORE compiling — apply now refuses any declared role with
  // no local binding (reserve-unbound-inherits-session-model), so a bare `apply` on a machine
  // that never ran full `wire` needs this too, not just wire's own step 11 (see
  // seedDefaultRoleBindingsIfMissing's doc comment). No-op when roles.json already exists.
  seedDefaultRoleBindingsIfMissing("apply");

  // WHERE roles go. An explicit `--roles=` wins; otherwise the machine policy in
  // ~/.petbox/wire.json, so a plain `apply` (a hook's, `update`'s, or full `wire`'s step 11)
  // never silently re-renders 90 project copies of roles the owner just moved into the profile.
  const { scope: roleScope, source: roleScopeSource } = resolveRoleScope(roleScopeFlag, homedir());
  log(`apply: roles → ${roleScope} scope (from ${roleScopeSource})`);
  // Same fragment refresh model set/unset/reset/profile use trigger (task
  // role-model-bindings-review-refactor, remainder E) — a standalone `apply` used to compile role
  // files without ever re-checking whether the machine's codex/qwen printed fragments still match
  // the roster it just compiled against. Once per invocation regardless of --all: the fragments
  // are machine-scoped (codex/qwen home configs), not per-project.
  printCodexRosterFragment("apply:");
  printQwenRosterFragment("apply:");
  if (roleScopeFlag !== undefined && !dryRun) {
    // Persist the DECISION, not the run. A --dry-run must leave the machine exactly as it found
    // it, this file included — a preview that changed the policy would make the next real run
    // behave differently from the one that was previewed.
    saveWireConfig({ roleScope: roleScopeFlag }, homedir());
    log(`apply: remembered roleScope=${roleScopeFlag} in ${wireConfigPath(homedir())}`);
  } else if (roleScopeFlag !== undefined && dryRun) {
    log(`apply: --dry-run — roleScope=${roleScopeFlag} NOT persisted (a preview writes nothing).`);
  }

  const adopt = createAdoptSet(adoptPaths);
  if (adopt.size > 0) {
    log(
      `apply: --adopt is active for ${adopt.size} explicitly named path(s). Nothing else changes ` +
        `behavior: any other unmarked file is still refused, and the run still exits ${WIRE_EXIT.hard}.`,
    );
  }

  if (all) {
    const code = await runApplyAll({ offline, dryRun, roleScope, adopt });
    exitWith(strongestExitCode(code, reportUnmatchedAdopt(adopt)));
    return;
  }

  if (roleScope === "user") {
    const userRoles = await applyUserRoles({
      dryRun,
      adopt,
      label: "apply [roles:user]",
    });
    const result = await performApply({
      offline,
      dryRun,
      roleScope,
      adopt,
      label: "apply",
    });
    reportSplitRunOutcome("apply", userRoles, result, dryRun);
    exitWith(strongestExitCode(userRoles.code, result.code, reportUnmatchedAdopt(adopt)));
    return;
  }

  const result = await performApply({ offline, dryRun, roleScope, adopt, label: "apply" });
  // Same libuv race doctor/status hit (Assertion failed: !(handle->flags & UV_HANDLE_CLOSING),
  // src\win\async.c): performApply's workspace probe is a live network round-trip (the definition
  // resolve alongside it used to be a second one; it reads files now), and a hard process.exit()
  // right after races Windows' async-handle teardown for
  // whichever socket is still closing — the caller sees exit 127, not the WIRE_EXIT code apply's
  // own message just printed. exitWith (wire-exit.ts) is the one sanctioned spelling of the fix.
  exitWith(strongestExitCode(result.code, reportUnmatchedAdopt(adopt)));
}

/**
 * `--roles=user` is TWO write passes in one command: the machine profiles (applyUserRoles) and
 * then the project tree (performApply). Each one reports honestly about ITSELF, and each one's
 * refusal says "Nothing was written by this step" — but nothing used to reconcile them, so a run
 * where the first pass wrote 15 files and the second refused on a broken project layer ended with
 * "Nothing was written" as its last word and exit 1. That reads as "the machine is untouched",
 * which is false, and it is false in the direction that matters: the operator stops looking.
 *
 * So say it plainly, once, at the point where both outcomes are known. Only when they actually
 * disagree — a failed pass alongside a pass that already wrote — is there anything to reconcile;
 * a clean run and a wholly-failed run both speak for themselves already.
 */
function reportSplitRunOutcome(
  label: string,
  userRoles: ApplyRunResult,
  project: ApplyRunResult,
  dryRun: boolean,
): void {
  const wrote = userRoles.summary.filesWritten + userRoles.summary.removed;
  if (project.code === WIRE_EXIT.ok || wrote === 0) return;
  const verb = dryRun ? "would have changed" : "already changed";
  console.error(
    `${label}: PARTIAL RUN — the project-scope step above refused (exit ${project.code}) and ` +
      `changed nothing, but the user-scope step ${verb} ${wrote} file(s) under the harness ` +
      `profiles BEFORE it ran. The machine is not in the state it was in before this command. ` +
      `Fix what the refusal names, then re-run \`petbox-wire apply\` — it is idempotent, and the ` +
      `already-written profile files will report as unchanged.`,
  );
}

/**
 * A `--adopt` path apply never even looked at is a FAILED INSTRUCTION, not a no-op: the operator
 * typed a path (a typo, a stale one, one from a project not in this sweep) and got a silent exit
 * 0 with nothing adopted. Reported by name and folded into the exit code as a hard failure —
 * nothing was written on its account, so this can only ever turn a quiet success into a loud one.
 */
function reportUnmatchedAdopt(adopt: AdoptSet): number {
  const unmatched = adopt.unmatched();
  if (unmatched.length === 0) return WIRE_EXIT.ok;
  console.error(
    `apply: --adopt named ${unmatched.length} path(s) apply never considered — nothing was adopted for ` +
      `them (exit ${WIRE_EXIT.hard}):\n` +
      unmatched.map((p) => `  ${p}`).join("\n") +
      `\nCheck the spelling, and that the path is one apply actually writes in a registered project.`,
  );
  return WIRE_EXIT.hard;
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
  readonly offline: boolean;
  readonly dryRun: boolean;
  readonly roleScope: RoleScope;
  readonly adopt: AdoptSet;
}): Promise<number> {
  const entries = readRegistry();
  const label = opts.dryRun ? "apply --all --dry-run" : "apply --all";
  log(`${label}: ${entries.length} registered project(s) in ${registryPath()}`);

  // Roles under the user scope are a MACHINE fact, not a per-project one: rendering them inside
  // the loop would write the identical 15 files eight times over. Once, up front, before any
  // project is touched — and its exit code folds into the sweep's like any row's.
  const userRoleCodes: number[] = [];
  if (opts.roleScope === "user") {
    const userRoles = await applyUserRoles({
      dryRun: opts.dryRun,
      adopt: opts.adopt,
      label: `${label} [roles:user]`,
    });
    userRoleCodes.push(userRoles.code);
  }

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

  const code = strongestExitCode(...userRoleCodes, ...rows.map((r) => r.code));
  return code;
}

/**
 * Render the role artifacts ONCE into the three harnesses' USER profiles (card:
 * normalize-all-environments-to-default item 1). 15 files instead of 90, and the only copy that
 * exists, so there is nothing left to drift against.
 *
 * The definition is the MACHINE-WIDE half of the cascade — base < user (definition-source.ts's
 * resolveUserScopeDefinition) — with the PROJECT layer deliberately excluded, and never resolved
 * against the caller's cwd (card: user-scope-roles-rendered-from-cwd-project-definition).
 * Measured 2026-09-02: the old code resolved a per-project server document against cwd, so the
 * SAME `apply --all --dry-run` reported "using server definition default v20" from $system and
 * "default v1" from pochtar — a run from the wrong directory silently downgraded the whole
 * machine profile to whichever project's document happened to be stale, and said nothing
 * alarming. User-scope roles are a MACHINE fact; writing N possibly-different documents to the
 * same 15 paths was always last-write-wins nonsense. Both layers used here are machine-wide, so:
 *   - deterministic by construction: identical bytes from any cwd, on any machine with the same
 *     kit build and the same ~/.petbox/agents — no registry lookup, no network, nothing per-cwd;
 *   - never "unavailable": the base ships inside the npm package (package.json's `files`
 *     allowlist) and is validated at module import time (agent-definition.ts's
 *     loadDefaultAgentDefinition) — a missing/corrupt copy throws loudly there, before this
 *     function ever runs, so there is no "source unreachable" branch to write here. The user
 *     layer is optional by design: absent = no opinion, never an error;
 *   - never a silent downgrade: a BROKEN user layer refuses the run below (loudly, naming the
 *     file) instead of quietly rendering the floor — the one thing D15 forbids is a resolve that
 *     keeps working off something other than what the operator wrote.
 *
 * Two things this deliberately does NOT do:
 *  - no pre-namespacing legacy cleanup (`worker.md` next to `petbox-worker.md`). Those unprefixed
 *    names were never written into a user profile by us, so there is nothing of ours to clean —
 *    the only thing such a probe could ever find is somebody else's file, which is exactly what
 *    `~/.factory/droids/worker.md` is on the owner's machine today.
 *  - no `.gitignore` policy. A harness profile is not a project checkout.
 * The orphan sweep always runs: a file cascade is never a degraded partial replica, so a role
 * dropped from it can never be mistaken for a network hiccup — its user-scope file must go too,
 * or it outlives the roster forever. (The project-scope path now agrees; see performApply.)
 */
async function applyUserRoles(opts: {
  readonly dryRun: boolean;
  readonly adopt: AdoptSet;
  readonly label: string;
}): Promise<ApplyRunResult> {
  const ledger: ApplyLedger = createLedger();
  const emit = (action: ApplyAction): void => {
    ledger.record(action);
    const rendered = formatAction(opts.label, action, opts.dryRun);
    if (rendered.stderr) console.error(rendered.text);
    else log(rendered.text);
  };

  let definition: AgentDefinition;
  let rolesData: RolesFile;
  try {
    // base < user only — see this function's doc comment on why the project layer is excluded.
    // A broken user layer throws LayerSourceError straight into the catch below, before a single
    // profile file is touched.
    const local = resolveUserScopeDefinition({ homeDir: homedir() });
    if (local.errors.length > 0) {
      throw new Error(
        `the definition layer cascade reported ${local.errors.length} error(s):\n` +
          formatDefinitionErrors(local.errors) +
          `\nNothing was written by this step (${opts.label}). Fix the layer files named above, or remove them.`,
      );
    }
    definition = local.definition;
    // The base is already validated at module import time (agent-definition.ts) and the cascade
    // guarantees structural completeness of what it adds — re-validating here would be a second
    // copy of the same check, not extra safety.
    log(
      `${opts.label}: ${formatDefinitionLayersLine(local)} — machine-wide, same on every cwd ` +
        `running this build`,
    );
    log(`${opts.label}: per-field provenance — which layer supplied each field of each resolved role:`);
    log(formatDefinitionProvenance(local));
    const dangling = findDanglingTargets(definition);
    if (dangling.length > 0) {
      throw new Error(
        `definition "${definition.name}" names ${dangling.length} role(s) it does not define:\n` +
          formatDanglingTargets(dangling) +
          `\nNothing was written by this step (${opts.label}). Fix the definition (add the role, or drop the reference).`,
      );
    }
    rolesData = loadRoles(homedir(), { strict: true });
  } catch (e) {
    console.error(`${opts.label}: hard failure — ${e instanceof Error ? e.message : String(e)}`);
    return {
      code: WIRE_EXIT.hard,
      summary: summarize(ledger.actions),
      writtenHarnesses: [],
      partialHarnesses: [],
      blockedHarnesses: [],
      hardError: true,
    };
  }

  const writtenHarnesses: string[] = [];
  const partialHarnesses: string[] = [];
  const blockedHarnesses: string[] = [];
  let clobberBlocked = false;
  // The sweep is unconditional here and in performApply alike: a file cascade is never a
  // degraded partial replica the way a network resolve could be (see the function doc comment).

  for (const harness of HARNESS_IDS) {
    const dir = userAgentFilesRoot(harness, homedir());
    log(`${opts.label}: [${harness}] user-scope agent dir = ${dir}`);
    const roleModels = resolveAgentRoles(rolesData, harness);
    const plan = planApply(definition, harness, roleModels);

    let writtenThisHarness = 0;
    let clobberedThisHarness = false;
    for (const file of plan.files) {
      // basename ONLY: planApply's relativePath carries the PROJECT layout, and two of the three
      // user-scope directories differ from it (opencode's is `agents`, not `agent`). Taking the
      // filename and re-rooting it is the whole translation — never a string edit of the path.
      const abs = join(dir, basename(file.relativePath));
      opts.adopt.consider(abs);
      const outcome = writeArtifact(abs, file.content, {
        dryRun: opts.dryRun,
        adopt: opts.adopt.has(abs),
      });
      if (outcome.kind === "blocked") {
        clobberBlocked = true;
        clobberedThisHarness = true;
        emit({ kind: "refuse", subject: "role", path: abs });
        continue;
      }
      if (outcome.reason !== "unchanged") {
        emit({
          kind: "write",
          subject: "role",
          path: abs,
          ...(outcome.reason === "own"
            ? { note: "updated in place — ours" }
            : outcome.reason === "adopted"
              ? { note: "ADOPTED — unmarked file overwritten because --adopt named this exact path" }
              : {}),
        });
        writtenThisHarness++;
      } else {
        emit({ kind: "unchanged", subject: "role", path: abs });
      }
    }

    for (const orphan of sweepOrphanArtifactsIn(dir, harness, definition, { dryRun: opts.dryRun })) {
      emit(
        orphan.outcome === "removed"
          ? {
              kind: "remove",
              subject: "orphan",
              path: orphan.path,
              note: `its role is no longer in definition "${definition.name}"`,
            }
          : {
              kind: "kept",
              subject: "orphan",
              path: orphan.path,
              note: "no role by that name in the definition",
            },
      );
    }

    for (const w of plan.warnings) console.error(`${opts.label}: warn — ${w}`);

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

  const summaryCounts = summarize(ledger.actions);
  log(formatSummaryLine(opts.label, summaryCounts, opts.dryRun));
  const code = classifyApplyExit({
    hardError: clobberBlocked,
    hadTruthfulnessBlock: partialHarnesses.length > 0 || blockedHarnesses.length > 0,
    unintendedIncomplete: false,
  });
  return {
    code,
    summary: summaryCounts,
    writtenHarnesses,
    partialHarnesses,
    blockedHarnesses,
    hardError: clobberBlocked,
  };
}

async function applyToRegistryEntry(
  entry: RegistryEntry,
  opts: {
    readonly offline: boolean;
    readonly dryRun: boolean;
    readonly roleScope: RoleScope;
    readonly adopt: AdoptSet;
  },
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
      offline: opts.offline,
      dryRun: opts.dryRun,
      roleScope: opts.roleScope,
      adopt: opts.adopt,
      cwd: dir,
      label: `apply[${entry.project}]`,
    });
    if (result.hardError) {
      return {
        project: entry.project,
        dir,
        outcome: "refused",
        detail:
          result.summary.refusedPaths.length > 0
            ? `refused to overwrite ${result.summary.refusedPaths.length} non-PetBox file(s)`
            : `hard failure (see log lines above)`,
        code: result.code,
      };
    }
    // filesWritten, not the old role-only counter: a project whose only change was twelve skill
    // files used to report "unchanged — no changes" here while the lines above it listed twelve
    // writes (observation apply-all-summary-undercounts-writes). Removals count as a change too —
    // a project that only had its role copies swept is not "unchanged" either.
    if (result.summary.filesWritten === 0 && result.summary.removed === 0) {
      return { project: entry.project, dir, outcome: "unchanged", detail: "no changes", code: result.code };
    }
    // Neutral wording on purpose ("writes=", not "would write"): the per-file lines are the only
    // place those two verbs appear, which is what lets the counting test grep for them and
    // compare against the summary without counting the summary itself (apply-ledger.ts).
    const parts = [
      `writes=${result.summary.filesWritten} (roles=${result.summary.roleFilesWritten} ` +
        `skills=${result.summary.skillFilesWritten})`,
      ...(result.summary.removed > 0 ? [`removals=${result.summary.removed}`] : []),
    ];
    return {
      project: entry.project,
      dir,
      outcome: "written",
      detail: parts.join(", "),
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

// Print active profile + agent/role/model tree from ~/.petbox/roles.json. Exit 0 when empty.
async function runRoles(argv: string[]): Promise<void> {
  // roles | roles export | roles --check-models  (+ optional --help)
  const sub = argv[1];
  if (sub === "--help" || sub === "-h") usage(0);
  if (sub === "--check-models") {
    for (let i = 2; i < argv.length; i++) {
      const a = argv[i];
      if (a === undefined) continue; // unreachable: i < argv.length is the loop condition
      if (a === "--help" || a === "-h") usage(0);
      console.error(`roles --check-models: unexpected argument: ${a}`);
      usage();
    }
    await runRolesCheckModels();
    return;
  }
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
      `roles: no bindings in ${rolesPath()} (activeProfile would be "default").\n` +
        `  Bindings are local — set models in that file or via a future apply path; nothing is invented.`,
    );
    return;
  }
  log(formatResolvedBinding(data));
}

/**
 * `roles --check-models` — run the stage-B2 live gate over EVERY binding in roles.json and print
 * the three-way tally. READ-ONLY by construction: it loads the file, consults each harness's
 * source, and writes nothing anywhere.
 *
 * Opt-in rather than folded into plain `roles` because the sweep costs one network round trip and
 * one `opencode models` spawn (~2s total, once, thanks to the shared ModelSourceCache) — a price
 * a plain listing should not silently pay.
 *
 * Exit code is deliberately 0 even with INVALID rows: this verb REPORTS, it does not gate. The
 * gate lives on the write path (`model set`), which is where a mistake can still be prevented.
 */
async function runRolesCheckModels(): Promise<void> {
  const data = loadRoles();
  const rows = await checkRolesModelValidity(data, { cache: createModelSourceCache() });
  if (rows.length === 0) {
    log(`roles: no bindings in ${rolesPath()} — nothing to check.`);
    return;
  }
  const counts = { valid: 0, invalid: 0, unverified: 0 };
  for (const row of rows) {
    counts[row.validity.verdict]++;
    if (row.validity.verdict === "valid") continue;
    log(`${row.profile} / ${row.agent} / ${row.role} = ${row.validity.model}`);
    log(`  ${formatModelValidity(row.validity).split("\n").join("\n  ")}`);
  }
  log(
    `roles --check-models: ${rows.length} bindings — ${counts.valid} valid, ${counts.invalid} invalid, ` +
      `${counts.unverified} could not be verified.`,
  );
  log(
    `  "valid" means the catalog knows the identifier. It is NOT proof the model works: reach ` +
      `through the active provider and a working credential are separate questions.`,
  );
}

// profile use <name> — set activeProfile; create empty shell if missing.
async function runProfile(argv: string[]): Promise<void> {
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
      `\n  wrote ${rolesPath()}`,
  );
  await refreshDerivedArtifactsAfterRolesWrite("profile use");
}

// A (profile, agent, role) cell as a one-line address, for slice-op output ("покажи, что именно
// изменится" — the card's own predictability requirement).
function cellLabel(cell: SliceCell): string {
  return `${cell.profile}/${cell.agent}/${cell.role}`;
}

// Shared --agent/--all-agents/--profile/--all-profiles[/--allow-unknown-model] flag scan for
// model set/unset/reset (task role-model-bindings-review-refactor, stage D) — ONE parser so the
// three verbs cannot drift into three slightly different flag grammars.
type ModelSliceFlags = {
  readonly agent: string;
  readonly allAgents: boolean;
  readonly profile: string | undefined;
  readonly allProfiles: boolean;
  readonly allowUnknownModel: boolean;
};

function parseModelSliceFlags(
  argv: string[],
  startIdx: number,
  cmdLabel: string,
  opts: { readonly allowUnknownModelFlag: boolean },
): ModelSliceFlags {
  let agent = "claude-code";
  let allAgents = false;
  let profile: string | undefined;
  let allProfiles = false;
  let allowUnknownModel = false;
  for (let i = startIdx; i < argv.length; i++) {
    const a = argv[i];
    if (a === undefined) continue; // unreachable: i < argv.length is the loop condition
    if (a === "--help" || a === "-h") usage(0);
    else if (a === "--agent") agent = argv[++i] ?? "";
    else if (a === "--all-agents") allAgents = true;
    else if (a === "--profile") profile = argv[++i];
    else if (a === "--all-profiles") allProfiles = true;
    else if (opts.allowUnknownModelFlag && a === "--allow-unknown-model") allowUnknownModel = true;
    else {
      console.error(`${cmdLabel}: unexpected argument: ${a}`);
      usage();
    }
  }
  if (!allAgents && !agent.trim()) {
    console.error(`${cmdLabel}: --agent requires a non-empty value (or pass --all-agents)`);
    usage();
  }
  if (profile !== undefined && allProfiles) {
    console.error(`${cmdLabel}: --profile and --all-profiles are mutually exclusive`);
    usage();
  }
  return { agent, allAgents, profile, allProfiles, allowUnknownModel };
}

// Print the SAME three-outcome wording runModelSet has used since stage B2, once per agent this
// gate ran against — the wording itself is unchanged; stage D only adds the loop and the
// optional agent prefix for the slice form ("written unverified" must never read as approval,
// and "does not know this id" must never read as an unreachable source).
function logModelValidity(validity: ModelValidity, agentPrefix?: string): void {
  const p = agentPrefix ? `${agentPrefix}: ` : "";
  if (validity.verdict === "invalid") {
    log(`model: ${p}WARN — the live source says this id is not valid: ${validity.detail}`);
    log(`  source: ${validity.source}`);
    log(`  Written anyway (this harness's source describes THIS machine, not the model's existence).`);
  } else if (validity.verdict === "unverified") {
    log(`model: ${p}COULD NOT VERIFY — ${validity.detail}`);
    log(`  source: ${validity.source}`);
    log(`  This is NOT a claim that the model is wrong; the source could not be consulted.`);
  } else {
    log(`model: ${p}verified — ${validity.detail}`);
    log(`  source: ${validity.source}`);
    log(`  Known to that catalog. That is NOT proof the model works: reachability through the`);
    log(`  active provider and a working credential are separate questions.`);
  }
}

// model set/unset/reset — the tool verbs for a role→model binding (spec binding-set-by-tool):
// before `model set` existed at all, ~/.petbox/roles.json could ONLY be written by hand-editing
// an undocumented JSON format, and hand-editing it wrong is exactly how the 2026-07-12 incident (a
// droid id in the claude-code block) happened.
//
// TWO gates run for `model set`, and they answer DIFFERENT questions — do not merge them:
//   1. setRoleModel's shape gate (harness-models.ts's classifyModel) — "is this even the right
//      harness's kind of id?", offline, and the only thing that existed before stage B2.
//   2. checkModelValidity's LIVE gate (model-validity.ts, stage B2) — "does the catalog that
//      would have to resolve this id actually know it?", against this machine's real
//      qwen/droid/codex/opencode sources. Three outcomes; `unverified` (source unreachable) never
//      becomes `invalid` and never blocks, because a missing key is not evidence about a model.
//
// `--all-roles`/`--all-agents`/`--all-profiles` (task role-model-bindings-review-refactor, stage
// D, defect #5) turn a single-cell edit into a SLICE edit — a whole harness across every role, one
// role across every harness, or every profile at once — reusing the exact same single-cell
// primitives per cell (roles.ts's setRoleModelSlice/unsetRoleModelSlice/resetRoleModelSlice), so
// there is no second offline validation path and no second CLI grammar. The live gate (2) runs
// ONCE per DISTINCT canonical agent the slice touches, sharing one ModelSourceCache — the model
// value is the SAME literal string across the whole slice, so per-cell repeats would just be the
// same network round trip N times over for no new information. `unset`/`reset` need no live gate
// at all: `unset` validates nothing, and `reset` writes the kit's OWN known-good default, never a
// user-supplied id.
async function runModel(argv: string[]): Promise<void> {
  const sub = argv[1];
  if (sub === "--help" || sub === "-h") usage(0);
  if (sub === "set") {
    await runModelSet(argv);
    return;
  }
  if (sub === "unset") {
    await runModelUnset(argv);
    return;
  }
  if (sub === "reset") {
    await runModelReset(argv);
    return;
  }
  console.error(
    `model: expected "set <role> <model>", "unset <role>" or "reset <role>"${sub ? `, got "${sub}"` : ""}`,
  );
  usage();
}

// model set <role|--all-roles> <model> [--agent <id>|--all-agents] [--profile <name>|--all-profiles]
//           [--allow-unknown-model]
async function runModelSet(argv: string[]): Promise<void> {
  const roleArg = argv[2];
  const allRoles = roleArg === "--all-roles";
  if (!allRoles && (!roleArg || roleArg.startsWith("-"))) {
    console.error("model set: requires a non-empty <role>, or --all-roles");
    usage();
  }
  const model = argv[3];
  if (!model || model.startsWith("-")) {
    console.error("model set: requires a non-empty <model> (use `model unset <role>` to clear a binding)");
    usage();
  }
  const flags = parseModelSliceFlags(argv, 4, "model set", { allowUnknownModelFlag: true });

  // Every distinct canonical agent this invocation touches — one entry for the single-cell path,
  // up to CANONICAL_AGENT_IDS.length for --all-agents. The live gate runs once per entry, sharing
  // one cache, BEFORE anything is loaded or written: a binding refused here never reaches the file
  // at all.
  const agents = flags.allAgents ? [...CANONICAL_AGENT_IDS] : [canonicalAgentId(flags.agent)];
  const validityCache = createModelSourceCache();
  const validityByAgent = new Map<string, ModelValidity>();
  for (const agent of agents) {
    validityByAgent.set(agent, await checkModelValidity(agent, model, { cache: validityCache }));
  }
  const blocked = agents.filter((a) => {
    const v = validityByAgent.get(a);
    return v !== undefined && v.verdict === "invalid" && v.blocking && !flags.allowUnknownModel;
  });
  if (blocked.length > 0) {
    for (const a of blocked) {
      const v = validityByAgent.get(a);
      if (v) console.error(`  - ${a}: ${v.detail}\n    source: ${v.source}`);
    }
    console.error(
      `model set: REFUSED — the live source says '${model}' is not valid for ${blocked.length} of ` +
        `${agents.length} agent(s) in this request. Pass --allow-unknown-model to write it anyway ` +
        `if you are certain.`,
    );
    exitWith(WIRE_EXIT.truthfulness);
    return;
  }

  const before = loadRoles();

  if (!allRoles && !flags.allAgents) {
    // Single cell — byte-for-byte the pre-slice behavior.
    const result = setRoleModel(before, {
      agent: flags.agent,
      role: roleArg,
      model,
      ...(flags.profile !== undefined ? { profile: flags.profile } : {}),
      allowUnknownModel: flags.allowUnknownModel,
    });
    const canon = canonicalAgentId(flags.agent);
    const profileName = (flags.profile ?? "").trim() || before.activeProfile;
    if (!result.ok) {
      console.error(`model set: REFUSED — ${result.reason}`);
      exitWith(WIRE_EXIT.truthfulness);
      return;
    }
    saveRoles(result.data);
    log(`model: set ${canon}/${roleArg} = ${model} (profile "${profileName}")`);
    if (result.warning) log(`model: warn — ${result.warning}`);
    const v = validityByAgent.get(canon);
    if (v) logModelValidity(v);
    log(`  wrote ${rolesPath()}`);
    await refreshDerivedArtifactsAfterRolesWrite("model set");
    log(`next: petbox-wire apply`);
    return;
  }

  // Slice form: same model, every cell the selector expands to. The live gate already ran above
  // (once per distinct agent) — this only re-runs the OFFLINE shape gate per cell, same as ever.
  const sel = {
    profiles: flags.allProfiles ? ("all" as const) : [flags.profile?.trim() || before.activeProfile],
    agents: flags.allAgents ? ("all" as const) : [flags.agent],
    roles: allRoles ? ("all" as const) : [roleArg as string],
    model,
    allowUnknownModel: flags.allowUnknownModel,
  };
  const result = setRoleModelSlice(before, sel);
  if (!result.ok) {
    // Per-cell detail first, the anchored summary line immediately before the exit (kept within
    // wire-process-exit-whitelist.test.ts's 3-line lookback window on purpose). This exit can be
    // preceded by the live-gate's network round trip above, so it ends the run through exitWith,
    // never a raw process.exit — the same libuv socket-teardown reasoning stage B2 applied to the
    // single-cell path.
    for (const o of result.outcomes) {
      if (!o.ok) console.error(`  - ${cellLabel(o.cell)}: ${o.reason}`);
    }
    console.error(`model set (slice): REFUSED — ${result.reason}`);
    exitWith(WIRE_EXIT.truthfulness);
    return;
  }
  saveRoles(result.data);
  log(`model set (slice): ${result.changes.length} cell(s) set to "${model}":`);
  for (const c of result.changes) {
    log(`  ${cellLabel(c.cell)}: ${c.modelBefore ?? "(unbound)"} -> ${c.modelAfter}`);
  }
  for (const c of result.changes) {
    if (c.warning) log(`model: warn — ${cellLabel(c.cell)}: ${c.warning}`);
  }
  for (const agent of agents) {
    const v = validityByAgent.get(agent);
    if (v) logModelValidity(v, agents.length > 1 ? agent : undefined);
  }
  log(`  wrote ${rolesPath()}`);
  await refreshDerivedArtifactsAfterRolesWrite("model set");
  log(`next: petbox-wire apply`);
}

// model unset <role|--all-roles> [--agent <id>|--all-agents] [--profile <name>|--all-profiles]
async function runModelUnset(argv: string[]): Promise<void> {
  const roleArg = argv[2];
  const allRoles = roleArg === "--all-roles";
  if (!allRoles && (!roleArg || roleArg.startsWith("-"))) {
    console.error("model unset: requires a non-empty <role>, or --all-roles");
    usage();
  }
  const flags = parseModelSliceFlags(argv, 3, "model unset", { allowUnknownModelFlag: false });

  const before = loadRoles();

  if (!allRoles && !flags.allAgents) {
    const result = unsetRoleModel(before, {
      agent: flags.agent,
      role: roleArg,
      ...(flags.profile !== undefined ? { profile: flags.profile } : {}),
    });
    saveRoles(result.data);
    const canon = canonicalAgentId(flags.agent);
    const profileName = (flags.profile ?? "").trim() || before.activeProfile;
    if (result.removed) {
      log(`model: unset ${canon}/${roleArg} (profile "${profileName}") — binding removed.`);
    } else {
      log(`model: ${canon}/${roleArg} had no binding in profile "${profileName}" — nothing to remove.`);
    }
    log(`  wrote ${rolesPath()}`);
    await refreshDerivedArtifactsAfterRolesWrite("model unset");
    log(`next: petbox-wire apply`);
    return;
  }

  const sel = {
    profiles: flags.allProfiles ? ("all" as const) : [flags.profile?.trim() || before.activeProfile],
    agents: flags.allAgents ? ("all" as const) : [flags.agent],
    roles: allRoles ? ("all" as const) : [roleArg as string],
  };
  const result = unsetRoleModelSlice(before, sel);
  saveRoles(result.data);
  const removed = result.changes.filter((c) => c.removed);
  log(`model unset (slice): ${removed.length} of ${result.changes.length} cell(s) had a binding removed:`);
  for (const c of removed) log(`  ${cellLabel(c.cell)}: removed`);
  log(`  wrote ${rolesPath()}`);
  await refreshDerivedArtifactsAfterRolesWrite("model unset");
  log(`next: petbox-wire apply`);
}

// model reset <role|--all-roles> [--agent <id>|--all-agents] [--profile <name>|--all-profiles]
//
// Explicit "give this cell back to the kit" (task role-model-bindings-review-refactor, stage D):
// unlike seedMissingRoleBindings (which only ever refreshes a cell ALREADY labelled "kit"), reset
// overwrites unconditionally — including an "owner" binding — and stamps origin "kit", so the
// next default change reaches this cell automatically again. A cell with no kit default at all
// (opencode; a role name the kit's seed does not know) is reported, never invented.
async function runModelReset(argv: string[]): Promise<void> {
  const roleArg = argv[2];
  const allRoles = roleArg === "--all-roles";
  if (!allRoles && (!roleArg || roleArg.startsWith("-"))) {
    console.error("model reset: requires a non-empty <role>, or --all-roles");
    usage();
  }
  const flags = parseModelSliceFlags(argv, 3, "model reset", { allowUnknownModelFlag: false });

  const before = loadRoles();

  if (!allRoles && !flags.allAgents) {
    const result = resetRoleModelToKitDefault(before, {
      agent: flags.agent,
      role: roleArg,
      ...(flags.profile !== undefined ? { profile: flags.profile } : {}),
    });
    const canon = canonicalAgentId(flags.agent);
    const profileName = (flags.profile ?? "").trim() || before.activeProfile;
    if (!result.ok) {
      console.error(`model reset: REFUSED — ${result.reason}`);
      process.exit(WIRE_EXIT.truthfulness);
    }
    saveRoles(result.data);
    log(
      `model: reset ${canon}/${roleArg} (profile "${profileName}") — ` +
        `${result.modelBefore ?? "(unbound)"} -> ${result.modelAfter} [origin: kit]`,
    );
    log(`  wrote ${rolesPath()}`);
    await refreshDerivedArtifactsAfterRolesWrite("model reset");
    log(`next: petbox-wire apply`);
    return;
  }

  const sel = {
    profiles: flags.allProfiles ? ("all" as const) : [flags.profile?.trim() || before.activeProfile],
    agents: flags.allAgents ? ("all" as const) : [flags.agent],
    roles: allRoles ? ("all" as const) : [roleArg as string],
  };
  const result = resetRoleModelSlice(before, sel);
  saveRoles(result.data);
  const applied = result.changes.filter((c): c is Extract<(typeof result.changes)[number], { ok: true }> => c.ok);
  const skipped = result.changes.filter((c) => !c.ok);
  log(`model reset (slice): ${applied.length} of ${result.changes.length} cell(s) reset to the kit default [origin: kit]:`);
  for (const c of applied) log(`  ${cellLabel(c.cell)}: ${c.modelBefore ?? "(unbound)"} -> ${c.modelAfter}`);
  if (skipped.length > 0) {
    log(`model reset (slice): ${skipped.length} cell(s) skipped — no kit default to reset to:`);
    for (const c of skipped) if (!c.ok) log(`  ${cellLabel(c.cell)}: ${c.reason}`);
  }
  log(`  wrote ${rolesPath()}`);
  await refreshDerivedArtifactsAfterRolesWrite("model reset");
  log(`next: petbox-wire apply`);
}

// `layers` — diagnostic-only subcommand answering the two questions manual `find` + hashing used
// to answer by hand (card role-definition-cascade-revisit, requirement 1, never covered by the
// accepted idea's spec_plan): which definition LAYERS exist on this machine, where they
// physically live, and — by FIELD, not "the files differ" — what they disagree about. Read-only:
// never writes, never gates apply's own exit code.
//
// Built entirely on layer-cascade.ts's resolveDefinitionLayers — this file does not re-derive a
// second comparator. With NO arguments it now checks EXACTLY what apply/doctor/status resolve:
// definition-source.ts's canonical layer list (`~/.petbox/agents` for user,
// `<project root>/.petbox/agents` for project) laid over the kit's shipped base
// (default-agents.json). That used to be this command's own documented guess, printed with a
// disclaimer that the base "is not yet a layer directory (client-side merge, P5, has not
// landed)" — both the guess and the disclaimer are gone, because the merge landed (card
// wire-stops-fetching-definition) and there is now one list, in one module, that everything uses.
//
// Passing explicit directories keeps the OTHER mode: your list, literally, lowest priority
// first, with NO base underneath it. That is what makes the command usable as a bench for an
// arbitrary layout (a scratch pair of directories, a candidate layer before it is installed)
// rather than only for the machine's real one.
//
// The trap this must not repeat (observation doctor-drift-check-silent-skip-unregistered-dir):
// "no divergence" and "could not check" must never look the same. Every early return below prints
// to stderr and uses a DIFFERENT exit code (LAYERS_EXIT.cannotCheck) than both the clean path
// (LAYERS_EXIT.ok) and the found-a-problem path (LAYERS_EXIT.cascadeError) — a script branching on
// exit code, not just prose, cannot confuse the three.
//
// Exit codes (own small taxonomy, not WIRE_EXIT's — this command never touches apply's roster):
//   0  clean       — the cascade resolved with zero cascade ERRORs (E0-E5/E1; warnings do not
//                    change the exit code — a W3 replica-layer nudge is not a hard problem).
//                    Includes the ordinary fresh-machine case: the kit base alone IS a resolvable
//                    cascade, and printing its provenance is a real answer, not a non-answer.
//   1  diverged    — cascade resolved but reported at least one ERROR (dangling target, orphan
//                    tombstone, incomplete new role, replace+append conflict, bad filename/mode)
//   2  usage       — bad arguments
//   3  cannotCheck — there is genuinely nothing to show: a present layer's source is broken and
//                    could not be read (LayerSourceError), or explicit-directory mode was given
//                    fewer than two present directories (no base is implied there, so one
//                    directory has nothing to be laid over) — NEVER folded into 0 or 1
const LAYERS_EXIT = { ok: 0, cascadeError: 1, usage: 2, cannotCheck: 3 } as const;

type LayerCandidate = { readonly label: string; readonly dir: string };

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
    ? definitionLayerCandidates(resolveApplyRoot(process.cwd()).root).map((c) => ({
        label: c.label,
        dir: c.dir,
      }))
    : explicitDirs.map((d, i) => ({ label: `arg${i + 1}:${basename(d)}`, dir: d }));

  log(
    `layers: checking ${candidates.length} candidate layer location(s), lowest priority first ` +
      `(${usingDefaults ? "the kit's canonical layer locations" : "explicit directories from argv"}):`,
  );
  if (usingDefaults) {
    log(
      `  base                 PRESENT   ${DEFAULT_AGENT_DEFINITION_PATH} (kit v${KIT_VERSION}) — ` +
        `the shipped floor, always in play; a flat document, not a directory, so its absence is a ` +
        `broken install rather than "no opinion".`,
    );
  }

  const present: LayerCandidate[] = [];
  for (const c of candidates) {
    // isLayerDirectory, not existsSync: a directory that exists but DECLARES nothing (empty, or
    // holding only `.DS_Store`/`Thumbs.db`/a README) has no opinion, and listing it as PRESENT
    // here would then contradict what apply/doctor actually resolve.
    const exists = isLayerDirectory(c.dir);
    log(`  ${c.label.padEnd(20)} ${exists ? "PRESENT  " : "absent   "} ${c.dir}`);
    if (exists) present.push(c);
  }

  // cannotCheck now means EXACTLY "there is nothing to show", and on the default path that can no
  // longer happen: the kit base is a layer, it is always there, and a full cascade — trace,
  // per-field provenance, diagnostics — is a real answer even when it has exactly one layer in
  // it. The old rule ("fewer than two ⇒ CANNOT CHECK") was written while the base was still a
  // footnote rather than a layer, and it aged into a lie: on a fresh machine — which is EVERY
  // consumer's state at publication — `layers` exited 3 while `doctor`, resolving the same
  // cascade, printed the whole table. The skill sends agents here first; it must answer.
  //
  // Explicit-directory mode keeps the two-directory minimum, because there is no base under it:
  // one directory alone genuinely has nothing to be laid over.
  if (!usingDefaults && present.length < 2) {
    console.error(
      present.length === 0
        ? "layers: CANNOT CHECK — no layer directory exists at any candidate location above. " +
            'This is NOT "no divergence": nothing was read, nothing was compared.'
        : `layers: CANNOT CHECK — only one layer is present (${present[0]!.label} at ` +
            `${present[0]!.dir}) and no base is implied in explicit-directory mode. Nothing to ` +
            'lay it over. This is NOT "no divergence": divergence needs at least two layers.',
    );
    exitWith(LAYERS_EXIT.cannotCheck);
    return;
  }

  let resolution: CascadeResolution;
  const resolveOptions: ResolveLayersOptions = usingDefaults ? { base: baseLayer() } : {};
  try {
    resolution = resolveDefinitionLayers(
      present.map((c) => c.dir),
      resolveOptions,
    );
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
      `layers: DIVERGED — ${errors.length} cascade ERROR(s) found across ` +
        `${resolution.layers.length} layer(s); see diagnostics above.`,
    );
    exitWith(LAYERS_EXIT.cascadeError);
    return;
  }
  log(
    resolution.layers.length === 1
      ? `layers: clean — one layer in play (${resolution.layers[0]!.name}), zero cascade errors. ` +
          `Nothing overrides it; this IS the "no divergence problem" answer, reached by actually ` +
          `resolving. Create ${candidates.map((c) => c.dir).join(" or ")} to add one.`
      : `layers: clean — ${resolution.layers.length} layer(s) compared, zero cascade errors ` +
          `(this IS the "no divergence problem" answer, reached by actually checking).`,
  );
  exitWith(LAYERS_EXIT.ok);
}

// ---- small helpers ---------------------------------------------------------

const log = (msg: string) => console.log(msg);

// deriveEnvVar / resolveWorkspace live in wire-identity.ts (importable by unit tests; wire.ts
// itself runs main() on import and cannot be imported).

// Cross-platform key store (~/.petbox/keys.json): a flat JSON map { "<ENV_VAR>": "<key-or-$VAR-reference>" }.
// The kit's own hooks read it (via registry.ts) with no env var required. The per-project MCP
// configs still reference ${ENV_VAR}, so persistKeyForAgents() additionally materializes a real
// environment variable per platform.
function keysStorePath(): string {
  return petboxKeysJsonPath();
}

// Read a key from the store, resolving a $VAR/${VAR} reference the same way registry.ts's
// readKeyStore does (that module is the single source of truth for the format — this just
// reuses inspectKeyStoreEntry rather than re-implementing the parse). Returns "" only when the
// entry is genuinely absent or a literal empty string. Throws UnresolvedEnvRefError — same as
// registry.ts — when the entry is a reference this process's environment cannot satisfy;
// callers on the CLI's bootstrap path are expected to catch it and fail with a clear message
// before doing anything else (never silently fall through to "no key found").
function readKeyFromStore(name: string, project = "(unknown project)"): string {
  const entry = inspectKeyStoreEntry(name);
  if (entry.kind === "absent") return "";
  if (entry.kind === "literal") return entry.value;
  if (entry.resolved === null) throw new UnresolvedEnvRefError(name, entry.refVar, project);
  return entry.resolved;
}

// Merge (never clobber) a key into the store. On POSIX tighten the file to 0600 (best-effort;
// skipped on Windows, where chmod is a no-op / can throw). `value` is written AS GIVEN — literal
// or a $VAR/${VAR} reference; callers decide which (see step 4 of main(), which writes a
// reference whenever the key it just resolved came from a live env var — card decision: bootstrap
// writes references itself, not only as a manual option).
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

// Auto-sync ~/.petbox/keys.json from the environment (card keys-json-doctor-drift-check): before
// this, the file was written ONLY by a full `wire` run (writeKeyToStore above), so a plain env-var
// rotation left it stale until the next full wire — sometimes indefinitely. Runs on `apply` and
// `doctor` too now, not only full `wire`. Copies env → file ONLY (matches registry.ts's env-first
// precedence, registry.ts:152); never touches an envVar unset in this process, so it can never
// clobber a good file value with nothing. Never logs or returns key material — only the envVar
// names it touched, for the caller to name in its own log line.
function syncKeysStoreFromEnv(): string[] {
  const drifts = detectKeysStoreDrift();
  if (drifts.length === 0) return [];
  const path = keysStorePath();
  const store = readJson(path) ?? {};
  for (const d of drifts) {
    const v = process.env[d.envVar];
    if (v) store[d.envVar] = v;
  }
  writeJson(path, store);
  if (process.platform !== "win32") {
    try {
      chmodSync(path, 0o600);
    } catch {
      /* best-effort */
    }
  }
  return drifts.map((d) => d.envVar);
}

// The agent MCP configs (.mcp.json `${VAR}`, opencode `{env:VAR}`, droid `${VAR}`) resolve the
// key from a REAL environment variable — keys.json alone only covers the kit hooks. Persist it:
//  - Windows: user-scope env via PowerShell (visible to NEW terminals);
//  - POSIX: regenerate ~/.petbox/env.sh from the whole key store and make sure the login
//    profiles source it (marker-guarded, idempotent).
function persistKeyForAgents(envVar: string, project = "(unknown project)"): void {
  if (SANDBOX_BASE_URL !== undefined) {
    // Loopback sandbox: this is the only step on the full-wire path that writes MACHINE-GLOBAL
    // state (HKCU Environment via PowerShell). Everything else lands under HOME, which the suite
    // already redirects to a temp dir. Skipping it keeps a test run from persisting a junk
    // PETBOX_*_API_KEY into the developer's own user environment.
    log(`[4/10] loopback sandbox — SKIPPED user-scope env persistence for ${envVar}.`);
    return;
  }
  if (process.platform === "win32") {
    // readKeyFromStore resolves a reference the same as everywhere else. This is called
    // immediately after step 4 just wrote the store (possibly as a reference — see there), so
    // the env var it points at is, by construction, the one this very process just observed
    // set; an UnresolvedEnvRefError here would mean something changed the environment out from
    // under this run mid-flight — genuinely exceptional, so it gets the same clean abort as
    // step 2 rather than a bare stack trace.
    let value: string;
    try {
      value = readKeyFromStore(envVar, project);
    } catch (e) {
      if (e instanceof UnresolvedEnvRefError) abortRun(WIRE_EXIT.usage, `[4/10] ${e.message}`);
      throw e;
    }
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
  // top-level main()) so it stays importable by tests, unlike wire.ts itself. Same clean-abort
  // treatment as the Windows branch above: an unresolved reference here is exceptional (see
  // persistKeyForAgentsPosix's comment) and gets a one-line message, not a bare stack trace.
  let envShPath: string;
  try {
    envShPath = persistKeyForAgentsPosix(homedir(), project);
  } catch (e) {
    if (e instanceof UnresolvedEnvRefError) abortRun(WIRE_EXIT.usage, `[4/10] ${e.message}`);
    throw e;
  }
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

// Raw-text counterparts to readJson/writeJson, for codex's config.toml (codex-toml.ts's
// section-preserving merge operates on the whole file's text, not a parsed object).
function readText(path: string): string {
  try {
    return readFileSync(path, "utf8");
  } catch {
    return "";
  }
}

function writeText(path: string, text: string): void {
  mkdirSync(dirname(path), { recursive: true });
  writeFileSync(path, text, "utf8");
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

// Delivery stamp for KIT_VERSION's hook-context fallback (agent-definition.ts's loadKitVersion,
// card kit-version-unknown-inside-hooks) — the FALLBACK sibling stamp (that doc comment's step
// 3), for the one case its same-directory stamp (step 2, written by bin/petbox-wire.js into the
// npx scratch dir) cannot cover: a checkout-sourced `update`, whose HERE never carries a
// same-directory kit-version.json at all (only bin.js writes one, and a checkout is not run
// through bin.js). Written NEXT TO the mirror (~/.petbox/kit-version.json, a SIBLING of STABLE),
// deliberately NOT inside it: pruneStaleMirrorEntries treats STABLE as an EXACT mirror of HERE
// and deletes anything HERE does not also ship, so a stamp living inside ~/.petbox/wire/ would be
// wiped and rewritten every single run, spamming the "orphan cleanup" log for a file that was
// never an orphan. Outside the mirror this problem does not exist at all.
//
// `version` is KIT_VERSION as already resolved in THIS run's context (HERE) — correct whether
// that resolution came from a checkout's real `../package.json`, or (for a real `npx` run) from
// the same-directory stamp bin.js already dropped into HERE before wire.ts was even imported —
// so this never needs a second resolver of its own. `kitHash` is the exact `after` fingerprint
// copyKitToStable already computes and logs — never a second hash.
//
// Best-effort: a failure to write the stamp must never fail the copy it rides along on. The
// hook-context fallback then simply stays "unknown", the same soft degradation as before this
// stamp existed.
function writeKitVersionStamp(kitHash: string, label: string): void {
  try {
    writeJson(join(petboxDir(), "kit-version.json"), {
      version: KIT_VERSION,
      kitHash,
      installedAt: new Date().toISOString(),
      source: HERE,
    });
  } catch (err) {
    log(
      `${label} stable copy: could not write kit-version stamp at ${join(petboxDir(), "kit-version.json")} ` +
        `(non-fatal, hook-context KIT_VERSION stays "unknown"): ${err instanceof Error ? err.message : String(err)}`,
    );
  }
}

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
  writeKitVersionStamp(after, label);
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
  const data = readJson(registryPath());
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
  const path = registryPath();
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
// the same inputs yields byte-identical output. Only the `name` entry is (re)generated.
//
// `serversKey` defaults to "mcpServers" (claude-code's .mcp.json, droid's .factory/mcp.json,
// qwen's .qwen/settings.json all use that key); opencode's own config nests servers under `mcp`
// instead, so callers pass `serversKey: "mcp"` there. `defaults` seeds top-level keys ONLY when
// they are still absent (opencode's `$schema`) — never overwrites a value the project already
// set (bug wire-mcp-wipes-foreign-servers: opencode's `theme` and any other top-level setting
// must survive untouched).
//
// Name-conflict warning (same bug, second half): if `name` was already claimed by SOMETHING ELSE
// — a foreign server of that name, or petbox's own entry from a prior run with different content
// (an env-var/URL rotation) — say so before overwriting it. A byte-identical re-run never prints
// this (existing === server), which is what keeps a plain idempotent re-wire quiet.
function mergeMcpServer(
  path: string,
  name: string,
  server: unknown,
  opts?: { readonly serversKey?: string; readonly defaults?: Readonly<Record<string, unknown>> },
): void {
  const serversKey = opts?.serversKey ?? "mcpServers";
  const data = readJson(path) ?? {};
  if (opts?.defaults) {
    for (const [k, v] of Object.entries(opts.defaults)) {
      if (!(k in data)) data[k] = v;
    }
  }
  if (!data[serversKey] || typeof data[serversKey] !== "object") data[serversKey] = {};
  const existing = data[serversKey][name];
  if (existing !== undefined && JSON.stringify(existing) !== JSON.stringify(server)) {
    console.error(
      `[7/10] WARNING: ${path} already had a "${name}" MCP server entry with different ` +
        `content — overwriting it with petbox's own config now. If that entry belonged to ` +
        `someone else's server, rename it before the next wire so this name is not claimed again.`,
    );
  }
  data[serversKey][name] = server;
  writeJson(path, data);
}

/**
 * A ledger of its own for a caller that has none — full `wire`'s step 7, which writes skills once
 * outside any apply pass. Same lines, same counting rule; the only difference is that nobody else
 * folds these actions into an exit code (step 11's apply re-runs the same writes right after).
 */
function reportSkillOutcomesStandalone(label: string, result: SkillWriteResult): boolean {
  const ledger = createLedger();
  return reportSkillOutcomes((action) => {
    ledger.record(action);
    const rendered = formatAction(label, action, false);
    if (rendered.stderr) console.error(rendered.text);
    else log(rendered.text);
  }, result);
}

/**
 * Fold one writeSkillFiles result into the caller's ledger. Returns true when at least one path
 * was REFUSED (the caller's clobber flag / exit 1). It no longer prints anything itself and no
 * longer returns the blocked paths: every line and every count now comes from the ledger, which
 * is what makes the `--dry-run` summary provably the summary of the run that would execute
 * (observation apply-all-summary-undercounts-writes — skill writes used to be invisible to the
 * counter entirely, so `one-c` with 12 real writes printed as "unchanged").
 */
function reportSkillOutcomes(emit: (action: ApplyAction) => void, result: SkillWriteResult): boolean {
  let refused = false;
  for (const outcome of result.writes) {
    if (outcome.kind === "blocked") {
      refused = true;
      emit({ kind: "refuse", subject: "skill", path: outcome.path });
    } else if (outcome.kind === "declared-manual") {
      // NOT a refusal and NOT an error (spec: wire-skill-manual-declared-not-error): the project
      // declared this path its own, the kit honoured that. Never a refusal, so it can never
      // reach the exit code.
      emit({ kind: "manual", subject: "skill", path: outcome.path });
    } else if (outcome.reason === "unchanged") {
      emit({ kind: "unchanged", subject: "skill", path: outcome.path });
    } else {
      emit({
        kind: "write",
        subject: "skill",
        path: outcome.path,
        ...(outcome.reason === "adopted"
          ? { note: "ADOPTED — unmarked file overwritten because --adopt named this exact path" }
          : outcome.reason !== "new"
            ? { note: outcome.reason }
            : {}),
      });
    }
  }
  // Pre-rename sweep (bug: wire-skill-cleanup-on-replace) — same rules as the agent-role rename
  // cleanup above: an owned leftover is removed, anything not ours is named and kept. Never a
  // refusal: keeping a file we may not delete is the correct outcome, not a failure of the run.
  for (const cleanup of result.cleanups) {
    if (cleanup.outcome === "removed") {
      emit({
        kind: "remove",
        subject: "skill",
        path: cleanup.path,
        note: `legacy name, ours, superseded${cleanup.removedDir ? " — and its now-empty directory" : ""}`,
      });
    } else if (cleanup.outcome === "kept-foreign") {
      emit({ kind: "kept", subject: "skill", path: cleanup.path });
    }
  }
  return refused;
}

// The project-scope `<project>/.qwen/settings.json` write, in ONE place because it now has TWO
// callers with two different reasons (card wire-qwen-project-settings-mcp-and-skills):
//
//   - writeProjectFiles, below — the full `wire` path, which has always written this file.
//   - performApply — `apply` never touched it, which is the bug the card was filed for: a project
//     wired by an older kit and kept current with `apply` alone never grew a `.qwen` directory,
//     so qwen fell back to `.mcp.json` (no env-var resolution) and reported zero tools.
//
// Warnings, not refusals, on both anomalies: a name conflict is the same "petbox wins the name,
// out loud" contract mergeMcpServer carries for the other four MCP sites (commit 1655231a), and a
// malformed `skills` key costs the skills pointer but must not cost the MCP server too.
function wireQwenProjectSettings(
  dir: string,
  envVar: string,
  baseUrl: string,
  label: string,
  opts?: { readonly dryRun?: boolean },
): { readonly path: string; readonly outcome: QwenProjectSettingsOutcome } {
  const settingsPath = join(dir, ".qwen", "settings.json");
  const outcome = mergeQwenProjectSettings({
    settingsPath,
    skillsDir: claudeSkillsDir(dir),
    mcpEntry: buildQwenMcpServerEntry(baseUrl, envVar),
    dryRun: opts?.dryRun,
  });
  if (outcome.mcpNameConflict) {
    console.error(
      `${label} WARNING: ${settingsPath} already had a "petbox" MCP server entry with different ` +
        `content — overwriting it with petbox's own config now. If that entry belonged to ` +
        `someone else's server, rename it before the next wire so this name is not claimed again.`,
    );
  }
  if (outcome.skillsWarning) {
    console.error(`${label} WARNING: ${settingsPath} — ${outcome.skillsWarning}`);
  }
  return { path: settingsPath, outcome };
}

function writeProjectFiles(dir: string, project: string, envVar: string, workspace: string): void {
  // .mcp.json (Claude Code) — a project-level file a person may hand-edit to add their own MCP
  // servers, so merge (never clobber) rather than regenerate whole, same primitive as droid's
  // .factory/mcp.json below (bug wire-mcp-wipes-foreign-servers: this used to be a whole-file
  // writeJson that silently destroyed every foreign server and any other top-level key on each
  // full `wire`).
  const mcpJsonPath = join(dir, ".mcp.json");
  mergeMcpServer(mcpJsonPath, "petbox", {
    type: "http",
    url: `${DEFAULT_BASE_URL}/mcp`,
    headers: { "X-Api-Key": `\${${envVar}}` },
  });
  log(`[7/10] merged petbox MCP server into ${mcpJsonPath}`);

  // .opencode/opencode.json (opencode) — same fix, same primitive, but opencode nests its server
  // map under `mcp` (not `mcpServers`) and the file commonly carries unrelated top-level settings
  // (e.g. `theme`) that must survive untouched. `$schema` is seeded only when the project hasn't
  // already set one of its own.
  const opencodeJsonPath = join(dir, ".opencode", "opencode.json");
  mergeMcpServer(
    opencodeJsonPath,
    "petbox",
    {
      type: "remote",
      url: `${DEFAULT_BASE_URL}/mcp`,
      enabled: true,
      headers: { "X-Api-Key": `{env:${envVar}}` },
    },
    { serversKey: "mcp", defaults: { $schema: "https://opencode.ai/config.json" } },
  );
  log(`[7/10] merged petbox MCP server into ${opencodeJsonPath}`);

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

  // .codex/config.toml (Codex CLI) — ONLY `[mcp_servers.petbox]`. `model_providers` /
  // `model_provider` are on codex's PROJECT-scope denylist (codex-spec.md §1) and must live in
  // USER scope instead (see installGlobalHooks below); `mcp_servers` is not denylisted, so this
  // is the one codex config key legitimately written per-project. Section-preserving merge
  // (codex-toml.ts), never a whole-file regenerate — a project's own config.toml (sandbox
  // policy, `instructions`, etc.) must survive untouched.
  // Name-conflict warning (bug wire-mcp-wipes-foreign-servers, second half): upsertBlock replaces
  // a `[mcp_servers.petbox]` section wholesale, silently, whether it was ours from a prior run or
  // someone else's block that happens to carry this name. Say so before overwriting, same as
  // mergeMcpServer does for the JSON configs above — quiet on a byte-identical idempotent re-run.
  const codexProjectConfigPath = join(dir, ".codex", "config.toml");
  const codexProjectBlocksBefore = parseTomlBlocks(readText(codexProjectConfigPath));
  const codexMcpBodyLines = buildMcpServerBlock(DEFAULT_BASE_URL, envVar);
  const existingCodexMcpBlock = findBlock(codexProjectBlocksBefore, "mcp_servers.petbox");
  // Trailing blank lines are a round-trip artifact when this is the FILE'S LAST block (the final
  // "\n" splits into a trailing "" element) — serializeTomlBlocks itself discards them on write,
  // so they must not count as a content difference here either, or every idempotent re-run would
  // misreport itself as a name conflict.
  const trimTrailingBlankLines = (lines: readonly string[]): string[] => {
    const out = [...lines];
    while (out.length > 0 && out[out.length - 1] === "") out.pop();
    return out;
  };
  if (
    existingCodexMcpBlock !== undefined &&
    trimTrailingBlankLines(existingCodexMcpBlock.lines).join("\n") !== codexMcpBodyLines.join("\n")
  ) {
    console.error(
      `[7/10] WARNING: ${codexProjectConfigPath} already had a [mcp_servers.petbox] section ` +
        `with different content — overwriting it with petbox's own config now. If that section ` +
        `belonged to someone else's server, rename it before the next wire so this name is not ` +
        `claimed again.`,
    );
  }
  const codexProjectBlocks = upsertBlock(codexProjectBlocksBefore, "mcp_servers.petbox", codexMcpBodyLines);
  writeText(codexProjectConfigPath, serializeTomlBlocks(codexProjectBlocks));
  log(`[7/10] merged petbox MCP server into ${codexProjectConfigPath}`);

  // .qwen/settings.json mcpServers.petbox (Qwen Code) — WORKSPACE scope, not project .mcp.json.
  // This is now the ONLY petbox MCP entry the kit writes for qwen: installGlobalHooks no longer
  // writes a user-scope one (owner decision 09.09.2026 — see its header). Defect
  // qwen-mcp-json-shadows-workspace-entry, live smoke wire-support-codex-qwen; verified against
  // the qwen-code source, packages/cli/src/config:
  //   - `.mcp.json` (written above, for claude-code) has NO env-var resolution
  //     (mcpJson.ts's loadProjectMcpServers never calls resolveEnvVarsInObject) — its `petbox`
  //     entry, read by qwen too (assembleMcpServers reads every settings source, .mcp.json
  //     included), would send the header literally as `${envVar}` text, and PetBox 401s.
  //   - `.mcp.json` also OUTRANKS anything at USER scope by name (mcpServers.ts:27-55's
  //     precedence: user/default < project .mcp.json < workspace/system < --mcp-config) — so back
  //     when a user-scope entry existed it never even got a chance to run inside a wired root; the
  //     broken .mcp.json one won and the model got zero mcp__petbox__* tools ("MCP server(s)
  //     failed to start"). Workspace scope is what actually clears it.
  //   - A `.qwen/settings.json` (`SettingScope.Workspace`) entry OUTRANKS `.mcp.json`
  //     (mcpServers.ts, same precedence list) AND is env-var-resolved (settings.ts's
  //     workspaceSettings = resolveEnvVarsInObject(...)) — this is the one file that both wins
  //     the precedence fight and actually expands `${envVar}`.
  //   - Workspace scope IS held behind qwen's pending-approval gate for an interactive run
  //     (mcp-server-config.ts's isGatedMcpScope: 'project' | 'workspace' both gated) — this kit
  //     does NOT pre-approve it (an approval hash is computed over the RESOLVED config, i.e. the
  //     literal key, so a later key rotation would silently invalidate a baked-in approval and
  //     the server would go back to being silently dropped). Headless callers already pass
  //     `--approval-mode yolo` (bypasses the gate outright); an interactive user gets a one-time
  //     approval prompt on first launch — see finishWireRun's closing NOTE (self-smoke.ts).
  // Only `mcpServers` belongs in this file: `modelProviders` has `mergeStrategy: REPLACE` at
  // USER scope (installGlobalHooks), so a partial copy here would silently wipe that map — hooks/
  // security.auth/agents.modelGrades stay user-scope-only too, unchanged by this block. Merge via
  // mergeMcpServer (same primitive as droid's `.factory/mcp.json` above): touches ONLY
  // mcpServers.petbox, never a project's own pre-existing `.qwen/settings.json` content.
  // `alwaysLoadTools: true`: qwen defers
  // MCP tools to `tool_search` by default UNLESS the active model's id matches
  // `/deepseek-(v3|v4|chat)/i` (config.ts) — see qwen-mcp-entry.ts's own header for why this is
  // set unconditionally regardless of which model a role currently binds.
  //
  // `skills.directories` rides in the SAME file and the same merge (card
  // wire-qwen-project-settings-mcp-and-skills): qwen reads foreign skill roots from that key, so
  // it gets an ABSOLUTE pointer at this project's own `.claude/skills` instead of a third copy of
  // every skill body. Absolute because qwen resolves a relative entry against the RUNTIME's cwd,
  // and PROJECT-scope because entries from that key always load at qwen's `user` LEVEL — putting
  // it in `~/.qwen/settings.json` would spill one project's skills into every project on the
  // machine. Both traps are measured; see qwen-project-settings.ts's header.
  const qwenReport = wireQwenProjectSettings(dir, envVar, DEFAULT_BASE_URL, "[7/10]");
  log(
    `[7/10] merged petbox MCP server + skills.directories into ${qwenReport.path} ` +
      `(workspace scope, ${qwenReport.outcome.reason})`,
  );

  // Skill bodies: `petbox` (project-scoped), `petbox-agent-factory` (on-demand, no
  // placeholders), `petbox-methodology` (thin, project-agnostic pointer at the LIVE
  // methodology this project runs — never this repo's own rules; see skill-files.ts),
  // `petbox-write-economy` (bodyRef/fragment write-cost mechanisms) and `petbox-node-authoring`
  // (node/comment BODY structure). Rendered once per skill (see PROJECT_SKILLS in
  // skill-files.ts — the one place a new skill is registered), then dropped into every native
  // skill surface (writeSkillFiles / skill-files.ts).
  reportSkillOutcomesStandalone("[7/10]", writeSkillFiles(dir, join(HERE, "templates"), project, workspace));
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
  'codex-push-session.ts"',
  'codex-pull-memory.ts"',
  'codex-subagent-model-gate.ts"',
  'qwen-push-session.ts"',
  'qwen-pull-memory.ts"',
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
//
// NO MCP SERVER ENTRY IS WRITTEN AT USER SCOPE — OWNER DECISION 09.09.2026, verbatim «Нет —
// убрать глобальную запись», answering "does qwen launched OUTSIDE any project need PetBox
// tools?". This function used to write `mcpServers.petbox` into $QWEN_HOME/settings.json with
// THIS run's project env-var name, and that write took a `envVar: string` parameter which is why
// the parameter is gone now. Why it was removed, concretely: the entry is machine-global while
// the env var it names is per-project, so it could only ever point at ONE project at a time and
// every subsequent `wire` run silently repointed it. Measured on the owner's own box: the file
// held `${PETBOX_SMOKE_API_KEY}` because the last `wire` had run from a throwaway smoke
// directory, that variable was set and VALID (`whoami` → project "smoke", scopes including
// tasks:write/memory:write/data:write), so a qwen session started outside any wired root was
// quietly reading and WRITING another project's boards and memory. Nothing warned.
// ACCEPTED COST, stated so nobody "fixes" it back: qwen launched outside a wired directory now
// has no PetBox boards and no memory at all. That is the decision, not an oversight.
// Inside a wired project nothing changes: writeProjectFiles's WORKSPACE-scope
// `.qwen/settings.json` entry is the one that has always actually governed (defect
// qwen-mcp-json-shadows-workspace-entry, live smoke wire-support-codex-qwen — see that
// function's own comment): it outranks the project's `.mcp.json` (whose petbox entry is never
// env-var resolved and would silently 401), and it resolves each project's OWN env var.
// Regression cover: wire-qwen-user-scope-no-mcp.test.ts (a real `wire` run against a fresh
// $HOME must leave $QWEN_HOME/settings.json with no `mcpServers` key at all).
// See doc/agent-wiring.md.
function installGlobalHooks(dir: string): void {
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

  // ---- Codex CLI: hooks.json + config.toml (providers, hook trust, model catalog) -----------
  //
  // codex-spec.md §3: an UNTRUSTED hook is skipped SILENTLY in headless codex runs — no error,
  // no warning. Route (a): the kit computes each hook's `trusted_hash` (codex-hook-trust.ts,
  // source-anchored) and writes it into `[hooks.state."<key>"]` in $CODEX_HOME/config.toml
  // ALONGSIDE the hooks.json entry it describes, in the SAME run, so a hook is never live
  // without also being pre-trusted.
  //
  // Providers/model_provider/model/model_catalog_json are USER-scope ONLY (codex-spec.md §1:
  // `model_provider`/`model_providers` are on codex's PROJECT-scope denylist) — this is the
  // only place they are written; writeProjectFiles's `.codex/config.toml` carries just
  // `[mcp_servers.petbox]`.
  const codexHome = codexHomeDir();
  const codexHooksJsonPath = join(codexHome, "hooks.json");
  const codexConfigPath = join(codexHome, "config.toml");
  // codexCatalogPath is no longer needed here — printCodexRosterFragment (below) computes its
  // own copy, since the fragment print was extracted out of this function.

  const codexPullCmd = `node "${join(STABLE, "codex-pull-memory.ts")}"`;
  const codexPushCmd = `node "${join(STABLE, "codex-push-session.ts")}"`;
  const codexGateCmd = `node "${join(STABLE, "codex-subagent-model-gate.ts")}"`;

  // hooks.json: the SAME `{event: [{matcher?, hooks:[{...}]}]}` shape Claude Code/Droid
  // settings.json hooks use (codex-spec.md §3's validated shape), so pruneStaleKitHooks is
  // reused unchanged; only the handler object's own fields differ (camelCase timeout/
  // commandWindows). `command` AND `commandWindows` are always written IDENTICAL: codex uses
  // `commandWindows` INSTEAD of `command` on Windows (discovery.rs:513-517) for BOTH execution
  // and the trust hash's normalized `command` field (codex-hook-trust.ts's header) — writing the
  // same value to both means "which one wins" never matters, on any platform.
  const codexHooksDoc = readJson(codexHooksJsonPath) ?? {};
  if (typeof codexHooksDoc.description !== "string") codexHooksDoc.description = "petbox-wire";
  if (!codexHooksDoc.hooks || typeof codexHooksDoc.hooks !== "object") codexHooksDoc.hooks = {};
  const codexValidCmds = new Set([codexPullCmd, codexPushCmd, codexGateCmd]);
  const prunedCodex = pruneStaleKitHooks(codexHooksDoc.hooks, codexValidCmds);
  if (prunedCodex > 0) {
    log(`[8/10] pruned ${prunedCodex} stale codex kit hook(s) not pointing at ${STABLE}.`);
  }

  const ensureCodexHook = (
    event: string,
    command: string,
    opts: { matcher?: string; timeoutSec?: number } = {},
  ): { groupIndex: number; handlerIndex: number } => {
    const groups: any[] = Array.isArray(codexHooksDoc.hooks[event]) ? codexHooksDoc.hooks[event] : [];
    for (let gi = 0; gi < groups.length; gi++) {
      const g = groups[gi];
      if (!g || !Array.isArray(g.hooks)) continue;
      for (let hi = 0; hi < g.hooks.length; hi++) {
        if (g.hooks[hi]?.command === command) {
          log(`[8/10] codex hook ${event} already present — skipped.`);
          return { groupIndex: gi, handlerIndex: hi };
        }
      }
    }
    const handler: any = { type: "command", command, commandWindows: command };
    if (opts.timeoutSec !== undefined) handler.timeout = opts.timeoutSec;
    const group: any = { hooks: [handler] };
    if (opts.matcher !== undefined) group.matcher = opts.matcher;
    groups.push(group);
    codexHooksDoc.hooks[event] = groups;
    log(`[8/10] codex hook ${event} added.`);
    return { groupIndex: groups.length - 1, handlerIndex: 0 };
  };

  // Timeouts: SessionStart/PreToolUse use codex-spec.md §3's own validated example values (30,
  // 15) — the general branch of normalize_command_hook has no upper clamp, so these apply
  // as-is. SessionEnd is DIFFERENT from that same spec example: codex clamps SessionEnd (and
  // Interrupt) to a HARD [1,3]-second ceiling regardless of configured value
  // (discovery.rs:742-763) — writing 30 here (as the spec's own example TOML does) would still
  // only ever run for 3s and, worse, would make the pre-computed trust hash WRONG (the hash
  // must hash the CLAMPED value, not the configured one) — see codex-push-session.ts's own
  // header for the operational consequence. So this writes 3, matching what codex actually
  // enforces, not what the spec's illustrative example happened to show.
  const codexStartPos = ensureCodexHook("SessionStart", codexPullCmd, { timeoutSec: 30 });
  const codexEndPos = ensureCodexHook("SessionEnd", codexPushCmd, { timeoutSec: 3 });
  const codexGatePos = ensureCodexHook("PreToolUse", codexGateCmd, {
    matcher: "spawn_agent",
    timeoutSec: 15,
  });

  writeJson(codexHooksJsonPath, codexHooksDoc);
  log(`[8/10] merged codex hooks into ${codexHooksJsonPath}`);

  // config.toml: pre-trusted [hooks.state] entries for the three hooks just written above, plus
  // the wired project's trust entry. NOTHING ELSE.
  //
  // OWNER DECISION 09.09.2026, verbatim «и назад тоже. все унифицировать» (task
  // wire-codex-config-print-fragment): the kit STOPPED writing `model_providers.*`,
  // `model_provider`, `model`, `http_headers` and `model_catalog_json` here, and stopped writing
  // the `petbox-model-catalog.json` those keys point at — the same scope boundary already applied
  // to qwen (see the qwen block below), applied backwards to codex. Two concrete harms went with
  // the old behaviour: a SINGLE static `x-opencode-session` uuid minted per install collapsed
  // every conversation on this machine into one prompt-cache bucket, and `context_window` was the
  // kit's own unverified 128000 against a measured 1048576.
  //
  // What replaced it is NOT silence. Dropping the catalog quietly would have cost every codex
  // role the `apply_patch` tool (9 tools instead of 10, measured) and swapped its window for
  // codex's 272000 fallback — exit 0, no warning anywhere. So the print is LOUD and the
  // divergence check names that consequence: see the block after the trust entries below, and
  // codex-config-fragment.ts's own header.
  let codexBlocks: readonly TomlBlock[] = parseTomlBlocks(readText(codexConfigPath));

  const codexTrustEntries: { key: string; hash: string }[] = [
    {
      key: codexHookStateKey(
        codexHooksJsonPath,
        "SessionStart",
        codexStartPos.groupIndex,
        codexStartPos.handlerIndex,
      ),
      hash: computeCodexHookTrustHash({ event: "SessionStart", command: codexPullCmd, timeoutSec: 30 }),
    },
    {
      key: codexHookStateKey(codexHooksJsonPath, "SessionEnd", codexEndPos.groupIndex, codexEndPos.handlerIndex),
      hash: computeCodexHookTrustHash({ event: "SessionEnd", command: codexPushCmd, timeoutSec: 3 }),
    },
    {
      key: codexHookStateKey(
        codexHooksJsonPath,
        "PreToolUse",
        codexGatePos.groupIndex,
        codexGatePos.handlerIndex,
      ),
      hash: computeCodexHookTrustHash({
        event: "PreToolUse",
        matcher: "spawn_agent",
        command: codexGateCmd,
        timeoutSec: 15,
      }),
    },
  ];
  for (const entry of codexTrustEntries) {
    codexBlocks = upsertBlock(codexBlocks, `hooks.state.${tomlString(entry.key)}`, buildHookStateBlock(entry.hash));
  }

  // Trust the wired project directory (defect codex-mcp-inert-untrusted-project, live smoke
  // wire-support-codex-qwen) — see applyCodexProjectTrust's own header comment (codex-toml.ts)
  // for why this is required at all and what it never touches.
  const codexTrust = applyCodexProjectTrust(codexBlocks, dir);
  codexBlocks = codexTrust.blocks;
  if (codexTrust.outcome === "already-trusted") {
    log(`[8/10] codex project trust: ${dir} already trusted in ${codexConfigPath} — skipped.`);
  } else if (codexTrust.outcome === "left-existing") {
    log(
      `[8/10] codex project trust: ${codexConfigPath} already sets trust_level = ` +
        `${codexTrust.existingValue} for ${dir} — an operator's own choice, left as-is. ` +
        `mcp_servers.petbox for this project stays inert until you trust it yourself ` +
        `(codex's own trust prompt, or set trust_level = "trusted" by hand).`,
    );
  } else {
    log(
      `[8/10] codex project trust: marked ${dir} trusted in ${codexConfigPath} ` +
        `(required for mcp_servers.petbox to load — codex config/src/loader/mod.rs).`,
    );
  }

  writeText(codexConfigPath, serializeTomlBlocks(codexBlocks));
  log(
    `[8/10] merged codex config into ${codexConfigPath} (${codexTrustEntries.length} hook trust ` +
      `entries + project trust only — model_providers/model_provider/model/http_headers/` +
      `model_catalog_json are printed, never written).`,
  );

  // The codex counterpart of the qwen fragment block below: compare the LIVE config.toml and the
  // catalog it points at against the roster, and print the ready-to-paste fragment when they
  // diverge. Extracted to printCodexRosterFragment (task role-model-bindings-review-refactor,
  // remainder E) so every roles.json write path — not only this full `wire` run's step 8 — can
  // show the SAME up-to-date fragment right after it writes, not only on the next full wire.
  printCodexRosterFragment("[8/10]", homedir());

  // ---- Qwen Code: everything in ONE user-scope settings.json ----------------------------------
  //
  // Unlike codex, qwen needs no separate hooks.json and no hook-trust hashing: qwen's own hook
  // schema nests `{matcher?, hooks:[{...}]}` groups directly under `settings.json`'s `hooks` key
  // (qwen-spec.md §3 — the SAME shape Claude Code's settings.json hooks already use, so
  // pruneStaleKitHooks/the ensureHook closure pattern below are reused, not reinvented), and
  // folder trust is DISABLED by default (qwen-spec.md §1: `security.folderTrust.enabled` default
  // false, and when disabled every folder reports trusted) — a user-scope hook is ALSO always
  // honored regardless of trust (qwen-spec.md §1) — so there is no codex-style "untrusted hook is
  // silently skipped" trap to defend against here.
  //
  // mcpServers.petbox is NOT written here (owner decision 09.09.2026 — see this function's own
  // header for the measured cross-project leak that motivated it and the accepted cost). The ONLY
  // petbox MCP entry the kit writes for qwen is writeProjectFiles's WORKSPACE-scope
  // `.qwen/settings.json` one (defect qwen-mcp-json-shadows-workspace-entry — see that entry's own
  // comment): it outranks the project's `.mcp.json` in qwen's own precedence order, and unlike
  // `.mcp.json` it is env-var-resolved. Workspace scope IS gated for an interactive run
  // (mcp-server-config.ts's isGatedMcpScope), same as project scope; this kit deliberately does
  // NOT pre-approve it (an approval hash bakes in the RESOLVED — i.e. literal-key — config, so a
  // later key rotation would silently invalidate it) — headless callers pass
  // `--approval-mode yolo` instead, and an interactive user gets a one-time prompt (finishWireRun
  // in self-smoke.ts).
  //
  // security.auth.selectedType/modelProviders/agents.modelGrades are also written here (qwen-spec.md
  // §7/§11/§12): `modelProviders` has `mergeStrategy: REPLACE`, so the whole map is regenerated
  // each run (same discipline as codex's provider blocks), and `agents.modelGrades` must be
  // seeded or EVERY spawn-time `model` parameter is rejected (qwen-spec.md §6).
  const qwenHome = qwenHomeDir();
  const qwenSettingsPath = join(qwenHome, "settings.json");
  const qwenSettings = readJson(qwenSettingsPath) ?? {};

  const qwenPullCmd = `node "${join(STABLE, "qwen-pull-memory.ts")}"`;
  const qwenPushCmd = `node "${join(STABLE, "qwen-push-session.ts")}"`;
  // Reuses the SAME PreToolUse gate script Claude Code installs (modelGateCmd, above) — qwen's
  // Agent tool params are byte-identical to Claude Code's Task/Agent tool (`subagent_type` +
  // optional `model`, verified live in the installed clone, agent.ts:225/718), so no payload
  // adapter is needed the way codex's `agent_type` field required one (codex-subagent-model-
  // gate.ts). Reusing evaluateModelGate's SAME policy, not a second one, per this task's brief.
  const qwenGateCmd = modelGateCmd;

  if (!qwenSettings.hooks || typeof qwenSettings.hooks !== "object") qwenSettings.hooks = {};
  const qwenValidCmds = new Set([qwenPullCmd, qwenPushCmd, qwenGateCmd]);
  const prunedQwen = pruneStaleKitHooks(qwenSettings.hooks, qwenValidCmds);
  if (prunedQwen > 0) {
    log(`[8/10] pruned ${prunedQwen} stale qwen kit hook(s) not pointing at ${STABLE}.`);
  }

  // Qwen HookDefinition shape (qwen-spec.md §3, types.ts:215-219): {matcher?, hooks:[HookConfig]}
  // — hooks.<Event> is CONCAT-merged across scopes, so appending a group here is safe. `timeout`
  // is MILLISECONDS for qwen (default 60000) — deliberately NOT the codex second-based numbers
  // (this task's brief). No hook here is ever marked `"async": true` — critical for SessionStart
  // specifically (qwen-spec.md §3: the async path returns no `additionalContext` at all before
  // the first model request is built) — so `async` is simply never set on any of these.
  //
  // `shell: "powershell"` on win32 (task qwen-stop-hook-path): measured live against the
  // installed 0.23.0 clone's own getShellConfigForHook (chunk-4F7GQGXB.js:151533-151551) — with
  // no `shell` set, a hook falls through to getShellConfiguration(), which on a plain Windows
  // shell (no MSYSTEM/git-bash markers — the owner's actual interactive terminal) resolves to
  // cmd.exe (argsPrefix ["/d","/s","/c"]). Reproduced the EXACT node spawn(executable,
  // [...argsPrefix, command], {shell:false}) call qwen's hookRunner makes with our real
  // `node "<STABLE path>"` command string: node's own Windows argv auto-quoting (triggered by the
  // command string's embedded space+quotes) double-escapes the inner `"` as `\"`, which cmd.exe's
  // OWN command-line parser does NOT unescape (cmd has no backslash-quote convention) — the
  // literal `\"` survives into the parsed token, landing INSIDE the path passed to node, which
  // then resolves it relative to cwd because the corrupted string no longer starts with a bare
  // drive letter. That is the reported symptom byte-for-byte: `Cannot find module
  // 'C:\"C:\...\qwen-push-session.ts"'`. Forcing `shell: "powershell"` picks
  // getShellConfigForHook's OTHER branch (executable "powershell", argsPrefix
  // ["-Command"]) — powershell.exe's own argv parsing DOES follow the standard
  // CommandLineToArgvW/MSVCRT convention that node's escaping targets, so it correctly recovers
  // the original string and hands node the clean, unquoted path. Measured working end-to-end with
  // the identical spawn call (exit 0, target script's process.argv came back byte-exact, no cwd-
  // relative corruption). Non-Windows is untouched: getShellConfigForHook maps any non-"powershell"
  // `shell` value to bash, and the default (no `shell` key) is already bash's own argsPrefix
  // ["-c"], which does not exhibit this corruption — the mis-escape is a cmd.exe-specific gap in
  // node's Windows quoting, not a general shell one.
  const ensureQwenHook = (event: string, command: string, opts: { matcher?: string; timeoutMs: number }) => {
    const wantShell = process.platform === "win32" ? "powershell" : undefined;
    const groups: any[] = Array.isArray(qwenSettings.hooks[event]) ? qwenSettings.hooks[event] : [];
    // Match on command AND shell, not command alone: an existing group installed before this
    // fix (no `shell` field, quietly broken on win32 — see this closure's header comment) must
    // NOT be treated as "already present" on a re-run, or the owner's already-installed, already-
    // corrupt hook survives every future `wire` forever. Stale-shell groups for this exact
    // command are dropped so the corrected handler below replaces them (this is idempotent
    // upgrade repair, not routine pruning — pruneStaleKitHooks above only drops commands whose
    // PATH no longer matches STABLE, so it never touches this case).
    for (const g of groups) {
      if (!Array.isArray(g?.hooks)) continue;
      g.hooks = g.hooks.filter((h: any) => !(h?.command === command && h?.shell !== wantShell));
    }
    qwenSettings.hooks[event] = groups.filter(
      (g) => !(g && Array.isArray(g.hooks) && g.hooks.length === 0),
    );
    const already = qwenSettings.hooks[event].some(
      (g: any) => Array.isArray(g?.hooks) && g.hooks.some((h: any) => h?.command === command && h?.shell === wantShell),
    );
    if (already) {
      log(`[8/10] qwen hook ${event} already present — skipped.`);
      return;
    }
    const handler: any = { type: "command", command, timeout: opts.timeoutMs };
    if (wantShell !== undefined) handler.shell = wantShell;
    const group: any = { hooks: [handler] };
    if (opts.matcher !== undefined) group.matcher = opts.matcher;
    qwenSettings.hooks[event].push(group);
    log(`[8/10] qwen hook ${event} added.`);
  };

  ensureQwenHook("SessionStart", qwenPullCmd, { timeoutMs: 10000 });
  // Stop AND StopFailure both point at the SAME push script (qwen-push-session.ts's own header):
  // StopFailure fires INSTEAD of Stop when a turn dies on an API error, so both must be hooked
  // for guaranteed coverage. Stop/StopFailure have NO matcher target (qwen-spec.md §3).
  ensureQwenHook("Stop", qwenPushCmd, { timeoutMs: 15000 });
  ensureQwenHook("StopFailure", qwenPushCmd, { timeoutMs: 15000 });
  // Matcher = "agent" (the tool's CANONICAL/internal name, `ToolNames.AGENT`, NOT the "Agent"
  // display name — verified live in the installed clone: coreToolScheduler.ts's `canonicalName`
  // is what `firePreToolUseHook` receives as `toolName`, and `tool_name` on the hook payload is
  // that same canonical name). Same perf rationale as Claude Code's own matcher (wire.ts's
  // MODEL_GATE_MATCHER comment): scopes the process spawn to spawn-tool calls only.
  ensureQwenHook("PreToolUse", qwenGateCmd, { matcher: "agent", timeoutMs: 5000 });

  // mcpServers.petbox — DELIBERATELY ABSENT (owner decision 09.09.2026; see this function's
  // header). Nothing is written under `mcpServers` at user scope, and nothing pre-existing there
  // is pruned either: a hand-added entry of the owner's own is not this kit's to delete, and the
  // one stale entry the kit itself had left behind was removed by hand, once, with a backup.
  // buildQwenMcpServerEntry still has exactly one caller — writeProjectFiles's workspace-scope
  // write (qwen-mcp-entry.ts is still the single source of truth for that shape).

  // security.auth.selectedType — the owner's live ~/.qwen/settings.json was found with
  // `selectedType: "qwen-oauth"` alongside an unrelated apiKey/baseUrl pair (qwen-spec.md §7's
  // own "inconsistent state" note) — qwen-oauth ignores both, so nothing resolved. Switching to
  // "openai" is what makes the modelProviders block below actually take effect.
  if (!qwenSettings.security || typeof qwenSettings.security !== "object") qwenSettings.security = {};
  if (!qwenSettings.security.auth || typeof qwenSettings.security.auth !== "object") {
    qwenSettings.security.auth = {};
  }
  qwenSettings.security.auth.selectedType = "openai";

  // modelProviders / providerProtocol / agents.modelGrades / security.outboundCorrelation —
  // OWNER DECISION 09.09.2026 (task wire-print-config-fragment): the kit STOPPED writing these.
  // They used to be regenerated whole every run (`modelProviders`/`providerProtocol` merge as
  // REPLACE, packages/cli settingsSchema.ts) — on this hand-configured machine that silently
  // destroyed a working three-leg layout: `${session_id}` (qwen's own per-request template,
  // QwenLM/qwen-code#11282) collapsed to a static uuid, `contextWindowSize` vanished (~1M context
  // fell back to a ~200k default), and `modelGrades` shrank to two ids. Provider entries are
  // hermetic — a top-level `model.generationConfig` does not fill a missing provider field — so
  // there was no way to merge around this short of not writing the map at all.
  //
  // The kit's role in this now stops at PRINTING a ready-to-paste fragment (qwen-config-
  // fragment.ts, built from qwen-model-catalog.ts's known ids + this machine's roles.json role→
  // model bindings, so `petbox-wire model set <role> <id> --agent qwen` changes the printed
  // `agents.modelGrades`/`model.name`) and WARNING when the live config's own modelProviders/
  // providerProtocol/modelGrades/outboundCorrelation/model.name diverge from what that roster
  // expects — never auto-fixing (task brief: "если фрагмент уже присутствует — кит говорит, что
  // присутствует, и не трогает"; `model.name` specifically is the owner's own `/model`-picker
  // choice — task qwen-model-name-into-fragment). See the wiki page `qwen-three-provider-legs-
  // howto` for the full manual howto this compiles.
  // Extracted to printQwenRosterFragment (task role-model-bindings-review-refactor, remainder E) —
  // same reasoning as printCodexRosterFragment above: every roles.json write path calls this, not
  // only this full `wire` run's step 8. Reads the settings file fresh from disk rather than the
  // in-memory `qwenSettings` this function has been mutating above — the fields this check reads
  // (modelProviders/providerProtocol/agents.modelGrades/security.outboundCorrelation) are none of
  // the ones this function's own hooks/security.auth mutations touch, so a fresh read
  // is equivalent, and it is what makes the function callable with no coupling to this one.
  printQwenRosterFragment("[8/10]", homedir());

  // model.name — task qwen-model-name-into-fragment, owner decision 09.09.2026: the kit STOPPED
  // writing this key too (previously set unconditionally to fix defect qwen-dead-default-model —
  // live smoke, wire-support-codex-qwen — a stale value like "coder-model" matching no
  // `modelProviders.<key>[].id`, which made a bare `qwen` run fall through to `security.auth`'s
  // own apiKey/baseUrl instead of the routing this kit wires). That fix over-corrected: qwen
  // itself treats `model.name` as a USER-CHOICE key, persisted as a pair with `model.baseUrl`
  // every time the owner picks a model via `/model` — the kit's write replaced only the `name`
  // half of that pair, so every `wire` run silently reverted the owner's own `/model` pick
  // (measured: the now-stale `baseUrl` then matches no provider, and qwen falls back to a
  // hardcoded default leg — a warning at startup, and the wrong model in use). `model.name` is
  // now PRINTED ONLY, as part of the same fragment as agents.modelGrades (qwen-config-fragment.ts,
  // findQwenConfigDivergence above already covers the "live value resolves to nothing" case with a
  // warning, never a rewrite) — never written here.

  // agents.modelGrades — NOT written (see the modelProviders block above): printed only, as part
  // of the same fragment, since it gates the Agent tool's spawn-time `model` PARAMETER (a
  // DIFFERENT thing from a role file's own `model:` frontmatter field, which is never gated) and
  // an unseeded machine rejects EVERY explicit spawn-time `model` with an empty `Available:` list.

  writeJson(qwenSettingsPath, qwenSettings);
  log(
    `[8/10] merged qwen settings into ${qwenSettingsPath} (hooks, ` +
      `security.auth.selectedType=openai — mcpServers.petbox is NOT written at user scope ` +
      `(owner decision 09.09.2026: qwen outside a wired project gets no petbox tools); ` +
      `modelProviders/providerProtocol/agents.modelGrades/outboundCorrelation/model.name ` +
      `are printed only, never written).`,
  );
}

// ---- printed-fragment refresh, callable from ANY roles.json write path ------------------------
// (task role-model-bindings-review-refactor, remainder E, defect #7: before this, the codex/qwen
// fragment checks only ever ran inside installGlobalHooks, i.e. only on a full `wire` run's step
// 8 — `model set`/`model unset`/`profile use`/a standalone `apply` all wrote roles.json without
// ever re-printing whether the machine's codex config.toml / qwen settings.json still matched it.
// Pure extraction of the two blocks installGlobalHooks used to inline (see the calls above) —
// same divergence checks, same output, just reachable from more than one call site now.

/** Compare the live codex config.toml + the catalog it points at against roles.json's ACTIVE
 * profile, and print the ready-to-paste fragment when they diverge. `label` is the caller's own
 * step/command tag, so the printed lines read as coming from whichever command actually ran. */
function printCodexRosterFragment(label: string, homeDir: string = homedir()): void {
  const codexHome = codexHomeDir(homeDir);
  const codexConfigPath = join(codexHome, "config.toml");
  const codexCatalogPath = join(codexHome, CODEX_CATALOG_FILE_NAME);
  const codexRolesData = loadRoles(homeDir);
  const liveConfigText = readText(codexConfigPath);
  const livePathRaw = readCodexCatalogPathFromConfig(liveConfigText);
  const livePath = livePathRaw === undefined ? undefined : resolve(codexHome, livePathRaw);
  let liveCatalog: unknown;
  let catalogError: string | undefined;
  if (livePath !== undefined) {
    try {
      liveCatalog = JSON.parse(readFileSync(livePath, "utf8"));
    } catch (e) {
      catalogError = e instanceof Error ? e.message : String(e);
    }
  }
  const divergence = findCodexConfigDivergence(
    { configText: liveConfigText, catalog: liveCatalog, catalogError },
    codexRolesData,
  );
  if (divergence.warnings.length === 0) {
    log(
      `${label} codex model_providers/model_provider/model/model_catalog_json at ` +
        `${codexConfigPath} (catalog: ${livePath}) already match the kit's roster — nothing to paste.`,
    );
  } else {
    console.error(
      `${label} codex config at ${codexConfigPath} diverges from the kit's roster in ` +
        `${divergence.warnings.length} way(s). The kit does NOT write these keys (owner ` +
        `decision 09.09.2026) — until you paste the fragment below, codex roles here can run ` +
        `WITHOUT the apply_patch tool and on the wrong context window, at exit 0 and with no ` +
        `warning from codex itself:`,
    );
    for (const w of divergence.warnings) console.error(`  - ${w}`);
    console.error(renderCodexConfigFragmentText(codexRolesData, codexCatalogPath).text);
  }
  for (const n of divergence.notes) log(`${label} codex config note: ${n}`);
}

/** Compare the live qwen settings.json against roles.json's ACTIVE profile, and print the
 * ready-to-paste fragment when they diverge. Reads settings.json fresh from disk — never coupled
 * to installGlobalHooks's own in-memory mutations (see its call site's comment). */
function printQwenRosterFragment(label: string, homeDir: string = homedir()): void {
  const qwenHome = qwenHomeDir(homeDir);
  const qwenSettingsPath = join(qwenHome, "settings.json");
  const qwenSettings = readJson(qwenSettingsPath) ?? {};
  const qwenRolesData = loadRoles(homeDir);
  const divergence = findQwenConfigDivergence(qwenSettings, qwenRolesData);
  if (divergence.warnings.length === 0) {
    log(
      `${label} qwen modelProviders/providerProtocol/agents.modelGrades/outboundCorrelation/` +
        `model.name at ${qwenSettingsPath} already match the kit's roster — nothing to paste.`,
    );
  } else {
    console.error(
      `${label} qwen config at ${qwenSettingsPath} diverges from the kit's roster in ` +
        `${divergence.warnings.length} way(s) — the kit does NOT write these keys (owner ` +
        `decision 09.09.2026); paste the fragment below yourself:`,
    );
    for (const w of divergence.warnings) console.error(`  - ${w}`);
    log(renderQwenConfigFragmentText(qwenRolesData));
  }
  for (const n of divergence.notes) log(`${label} qwen config note: ${n}`);
}

/**
 * Everything a roles.json write path can safely refresh WITHOUT a separate `wire`/`apply` call
 * (acceptance #4): both PRINTED fragments always (they are machine-scoped, independent of
 * roleScope), and the WRITTEN per-harness role files (applyUserRoles) only when this machine's
 * roleScope is "user" — applyUserRoles needs no project cwd at all (its own doc comment), so it is
 * always safe to call from here; roleScope "project" needs a project cwd this command was never
 * given (a bare `model set` has no idea which project to render into), so that case is left on
 * the existing "next: petbox-wire apply" hint instead of guessing a directory.
 */
async function refreshDerivedArtifactsAfterRolesWrite(label: string): Promise<void> {
  printCodexRosterFragment(`${label}:`);
  printQwenRosterFragment(`${label}:`);
  const { scope } = resolveRoleScope(undefined, homedir());
  if (scope !== "user") {
    log(
      `${label}: roleScope=${scope} — per-harness role files NOT refreshed here (no project cwd to ` +
        `render into); run \`petbox-wire apply\`.`,
    );
    return;
  }
  const result = await applyUserRoles({ dryRun: false, adopt: NO_ADOPT, label: `${label} [roles:user]` });
  if (result.code !== WIRE_EXIT.ok) {
    console.error(
      `${label}: refreshing user-scope role files reported exit ${result.code} — run ` +
        `\`petbox-wire apply\` to see the full detail.`,
    );
  }
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

// HARNESS_ROLE_MODEL_SEEDS / seedMissingRoleBindings now live in roles.ts (single source of
// truth shared with status.ts's per-role model-source enumeration — see that file's comment).

// Seed ~/.petbox/roles.json's role->model bindings — for a brand-new file (nothing exists yet)
// AND for a pre-existing one that predates a harness this kit now knows how to seed. Without
// this, a brand-new machine's roles.json is empty, and apply now REFUSES to write a declared
// role with no local model binding on any CLOSED-model-space harness
// (reserve-unbound-inherits-session-model; apply-artifacts.ts's planApply) — so an unseeded
// roster on a fresh machine would leave claude-code fully blocked (exit WIRE_EXIT.truthfulness),
// not merely silently tier-drifting the way the 2026-07-26 incident (and, structurally, the
// 2026-07-12 one before it) grew from.
// Called from BOTH the full `wire` run (step 11, below) and the standalone `apply` subcommand
// (runApply) — previously only the former, so running a bare `apply` on a fresh machine (no
// roles.json yet) hit the unbound-role refusal with nothing to fix it.
//
// bug harness-seed-skipped-when-roles-json-exists (live smoke, wire-support-codex-qwen): the
// OLD version of this function only ever seeded on a totally ABSENT file — `if (existsSync(...))
// return`. Adding codex/qwen to HARNESS_IDS did nothing for any machine that already had a
// roles.json from before those harnesses existed (three profiles, each already carrying
// opencode/droid/claude-code): the new harnesses got zero bindings, forever, and every codex/qwen
// role silently inherited the session model. Fixed by running seedMissingRoleBindings — which
// fills in only what is ABSENT, per harness, per role, across EVERY profile in the file — on
// every call, not only the file-doesn't-exist branch.
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
  const fresh = !existsSync(rolesPath());
  const loaded = fresh
    ? {
        data: {
          formatVersion: ROLES_FORMAT_VERSION,
          activeProfile: "default",
          profiles: { default: { agents: {} } },
        } satisfies RolesFile,
        migrations: [],
      }
    : loadRolesMigrated();
  const before: RolesFile = loaded.data;
  logRolesMigration(label, loaded.migrations);
  const { data: after, changed, refreshed, reattributed } = seedMissingRoleBindings(before);
  logKitBindingRefresh(label, refreshed);
  logKitBindingReattribution(label, reattributed);
  // The format migration is a REASON TO WRITE in its own right: on a machine whose roles.json
  // already has every harness, the seeder changes nothing, and without this the migrated file
  // (origin/provider stamped, kit bindings brought up to the current defaults) would be recomputed
  // in memory on every run and never persisted.
  const migrated = loaded.migrations.length > 0;
  if (!changed && !migrated) {
    log(`${label} roles: ${rolesPath()} already exists — left as-is (existing bindings kept).`);
    logUnregisteredModelWarnings(label, after);
    logProviderInconsistencies(label, after);
    return;
  }
  saveRoles(after);
  if (!changed) {
    log(
      `${label} roles: ${rolesPath()} rewritten at format v${ROLES_FORMAT_VERSION} ` +
        `(origin + provider recorded per binding); no seed binding was missing.`,
    );
    logUnregisteredModelWarnings(label, after);
    logProviderInconsistencies(label, after);
    return;
  }
  const seededHarnesses = Object.keys(HARNESS_ROLE_MODEL_SEEDS).filter((harness) =>
    Object.entries(after.profiles).some(([profileName, afterProfile]) => {
      const beforeProfile = before.profiles[profileName];
      const key = agentLookupKeys(harness).find((k) => k in afterProfile.agents) ?? harness;
      const afterCount = Object.keys(afterProfile.agents[key]?.roles ?? {}).length;
      const beforeKey = beforeProfile
        ? agentLookupKeys(harness).find((k) => k in beforeProfile.agents)
        : undefined;
      const beforeCount = beforeKey
        ? Object.keys(beforeProfile?.agents[beforeKey]?.roles ?? {}).length
        : 0;
      return afterCount > beforeCount;
    }),
  );
  log(
    fresh
      ? `${label} roles: seeded ${rolesPath()} — profile "default": ${seededHarnesses.join(", ")} ` +
          `bound from this kit's default seeds. opencode is intentionally left unbound (its model ` +
          `space is open/unknowable from the kit) — apply will warn about it, not fail; bind it ` +
          `yourself with \`petbox-wire model set <role> <model> --agent opencode\` when you know what ` +
          `to bind it to.`
      : `${label} roles: ${rolesPath()} already existed — filled in missing default seed ` +
          `bindings for: ${seededHarnesses.join(", ")} (every OWNER-set binding, any harness, ` +
          `left byte-for-byte; kit-set ones are refreshed to the current defaults and reported ` +
          `above).`,
  );
  logUnregisteredModelWarnings(label, after);
  logProviderInconsistencies(label, after);
}

// The roles.json format migration (v1 -> v2) rewrites model VALUES — every binding it attributes
// to a past version of this kit is brought up to the kit's current default. That is the entire
// point (a changed default never used to reach a machine that already had a roles.json), and it is
// also exactly the kind of change that must never happen quietly: the operator sees each cell, its
// old value, its new value, and the origin the migration decided.
function logRolesMigration(label: string, migrations: readonly RoleBindingMigration[]): void {
  if (migrations.length === 0) return;
  const rewritten = rewrittenByMigration(migrations);
  const owned = migrations.filter((m) => m.origin === "owner").length;
  log(
    `${label} roles: migrated ${rolesPath()} to format v${ROLES_FORMAT_VERSION} — ` +
      `${migrations.length} binding(s) labelled with origin + provider, ${owned} attributed to ` +
      `you and left byte-for-byte, ${rewritten.length} kit-seeded one(s) updated to this kit's ` +
      `current defaults.`,
  );
  for (const m of rewritten) {
    log(
      `  - profile '${m.profile}' ${m.agent}/${m.role}: ${m.modelBefore} -> ${m.modelAfter} ` +
        `(provider ${m.provider ?? "unknown"}; matched a seed this kit shipped previously, so it ` +
        `was this kit's own past choice, not yours)`,
    );
  }
}

// A kit-origin binding moving because the kit's defaults moved — the steady-state version of the
// migration report above, on a file already at the current format.
function logKitBindingRefresh(label: string, refreshed: readonly RoleBindingRefresh[]): void {
  if (refreshed.length === 0) return;
  log(
    `${label} roles: ${refreshed.length} kit-set binding(s) updated to this kit's current ` +
      `defaults (bindings you set yourself are never touched):`,
  );
  for (const r of refreshed) {
    log(`  - profile '${r.profile}' ${r.agent}/${r.role}: ${r.modelBefore} -> ${r.modelAfter}`);
  }
}

// A binding still LABELLED kit whose value this kit has never shipped — i.e. edited straight in
// the file rather than through `model set`, which stamps the origin itself. The value is kept and
// the label corrected, so the next run does not silently revert a deliberate edit; saying so out
// loud is what keeps the origin field honest rather than magic.
function logKitBindingReattribution(
  label: string,
  reattributed: readonly RoleBindingReattribution[],
): void {
  if (reattributed.length === 0) return;
  log(
    `${label} roles: ${reattributed.length} binding(s) marked as set by this kit no longer hold a ` +
      `value this kit ships — treating them as yours from now on (value kept as-is):`,
  );
  for (const r of reattributed) {
    log(`  - profile '${r.profile}' ${r.agent}/${r.role}: ${r.model}`);
  }
}

// The `provider` label on a binding is only worth reading if something checks it against the value
// it claims to describe. The kit derives it on every write, so this can only fire on a hand-edited
// file — which is precisely the case where a reader would otherwise trust a label that lies.
// Internal consistency ONLY (value vs label); checking either against live machine config is
// stage B2.
function logProviderInconsistencies(label: string, data: RolesFile): void {
  const issues = findBindingProviderInconsistencies(data);
  if (issues.length === 0) return;
  console.error(
    `${label} roles: ${issues.length} binding(s) carry a provider label that contradicts their ` +
      `own model value. Warning only: nothing was rewritten or blocked.`,
  );
  for (const i of issues) console.error(`  - ${i}`);
}

// Runs on EVERY seedDefaultRoleBindingsIfMissing call — both the freshly-seeded/newly-changed
// path and the no-op "already exists, left as-is" path — because the gap this closes
// (task wire-support-codex-qwen, silent-misroute follow-up) is specifically about bindings
// seedMissingRoleBindings deliberately never touches: a profile that already had a codex/qwen
// entry before the kit's own seed values changed keeps its OLD id forever, invisibly, exactly
// because the seeder is purely additive. Warn-only (findUnregisteredRoleBindings never mutates
// roles.json or blocks the run) — see model-registration-check.ts's header for the full
// consequence-by-harness reasoning. Runs for BOTH `wire`'s step 11 and the standalone `apply`
// subcommand, since both call seedDefaultRoleBindingsIfMissing (this file's own comment on that
// function) — a single hook point instead of two call sites that could drift.
function logUnregisteredModelWarnings(label: string, data: RolesFile): void {
  const warnings = findUnregisteredRoleBindings(data);
  if (warnings.length === 0) return;
  console.error(
    `${label} roles: ${warnings.length} role binding(s) name a model id the kit does not ` +
      `currently register — see roles.ts's CODEX_ROLE_MODEL_SEED/QWEN_ROLE_MODEL_SEED for what ` +
      `changed. Warning only: nothing was rewritten or blocked.`,
  );
  for (const w of warnings) console.error(`  - ${w}`);
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
    await runRoles(argv);
    return;
  }
  if (isProfileCommand(argv)) {
    await runProfile(argv);
    return;
  }
  if (isModelCommand(argv)) {
    await runModel(argv);
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
  // Set only in the env-fallback branch below (never for --key): tells step 4 whether to
  // persist a $VAR reference instead of a literal (card decision — see there).
  let keyFromEnv = false;
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
    // keyFromEnv tracks provenance for step 4 below (card decision: bootstrap writes a $VAR
    // reference into keys.json whenever the key it used came straight from a live env var,
    // not only as a manual option for the owner). A key found via the file fallback is, by
    // definition, already whatever the file said (literal or reference) — nothing new to write.
    keyFromEnv = !!process.env[envVar];
    if (keyFromEnv) {
      key = process.env[envVar]!;
    } else {
      // readKeyFromStore throws UnresolvedEnvRefError — not silence, not "" — when keys.json
      // holds a reference this environment can't satisfy. That is a DIFFERENT, more specific
      // failure than "nothing wired yet" (the plain-absent case below), so it gets its own
      // clean operator-facing abort instead of falling through to the generic message.
      try {
        key = readKeyFromStore(envVar, project) || "";
      } catch (e) {
        if (e instanceof UnresolvedEnvRefError) abortRun(WIRE_EXIT.usage, `[2/10] ${e.message}`);
        throw e;
      }
    }
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
  //
  // keys-json-supports-env-var-references, decision 1: whenever the key came straight from a
  // live env var, keys.json gets a $VAR reference, not a copy of the secret — not only as a
  // manual option for the owner, bootstrap does this itself by default. A key from --key or
  // from the file fallback is written as-is (there is no live env value to point at, or the
  // file already dictated the form — see readKeyFromStore/writeKeyToStore's comments).
  const storeValue = keyFromEnv ? toEnvRef(envVar) : key;
  writeKeyToStore(envVar, storeValue);
  log(
    `[4/10] persisted ${envVar} to ${keysStorePath()} ` +
      `(${keyFromEnv ? "as a $VAR reference — no key material copied into the file" : "as a literal value"}).`,
  );
  persistKeyForAgents(envVar, project);

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

  // 7c. seed a default role→model binding (fresh machine only) — MUST run BEFORE step 8's qwen
  // config-fragment print (task wire-fragment-first-run-empty-grades, found by independent review
  // of wire-print-config-fragment): step 8's installGlobalHooks reads roles.json via loadRoles()
  // to build the printed `agents.modelGrades` fragment. On a brand-new machine roles.json does not
  // exist yet — printing before this seed step ran gave the very first `wire` run on any machine
  // an empty `agents.modelGrades: {}` fragment, silently correct on the SECOND run only. That is
  // exactly the one run a fresh owner is expected to paste (this fragment is meant to be pasted
  // once, not diffed run over run) — so the bug was invisible to the one person who could not
  // afford it. NEVER ABORTS THE RUN (see this function's own header comment): the key is already
  // validated by this point, so a compile hiccup here must not throw away the rest of an otherwise
  // successful wire run; re-running `petbox-wire apply` retries just this step. Idempotent by
  // construction (seedMissingRoleBindings is purely additive — see its own doc comment) — moving
  // this call earlier does not create a second write: step 11 below no longer calls it a second
  // time, it only applies/compiles against whatever this call already settled.
  seedDefaultRoleBindingsIfMissing("[7c/10]");

  // 8. global install — installs the live Stop/SessionStart hooks and, unconditionally, prunes the
  // dead prompt-rag UserPromptSubmit hook left behind by a kit that still had the feature.
  installGlobalHooks(dir);

  // 9. cleanup legacy
  if (args.cleanupLegacy) cleanupLegacy(dir);
  else log(`[9/10] cleanup-legacy not requested — skipped.`);

  // 10. self-smoke
  const smokeOk = await selfSmoke(baseUrl, project, key);

  // 11. apply — compile per-harness startup artifacts NOW, so the freshly-wired roster is actually
  // usable. The default-binding SEED itself already ran at step 7c (task
  // wire-fragment-first-run-empty-grades) — moved there so step 8's printed qwen fragment sees a
  // seeded roster on the very first run; this step only compiles/applies against whatever roles.json
  // already holds by now. NEVER ABORTS THE RUN: the key is already validated and every other file
  // is already written by this point, so a compile hiccup here (e.g. a transient workspace-probe
  // failure, which only downgrades the skill refresh) must not throw away work that already
  // succeeded; re-running `petbox-wire apply` retries just this step (fresh-wire-roster-unusable).
  //
  // "Does not abort" is NOT "does not count" (full-wire-exit-ignores-step-11). Those two were
  // fused in this comment and the code only implemented the first: step 11 could return 1
  // (a clobber refusal), 3 or 4 and the run still ended 0, which made the exit-code table's
  // "0 — every requested step ran" false for the full-wire path and re-opened the very bug this
  // step exists to close — a machine whose agent artifacts were never written is then
  // indistinguishable, to a script, from a fully wired one.
  // The machine's remembered role-scope policy (~/.petbox/wire.json) applies here too — a full
  // `wire` re-run on a machine that moved its roles into the harness profiles must not silently
  // re-create the project copies it just deleted (card: normalize-all-environments-to-default).
  const step11Scope = loadWireConfig(homedir()).roleScope;
  log(`[11/10] roles → ${step11Scope} scope (machine policy: ${wireConfigPath(homedir())})`);
  const userRoleResult =
    step11Scope === "user"
      ? await applyUserRoles({
          dryRun: false,
          adopt: NO_ADOPT,
          label: "[11/10 roles:user]",
        })
      : undefined;
  const applyResult = await performApply({
    offline: false,
    roleScope: step11Scope,
    label: "[11/10]",
  });
  // Same two-pass reconciliation `apply --roles=user` needs (see reportSplitRunOutcome): under the
  // user policy this step has already written the harness profiles before the project pass ran.
  if (userRoleResult) reportSplitRunOutcome("[11/10]", userRoleResult, applyResult, false);
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
    // The user-scope role pass is part of step 11 when the machine policy says so; its failure
    // must not be weaker than the project pass's for the sole reason of being a separate call.
    userRoleResult?.code ?? WIRE_EXIT.ok,
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
