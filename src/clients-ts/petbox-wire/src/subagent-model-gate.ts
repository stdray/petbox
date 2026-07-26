// Claude Code PreToolUse hook (global) — the kit's FIRST PreToolUse hook (only SessionStart /
// Stop existed before this). Encodes exactly one rule, decided 2026-07-26 on
// subagent-model-enforcement-hook / orchestrator-rule3-unexecutable:
//
//   subagent_type matches petbox-* (a role compiled by `petbox-wire apply`, whose model is
//   pinned in its own frontmatter) AND the spawn call also passes a `model` parameter
//   → BLOCK. The role's own compiled definition already sets the model; a caller-supplied
//   `model` on top of that is always redundant with the pin and never a legitimate override
//   (there is no "pin, but let the caller pick anyway" case for these roles).
//
// Deliberately NOT built (rejected 2026-07-26, see the card's decision comment): any check on
// native subagent types (general-purpose, Explore, Plan, ...). They carry no pin — the absence
// of `model` on their spawn is accepted, named-in-prose model-parent inheritance, not a defect
// this hook polices. Warning on a native spawn was rejected as noise on every exploration call;
// forbidding native types outright was rejected because it would lose Explore's real read-only
// tools-deny. So: petbox-* + model present is the ONLY branch here, and it takes no judgment —
// a role either has a pin or it doesn't, and the prefix says which, mechanically.
//
// Harness scope: Claude Code ONLY. The Task tool's `model` spawn parameter is the thing being
// gated, and Claude Code is the only harness where that parameter exists and does anything —
// Factory Droid silently ignores a `model` field on its equivalent spawn call, and opencode has
// no such parameter at all. So this hook is not "missing support" for those harnesses; there is
// nothing there for it to gate. It is installed only into ~/.claude/settings.json (see
// installGlobalHooks in wire.ts) — never into ~/.factory/settings.json.
//
// Hook contract (this kit's non-negotiable invariant, same as every other hook here): a silent,
// fast, exit-0 pass-through beats a broken session on ANY unexpected input — no stdin, unparsable
// JSON, a missing field, a tool call that is not a subagent spawn at all. This hook only ever
// speaks up when it has recognized, unambiguously, the ONE pattern it exists to block; everything
// else is a no-op with no stdout at all (so a rebuilt-from-scratch reader of ~/.claude/settings.json
// sees nothing where there is nothing to say).
//
// Deliberately tool_name-agnostic INSIDE this decision function: rather than gate on an exact
// `tool_name` (Claude Code's internal name for the subagent-spawn tool is not a stable public
// contract this kit wants to depend on), the gate looks only at the SHAPE of `tool_input` — does
// it carry a string `subagent_type` and a non-empty string `model`? Any tool call whose input
// lacks that shape (i.e. everything that is not a subagent spawn) is a pass-through by
// construction. wire.ts's registration DOES add a `matcher` (`^(Task|Agent)$`) restricting which
// tool calls even start this process at all — that is a perf optimization (measured ~60ms per
// spawn with no matcher, on every PreToolUse event globally) layered on top, not a second
// decision point: the shape check above is what makes the call correct, the matcher only makes
// it cheap. See wire.ts's installGlobalHooks for the matcher's own stated failure mode (a future
// tool rename would make the matcher silently stop selecting it).
//
// Output format: Claude Code's modern PreToolUse decision channel — exit 0 always (never exit 2:
// a wrongly-shaped stdin must never itself read as "block everything" the way a crash-driven
// exit 2 could), and on the one recognized violation, JSON on stdout:
//   { hookSpecificOutput: { hookEventName: "PreToolUse", permissionDecision: "deny",
//                           permissionDecisionReason: "<message the agent reads>" } }
// permissionDecisionReason is surfaced back to the calling agent as the reason its tool call was
// blocked, so the message must stand on its own — it is the ONLY thing the agent sees.

import { pathToFileURL } from "node:url";

const PETBOX_ROLE_PREFIX = "petbox-";

export type ModelGateDecision =
  | { readonly blocked: false }
  | { readonly blocked: true; readonly reason: string };

// Pure, exported for unit testing. `rawInput` is whatever JSON.parse produced from stdin (or
// anything else a caller hands it) — untyped and untrusted by construction.
export function evaluateModelGate(rawInput: unknown): ModelGateDecision {
  if (!rawInput || typeof rawInput !== "object") return { blocked: false };
  const toolInput = (rawInput as { tool_input?: unknown }).tool_input;
  if (!toolInput || typeof toolInput !== "object") return { blocked: false };
  const t = toolInput as { subagent_type?: unknown; model?: unknown };

  const subagentType = typeof t.subagent_type === "string" ? t.subagent_type.trim() : "";
  if (!subagentType.startsWith(PETBOX_ROLE_PREFIX)) return { blocked: false };

  const model = typeof t.model === "string" ? t.model.trim() : "";
  if (!model) return { blocked: false };

  return {
    blocked: true,
    reason:
      `subagent_type "${subagentType}" is a PetBox role — its model is already pinned in the ` +
      `role's compiled frontmatter (petbox-wire apply). Remove the \`model\` parameter from this ` +
      `spawn call; passing one here can only fight the role's own pin, never legitimately ` +
      `override it.`,
  };
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

// Same Windows-pipe-flush concern as pull-memory.ts's writeStdout: process.stdout.write() can
// return before the OS-level write completes, so main() awaits the callback before letting the
// process exit — otherwise a fast exit could truncate the JSON decision.
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
    decision = evaluateModelGate(parsed);
  } catch {
    // Unparsable/absent stdin, or any other surprise: the hook invariant is silent pass-through,
    // never a broken session over a shape it didn't expect.
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
  // Pass-through: no stdout at all — "no decision" is the documented way to let Claude Code's
  // normal permission flow continue untouched.
}

// Run main() ONLY when this file is the process entrypoint (`node .../subagent-model-gate.ts`),
// never on import. Without this guard the module is both a library and an entrypoint: importing
// evaluateModelGate for a unit test starts main(), which waits on a stdin that a test runner never
// closes, and the importing file hangs FOREVER — not a slow test, a wedged process. That cost two
// wedged gate runs (~2h) and would have hung `TsWireTest` in CI on every push, since the kit's
// tests are a required dependency of SdkChecks. wire.ts has the same shape and is the reason
// status.ts may not import it; this file is importable on purpose, so it must not self-execute.
const invokedDirectly =
  process.argv[1] !== undefined && import.meta.url === pathToFileURL(process.argv[1]).href;

if (invokedDirectly) {
  main()
    .then(() => process.exit(0))
    .catch(() => process.exit(0)); // the invariant applies to main() itself, not just its try/catch
}
