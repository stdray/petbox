// Codex CLI PreToolUse hook (global) — the codex port of subagent-model-gate.ts. Same policy,
// no second decision: this file translates codex's PreToolUse shape into the shape
// evaluateModelGate already understands and calls that SAME exported function — see
// subagent-model-gate.ts's own header for the rule (petbox-* role + an explicit spawn-time
// `model` → BLOCK) and why it exists.
//
// Codex's spawn-tool shape differs from Claude Code's Task/Agent tool in field NAMES, not in
// the concept: `tool_name` is "spawn_agent" (codex-spec.md §3/§4), and the role identifier
// argument is `agent_type` (multi_agents_v2/spawn.rs's `SpawnAgentArgs.agent_type:
// Option<String>`) — Claude Code's equivalent field is `subagent_type`. The requested subagent
// model is at `tool_input.model` on that PreToolUse, exactly as for Claude Code, and equally
// optional (codex-spec.md §3: "hook_runtime.rs:203; SpawnAgentArgs.model: Option<String>" — NOT
// the payload's top-level `model`, which is the CURRENT TURN's model).
//
// This gate is deliberately tool_name-agnostic at the decision level too (same reasoning as
// subagent-model-gate.ts): rather than gate on tool_name, it renames `agent_type` to
// `subagent_type` in the raw input and hands the WHOLE thing to evaluateModelGate, which then
// applies its own shape check unchanged. A codex tool call whose input lacks an `agent_type`
// string is a pass-through by construction, same as Claude Code's shape guard.
//
// Output format: codex's PreToolUse deny (codex-spec.md §3, schema.rs:131-140) — the SAME key
// shape Claude Code uses:
//   { hookSpecificOutput: { hookEventName: "PreToolUse", permissionDecision: "deny",
//                           permissionDecisionReason: "<message the agent reads>" } }
// Pass-through (no violation recognized) emits nothing on stdout — codex's documented way to
// let its own permission flow continue untouched, same as Claude Code's.

import { pathToFileURL } from "node:url";
import { evaluateModelGate, type ModelGateDecision } from "./subagent-model-gate.ts";

const CODEX_SPAWN_TOOL_NAME = "spawn_agent";

/** Pure, exported for unit testing. Translates codex's `agent_type` field to the
 * `subagent_type` shape evaluateModelGate expects, then defers to it entirely — no second
 * policy. */
export function evaluateCodexModelGate(rawInput: unknown): ModelGateDecision {
  if (!rawInput || typeof rawInput !== "object") return { blocked: false };
  const toolInput = (rawInput as { tool_input?: unknown }).tool_input;
  if (!toolInput || typeof toolInput !== "object") return { blocked: false };
  const t = toolInput as { agent_type?: unknown; model?: unknown };
  return evaluateModelGate({
    tool_input: { subagent_type: t.agent_type, model: t.model },
  });
}

function readStdin(): Promise<string> {
  return new Promise((resolve) => {
    let buf = "";
    process.stdin.setEncoding("utf8");
    process.stdin.on("data", (c) => (buf += c));
    process.stdin.on("end", () => resolve(buf));
    process.stdin.on("error", () => resolve(buf));
  });
}

// Same Windows-pipe-flush concern as subagent-model-gate.ts's writeStdout — see that file for
// the full rationale.
function writeStdout(text: string): Promise<void> {
  return new Promise((resolve) => {
    if (text.length === 0) {
      resolve();
      return;
    }
    process.stdout.write(text, () => resolve());
  });
}

async function main(): Promise<void> {
  let decision: ModelGateDecision = { blocked: false };
  try {
    const raw = await readStdin();
    const parsed = raw.trim().length > 0 ? JSON.parse(raw) : null;
    decision = evaluateCodexModelGate(parsed);
  } catch {
    decision = { blocked: false };
  }

  if (decision.blocked) {
    await writeStdout(
      JSON.stringify({
        hookSpecificOutput: {
          hookEventName: "PreToolUse",
          permissionDecision: "deny",
          permissionDecisionReason: decision.reason,
        },
      }),
    );
  }
}

// Reference kept so a future reader can see which tool name this hook is matcher-scoped to at
// install time (wire.ts's installGlobalHooks) without cross-referencing that file.
export { CODEX_SPAWN_TOOL_NAME };

// Same not-on-import guard as subagent-model-gate.ts, for the identical reason: importing
// evaluateCodexModelGate for a unit test must never start main() and hang on a stdin nothing
// closes.
const invokedDirectly =
  process.argv[1] !== undefined && import.meta.url === pathToFileURL(process.argv[1]).href;

if (invokedDirectly) {
  // Same hard-exit reasoning as subagent-model-gate.ts's own invokedDirectly block: no network
  // call in this file, and it sits on the hot path of every gated tool call — a guaranteed-
  // immediate exit is the safer end, and truncation is already excluded by writeStdout awaiting
  // the write callback before main() resolves.
  main()
    .then(() => process.exit(0))
    .catch(() => process.exit(0));
}
