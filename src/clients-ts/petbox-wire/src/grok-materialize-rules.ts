// Grok Build SessionStart hook — TEMP workaround for "passive hooks: stdout is ignored"
// (Grok docs 10-hooks.md). Claude Code injects pull-memory stdout into context; Grok does not.
//
// Instead: materialize protocol + canon into a file Grok DOES load as project rules:
//   <registered-prefix>/.grok/rules/petbox-session.local.md  (gitignored)
// and ensure a static bootstrap sibling exists (petbox-memory.md) for cold clones.
//
// Best-effort, never blocks — always exit 0. Not prompt-RAG.
// intake: grok-sessionstart-stdout-ignored · work: grok-session-rules-temp

import { mkdirSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { fetchCanonBlock } from "./canon.ts";
import { buildProtocol, grokPetboxTool } from "./protocol.ts";
import { resolveProject } from "./registry.ts";
import { writeGrokBootstrapRules } from "./grok-rules-static.ts";

type HookInput = {
  cwd?: string;
  workspaceRoot?: string;
  source?: string;
};

const LOCAL_RULES = "petbox-session.local.md";

function readStdin(): Promise<string> {
  return new Promise((resolve) => {
    let buf = "";
    process.stdin.setEncoding("utf8");
    process.stdin.on("data", (c) => (buf += c));
    process.stdin.on("end", () => resolve(buf));
    process.stdin.on("error", () => resolve(buf));
  });
}

function resolveCwd(j: HookInput): string {
  if (typeof j.cwd === "string" && j.cwd.trim()) return j.cwd.trim();
  if (typeof j.workspaceRoot === "string" && j.workspaceRoot.trim()) return j.workspaceRoot.trim();
  const env =
    process.env.GROK_WORKSPACE_ROOT ||
    process.env.CLAUDE_PROJECT_DIR ||
    process.env.GROK_PROJECT_DIR ||
    "";
  return typeof env === "string" ? env : "";
}

async function main(): Promise<void> {
  let source = "startup";
  let cwd = "";
  try {
    const raw = await readStdin();
    const j: HookInput = raw.trim() ? JSON.parse(raw) : {};
    if (typeof j.source === "string" && j.source.trim()) source = j.source.trim();
    cwd = resolveCwd(j);
  } catch {
    cwd = resolveCwd({});
  }

  try {
    const resolved = resolveProject(cwd);
    if (!resolved) return;

    // Static bootstrap (self-intro contract) — idempotent overwrite keeps tool names in sync.
    writeGrokBootstrapRules(resolved.prefix, resolved.project);

    let out = buildProtocol(resolved.project, grokPetboxTool, { source });
    out += `\n\n---\n\n> **Grok Build channel:** SessionStart stdout is ignored by Grok; this file is the temp inject path (\`.grok/rules/${LOCAL_RULES}\`). Refreshed each SessionStart. See intake \`grok-sessionstart-stdout-ignored\`.\n`;

    const canon = await fetchCanonBlock(resolved);
    if (canon) out += `\n\n${canon}`;

    const rulesDir = join(resolved.prefix, ".grok", "rules");
    mkdirSync(rulesDir, { recursive: true });
    writeFileSync(join(rulesDir, LOCAL_RULES), out, "utf8");
  } catch {
    // best-effort
  }
}

main().finally(() => process.exit(0));
