/**
 * PetBox plugin for opencode (GLOBAL) — the opencode port of the two Claude Code hooks:
 *
 *   1. pull-memory  (SessionStart) → inject the PetBox memory protocol so the agent recalls
 *      relevant memory and captures learnings via the connected `petbox` MCP. Appended to the
 *      system prompt via `experimental.chat.system.transform`.
 *
 *   2. push-session (Stop) → mirror the session conversation into PetBox's Session module so it
 *      auto-populates. Fires on `session.idle` (opencode's "the turn finished") and pushes the
 *      INCREMENT via the server-authoritative append cursor (see append.ts) — the plugin is
 *      long-lived, so it remembers each session's lastOrdinal from the previous response in
 *      process memory (no durable state); a restart self-heals off the structured 409 gap
 *      reject, and old servers without the append route fall back to the full-snapshot push.
 *
 * Unlike the per-project copy this is installed once at user scope. The active project + API
 * key + base URL are resolved from `directory` (PluginInput) via the shared registry. If the
 * cwd is not a registered project (or the key is missing) BOTH hooks are no-ops — but the
 * plugin still loads cleanly in every project.
 *
 * Both hooks are best-effort and must never break a turn (every failure is swallowed).
 *
 * MCP note: opencode exposes MCP tools as `<server>_<tool>`, so the petbox memory verbs are
 * `petbox_memory_recall` / `petbox_memory_remember` / `petbox_memory_get` /
 * `petbox_memory_upsert` (the Claude `mcp__petbox__*` names do not apply here).
 */
import type { Plugin } from "@opencode-ai/plugin";
import { pushTranscript } from "./append.ts";
import { resolveProject } from "./registry.ts";

function memoryProtocol(project: string): string {
  return `## PetBox memory

This project is wired to a PetBox instance over the \`petbox\` MCP server (project
\`${project}\`). Its memory verbs are exposed as \`petbox_memory_recall\` /
\`petbox_memory_remember\` / \`petbox_memory_get\` / \`petbox_memory_upsert\`.

In your FIRST response this session, open with exactly this line (so it's visible the
protocol is active):
\`🧠 PetBox memory active\`

PetBox remembers a LOT about this project — curated facts AND the full session history.
Start reasoning about anything past from a SEARCH, not from assumption. Two legs:

- **Facts — \`petbox_memory_recall\`**: a \`query\` of a few words you are confident appear
  (tokens ANDed, prefix-matched; wordforms stem), pass \`bodyLen\` (e.g. 240) for cheap
  snippets. With no \`scope\` it cascades project ⊕ workspace and EVERY store — curated
  notes and the machine-distilled \`autocaptured\` quarantine alike (the store label in each
  hit tells you which). Pull a full body with \`petbox_memory_get\`.
- **Past conversations — \`petbox_session_search\`**: when you need HOW something was decided,
  an error text, or any detail a fact wouldn't carry — two-stage search over the whole
  session archive; every hit carries the message ordinal, so \`petbox_session_get\` jumps to
  the verbatim source.

As you work, **capture** incrementally (don't wait for session end): after a decision, a
fixed bug, a discovered pattern, or a stated preference, store a concise fact via
\`petbox_memory_remember\` (\`text\` = the learning; \`type\` = User|Feedback|Project|Reference;
\`scope\` = workspace for facts that span projects or are about the user, else omit for this
project). Curated/temporal edits go through \`petbox_memory_upsert\`.

**Background autocapture is LIVE:** after a session settles (~minutes), the server distills
durable facts and recurring behavior patterns into the \`autocaptured\` store on its own. So:
(1) don't re-store what recall already shows as autocaptured — promotion is the owner's
call; (2) the **end-of-session sweep** is an INSURANCE pass, not the only capture: before
you stop, store the 1-3 learnings that must not wait for background distillation — skip
narration and anything derivable from code/git.`;
}

export const PetboxPlugin: Plugin = async ({ client, directory }) => {
  // Resolve the active project once at load. null → both hooks no-op.
  const resolved = resolveProject(directory ?? "");

  // Avoid re-POSTing the same state when session.idle fires repeatedly.
  const lastPushed = new Map<string, string>();
  // Per-session server cursor (lastOrdinal from the previous response). Process memory only —
  // a plugin restart just means the first push self-heals via the structured gap reject.
  const cursors = new Map<string, number>();

  async function pushSession(sessionID: string): Promise<void> {
    if (!resolved || !sessionID) return;

    const res = await client.session.messages({ path: { id: sessionID } });
    const messages = res.data;
    if (!Array.isArray(messages) || messages.length === 0) return;

    // The whole conversation (user + assistant text turns), ordered — pushTranscript sends
    // only the tail past the remembered server cursor (the increment), not the full history.
    const msgs = messages
      .map((m: any) => {
        const text = m.parts
          .filter((p: any) => p.type === "text" && typeof p.text === "string")
          .map((p: any) => p.text)
          .join("\n")
          .trim();
        return text ? { role: m.info.role, content: text } : null;
      })
      .filter(Boolean) as { role: string; content: string }[];
    if (msgs.length === 0) return;
    const lastID = messages[messages.length - 1]?.info?.id ?? "";
    if (lastPushed.get(sessionID) === lastID) return;

    const lastOrdinal = await pushTranscript(
      {
        baseUrl: resolved.baseUrl,
        project: resolved.project,
        sessionId: sessionID,
        apiKey: resolved.apiKey,
        agent: "opencode",
        timeoutMs: 8000,
      },
      msgs,
      cursors.get(sessionID) ?? null,
    );
    if (lastOrdinal !== null) {
      cursors.set(sessionID, lastOrdinal);
      lastPushed.set(sessionID, lastID);
    }
  }

  return {
    // Port of pull-memory — make the memory protocol part of the system prompt.
    "experimental.chat.system.transform": async (_input, output) => {
      if (!resolved) return;
      output.system.push(memoryProtocol(resolved.project));
    },

    // Port of push-session — mirror the finished turn into PetBox's Session module.
    event: async ({ event }) => {
      if (event.type !== "session.idle") return;
      const sessionID = (event as any).properties?.sessionID;
      try {
        await pushSession(sessionID);
      } catch {
        /* best-effort: never break the turn */
      }
    },
  };
};

export default PetboxPlugin;
