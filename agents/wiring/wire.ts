// Bootstrap CLI for the global agent-wiring kit.
//
//   node wire.ts <dir> <projectKey> [--env VAR] [--key KEY] [--workspace WS] [--cleanup-legacy]
//
// Idempotently wires a project to PetBox:
//   1. derive the env-var name for the API key
//   2. obtain the key (--key, else user-scope env)            — minting keys is OUT OF SCOPE
//   3. (--key) persist it to user-scope env
//   4. validate the key against /api/auth/validate
//   5. upsert the registry entry (prefix → project, envVar)
//   6. (re)generate per-project config files:
//        - .mcp.json                         (Claude Code MCP)
//        - .opencode/opencode.json           (opencode MCP)
//        - .factory/mcp.json                 (Factory Droid MCP — idempotent merge)
//        - .claude/skills/petbox/SKILL.md    (Claude Code skill; opencode reads it via its
//                                             Claude-compatible skills discovery path)
//        - .factory/skills/petbox/SKILL.md   (Factory Droid skill)
//   7. install the global Claude + Droid hooks + opencode plugin (merge, never clobber live files)
//   8. (--cleanup-legacy) remove the project's old per-project hook/plugin copies
//   9. self-smoke: POST a tiny session and assert the server applied it
//
// Unlike the hooks, this is a CLI: step failures surface loudly (no silent swallow).

import { execFileSync } from "node:child_process";
import { existsSync, mkdirSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { homedir } from "node:os";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const DEFAULT_BASE_URL = "https://petbox.3po.su";
const HERE = dirname(fileURLToPath(import.meta.url));

// ---- arg parsing -----------------------------------------------------------

type Args = {
  dir: string;
  projectKey: string;
  env?: string;
  key?: string;
  workspace?: string;
  cleanupLegacy: boolean;
};

function usage(): never {
  console.error(
    "usage: node wire.ts <dir> <projectKey> [--env VAR] [--key KEY] [--workspace WS] [--cleanup-legacy]",
  );
  process.exit(2);
}

function parseArgs(argv: string[]): Args {
  const positionals: string[] = [];
  let env: string | undefined;
  let key: string | undefined;
  let workspace: string | undefined;
  let cleanupLegacy = false;
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    if (a === "--env") env = argv[++i];
    else if (a === "--key") key = argv[++i];
    else if (a === "--workspace") workspace = argv[++i];
    else if (a === "--cleanup-legacy") cleanupLegacy = true;
    else if (a.startsWith("--")) {
      console.error(`unknown flag: ${a}`);
      usage();
    } else positionals.push(a);
  }
  if (positionals.length < 2) usage();
  return { dir: positionals[0], projectKey: positionals[1], env, key, workspace, cleanupLegacy };
}

// ---- small helpers ---------------------------------------------------------

const log = (msg: string) => console.log(msg);

function deriveEnvVar(projectKey: string): string {
  return projectKey.toUpperCase().replace(/[^A-Z0-9]/g, "_") + "_API_KEY";
}

// Read/write a user-scope environment variable via PowerShell (the current process may not
// have it). Returns "" if unset.
function getUserEnv(name: string): string {
  try {
    const out = execFileSync(
      "powershell",
      [
        "-NoProfile",
        "-NonInteractive",
        "-Command",
        `[Environment]::GetEnvironmentVariable('${name}','User')`,
      ],
      { encoding: "utf8" },
    );
    return out.trim();
  } catch {
    return "";
  }
}

function setUserEnv(name: string, value: string): void {
  execFileSync(
    "powershell",
    [
      "-NoProfile",
      "-NonInteractive",
      "-Command",
      `[Environment]::SetEnvironmentVariable('${name}', $env:WIRE_KEY_VALUE, 'User')`,
    ],
    { encoding: "utf8", env: { ...process.env, WIRE_KEY_VALUE: value } },
  );
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
    console.error(`[4/9] validate: could not reach ${uri} — ${(e as Error).message}. Aborting.`);
    process.exit(1);
  }

  if (resp.status === 401) {
    console.error(`[4/9] validate: server rejected the API key (401). Aborting.`);
    process.exit(1);
  }
  if (!resp.ok) {
    // Non-standard / endpoint missing → warn and continue.
    log(`[4/9] validate: unexpected status ${resp.status} (endpoint missing?); continuing with a warning.`);
    return;
  }
  let body: any = null;
  try {
    body = await resp.json();
  } catch {
    log(`[4/9] validate: 200 but non-JSON body; continuing with a warning.`);
    return;
  }
  // Contract (AuthApi.cs): 200 => { project, scopes } (camelCase, ASP.NET web defaults).
  const proj = body?.project ?? body?.Project;
  if (typeof proj === "string" && proj.length > 0) {
    if (proj !== projectKey) {
      console.error(
        `[4/9] validate: key belongs to project '${proj}', not '${projectKey}'. Aborting.`,
      );
      process.exit(1);
    }
    log(`[4/9] validate: OK — key scoped to '${proj}'.`);
  } else {
    log(`[4/9] validate: 200 without a project field; continuing with a warning.`);
  }
}

