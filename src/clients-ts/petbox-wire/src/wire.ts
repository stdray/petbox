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
// NOT touch keys, registry entries, sticky prompt-rag/telemetry flags, per-project MCP/skills,
// or require projectKey/key.
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
//       all links point at the stable copy (~/.petbox/wire/). (--prompt-rag) additionally installs
//       the OPT-IN Claude Code UserPromptSubmit prompt-RAG hook (off by default; safe exact-match
//       context injection — see prompt-rag.ts).
//    9. (--cleanup-legacy) remove the project's old per-project hook/plugin copies
//   10. self-smoke: POST a tiny session and assert the server applied it
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
  fetchAgentDefinition,
} from "./agent-def-fetch.ts";
import {
  DEFAULT_AGENT_DEFINITION,
  validateAgentDefinition,
  type AgentDefinition,
} from "./agent-definition.ts";
import {
  formatApplyBlocked,
  planApply,
  type ApplyPlan,
} from "./apply-artifacts.ts";
import { HARNESS_IDS } from "./harness-capabilities.ts";
import { persistKeyForAgentsPosix } from "./posix-env.ts";
import { classifyApplyExit, WIRE_EXIT } from "./wire-exit.ts";
import {
  PROMPT_RAG_DEFAULTS,
  readRegistry,
  resolveProject,
  type PromptRagConfig,
} from "./registry.ts";
import {
  exportRolesBootstrap,
  formatResolvedBinding,
  isEmptyRoles,
  loadRoles,
  resolveAgentRoles,
  saveRoles,
  useProfile,
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
  // Per-project prompt-RAG gate (step 1, registry-gated). Tri-state via two flags:
  //   --prompt-rag    → promptRag=true  (enable on THIS project + install/keep the global hook)
  //   --no-prompt-rag → promptRag=false (disable on THIS project; the global hook stays, self-gates)
  //   neither         → promptRag=undefined = STICKY (leave the project's existing state untouched)
  // The global UserPromptSubmit hook fires for every registered project and self-gates per project
  // from ~/.petbox/projects.json, so enabling is per-project even though the hook is global.
  promptRag: boolean | undefined;
};

const DEFAULT_TELEMETRY_LOG = "cc-telemetry";

