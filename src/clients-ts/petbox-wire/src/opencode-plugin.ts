/**
 * PetBox plugin for opencode (GLOBAL) — the opencode port of the two Claude Code hooks:
 *
 *   1. pull-memory  (SessionStart) → inject the PetBox memory protocol so the agent recalls
 *      relevant memory and captures learnings via the connected `petbox` MCP. Appended to the
 *      system prompt via the v1 `experimental.chat.system.transform` hook / the v2 `session`
 *      `"context"` hook (see the V1/V2 split below).
 *
 *   2. push-session (Stop) → mirror the session conversation into PetBox's Session module so it
 *      auto-populates. Fires on `session.idle` (opencode's "the turn finished") and pushes the
 *      INCREMENT via the server-authoritative append cursor (see append.ts) — the plugin is
 *      long-lived, so it remembers each session's lastOrdinal from the previous response in
 *      process memory (no durable state); a restart self-heals off the structured 409 gap
 *      reject, and old servers without the append route fall back to the full-snapshot push.
 *
 * Unlike the per-project copy this is installed once at user scope. The active project + API
 * key + base URL are resolved from the plugin instance's own directory (v1: `PluginInput.
 * directory`; v2: `ctx.location.directory`) via the shared registry. If the cwd is not a
 * registered project (or the key is missing) BOTH hooks are no-ops — but the plugin still loads
 * cleanly in every project.
 *
 * Both hooks are best-effort and must never break a turn (every failure is swallowed).
 *
 * MCP note: opencode exposes MCP tools as `<server>_<tool>`, so the petbox memory verbs are
 * `petbox_memory_search` / `petbox_memory_remember` / `petbox_memory_get` /
 * `petbox_memory_upsert` (the Claude `mcp__petbox__*` names do not apply here). Unchanged by the
 * V1→V2 port — the migration guide's breaking changes are plugin API, server API/clients, and
 * `tui.json`→`cli.json`; MCP tool-name prefixing is not one of them.
 *
 * ============================================================================================
 * V1/V2 DUAL SUPPORT (card opencode-v2-plugin-api-port) — ONE SOURCE, chosen by measurement
 * ============================================================================================
 *
 * OpenCode 2 (`@opencode/cli`) ships a NEW plugin API; a V1 plugin implementation (the shape
 * this file used exclusively before this port: `export default async ({client, directory}) =>
 * ({ /* hooks by string key *\/ })`) does not run in V2 at all — silent no-load, not a crash
 * (https://opencode.ai/v2/docs/migrate-v1/#plugins).
 *
 * The alternative to one dual-shape source was two separate entry points (and a wire-time branch
 * choosing which one to shim, keyed off the installed major). That was NOT needed, by
 * measurement: opencode's own V1→V2 plugin migration guide
 * (https://opencode.ai/v2/docs/build/plugins/migrate-v1#support-v1-and-v2-from-one-package)
 * documents a single default-exported OBJECT that carries BOTH shapes — a V2 `{id, setup}` (what
 * `Plugin.define` returns, verified by reading `@opencode/plugin@2.0.12`'s `dist/promise/
 * plugin.js`: `define` is the identity function, so the object needs no special branding) PLUS a
 * `server` method holding the classic V1 function. The V1 loader recognizes this object
 * entrypoint (type `PluginModule = { id?: string; server: Plugin; tui?: never }`, confirmed
 * present in `@opencode-ai/plugin@1.18.32`'s `dist/index.d.ts` — the doc calls the minimum
 * supporting release "OpenCode 1.18.29 and newer"); the V2 loader reads `id`/`setup` off the same
 * object and ignores `server`. Both `opencode-ai@1.18.32` (latest V1 CLI on npm as of this port)
 * and `@opencode/cli@2.0.12` (latest V2) were installed and inspected to confirm this — see the
 * card's verdict comment for the live V2 load test. This satisfies "keep v1 working" (Do §2)
 * without a second build artifact: `PetboxPlugin` below is untouched (still the exact V1
 * function every existing test drives directly), and `petboxPluginV2Setup` is new, additive code
 * sharing every content-building helper with it.
 *
 * V1 EXTENSION POINT → V2 API map for the two hooks this plugin actually uses (full table:
 * https://opencode.ai/v2/docs/build/plugins/migrate-v1#register-hooks-in-setup):
 *
 *   event                                    → ctx.event.subscribe(), filtered to
 *                                               "session.execution.succeeded"/"failed"/
 *                                               "interrupted" — NOT "session.idle" (see the
 *                                               pushSession registration below: that event type
 *                                               still exists in v2's schema but was measured live
 *                                               to never fire on turn completion; a finding, not
 *                                               in the migration doc's own table)
 *   experimental.chat.system.transform       → ctx.session.hook("context", ...), edit event.system
 *
 * Every OTHER V1 hook key this plugin never registered (chat.message, chat.params, chat.headers,
 * permission.ask, command.execute.before, tool.execute.before/after, shell.env, tool.definition,
 * the `experimental.*` auth/provider/compaction/text hooks) has a documented V2 destination too
 * (ctx.session.hook("prompt"|"context"|"model.request"|...), ctx.permission.hook("evaluate"),
 * ctx.tool.hook(...), ctx.tool.transform(...), ctx.shell.hook("create.before"), ...) — none of
 * them are a gap for THIS plugin because none of them were ever used. Nothing in the V1 surface
 * this plugin depends on lacks a V2 equivalent.
 */