// ---- step 5: registry ------------------------------------------------------

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
  log(`[5/9] registry: upserted ${prefix} → ${project} (${envVar}) in ${path}`);
}

// ---- step 6: per-project files --------------------------------------------

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
  log(`[6/9] wrote ${join(dir, ".mcp.json")}`);

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
  log(`[6/9] wrote ${join(dir, ".opencode", "opencode.json")}`);

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
  log(`[6/9] merged petbox MCP server into ${droidMcpPath}`);

  // SKILL.md — render once from the template, then drop a copy into every native skill surface.
  const tpl = readFileSync(join(HERE, "templates", "SKILL.md"), "utf8");
  const skill = tpl
    .replace(/\{\{PROJECT\}\}/g, project)
    .replace(/\{\{WORKSPACE\}\}/g, workspace);
  for (const surface of SKILL_SURFACES) {
    const skillPath = join(dir, ...surface, "petbox", "SKILL.md");
    mkdirSync(dirname(skillPath), { recursive: true });
    writeFileSync(skillPath, skill, "utf8");
    log(`[6/9] wrote ${skillPath}`);
  }
}

// ---- step 7: global install ------------------------------------------------

function installGlobalHooks(): void {
  const pushPath = join(HERE, "push-session.ts");
  const pullPath = join(HERE, "pull-memory.ts");
  const pushCmd = `node "${pushPath}"`;
  const pullCmd = `node "${pullPath}"`;

  const settingsPath = join(homedir(), ".claude", "settings.json");
  const settings = readJson(settingsPath) ?? {};
  if (!settings.hooks || typeof settings.hooks !== "object") settings.hooks = {};

  // Claude Code hooks shape: settings.hooks[event] = [{ matcher?, hooks: [{type, command}] }]
  const ensureHook = (event: string, command: string) => {
    const groups: any[] = Array.isArray(settings.hooks[event]) ? settings.hooks[event] : [];
    const already = groups.some(
      (g) => Array.isArray(g?.hooks) && g.hooks.some((h: any) => h?.command === command),
    );
    if (already) {
      log(`[7/9] claude hook ${event} already present — skipped.`);
      return;
    }
    groups.push({ hooks: [{ type: "command", command }] });
    settings.hooks[event] = groups;
    log(`[7/9] claude hook ${event} added.`);
  };

  ensureHook("Stop", pushCmd);
  ensureHook("SessionStart", pullCmd);
  writeJson(settingsPath, settings);
  log(`[7/9] merged hooks into ${settingsPath}`);

  // Factory Droid hooks: same JSON shape as Claude Code, merged into ~/.factory/settings.json
  // under the `hooks` key (a documented fallback location). Droid exposes petbox tools as
  // `mcp__petbox__*` and delivers Claude-Code-compatible snake_case payloads, so it reuses the
  // shared protocol/append flow via its own thin hooks. No `enableHooks` flag is set: the droid
  // hooks reference does not document one gating hook execution.
  const droidPushPath = join(HERE, "droid-push-session.ts");
  const droidPullPath = join(HERE, "droid-pull-memory.ts");
  const droidPushCmd = `node "${droidPushPath}"`;
  const droidPullCmd = `node "${droidPullPath}"`;

  const droidSettingsPath = join(homedir(), ".factory", "settings.json");
  const droidSettings = readJson(droidSettingsPath) ?? {};
  if (!droidSettings.hooks || typeof droidSettings.hooks !== "object") droidSettings.hooks = {};

  const ensureDroidHook = (event: string, command: string) => {
    const groups: any[] = Array.isArray(droidSettings.hooks[event]) ? droidSettings.hooks[event] : [];
    const already = groups.some(
      (g) => Array.isArray(g?.hooks) && g.hooks.some((h: any) => h?.command === command),
    );
    if (already) {
      log(`[7/9] droid hook ${event} already present — skipped.`);
      return;
    }
    groups.push({ hooks: [{ type: "command", command }] });
    droidSettings.hooks[event] = groups;
    log(`[7/9] droid hook ${event} added.`);
  };

  ensureDroidHook("Stop", droidPushCmd);
  ensureDroidHook("SessionStart", droidPullCmd);
  writeJson(droidSettingsPath, droidSettings);
  log(`[7/9] merged droid hooks into ${droidSettingsPath}`);

  // Global opencode plugin: thin shim re-exporting the kit plugin from an absolute file URL.
  const pluginAbs = join(HERE, "opencode-plugin.ts");
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
  log(`[7/9] wrote global opencode plugin shim ${shimPath} → ${pluginUrl}`);
}