// Print the usage banner and exit. `--help`/`-h` → stdout + exit 0; argument errors →
// stderr + exit WIRE_EXIT.usage (2). Same text either way.
function usage(exitCode: number = WIRE_EXIT.usage): never {
  const text =
    "usage: npx petbox-wire <dir> <projectKey> [--env VAR] [--key KEY] [--workspace WS] [--cleanup-legacy]\n" +
    "                       [--telemetry] [--telemetry-log <name>] [--prompt-rag | --no-prompt-rag]\n" +
    "       npx petbox-wire update\n" +
    "       npx petbox-wire apply [--definition <key>] [--offline]\n" +
    "       npx petbox-wire doctor\n" +
    "       npx petbox-wire roles\n" +
    "       npx petbox-wire roles export\n" +
    "       npx petbox-wire profile use <name>\n" +
    "       npx petbox-wire --help\n" +
    "\n" +
    "Wire a project to PetBox: global hooks, MCP configs and skills. prompt-RAG is OFF by default\n" +
    "(opt in per project with --prompt-rag; --no-prompt-rag or a plain re-run removes the global hook).\n" +
    "\n" +
    "update       Refresh ~/.petbox/wire only (protocol/scripts/templates) from this package. Does not\n" +
    "             touch keys, registry, sticky prompt-rag/telemetry, or per-project MCP/skills.\n" +
    "             Kit-copy only — does NOT compile per-harness agent artifacts (use apply).\n" +
    "apply        Compile per-harness startup artifacts from a portable agent definition + local\n" +
    "             role→model binding (~/.petbox/roles.json). Tries GET /api/{project}/agent-defs/{key}\n" +
    "             when cwd resolves via ~/.petbox/projects.json; falls back to the built-in default on\n" +
    "             network/404/auth miss or --offline. --definition <key> selects the server doc\n" +
    "             (default key: default). Writes agent files under the project root:\n" +
    "             claude-code .claude/agents/, opencode .opencode/agent/, droid .factory/droids/.\n" +
    "             model: frontmatter only when bound (droid unbound → model: inherit) — never invents\n" +
    "             a concrete model id. Clean roles are written; dirty roles are skipped and reported.\n" +
    "             Exit codes: 0 full success; 1 hard failure (invalid definition/throw); 2 usage/args;\n" +
    "             3 truthfulness partial/block (policy — distinct from usage).\n" +
    "doctor       Run the definition truthfulness gate for every known harness against the default\n" +
    "             definition (+ optional local binding is noted, not required). Prints OK or each\n" +
    "             violation; exit 1 on any violation. Offline.\n" +
    "roles        Print the local role→model binding for the active profile (~/.petbox/roles.json).\n" +
    "             Offline; empty store exits 0 with a clear message (never invents default models).\n" +
    "roles export Write a bootstrap copy of roles.json to stdout (no secrets; pipe to a file on a\n" +
    "             new machine). Offline.\n" +
    "profile use  Set activeProfile in ~/.petbox/roles.json (creates an empty profile shell if missing).\n" +
    "             Offline. Re-run apply to rebuild artifacts after changing the active profile.";
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
  let promptRag: boolean | undefined = undefined; // undefined = STICKY (neither flag passed)
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    if (a === "--help" || a === "-h") usage(0);
    else if (a === "--env") env = argv[++i];
    else if (a === "--key") key = argv[++i];
    else if (a === "--workspace") workspace = argv[++i];
    else if (a === "--cleanup-legacy") cleanupLegacy = true;
    else if (a === "--telemetry") telemetry = true;
    else if (a === "--telemetry-log") telemetryLog = argv[++i];
    else if (a === "--prompt-rag") promptRag = true;
    else if (a === "--no-prompt-rag") promptRag = false;
    else if (a.startsWith("--")) {
      console.error(`unknown flag: ${a}`);
      usage();
    } else positionals.push(a);
  }
  if (positionals.length < 2) usage();
  if (!telemetryLog || !telemetryLog.trim()) {
    console.error("--telemetry-log requires a non-empty log name");
    usage();
  }
  return {
    dir: positionals[0],
    projectKey: positionals[1],
    env,
    key,
    workspace,
    cleanupLegacy,
    telemetry,
    telemetryLog: telemetryLog.trim(),
    promptRag,
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

// Longest registry prefix that covers cwd (no API key required). Falls back to cwd.
function resolveApplyRoot(cwd: string): { root: string; via: "registry" | "cwd" } {
  try {
    const entries = readRegistry();
    const nd = cwd.replace(/[\\/]+/g, "/").replace(/\/+$/, "");
    const ndCmp = process.platform === "win32" ? nd.toLowerCase() : nd;
    let best: string | null = null;
    let bestLen = -1;
    for (const e of entries) {
      let np = String(e.prefix).replace(/[\\/]+/g, "/").replace(/\/+$/, "");
      const npCmp = process.platform === "win32" ? np.toLowerCase() : np;
      const under = ndCmp === npCmp || ndCmp.startsWith(npCmp + "/");
      if (under && npCmp.length > bestLen) {
        best = e.prefix;
        bestLen = npCmp.length;
      }
    }
    if (best) return { root: best, via: "registry" };
  } catch {
    /* fall through */
  }
  return { root: cwd, via: "cwd" };
}

// doctor — truthfulness gate for each known harness vs default definition. Exit 1 on violation.
function runDoctor(argv: string[]): void {
  for (let i = 1; i < argv.length; i++) {
    const a = argv[i];
    if (a === "--help" || a === "-h") usage(0);
    console.error(`doctor: unexpected argument: ${a}`);
    usage();
  }

  validateAgentDefinition(DEFAULT_AGENT_DEFINITION);
  const roles = loadRoles();
  const bindingNote = isEmptyRoles(roles)
    ? "local binding: (empty — not required for doctor)"
    : `local binding: activeProfile=${roles.activeProfile} (observational; gate uses definition only)`;

  log(`doctor: definition="${DEFAULT_AGENT_DEFINITION.name}" (${DEFAULT_AGENT_DEFINITION.roles.length} roles)`);
  log(`doctor: ${bindingNote}`);

  let failed = false;
  for (const harness of HARNESS_IDS) {
    const violations = checkTruthfulness(DEFAULT_AGENT_DEFINITION, harness);
    if (violations.length === 0) {
      log(`doctor: ${harness} — OK`);
    } else {
      failed = true;
      console.error(`doctor: ${harness} — ${violations.length} violation(s):`);
      console.error(formatViolations(violations));
    }
  }

  if (failed) {
    console.error("doctor: FAILED — definition requires capability/ies a harness does not declare.");
    process.exit(1);
  }
  log("doctor: all known harnesses OK.");
}

// apply — compile per-harness artifacts (distinct from update kit-copy).
// Definition source: server fetch when registry resolves cwd; else offline default.
//
// Per role × harness (definition-truthfulness + wiring-startup-symmetry):
//   - dirty roles → skip + report (never silent); clean roles still written
// Exit codes (see WIRE_EXIT / classifyApplyExit — usage must stay distinct from truthfulness):
//   0 — full success: every known harness wrote all its roles, no skips
//   1 — hard failure: invalid definition / unexpected throw (NOT bad args)
//   2 — usage / bad arguments (via usage())
//   3 — truthfulness: policy blocked some roles/harnesses (partial write possible)
async function runApply(argv: string[]): Promise<void> {
  let definitionKey = DEFAULT_DEFINITION_KEY;
  let offline = false;
  for (let i = 1; i < argv.length; i++) {
    const a = argv[i];
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

  const { root, via } = resolveApplyRoot(process.cwd());
  let definition;
  try {
    definition = await resolveApplyDefinition({
      offline,
      definitionKey,
      cwd: process.cwd(),
    });
    validateAgentDefinition(definition);
  } catch (e) {
    console.error(`apply: hard failure — ${e instanceof Error ? e.message : String(e)}`);
    process.exit(WIRE_EXIT.hard);
  }

  const rolesData = loadRoles();
  log(`apply: root=${root} (via ${via})`);
  log(`apply: definition="${definition.name}", harnesses=${HARNESS_IDS.join(",")}`);

  let written = 0;
  const writtenHarnesses: string[] = [];
  const partialHarnesses: string[] = [];
  const blockedHarnesses: string[] = [];
  for (const harness of HARNESS_IDS) {
    const roleModels = resolveAgentRoles(rolesData, harness);
    const plan = planApply(definition, harness, roleModels);

    for (const file of plan.files) {
      const abs = join(root, file.relativePath);
      mkdirSync(dirname(abs), { recursive: true });
      writeFileSync(abs, file.content, "utf8");
      log(`apply: wrote ${abs}`);
      written++;
    }

    if (plan.violations.length > 0) {
      console.error(formatApplyBlocked(plan.violations, plan.harness, plan.skippedRoles));
      if (plan.files.length > 0) partialHarnesses.push(plan.harness);
      else blockedHarnesses.push(plan.harness);
    } else if (plan.files.length > 0) {
      writtenHarnesses.push(plan.harness);
    }
  }

  // Structured summary (machine-readable-ish one line + human detail above).
  const summary = {
    writtenFiles: written,
    writtenHarnesses,
    partialHarnesses,
    blockedHarnesses,
  };
  log(
    `apply: result written=${written} ` +
      `ok=[${writtenHarnesses.join(",")}] ` +
      `partial=[${partialHarnesses.join(",")}] ` +
      `blocked=[${blockedHarnesses.join(",")}]`,
  );

  const hadTruthfulnessBlock = partialHarnesses.length > 0 || blockedHarnesses.length > 0;
  const code = classifyApplyExit({ hadTruthfulnessBlock });
  if (code === WIRE_EXIT.ok) {
    log("apply: done — all known harnesses accepted every role.");
    process.exit(WIRE_EXIT.ok);
  }
  console.error(
    `apply: truthfulness partial — some roles/harnesses blocked (exit ${WIRE_EXIT.truthfulness}). ${JSON.stringify(summary)}`,
  );
  process.exit(WIRE_EXIT.truthfulness);
}

// Pick server definition when possible; always fall back to DEFAULT_AGENT_DEFINITION.
async function resolveApplyDefinition(opts: {
  offline: boolean;
  definitionKey: string;
  cwd: string;
}): Promise<AgentDefinition> {
  if (opts.offline) {
    log("apply: offline default definition");
    return DEFAULT_AGENT_DEFINITION;
  }

  const resolved = resolveProject(opts.cwd);
  if (!resolved) {
    log("apply: offline default definition");
    return DEFAULT_AGENT_DEFINITION;
  }

  const fetched = await fetchAgentDefinition({
    baseUrl: resolved.baseUrl,
    projectKey: resolved.project,
    apiKey: resolved.apiKey,
    definitionKey: opts.definitionKey,
  });
  if (!fetched) {
    log("apply: offline default definition");
    return DEFAULT_AGENT_DEFINITION;
  }

  log(`apply: using server definition ${fetched.key} v${fetched.version}`);
  return fetched.definition;
}

// Print active profile + agent/role/model tree from ~/.petbox/roles.json. Exit 0 when empty.
function runRoles(argv: string[]): void {
  // roles | roles export  (+ optional --help)
  const sub = argv[1];
  if (sub === "--help" || sub === "-h") usage(0);
  if (sub === "export") {
    for (let i = 2; i < argv.length; i++) {
      const a = argv[i];
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

// ---- small helpers ---------------------------------------------------------

const log = (msg: string) => console.log(msg);

function deriveEnvVar(projectKey: string): string {
  return projectKey.toUpperCase().replace(/[^A-Z0-9]/g, "_") + "_API_KEY";
}

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

async function validateKey(baseUrl: string, key: string, projectKey: string): Promise<void> {
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
    return;
  }
  let body: any = null;
  try {
    body = await resp.json();
  } catch {
    log(`[3/10] validate: 200 but non-JSON body; continuing with a warning.`);
    return;
  }
  // Contract (AuthApi.cs): 200 => { project, scopes } (camelCase, ASP.NET web defaults).
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
  // never DELETES, so a downgrade (e.g. an older npm package whose src lacks prompt-rag.ts) would
  // leave a NEWER orphan file standing next to OLDER peers → version skew: prompt-rag.ts kept from a
  // prior install importing `PROMPT_RAG_DEFAULTS` from a registry.ts the downgrade just reverted,
  // which no longer exports it → SyntaxError on every prompt. Remove every top-level STABLE entry
  // absent from HERE before copying, so the install can only ever match the shipped kit.
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

// Safe kit-text refresh only: mirror THIS package into ~/.petbox/wire with orphan cleanup.
// Intentionally does NOT: rotate/require API keys, touch ~/.petbox/keys.json or projects.json,
// reinstall global hooks, rewrite per-project MCP/skills, or flip sticky prompt-rag/telemetry.
// v1: STABLE kit only — re-run full wire to regenerate per-project skill bodies / MCP configs.
function runUpdate(argv: string[]): void {
  // `update` takes no flags other than help; reject extras so typos don't silently no-op.
  for (let i = 1; i < argv.length; i++) {
    const a = argv[i];
    if (a === "--help" || a === "-h") usage(0);
    console.error(`update: unexpected argument: ${a}`);
    usage();
  }
  log(`update: refreshing stable kit ${STABLE} from ${HERE}`);
  log(`update: source hash ${kitFingerprint(HERE)}`);
  const result = copyKitToStable("update:");
  if (result.skipped) {
    log(`update: done — kit already at ${STABLE} (hash ${result.after}).`);
  } else if (result.before === result.after) {
    log(`update: done — kit unchanged (hash ${result.after}).`);
  } else {
    log(`update: done — kit hash ${result.before} → ${result.after}.`);
  }
  log(
    "update: skipped keys, registry, sticky prompt-rag/telemetry, global hooks reinstall, " +
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

// Merge a prompt-RAG update onto the entry's existing config.
//   update === undefined → STICKY: return the existing config unchanged (may be undefined).
//   update = { enabled }  → flip enabled, but PRESERVE any already-tuned tolerances (fall back to
//                           PROMPT_RAG_DEFAULTS when neither the update nor the existing set them).
// This is what keeps `promptRag` from being dropped on a plain re-run (sticky) and what lets
// --prompt-rag / --no-prompt-rag toggle enablement without clobbering custom cap/requireHyphen.
function mergePromptRag(
  existing: PromptRagConfig | undefined,
  update: PromptRagConfig | undefined,
): PromptRagConfig | undefined {
  if (!update) return existing;
  return {
    enabled: update.enabled,
    cap: update.cap ?? existing?.cap ?? PROMPT_RAG_DEFAULTS.cap,
    requireHyphen: update.requireHyphen ?? existing?.requireHyphen ?? PROMPT_RAG_DEFAULTS.requireHyphen,
  };
}

// Upsert the registry entry for `prefix`. `promptRag` is the requested per-project prompt-RAG
// update (undefined = leave the existing state as-is). The prior entry's `promptRag` is read back
// and merged so a plain re-run never drops it, and neither prefix/project/envVar/baseUrl nor a
// tuned promptRag is lost.
function upsertRegistry(
  prefix: string,
  project: string,
  envVar: string,
  baseUrl: string,
  promptRag?: PromptRagConfig,
): void {
  const path = join(homedir(), ".petbox", "projects.json");
  const data = readJson(path) ?? {};
  const entries: any[] = Array.isArray(data.entries) ? data.entries : [];
  const norm = (p: string) => p.replace(/[\\/]+/g, "/").replace(/\/+$/, "").toLowerCase();
  const np = norm(prefix);
  const prior = entries.find((e) => norm(String(e?.prefix ?? "")) === np);
  const next = entries.filter((e) => norm(String(e?.prefix ?? "")) !== np);
  const entry: any = { prefix, project, envVar };
  if (baseUrl !== DEFAULT_BASE_URL) entry.baseUrl = baseUrl;
  const mergedRag = mergePromptRag(
    prior && typeof prior.promptRag === "object" ? prior.promptRag : undefined,
    promptRag,
  );
  if (mergedRag) entry.promptRag = mergedRag;
  next.push(entry);
  writeJson(path, { entries: next });
  const ragNote = mergedRag ? ` [prompt-rag: ${mergedRag.enabled ? "on" : "off"}]` : "";
  log(`[6/10] registry: upserted ${prefix} → ${project} (${envVar})${ragNote} in ${path}`);
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

// prompt-RAG self-audit log — the UserPromptSubmit hook (prompt-rag.ts) ships ONE CLEF record per
// enabled-project invocation to this named log for injection-rate + precision eval. Path-based CLEF
// ingest 404s if the log is absent, so create it when ENABLING prompt-RAG. Idempotent (409 =
// already exists = success). Non-fatal by design: a failure (e.g. the wired key lacks logs:admin)
// only means the first audits 404 until the log is created out-of-band — never a broken wiring — so
// WARN and continue rather than abort (unlike ensureTelemetryLog, whose absence breaks OTLP export).
const PROMPT_RAG_AUDIT_LOG = "prompt-rag-audit";

async function ensurePromptRagAuditLog(baseUrl: string, project: string, key: string): Promise<void> {
  const uri = `${baseUrl}/api/logs/${project}/logs`;
  try {
    const resp = await fetch(uri, {
      method: "POST",
      headers: { "X-Api-Key": key, "Content-Type": "application/json" },
      body: JSON.stringify({
        name: PROMPT_RAG_AUDIT_LOG,
        description:
          "prompt-RAG hook self-audit (injection-rate + precision) — one record per enabled-project UserPromptSubmit invocation.",
      }),
      signal: AbortSignal.timeout(12000),
    });
    if (resp.ok || resp.status === 409) {
      log(`[prompt-rag] audit log '${PROMPT_RAG_AUDIT_LOG}' ready in project '${project}' (HTTP ${resp.status}).`);
      return;
    }
    const text = await resp.text().catch(() => "");
    console.error(
      `[prompt-rag] WARN could not ensure audit log '${PROMPT_RAG_AUDIT_LOG}' — HTTP ${resp.status} ${text}. ` +
        `The hook still runs; audits 404 (silently) until the log exists (create it via mcp__petbox__log_create).`,
    );
  } catch (e) {
    console.error(
      `[prompt-rag] WARN could not reach ${uri} — ${(e as Error).message}. Audit log not ensured; the hook still runs.`,
    );
  }
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

// Remove EVERY hook (across all events) whose command targets the given STABLE kit file, then drop
// now-empty groups. Mutates hooksObj in place; returns the count pruned. Commands quote the path
// (`node "<...>/prompt-rag.ts"`, optionally ` --agent droid`), so matching the quoted basename
// catches both the Claude Code and Droid variants. Used to keep prompt-RAG OFF by default and to
// self-heal a version-skewed install where the kit no longer ships the referenced file.
function pruneHooksTargeting(hooksObj: any, fileBasename: string): number {
  let removed = 0;
  const needle = `${fileBasename}"`;
  for (const event of Object.keys(hooksObj)) {
    const groups: any[] = Array.isArray(hooksObj[event]) ? hooksObj[event] : [];
    for (const g of groups) {
      if (!g || !Array.isArray(g.hooks)) continue;
      const before = g.hooks.length;
      g.hooks = g.hooks.filter(
        (h: any) => !(typeof h?.command === "string" && h.command.includes(needle)),
      );
      removed += before - g.hooks.length;
    }
    hooksObj[event] = groups.filter((g) => !(g && Array.isArray(g.hooks) && g.hooks.length === 0));
  }
  return removed;
}

function installGlobalHooks(promptRag: boolean): void {
  const pushCmd = `node "${join(STABLE, "push-session.ts")}"`;
  const pullCmd = `node "${join(STABLE, "pull-memory.ts")}"`;
  const promptRagCmd = `node "${join(STABLE, "prompt-rag.ts")}"`;
  // Droid reuses the SAME prompt-rag.ts hook (its UserPromptSubmit contract is CC-compatible:
  // stdin JSON carries cwd+prompt, stdout on exit 0 is injected as context). Only the emitted
  // pointer's tool name differs — `--agent droid` selects droidPetboxTool (`petbox___…`).
  const droidPromptRagCmd = `node "${join(STABLE, "prompt-rag.ts")}" --agent droid`;
  const droidPushCmd = `node "${join(STABLE, "droid-push-session.ts")}"`;
  const droidPullCmd = `node "${join(STABLE, "droid-pull-memory.ts")}"`;
  // Every kit hook command this run considers current — the prune keeps these, drops the rest.
  const validCmds = new Set([pushCmd, pullCmd, droidPushCmd, droidPullCmd]);
  // Version-skew guard: only ever wire prompt-RAG when this run enables it AND the kit actually
  // ships the hook file. A downgraded kit (published package behind canon) that lacks prompt-rag.ts
  // must NOT leave a hook pointing at a missing/mismatched file — it self-heals to pruned instead.
  const stableHasPromptRag = existsSync(join(STABLE, "prompt-rag.ts"));
  if (promptRag && !stableHasPromptRag) {
    log(`[8/10] prompt-rag requested but ${join(STABLE, "prompt-rag.ts")} is not shipped by this kit — skipping install (version-skew guard).`);
  }
  const wantPromptRag = promptRag && stableHasPromptRag;

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
  // prompt-RAG (Claude Code only in v1): the UserPromptSubmit exact-match context injector is a
  // GLOBAL hook, but OFF BY DEFAULT. It is installed ONLY when this run both requests it
  // (--prompt-rag) and ships the file (wantPromptRag); otherwise any previously-installed prompt-rag
  // hook is PRUNED. So a plain re-run, --no-prompt-rag, or a downgraded kit that lacks prompt-rag.ts
  // all converge on "no prompt-rag hook" — the recurring version-skew crash can't survive a wire.
  // (The per-project registry flag still gates injection at runtime; this is the install-time gate.)
  // UserPromptSubmit takes no matcher (CC docs), so the group shape { hooks: [...] } is correct.
  if (wantPromptRag) {
    ensureHook("UserPromptSubmit", promptRagCmd);
  } else {
    const pruned = pruneHooksTargeting(settings.hooks, "prompt-rag.ts");
    if (pruned > 0) log(`[8/10] pruned ${pruned} claude prompt-rag UserPromptSubmit hook(s) — prompt-RAG off (default / --no-prompt-rag / kit lacks the file).`);
    else log(`[8/10] claude hook UserPromptSubmit (prompt-rag) — off by default; not installed.`);
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
  // prompt-RAG for Droid: same GLOBAL, per-project-self-gating model as the CC hook. Droid's
  // UserPromptSubmit is CC-compatible, so we install the shared prompt-rag.ts with `--agent droid`
  // (only the pointer's tool name differs). Installed on --prompt-rag (enable); like the CC hook it
  // is intentionally NOT in KIT_HOOK_SUFFIXES, so a plain re-run / --no-prompt-rag / a wire of a
  // different project leaves it untouched (per-project disable is the registry flag, not removal).
  // Its command differs from the CC promptRagCmd (the `--agent droid` suffix), so ensureDroidHook's
  // exact-command dedup keeps the two hooks independent and idempotent.
  if (wantPromptRag) {
    ensureDroidHook("UserPromptSubmit", droidPromptRagCmd);
  } else {
    const prunedRag = pruneHooksTargeting(droidSettings.hooks, "prompt-rag.ts");
    if (prunedRag > 0) log(`[8/10] pruned ${prunedRag} droid prompt-rag UserPromptSubmit hook(s) — prompt-RAG off (default / --no-prompt-rag / kit lacks the file).`);
    else log(`[8/10] droid hook UserPromptSubmit (prompt-rag) — off by default; not installed.`);
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

async function selfSmoke(baseUrl: string, project: string, key: string): Promise<void> {
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
    return;
  }
  const text = await resp.text();
  if (!resp.ok) {
    console.error(`[10/10] self-smoke: HTTP ${resp.status} — ${text}`);
    process.exitCode = 1;
    return;
  }
  let parsed: any = null;
  try {
    parsed = JSON.parse(text);
  } catch {
    /* keep raw */
  }
  if (typeof parsed?.version === "number") {
    log(`[10/10] self-smoke: OK — sessionId=${parsed.sessionId}, version=${parsed.version}, messages=${parsed.messageCount}`);
  } else {
    console.error(`[10/10] self-smoke: server did not return a numeric version — ${text}`);
    process.exitCode = 1;
  }
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
    runDoctor(argv);
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

  const args = parseArgs(argv);
  const dir = resolve(args.dir);
  const project = args.projectKey;
  const baseUrl = DEFAULT_BASE_URL;
  const workspace = args.workspace ?? "stdray";

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
          `  Mint a key from a Claude session on the $system project:\n` +
          `    mcp__petbox__apikey_create  (project='${project}')\n` +
          `  Then re-run with --key <KEY>. (Minting keys is out of scope for wire.ts.)`,
      );
      process.exit(1);
    }
    log(`[2/10] using existing ${envVar} (env or key store).`);
  }

  // 3. validate — BEFORE persisting anything, so a bad key never lands in the stores.
  await validateKey(baseUrl, key, project);

  // 4. persist everywhere agents look: keys.json (kit hooks read it immediately) + a real
  // env var per platform (the per-project MCP configs reference ${envVar}). Idempotent, so
  // re-runs self-heal a machine where only one of the two exists.
  writeKeyToStore(envVar, key);
  log(`[4/10] persisted ${envVar} to ${keysStorePath()}.`);
  persistKeyForAgents(envVar);

  // 5. stable kit copy
  copyKitToStable();

  // 6. registry — carry the per-project prompt-RAG update (undefined = sticky). --prompt-rag writes
  // { enabled:true, +default tolerances }; --no-prompt-rag writes { enabled:false } (keeping any
  // tuned tolerances); neither leaves the project's existing promptRag untouched.
  const promptRagUpdate: PromptRagConfig | undefined =
    args.promptRag === undefined ? undefined : { enabled: args.promptRag };
  upsertRegistry(dir, project, envVar, baseUrl, promptRagUpdate);

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

  // 7c. prompt-RAG audit log (only when ENABLING): the hook's best-effort ingest 404s until
  // `prompt-rag-audit` exists. Idempotent + non-fatal.
  if (args.promptRag === true) {
    await ensurePromptRagAuditLog(baseUrl, project, key);
  }

  // 8. global install — install the global prompt-RAG hook only when ENABLING (--prompt-rag);
  // disable/sticky never add or remove it (it self-gates from the registry).
  installGlobalHooks(args.promptRag === true);

  // 9. cleanup legacy
  if (args.cleanupLegacy) cleanupLegacy(dir);
  else log(`[9/10] cleanup-legacy not requested — skipped.`);

  // 10. self-smoke
  await selfSmoke(baseUrl, project, key);

  if (process.env[envVar]) {
    log(`done.`);
  } else {
    log(
      `done. NOTE: start a NEW terminal${process.platform === "win32" ? "" : " (login shell)"} before launching agents — ` +
        `their MCP configs read ${envVar} from the environment. The kit hooks work immediately (keys.json).`,
    );
  }
}

main().catch((e) => {
  console.error(e?.stack ?? String(e));
  process.exit(1);
});