import type { Hooks, Plugin as PluginV1 } from "@opencode-ai/plugin";
import { Plugin as PluginV2 } from "@opencode/plugin";
import { DEFAULT_AGENT_DEFINITION, type AgentDefinition } from "./agent-definition.ts";
import { resolveApplyRoot } from "./apply-root.ts";
import { pushTranscript } from "./append.ts";
import { fetchCanonBlock } from "./canon.ts";
import { resolveDefinitionForSession } from "./definition-source.ts";
import { buildProtocol, opencodePetboxTool } from "./protocol.ts";
import { resolveProject, UnresolvedEnvRefError, type ResolvedProject } from "./registry.ts";
import { buildAutoSkillsIndex, buildOwnerOnlySkillsBlock } from "./skill-files.ts";
import { buildStaleBaseWarning } from "./worktree-base-guard.ts";
import type { Msg } from "./transcript.ts";

// ---------------------------------------------------------------------------------------------
// SHARED (version-independent) load-time resolution and system-prompt block construction. Both
// the V1 `server` function and the V2 `setup` function call this ONCE at plugin load, given only
// the instance's own directory — exactly the same inputs V1's PluginInput.directory always gave
// it, and exactly what V2's ctx.location.directory gives it too.
// ---------------------------------------------------------------------------------------------

type LoadState = {
  readonly resolved: ResolvedProject | null;
  readonly envRefNote: string;
  readonly agentDefinition: AgentDefinition;
  readonly defNote: string;
};

function resolveOnLoad(directory: string): LoadState {
  // UnresolvedEnvRefError (registry.ts) is NOT the ordinary "not a registered project" case:
  // the project IS wired and a key SHOULD exist, so leaving `resolved` null the same silent way
  // would be indistinguishable from never having wired it (decision 2, card
  // keys-json-supports-env-var-references — see pull-memory.ts's identical catch for the fuller
  // rationale). registry.ts already traced this to wire.log; this ALSO surfaces it on stderr and,
  // once the system-prompt block below runs, in the system prompt itself — the one channel this
  // plugin has that reaches the owner in-session. Both hooks still no-op exactly as before
  // (`resolved` stays null) — only the silence is replaced with a note.
  let resolved: ResolvedProject | null = null;
  let envRefNote = "";
  try {
    resolved = resolveProject(directory);
  } catch (e) {
    if (e instanceof UnresolvedEnvRefError) {
      console.error(e.message);
      envRefNote = `⚠ ${e.message}`;
    }
  }

  // Resolve the banner's orchestrator notes ONCE at plugin load, from the FILE cascade
  // base < user < project (definition-source.ts) — the same resolve `apply` compiles from, and
  // no network at all. This used to be an HTTP fetch bounded by an ~8s timeout; the plugin
  // instance is long-lived, so it was a one-time load-time cost, but it was also the reason a
  // cold opencode start depended on PetBox being up (card wire-stops-fetching-definition).
  let agentDefinition: AgentDefinition = DEFAULT_AGENT_DEFINITION;
  // Broken-layer marker (spec broken-layer-fails-loudly) — "" when the cascade resolved cleanly.
  // Otherwise a one-line marker naming the file that broke, pushed ahead of the protocol block in
  // the system prompt, same rationale as pull-memory.ts / droid-pull-memory.ts.
  let defNote = "";
  if (resolved) {
    const got = resolveDefinitionForSession({
      root: resolveApplyRoot(directory).root,
      logSource: `opencode-plugin[${resolved.project}]`,
    });
    agentDefinition = got.definition;
    defNote = got.note;
  }

  return { resolved, envRefNote, agentDefinition, defNote };
}

