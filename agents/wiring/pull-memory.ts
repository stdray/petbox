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

Before substantive work, **recall**: call \`mcp__petbox__memory_recall\` with a \`query\` of a few words you are confident appear (tokens are ANDed, prefix-matched), and pass \`bodyLen\` (e.g. 240) so hits come back as a \`description\` + a short snippet rather than full bodies — that keeps session start cheap. With no \`scope\` it cascades project ⊕ workspace; hits come back labelled by scope (project = this project, workspace = cross-project shared). Skim them for relevant past decisions, conventions, gotchas; when a hit looks relevant, pull its full body with \`mcp__petbox__memory_get\`.

As you work, **capture** incrementally (don't wait for session end): after a decision, a fixed bug, a discovered pattern, or a stated preference, store a concise fact via \`mcp__petbox__memory_remember\` (\`text\` = the learning; \`type\` = User|Feedback|Project|Reference; \`scope\` = workspace for facts that span projects or are about the user, else omit for this project). Aim for 1-3 memories per substantial interaction. Curated/temporal edits go through \`mcp__petbox__memory_upsert\`.

**End-of-session sweep (prompt-driven autocapture):** when the work concludes — you've answered the request, are wrapping up, or the user signals done — do ONE final pass before you stop: name the 1-3 most durable learnings from this session (a decision + its why, a fixed bug's root cause, a discovered convention/gotcha, a stated preference) that you have NOT already stored, and \`memory_remember\` each. Skip raw narration and anything derivable from code/git — only facts worth recalling next time. If nothing qualifies, capture nothing. (This is the manual stand-in for hook→distill autocapture, which is blocked on the llm-router module — see idea \`memory/autocapture\`.)`;

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