// ---- step 8: cleanup legacy ------------------------------------------------

function cleanupLegacy(dir: string): void {
  // .claude/hooks/ — drop the whole per-project hooks folder.
  const hooksDir = join(dir, ".claude", "hooks");
  if (existsSync(hooksDir)) {
    rmSync(hooksDir, { recursive: true, force: true });
    log(`[8/9] removed ${hooksDir}`);
  }

  // .claude/settings.local.json — drop ONLY the hooks key, keep permissions etc.
  const localPath = join(dir, ".claude", "settings.local.json");
  const local = readJson(localPath);
  if (local && typeof local === "object" && "hooks" in local) {
    delete local.hooks;
    writeJson(localPath, local);
    log(`[8/9] removed 'hooks' key from ${localPath}`);
  }

  // .opencode/plugin/ — drop the per-project plugin folder.
  const pluginDir = join(dir, ".opencode", "plugin");
  if (existsSync(pluginDir)) {
    rmSync(pluginDir, { recursive: true, force: true });
    log(`[8/9] removed ${pluginDir}`);
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
          log(`[8/9] removed ${p}`);
        }
      }
    } else {
      log(`[8/9] kept .opencode deps — package.json has non-plugin deps: ${keys.join(", ")}`);
    }
  }
}

// ---- step 9: self-smoke ----------------------------------------------------

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
    console.error(`[9/9] self-smoke: POST failed — ${(e as Error).message}`);
    process.exitCode = 1;
    return;
  }
  const text = await resp.text();
  if (!resp.ok) {
    console.error(`[9/9] self-smoke: HTTP ${resp.status} — ${text}`);
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
    log(`[9/9] self-smoke: OK — sessionId=${parsed.sessionId}, version=${parsed.version}, messages=${parsed.messageCount}`);
  } else {
    console.error(`[9/9] self-smoke: server did not return a numeric version — ${text}`);
    process.exitCode = 1;
  }
}

// ---- main ------------------------------------------------------------------

async function main(): Promise<void> {
  const args = parseArgs(process.argv.slice(2));
  const dir = resolve(args.dir);
  const project = args.projectKey;
  const baseUrl = DEFAULT_BASE_URL;
  const workspace = args.workspace ?? "stdray";

  if (!existsSync(dir)) {
    console.error(`directory does not exist: ${dir}`);
    process.exit(1);
  }

  // 1. env var
  const envVar = args.env ?? deriveEnvVar(project);
  log(`[1/9] envVar = ${envVar}`);

  // 2/3. key
  let key = args.key;
  if (key) {
    setUserEnv(envVar, key);
    log(`[3/9] persisted ${envVar} to user-scope env. NOTE: visible only to NEW terminals.`);
  } else {
    key = getUserEnv(envVar) || process.env[envVar] || "";
    if (!key) {
      console.error(
        `[2/9] no API key found.\n` +
          `  Provide one with --key <KEY>, or set ${envVar} at user scope first.\n` +
          `  Mint a key from a Claude session on the $system project:\n` +
          `    mcp__petbox__apikey_create  (project='${project}')\n` +
          `  Then re-run with --key <KEY>. (Minting keys is out of scope for wire.ts.)`,
      );
      process.exit(1);
    }
    log(`[2/9] using existing user-scope ${envVar}.`);
  }

  // 4. validate
  await validateKey(baseUrl, key, project);

  // 5. registry
  upsertRegistry(dir, project, envVar, baseUrl);

  // 6. project files
  writeProjectFiles(dir, project, envVar, workspace);

  // 7. global install
  installGlobalHooks();

  // 8. cleanup legacy
  if (args.cleanupLegacy) cleanupLegacy(dir);
  else log(`[8/9] cleanup-legacy not requested — skipped.`);

  // 9. self-smoke
  await selfSmoke(baseUrl, project, key);

  log(`done. Restart your terminal so the user-scope env var is visible to new sessions.`);
}

main().catch((e) => {
  console.error(e?.stack ?? String(e));
  process.exit(1);
});