// Build the system-prompt blocks for ONE request, in order. Called on EVERY hook firing (no
// once-per-session gate) — see the salience-index comment further down for why that is load-
// bearing, not an oversight: opencode rebuilds the system prompt from scratch per request in
// both V1 and V2.
async function buildSystemBlocks(state: LoadState, directory: string): Promise<string[]> {
  const blocks: string[] = [];
  if (state.envRefNote) blocks.push(state.envRefNote);
  if (!state.resolved) return blocks;

  // Stale-base warning first, so it stays prominent — see worktree-base-guard.ts. This can fire
  // on EVERY turn (opencode is long-lived), so the module throttles its own best-effort git
  // fetch internally; only the instant, network-free rev-list count runs unthrottled here.
  const staleWarn = await buildStaleBaseWarning({ cwd: directory });
  if (staleWarn) blocks.push(staleWarn);
  if (state.defNote) blocks.push(state.defNote);
  blocks.push(
    buildProtocol(state.resolved.project, opencodePetboxTool, {
      harness: "opencode",
      definition: state.agentDefinition,
    }),
  );
  // Append the curated memory canon when available (best-effort; degrades to nothing).
  const canon = await fetchCanonBlock(state.resolved);
  if (canon) blocks.push(canon);
  // Inject the petbox-* skills salience index (bug: opencode-skills-not-autoinjected) — on
  // EVERY request, deliberately, NOT once per session. opencode builds the system prompt FRESH
  // for every request, including its own small-model SESSION-TITLE generation call, which
  // shares the session's id with the real chat request that follows it — a once-per-session gate
  // is consumed by the title request and the agent's actual prompt never sees the block at all
  // (measured live, opencode 1.18.25: system.length before the hook was 1 on both call 1 and
  // call 2 of the same session). Cost of pushing every time is the honest one: ~810 B per
  // request in this repo, an order of magnitude under the canon block already sitting next to
  // it, and it buys the ONLY thing this injection is for — the index being in the prompt the
  // agent actually answers from.
  const skillsIndex = buildAutoSkillsIndex(directory);
  if (skillsIndex) blocks.push(skillsIndex);
  // Owner-only skills (work: user-invocable-skills-invisible-to-model) — same every-request
  // treatment as the salience index above, and the SAME degrade-to-nothing contract. Unlike
  // Claude Code / Droid, opencode does not recognize `disable-model-invocation` at all, so
  // this renders the opencode-specific truth (already in your listing, call natively) rather
  // than the Claude-Code one (see skill-files.ts's buildOwnerOnlySkillsBlock).
  const ownerOnlySkills = buildOwnerOnlySkillsBlock(directory, "opencode");
  if (ownerOnlySkills) blocks.push(ownerOnlySkills);
  return blocks;
}

// ---------------------------------------------------------------------------------------------
// V1 — unchanged shape, unchanged behaviour. Every existing test drives this function directly
// (`PetboxPlugin({client, directory})`), so it is untouched by the port: it now delegates its
// content-building to the shared helpers above, but its hook keys, its output shape, and its
// `pushSession` extraction logic over `client.session.messages()` are exactly what they were.
// ---------------------------------------------------------------------------------------------

