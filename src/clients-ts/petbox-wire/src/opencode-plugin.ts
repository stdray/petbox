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
import { agentDefinitionBannerNote, resolveAgentDefinitionForSession } from "./agent-def-fetch.ts";
import { DEFAULT_AGENT_DEFINITION, type AgentDefinition } from "./agent-definition.ts";
import { pushTranscript } from "./append.ts";
import { fetchCanonBlock } from "./canon.ts";
import { buildProtocol, opencodePetboxTool } from "./protocol.ts";
import { resolveProject } from "./registry.ts";
import { buildAutoSkillsIndex, shouldInjectOnce } from "./skill-files.ts";
import { buildStaleBaseWarning } from "./worktree-base-guard.ts";

export const PetboxPlugin: Plugin = async ({ client, directory }) => {
  // Resolve the active project once at load. null → both hooks no-op.
  const resolved = resolveProject(directory ?? "");

  // Resolve the banner's orchestrator notes ONCE at plugin load — server → LKG cache → the
  // built-in default, same order `apply` uses (resolveAgentDefinitionForSession wraps
  // agent-def-fetch.ts's resolveAgentDefinitionWithLkg). Bounded by that helper's own ~8s
  // fetch timeout; the plugin instance is long-lived for the opencode session, so this is a
  // one-time load-time cost, not a per-prompt one, and never throws/blocks indefinitely.
  let agentDefinition: AgentDefinition = DEFAULT_AGENT_DEFINITION;
  // Degradation note text (bug: wire-silent-failures-invisible) — "" when the load-time fetch
  // reached the live server; a built-in-fallback/LKG source otherwise gets a one-line marker in
  // the system prompt, same rationale as pull-memory.ts / droid-pull-memory.ts's identical
  // addition (source "default" used to report stale:false and stay completely silent).
  let defNote = "";
  if (resolved) {
    const got = await resolveAgentDefinitionForSession(resolved);
    agentDefinition = got.definition;
    defNote = agentDefinitionBannerNote(got);
  }

  // Sessions the petbox-* skills SALIENCE INDEX has already been injected into (bug:
  // opencode-skills-not-autoinjected) — chat.system.transform fires on EVERY turn, but the
  // index only needs to land once per session (it stays in the model's context from then on);
  // see shouldInjectOnce's doc comment. This is an index of WHEN to call `skill(name)`, not the
  // skill bodies themselves — those still arrive lazily through opencode's own native `skill`
  // tool, unchanged (see skill-files.ts's module comment for why: don't duplicate a cheap
  // surface into an expensive one).
  const injectedSkillsFor = new Set<string>();

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

    // NOT implemented here: subagentRuns (spec: subagent-run-provenance — see transcript.ts /
    // droid-transcript.ts for the Claude Code and droid equivalents). `client.session.messages()`
    // only surfaces text parts today (this function filters to `p.type === "text"` above); a
    // local check of real opencode session storage (~/.local/share/opencode/storage/part) turned
    // up no tool_use/task parts to confirm the shape a subagent spawn would take here, so adding
    // this would mean guessing a schema rather than reading one — left out rather than invented.
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

  // No per-prompt context injection is wired here. (The kit's prompt-RAG experiment — exact-match
  // per-prompt pointer injection on Claude Code's UserPromptSubmit — has been removed entirely, and
  // opencode never had a clean equivalent of that hook to port it to.)
  return {
    // Port of pull-memory — make the memory protocol part of the system prompt.
    "experimental.chat.system.transform": async (input, output) => {
      if (!resolved) return;
      // Stale-base warning first, so it stays prominent — see worktree-base-guard.ts. This
      // handler can fire on EVERY turn (opencode is long-lived), so the module throttles its
      // own best-effort git fetch internally; only the instant, network-free rev-list count
      // runs unthrottled here.
      const staleWarn = await buildStaleBaseWarning({ cwd: directory ?? "" });
      if (staleWarn) output.system.push(staleWarn);
      if (defNote) output.system.push(defNote);
      output.system.push(
        buildProtocol(resolved.project, opencodePetboxTool, {
          harness: "opencode",
          definition: agentDefinition,
        }),
      );
      // Append the curated memory canon when available (best-effort; degrades to nothing).
      const canon = await fetchCanonBlock(resolved);
      if (canon) output.system.push(canon);
      // Inject the petbox-* skills salience index (bug: opencode-skills-not-autoinjected) —
      // once per session, not every turn (shouldInjectOnce): this hook is opencode's only
      // injection point and, per the capability matrix (harness-capabilities.ts), fires
      // identically for main sessions and subagents, which is exactly the path Claude Code's
      // SessionStart hook cannot reach (AGENTS.md §10). A short "when to call which skill" list,
      // NOT the bodies — those stay behind opencode's own lazy `skill` tool (skill-files.ts).
      if (shouldInjectOnce(injectedSkillsFor, input.sessionID)) {
        const skillsIndex = buildAutoSkillsIndex(directory ?? "");
        if (skillsIndex) output.system.push(skillsIndex);
      }
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
