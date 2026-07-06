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
 * `petbox_memory_search` / `petbox_memory_remember` / `petbox_memory_get` /
 * `petbox_memory_upsert` (the Claude `mcp__petbox__*` names do not apply here).
 */
import type { Plugin } from "@opencode-ai/plugin";
import { pushTranscript } from "./append.ts";
import { fetchCanonBlock } from "./canon.ts";
import { buildProtocol, opencodePetboxTool } from "./protocol.ts";
import { resolveProject } from "./registry.ts";

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

  // prompt-RAG (exact-match per-prompt pointer injection) is NOT ported to opencode. Assessed
  // against @opencode-ai/plugin's Hooks interface (v1.15.12 index.d.ts; the same surface exists back
  // to ≥1.1.36): opencode has NO clean equivalent of Claude Code's UserPromptSubmit — there is no
  // hook that (a) fires once per user-message submission, (b) receives that prompt, and (c) lets you
  // append per-turn context via a return value/stdout.
  //   - `experimental.chat.system.transform` (used above) reaches the model but its input is only
  //     { sessionID, model } — it never sees the user prompt, so it can't do prompt-conditional
  //     exact-match (it's for the STATIC memory protocol).
  //   - `chat.message` sees a new user message (output { message, parts }) but the docs frame it as
  //     "called when a new message is received" (observational); whether mutating parts reaches the
  //     model is undocumented/unconfirmed.
  //   - `experimental.chat.messages.transform` (output.messages) DOES reach the model and can read
  //     the last user turn — but it's `experimental`, fires on EVERY completion within a turn (tool
  //     loops, sub-agents, compaction continues), not once per user submission, and would require
  //     mutating the message array. That is a materially worse, noisier surrogate than CC's clean
  //     stdout inject — exactly the RAG-as-dump failure mode this feature is designed to avoid.
  // Verdict: DEFER. No injection is wired here on purpose (don't force a bad surrogate). If a future
  // opencode release adds a true per-user-message context hook, port the shared prompt-rag.ts core
  // (buildInjectionForProject) with the opencodePetboxTool namer (`petbox_tasks_node_get`).
  return {
    // Port of pull-memory — make the memory protocol part of the system prompt.
    "experimental.chat.system.transform": async (_input, output) => {
      if (!resolved) return;
      output.system.push(buildProtocol(resolved.project, opencodePetboxTool));
      // Append the curated memory canon when available (best-effort; degrades to nothing).
      const canon = await fetchCanonBlock(resolved);
      if (canon) output.system.push(canon);
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
