// Claude Code SessionStart hook (global) — port of pull-memory.ps1.
//
// Injects the PetBox memory protocol so the agent recalls relevant memory at session start
// and captures learnings as it works, via the already-connected petbox MCP (native memory.*
// tools). Stdout is added to the session context by Claude Code.
//
// The project is resolved from cwd via the shared registry; if the cwd is not a registered
// project this prints nothing and exits 0. Best-effort, never blocks — always exit 0.

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

function protocol(project: string, source: string): string {
  let out = `## PetBox memory

This project is wired to PetBox (project \`${project}\`) over the connected \`petbox\` MCP.

In your FIRST response this session, open with exactly this line (so it's visible the protocol is active):
\`🧠 PetBox memory active\`

PetBox remembers a LOT about this project — the curated facts AND the full session history (imported + auto-pushed). Start reasoning about anything past from a SEARCH, not from assumption.

**Rule — search before you (re)work:** before re-deriving, re-investigating, or re-deciding ANYTHING about this project's past, run \`mcp__petbox__memory_search\` FIRST — redoing work the project already remembers is the failure mode this protocol exists to prevent. And before you store a new fact, \`mcp__petbox__memory_search\` for an existing one and edit that instead (duplicates poison recall).

The entry point has two legs:

- **Facts — \`mcp__petbox__memory_search\`**: a \`q\` of a few words you are confident appear (tokens are ANDed, prefix-matched; wordforms stem), pass \`bodyLen\` (e.g. 240) for cheap snippets. With no \`scope\` it cascades project ⊕ workspace and EVERY store — curated notes and the machine-distilled \`autocaptured\` quarantine alike (the store label in each hit tells you which). Without \`q\` it's a plain listing (freshest first). Pull a full body with \`mcp__petbox__memory_get\`.
- **Past conversations — \`mcp__petbox__session_search\`**: when you need HOW something was decided, an error text, or any detail a fact wouldn't carry — two-stage search over the whole session archive; every hit carries the message ordinal, so \`mcp__petbox__session_get\` jumps to the verbatim source.

As you work, **capture** incrementally (don't wait for session end): after a decision, a fixed bug, a discovered pattern, or a stated preference, store a concise fact via \`mcp__petbox__memory_remember\` (\`text\` = the learning; \`type\` = User|Feedback|Project|Reference; \`scope\` = workspace for facts that span projects or are about the user, else omit for this project). Curated/temporal edits go through \`mcp__petbox__memory_upsert\`.

**Background autocapture is LIVE:** after a session settles (~minutes), the server distills durable facts and recurring behavior patterns from it into the \`autocaptured\` store on its own. So: (1) don't re-store what memory_search already shows as autocaptured — promote-worthy entries are the owner's call; (2) the **end-of-session sweep** is now an INSURANCE pass, not the only capture: before you stop, store the 1-3 learnings that must not wait for background distillation (a key decision + why, a root cause, a gotcha) — skip narration and anything derivable from code/git.`;

  if (source === "resume" || source === "compact") {
    out += `\n\nSession ${source} — also recall recent session/decision memories to pick up where you left off.`;
  }
  return out;
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
    process.stdout.write(protocol(resolved.project, source));
  } catch {
    // best-effort
  }
}

main().finally(() => process.exit(0));
