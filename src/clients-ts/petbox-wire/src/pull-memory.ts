// Claude Code SessionStart hook (global) — port of pull-memory.ps1.
//
// Injects the PetBox memory protocol so the agent recalls relevant memory at session start
// and captures learnings as it works, via the already-connected petbox MCP (native memory.*
// tools). Stdout is added to the session context by Claude Code.
//
// The project is resolved from cwd via the shared registry; if the cwd is not a registered
// project this prints nothing and exits 0. Best-effort, never blocks — always exit 0.

import { fetchCanonBlock } from "./canon.ts";
import { buildProtocol, mcpPetboxTool } from "./protocol.ts";
import { resolveProject } from "./registry.ts";

type HookInput = { cwd?: string; source?: string };

function readStdin(): Promise<string> {
  return new Promise((resolve) => {
    let buf = "";
    process.stdin.setEncoding("utf8");
    process.stdin.on("data", (c) => (buf += c));
    process.stdin.on("end", () => resolve(buf));
    process.stdin.on("error", () => resolve(buf));
  });
}

async function main(): Promise<void> {
  let source = "startup";
  let cwd = "";
  try {
    const raw = await readStdin();
    const j: HookInput = JSON.parse(raw);
    if (typeof j.source === "string" && j.source.trim()) source = j.source.trim();
    if (typeof j.cwd === "string") cwd = j.cwd;
  } catch {
    // fall through with defaults; cwd stays empty → resolves to null below
  }

  try {
    const resolved = resolveProject(cwd);
    if (!resolved) return; // not a registered project → no output
    let out = buildProtocol(resolved.project, mcpPetboxTool, { source, harness: "claude-code" });
    // Append the curated memory canon when available (best-effort; degrades to nothing).
    const canon = await fetchCanonBlock(resolved);
    if (canon) out += `\n\n${canon}`;
    process.stdout.write(out);
  } catch {
    // best-effort
  }
}

main().finally(() => process.exit(0));