export const PetboxPlugin: PluginV1 = async ({ client, directory }) => {
  const state = resolveOnLoad(directory ?? "");

  // Avoid re-POSTing the same state when session.idle fires repeatedly.
  const lastPushed = new Map<string, string>();
  // Per-session server cursor (lastOrdinal from the previous response). Process memory only —
  // a plugin restart just means the first push self-heals via the structured gap reject.
  const cursors = new Map<string, number>();

  async function pushSession(sessionID: string): Promise<void> {
    if (!state.resolved || !sessionID) return;

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
      .filter(Boolean) as Msg[];
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
        baseUrl: state.resolved.baseUrl,
        project: state.resolved.project,
        sessionId: sessionID,
        apiKey: state.resolved.apiKey,
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
  const hooks: Hooks = {
    // Port of pull-memory — make the memory protocol part of the system prompt.
    "experimental.chat.system.transform": async (_input, output) => {
      const blocks = await buildSystemBlocks(state, directory ?? "");
      output.system.push(...blocks);
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
  return hooks;
};

// ---------------------------------------------------------------------------------------------
// V2 — same two hooks, registered through ctx.session.hook / ctx.event.subscribe per the
// migration table above. `petboxPluginV2Setup` is the `setup(ctx)` V2 plugins register through
// `Plugin.define`; exported standalone (rather than only inline in the default export) so a test
// can call it directly with a minimal mock context, the same way the V1 tests call `PetboxPlugin`
// directly with a minimal mock `PluginInput`.
// ---------------------------------------------------------------------------------------------

export const petboxPluginV2Setup: PluginV2.Plugin["setup"] = async (ctx) => {
  const directory = ctx.location.directory;
  const state = resolveOnLoad(directory);

  const lastPushed = new Map<string, string>();
  const cursors = new Map<string, number>();

  async function pushSession(sessionID: string): Promise<void> {
    if (!state.resolved || !sessionID) return;

    const entries = await ctx.session.context({ sessionID });
    if (!Array.isArray(entries) || entries.length === 0) return;

    // v2's SessionMessageInfo is a discriminated union (user/assistant/system/skill/shell/
    // synthetic/compaction/idle/agent-selected/model-selected/location-switched) — only "user"
    // and "assistant" carry conversation text; the rest are session-lifecycle bookkeeping v1's
    // `m.parts` shape never surfaced as separate message rows in the first place, so skipping
    // them here is not a narrowing of what v1 pushed, it is the same filter expressed against a
    // richer message set.
    const msgs = entries
      .map((m): Msg | null => {
        if (m.type === "user") {
          const text = m.text.trim();
          return text ? { role: "user", content: text } : null;
        }
        if (m.type === "assistant") {
          const text = m.content
            .filter((p): p is Extract<typeof p, { type: "text" }> => p.type === "text")
            .map((p) => p.text)
            .join("\n")
            .trim();
          return text ? { role: "assistant", content: text } : null;
        }
        return null;
      })
      .filter((m): m is Msg => m !== null);
    if (msgs.length === 0) return;
    const lastID = entries[entries.length - 1]?.id ?? "";
    if (lastPushed.get(sessionID) === lastID) return;

    // Same scope note as the V1 pushSession above: no subagentRuns provenance here either —
    // v2's SessionMessageAssistant carries tool content inline (content: text|reasoning|tool),
    // not a separate task/subagent part kind confirmed against real storage, so nothing is
    // invented here that wasn't already true of the v1 port.
    const lastOrdinal = await pushTranscript(
      {
        baseUrl: state.resolved.baseUrl,
        project: state.resolved.project,
        sessionId: sessionID,
        apiKey: state.resolved.apiKey,
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

  // Port of pull-memory: "context" fires immediately before an agent's own model request — kind
  // "primary" only (compaction/title/generate are SEPARATE hook names in v2: "compaction",
  // "title", "generate"), so registering only on "context" naturally excludes the small-model
  // title-generation call that caused the v1 once-per-session-gate bug in the first place. That
  // bug's actual fix (push every time, no gate) still applies unconditionally here regardless.
  await ctx.session.hook("context", async (event) => {
    const blocks = await buildSystemBlocks(state, directory);
    for (const text of blocks) event.system.push({ type: "text", text });
  });

  // Port of push-session: v1's `event` hook becomes a subscription to the public event stream
  // (https://opencode.ai/v2/docs/build/plugins/migrate-v1#migrate-events-and-cleanup). Cleanup
  // (the returned function) aborts it — mirrors v1's implicit "hooks live as long as the plugin
  // instance", made explicit here because v2 requires an explicit unsubscribe.
  //
  // NOT "session.idle" — that event TYPE still exists in v2's schema (SessionIdle in
  // @opencode/client's event union) and the migration guide's own hook table lists plain
  // "event → ctx.event.subscribe()" with no further detail, but it does not fire on turn
  // completion in v2. Measured live (opencode 2.0.12, `opencode serve` + a real deepseek-v4-flash
  // turn, instrumented plugin logging every event.type received): the full per-turn sequence ran
  // session.created → session.execution.started → session.step.* / session.text.* /
  // session.reasoning.* (streaming) → session.execution.succeeded, with "session.idle" NEVER
  // observed across multiple complete turns. "session.execution.succeeded" (and its siblings
  // "session.execution.failed" / "session.execution.interrupted", same {data:{sessionID}} shape)
  // is v2's actual "the turn finished" signal — this plugin pushes on all three (not just
  // success) to match v1's behavior of pushing on session.idle regardless of how the turn ended,
  // so a failed/interrupted turn's partial transcript is not silently dropped.
  const controller = new AbortController();
  void (async () => {
    for await (const event of ctx.event.subscribe({ signal: controller.signal })) {
      if (
        event.type !== "session.execution.succeeded" &&
        event.type !== "session.execution.failed" &&
        event.type !== "session.execution.interrupted"
      ) {
        continue;
      }
      try {
        await pushSession(event.data.sessionID);
      } catch {
        /* best-effort: never break the turn */
      }
    }
  })();
  return () => controller.abort();
};

const petboxPluginV2: PluginV2.Plugin = PluginV2.define({
  id: "petbox",
  setup: petboxPluginV2Setup,
});

// Default export: the dual-shape object opencode's V1 and V2 loaders each recognize on their
// own terms (see the module comment above). `Plugin.define` is an identity function (verified
// against @opencode/plugin@2.0.12's dist/promise/plugin.js), so spreading its result costs
// nothing beyond what a plain `{id, setup}` literal would — this is exactly the shape opencode's
// own "Support V1 and V2 from one package" migration-guide section documents.
export default {
  ...petboxPluginV2,
  server: PetboxPlugin,
};
