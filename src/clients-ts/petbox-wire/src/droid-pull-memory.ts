// Factory Droid SessionStart hook (global) — the droid port of pull-memory.ts.
//
// Injects the PetBox memory protocol + curated canon so the agent recalls relevant memory at
// session start and captures learnings as it works, via the already-connected petbox MCP.
//
// Droid names MCP tools `<server>___<tool>` (triple underscore) at runtime — observed live
// in exec mode; the docs' `mcp__<server>__<tool>` form did not match. The protocol renders
// with droidPetboxTool (`petbox___*`); the canon block stays byte-identical across agents. Droid's SessionStart stdin
// is snake_case (`session_id`, `transcript_path`, `cwd`, `source`), the same shape Claude Code
// uses, so we resolve the project from `cwd` and pass `source` through for the resume nudge.
//
// Output contract (docs): a SessionStart hook returns context to the model via the structured
// JSON `{ hookSpecificOutput: { hookEventName: "SessionStart", additionalContext } }` on stdout
// (stdout-as-context is also accepted, but the structured form is the documented preference).
//
// Best-effort, always exit 0, no output for an unregistered cwd.

import { fetchCanonBlock } from "./canon.ts";
import { buildProtocol, droidPetboxTool } from "./protocol.ts";
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
    let context = buildProtocol(resolved.project, droidPetboxTool, { source, harness: "droid" });
    // Append the curated memory canon when available (best-effort; degrades to nothing).
    const canon = await fetchCanonBlock(resolved);
    if (canon) context += `\n\n${canon}`;
    const out = {
      hookSpecificOutput: {
        hookEventName: "SessionStart",
        additionalContext: context,
      },
    };
    process.stdout.write(JSON.stringify(out));
  } catch {
    // best-effort
  }
}

main().finally(() => process.exit(0));
