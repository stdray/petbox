/**
 * PetBox plugin for opencode (GLOBAL) — the opencode port of the two Claude Code hooks:
 *
 *   1. pull-memory  (SessionStart) → inject the PetBox memory protocol so the agent recalls
 *      relevant memory and captures learnings via the connected `petbox` MCP. Appended to the
 *      system prompt via `experimental.chat.system.transform`.
 *
 *   2. push-session (Stop) → mirror the session conversation into PetBox's Session module so it
 *      auto-populates. Fires on `session.idle` (opencode's "the turn finished") and POSTs the
 *      full user/assistant text history to the PetBox REST API (last-write-wins replace).
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
import { resolveProject } from "./registry.ts";

function memoryProtocol(project: string): string {
  return `## PetBox memory

This project is wired to a PetBox instance over the \`petbox\` MCP server (project
\`${project}\`). Its memory verbs are exposed as \`petbox_memory_recall\` /
\`petbox_memory_remember\` / \`petbox_memory_get\` / \`petbox_memory_upsert\`.

In your FIRST response this session, open with exactly this line (so it's visible the
protocol is active):
\`🧠 PetBox memory active\`

Before substantive work, **recall**: call \`petbox_memory_recall\` with a \`query\` of a few
words you are confident appear (tokens are ANDed, prefix-matched), and pass \`bodyLen\`
(e.g. 240) so hits come back as a \`description\` + a short snippet rather than full bodies —
that keeps session start cheap. With no \`scope\` it cascades project ⊕ workspace; hits come
back labelled by scope (project = this project, workspace = cross-project shared). Skim them
for relevant past decisions, conventions, gotchas; when a hit looks relevant, pull its full
body with \`petbox_memory_get\`.

As you work, **capture** incrementally (don't wait for session end): after a decision, a
fixed bug, a discovered pattern, or a stated preference, store a concise fact via
\`petbox_memory_remember\` (\`text\` = the learning; \`type\` = User|Feedback|Project|Reference;
\`scope\` = workspace for facts that span projects or are about the user, else omit for this
project). Aim for 1-3 memories per substantial interaction. Curated/temporal edits go through
\`petbox_memory_upsert\`.

**End-of-session sweep:** when the work concludes, do ONE final pass before you stop: name
the 1-3 most durable learnings from this session that you have NOT already stored, and
\`petbox_memory_remember\` each. Skip raw narration and anything derivable from code/git — only
facts worth recalling next time. If nothing qualifies, capture nothing.`;
}

export const PetboxPlugin: Plugin = async ({ client, directory }) => {
  // Resolve the active project once at load. null → both hooks no-op.
  const resolved = resolveProject(directory ?? "");

  // Avoid re-POSTing the same state when session.idle fires repeatedly.
  const lastPushed = new Map<string, string>();

  async function pushSession(sessionID: string): Promise<void> {
    if (!resolved || !sessionID) return;

    const res = await client.session.messages({ path: { id: sessionID } });
    const messages = res.data;
    if (!Array.isArray(messages) || messages.length === 0) return;

    // The whole conversation (user + assistant text turns) as ordered ndjson messages —
    // the endpoint is last-write-wins and the server numbers the messages, so each idle
    // re-sends the full transcript (not just the last turn) and it self-heals.
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

    const body = msgs.map((m) => JSON.stringify(m)).join("\n");
    const uri = `${resolved.baseUrl}/api/sessions/${resolved.project}/${encodeURIComponent(sessionID)}?agent=opencode`;
    const resp = await fetch(uri, {
      method: "POST",
      headers: { "X-Api-Key": resolved.apiKey, "Content-Type": "application/x-ndjson; charset=utf-8" },
      body,
      signal: AbortSignal.timeout(8000),
    });
    if (resp.ok) lastPushed.set(sessionID, lastID);
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
