// PetBox session mirror + memory-protocol banner for pi — the UNOFFICIAL pi harness adapter.
//
// This file is the REPO copy and the source of truth; it is hand-deployed (there is no
// generated artifact and no `petbox-wire apply` step for it) to pi's user extension dir
// `~/.pi/agent/extensions/petbox-pi.ts` — or to `<project>/.pi/extensions/petbox-pi.ts` for a
// single project. Install/verify steps: tools/pi/README.md. It is deliberately NOT part of
// petbox-wire: the kit ships the shared ladder, this file is only the pi-shaped adapter.
//
// What it does: incremental push of the active session branch into the PetBox Sessions module
// (one push per turn) and the memory-protocol + canon banner every Claude Code / codex /
// opencode session start injects, delivered as a pi system-prompt SECTION. pi may load
// extensions without starting a session, so the factory only registers event handlers — no
// timers, sockets or processes are allowed here.
//
// Everything reusable comes from the stable petbox-wire kit (~/.petbox/wire) — project
// registry, incremental append protocol, transcript filters, trace sink, and the same
// definition → protocol → canon → assembleSessionBanner ladder pull-memory.ts renders — exactly
// as the Claude Code / codex / opencode hooks use them. This file only adapts pi's in-process
// session tree to the kit's Msg[] dialogue shape and pi's per-run prompt sections to the kit's
// banner text; it never writes into ~/.petbox/wire.
//
// The imports below are machine-absolute because the deploy target is not fixed (user-SCOPED
// extension dir vs project-scoped one sit at different depths, so no relative specifier can
// reach the mirror from both). On a machine whose kit mirror is not under this path, the prefix
// has to be repointed — see tools/pi/README.md's "Portability" note.

import type { ExtensionAPI, ExtensionContext, SessionEntry } from "@earendil-works/pi-coding-agent";
import { pushTranscript } from "C:/Users/stdray/.petbox/wire/append.ts";
import type { PushTarget } from "C:/Users/stdray/.petbox/wire/append.ts";
import { resolveApplyRoot } from "C:/Users/stdray/.petbox/wire/apply-root.ts";
import { fetchCanonBlock } from "C:/Users/stdray/.petbox/wire/canon.ts";
import type { AgentDefinition } from "C:/Users/stdray/.petbox/wire/agent-definition.ts";
import { resolveDefinitionForSession } from "C:/Users/stdray/.petbox/wire/definition-source.ts";
import { buildProtocol, mcpPetboxTool } from "C:/Users/stdray/.petbox/wire/protocol.ts";
import { resolveProject, UnresolvedEnvRefError } from "C:/Users/stdray/.petbox/wire/registry.ts";
import type { ResolvedProject } from "C:/Users/stdray/.petbox/wire/registry.ts";
import {
  assembleSessionBanner,
  describeCanonDegradation,
  logBudgetOverage,
  SESSION_BANNER_BUDGET_BYTES,
} from "C:/Users/stdray/.petbox/wire/session-budget.ts";
import { buildOwnerOnlySkillsBlock } from "C:/Users/stdray/.petbox/wire/skill-files.ts";
import { extractText, isExcluded } from "C:/Users/stdray/.petbox/wire/transcript.ts";
import type { Msg } from "C:/Users/stdray/.petbox/wire/transcript.ts";
import { wireLog } from "C:/Users/stdray/.petbox/wire/wire-log.ts";

// Same per-request budget as push-session.ts / codex-push-session.ts.
const FETCH_TIMEOUT_MS = 12000;

// Same canon wall-clock budget as pull-memory.ts's SESSION_FETCH_BUDGET_MS — deliberately short:
// the offline cache (~/.petbox/cache/<project>.canon.md) makes a slow server cost the session
// ~2s, not ~8s, and freshness is only lost in the window where the canon changed AND the server
// is down right now.
const CANON_FETCH_BUDGET_MS = 2000;

// pi is not in the kit's capability matrix — an unknown harness declares NO capabilities, so
// the protocol renders the main-session self-intro and never promises spawn/fan-out prose
// (harness-capabilities.ts: harnessCapabilities falls back to an empty set).
const HARNESS = "pi";

// System-prompt section key. pi validates section names against /^[a-z][a-z0-9_-]*$/
// (dist/core/system-prompt.js buildSystemPromptSections) and wraps the content in a matching
// <petbox> tag; the value below is overwritten per run, never appended to.
const BANNER_SECTION = "petbox";

// customType of the one-shot recall nudge message (see recallNudge below). Surfaces in the
// transcript as a `custom_message` entry, so a session file tells you the nudge was delivered.
const NUDGE_CUSTOM_TYPE = "petbox-recall-nudge";

// Prefix every petbox MCP tool name shares, taken from the kit's OWN namer instead of being
// spelled out a second time (mcpPetboxTool is `<prefix><verb>`, so an empty verb IS the prefix).
const PETBOX_TOOL_PREFIX = mcpPetboxTool("");

// A MessageEntry whose role is "system" carries the structured prompt state; only the section
// THIS file owns is read back out of it (frozenBannerSection).
//
// The value read back is WRAPPED in the section's own tag, because pi stores sections the way
// buildSystemPromptSections built them (`<name>\n…\n</name>` for every section except
// `preamble`), while the handler sees the inner text. Feeding the stored value straight back in
// makes pi wrap it a SECOND time: the stored banner then grows one tag level per session_start,
// the bytes differ from the previous system message, and pi re-appends the whole (now nested)
// banner — the exact churn the freeze exists to prevent, reintroduced through the back door.
// Found live, not by the driver: one session file carried three nesting levels after three
// restarts (the driver's stub did not emulate the wrapping until it was fixed to).
const BANNER_OPEN = `<${BANNER_SECTION}>`;
const BANNER_CLOSE = `</${BANNER_SECTION}>`;
function unwrapStoredSection(stored: string): string {
  if (!stored.startsWith(BANNER_OPEN) || !stored.endsWith(BANNER_CLOSE)) return stored;
  return stored.slice(BANNER_OPEN.length, stored.length - BANNER_CLOSE.length).replace(/^\n/, "").replace(/\n$/, "");
}

function sectionOf(entry: SessionEntry): string | null {
  if (entry.type !== "message") return null;
  const message = entry.message;
  if (message.role !== "system") return null;
  const section = message.sections?.[BANNER_SECTION];
  return typeof section === "string" && section.length > 0 ? unwrapStoredSection(section) : null;
}

type MirrorState = {
  readonly target: PushTarget;
  // Server cursor from the previous response, kept in process memory the same way the
  // opencode plugin keeps its per-session cursors. A pi process serves one session at a
  // time, so one field is enough; a lost cursor only costs an idempotent overlap resend.
  lastOrdinal: number | null;
};

// What one session's banner amounts to. `text` is the prompt section; `nudge` is the
// resumed/compacted recall line, delivered separately as a message — see the long comment on
// recallNudge for why it must never live inside the section.
type BannerState = {
  readonly text: string | null;
  readonly nudge: string;
  // True when `text` came from the transcript itself rather than from this run's assembly. The
  // capability gate needs the distinction: a session that cannot act on the protocol gets nothing
  // NEW injected into it, but a banner the transcript already carries is left standing (dropping
  // it would not be silence — pi turns "absent from the options" into a section DELETION).
  readonly frozen: boolean;
};

const NO_BANNER: BannerState = { text: null, nudge: "", frozen: false };

// Reset on session_start, cleared on session_shutdown — never persisted. Like `mirror`, one
// slot per process: a /new (reason "new") or resume re-fires session_start, which invalidates
// the previous project's banner before building the new one.
let mirror: MirrorState | null = null;

// Per-session memo of the assembled banner. A PROMISE, not a value: session_start kicks the
// heavy definition/canon/protocol work off WITHOUT blocking the session, and the first
// before_agent_start of the run awaits it (later runs get the resolved value immediately).
// Never rejects — buildBanner degrades to NO_BANNER with a wire.log trace.
let banner: Promise<BannerState> | null = null;

// One nudge per session, not per run: before_agent_start fires on every run, and the message it
// returns is appended to the transcript, so without this flag a resumed session would collect a
// copy of the same line every turn.
let nudgeSent = false;

// Can THIS session act on the protocol at all? Decided once, on the session's first run, and
// frozen — see the gate in before_agent_start. null = not decided yet.
let bannerAllowed: boolean | null = null;

// The protocol names petbox MCP tools (mcpPetboxTool), so it only makes sense where those tools
// are actually CALLABLE. A pi subagent does not have them: every child session measured carries
// the strict native allowlist (`read, grep, find, ls, bash, edit, write, contact_supervisor`) and
// no `mcp__petbox__*` in its declared catalog, and pi-subagents' own worker.md says so ("The
// builtin worker uses a strict tool allowlist. It does not inherit ambient extension tools from
// the parent session"). The banner used to arrive there anyway, instructing the child to run
// memory_search/tasks_search it could not run — spec definition-truthfulness, bug
// truthfulness-no-capability-claims-and-no-unreachable-tools.
//
// Capability, not agent class, decides: ask the session what it DECLARES. `getActiveTools()` is
// the set the model can call without a hop, which is the honest predicate — `getAllTools()` also
// lists tools registered but filtered out by an allowlist. A subagent configured with petbox MCP
// tools in its own `tools:` list therefore still gets the banner, and so does any future subagent
// extension that exposes them. This deliberately does NOT sniff pi-subagents (no PI_SUBAGENT_*
// env, no name pattern): swapping the subagent extension must not change what the banner claims.
function petboxToolsAreCallable(pi: ExtensionAPI): boolean {
  return pi.getActiveTools().some((name) => name.startsWith(PETBOX_TOOL_PREFIX));
}

// Active-branch dialogue only: getBranch() walks root→leaf along the current branch (so
// abandoned branches never leak in), and only user/assistant TEXT turns are kept — tool
// results, thinking, and system messages are excluded for the same reason the kit's own
// transcript.ts builders exclude them (tool dumps can carry secrets; the server wants the
// dialogue). An assistant turn with no text blocks (pure tool call) collapses to "" and
// is skipped.
function collectDialogue(entries: readonly SessionEntry[]): Msg[] {
  const msgs: Msg[] = [];
  for (const entry of entries) {
    if (entry.type !== "message") continue;
    const message = entry.message;
    if (message.role !== "user" && message.role !== "assistant") continue;
    const text = extractText(message);
    if (text.length === 0) continue;
    if (isExcluded(text)) continue;
    msgs.push({ role: message.role, content: text });
  }
  return msgs;
}

// Best-effort push for every phase: a failed push must never break the turn or the shutdown
// path, so adapter/kit/network failures are swallowed here and traced to wire.log (Class Б —
// quiet on the surface, visible to `petbox-wire doctor`).
async function pushNow(state: MirrorState, ctx: ExtensionContext, phase: string): Promise<void> {
  try {
    const msgs = collectDialogue(ctx.sessionManager.getBranch());
    if (msgs.length === 0) return; // nothing on the branch yet — an empty body is a server 400
    const last = await pushTranscript(state.target, msgs, state.lastOrdinal);
    if (last !== null) state.lastOrdinal = last;
  } catch (e) {
    wireLog(
      "pi",
      `${phase} push failed for ${state.target.project}/${state.target.sessionId}: ${e instanceof Error ? e.message : String(e)}`,
    );
  }
}

// The banner this TRANSCRIPT already carries, if any — the newest `petbox` section on the
// active branch, verbatim.
//
// This is the fix for the mid-conversation re-injection (observation
// pi-banner-reinjected-mid-conversation), and the reason it has to live here rather than in the
// kit: pi keeps the system prompt as an append-only transcript of section PATCHES
// (dist/core/agent-session.js `_preparePromptAndToolLoadout` → `diffSystemPromptSections`) and,
// when any section differs from the one the model currently has, emits a NEW system message
// carrying that section WHOLE. So a banner whose bytes ever change inside one conversation is
// re-injected in full — 6-8 KB of prompt, mid-dialogue, including the "Your FIRST response MUST
// open with …" imperative, which is why the model used to introduce itself again. Whatever the
// cause of a byte change (the SessionStart reason, a canon edit, a definition-layer edit), it
// can only bite if the adapter re-derives the section; reusing the transcript's own copy makes
// the flip impossible by construction.
//
// The deliberate trade, stated so nobody "fixes" it later: a banner is a SESSION-START artifact.
// A resumed session keeps the canon it started with and gets this file's one-line recall nudge
// instead; a canon/definition change reaches the model at its next session, not mid-dialogue.
// Bonus: a resumed session no longer pays the canon fetch.
function frozenBannerSection(ctx: ExtensionContext): string | null {
  const entries = ctx.sessionManager.getBranch();
  for (let i = entries.length - 1; i >= 0; i--) {
    const section = sectionOf(entries[i]!);
    if (section !== null) return section;
  }
  return null;
}

// The recall nudge for a resumed (or compacted) session — the kit's own SessionStart suffix,
// which protocol.ts appends to the protocol text for `resume`/`compact` and nowhere else.
//
// It is derived by diffing the kit's two renders of the SAME protocol rather than by copying the
// sentence here: the wording stays in the kit (protocol.ts's appendSessionSourceSuffix), so it
// can change there without a second copy drifting. `source` deliberately does NOT reach the
// banner section itself — the reason is a property of the MOMENT, not of the prompt, and letting
// it into the section is exactly what used to change the section's bytes between two session
// starts of one conversation (the observed +94 B: this very sentence).
function recallNudge(project: string, definition: AgentDefinition, source: string): string {
  const base = buildProtocol(project, mcpPetboxTool, { harness: HARNESS, definition });
  const sourced = buildProtocol(project, mcpPetboxTool, { source, harness: HARNESS, definition });
  return sourced.startsWith(base) ? sourced.slice(base.length).trim() : "";
}

// Assemble the memory-protocol banner for one resolved project, following the SAME ladder as
// pull-memory.ts: definition (file cascade, no network) → protocol → canon under a wall-clock
// budget → assembleSessionBanner's degradation ladder (protocol always wins; canon sheds leg
// by leg; owner-only skills between the legs). Never throws: a banner failure degrades to "no
// banner" with a wire.log trace — the session must never fail over memory-protocol prose, and
// every degradation the model DOES see is loud in the banner text itself, not in an exception.
//
// `frozen` short-circuits the canon leg: the transcript already carries this session's banner,
// so there is nothing to assemble (see frozenBannerSection for the whole argument).
async function buildBanner(
  resolved: ResolvedProject,
  cwd: string,
  source: string,
  frozen: string | null,
): Promise<BannerState> {
  try {
    const applyRoot = resolveApplyRoot(cwd).root;
    // Broken definition layer → note prepended below + Class-Б trace written by the kit;
    // the protocol under it renders from the kit base instead of a half-applied cascade.
    const defResult = resolveDefinitionForSession({
      root: applyRoot,
      logSource: `petbox-pi[${resolved.project}]`,
    });
    const nudge = recallNudge(resolved.project, defResult.definition, source);
    if (frozen !== null) return { text: frozen, nudge, frozen: true };
    const canon = await fetchCanonBlock(resolved, { timeoutMs: CANON_FETCH_BUDGET_MS });
    const protocol = buildProtocol(resolved.project, mcpPetboxTool, {
      harness: HARNESS,
      definition: defResult.definition,
    });
    const ownerOnlySkills = buildOwnerOnlySkillsBlock(applyRoot, HARNESS);
    const assembled = assembleSessionBanner(protocol, canon, SESSION_BANNER_BUDGET_BYTES, ownerOnlySkills);
    if (assembled.overBudget) {
      // Content existed and had to be cut — a breakage, not an expected absence: log loudly
      // (stderr + ~/.petbox/wire.log) rather than ship a banner pi would silently carry whole
      // while the degradation went unnoticed, exactly like pull-memory.ts.
      await logBudgetOverage(
        `petbox-pi[${resolved.project}]: session banner exceeded budget — ` +
          `protocol=${assembled.protocolBytes}B canon=${assembled.canonBytes}B ` +
          `ownerOnlySkills=${assembled.extraBytes}B (${assembled.extraIncluded ? "kept" : "DROPPED"}) ` +
          `budget=${SESSION_BANNER_BUDGET_BYTES}B — canon ${describeCanonDegradation(assembled)}.`,
      );
    }
    // Broken-layer marker LEADS the banner (spec: broken-layer-fails-loudly) — loudness lives
    // in the text the model reads, "" on every healthy session.
    const defNote = defResult.note;
    return { text: defNote ? `${defNote}\n${assembled.text}` : assembled.text, nudge, frozen: false };
  } catch (e) {
    wireLog("pi", `banner build failed for ${resolved.project}: ${e instanceof Error ? e.message : String(e)}`);
    // A resumed transcript keeps the banner it had: returning NO_BANNER here would not be "no
    // banner", it would be a DELETION — the section is absent from the options, so pi's diff
    // emits `petbox: null` and the model loses the protocol mid-conversation.
    return frozen !== null ? { text: frozen, nudge: "", frozen: true } : NO_BANNER;
  }
}

export default function (pi: ExtensionAPI): void {
  pi.on("session_start", (event, ctx) => {
    try {
      mirror = null; // a fresh start always invalidates any previous session's cursor
      banner = null; // …and the previous project's banner (per-session, never per-process)
      nudgeSent = false;
      bannerAllowed = null;
      let resolved: ResolvedProject | null = null;
      try {
        resolved = resolveProject(ctx.cwd);
      } catch (e) {
        // UnresolvedEnvRefError: the project IS wired but its $VAR key reference cannot
        // resolve. registry.ts already traced it to wire.log; surface it on stderr and
        // no-op, exactly like codex-push-session.ts — pushing with a broken key would only
        // turn a configuration problem into repeated HTTP 401s. The warning still reaches
        // the model as the banner (pull-memory.ts puts the same line on stdout), so the
        // session learns WHY there is no protocol instead of silently seeing none.
        if (e instanceof UnresolvedEnvRefError) {
          console.error(e.message);
          banner = Promise.resolve({ text: `⚠ ${e.message}`, nudge: "", frozen: false });
        }
        return;
      }
      if (!resolved) return; // cwd not a registered project — expected silence, not an error
      // Heavy work starts here and is NOT awaited: session readiness must not wait on the
      // canon fetch; before_agent_start awaits the memo on the first run instead.
      // event.reason doubles as the nudge source — "resume" gets the kit's recall line.
      banner = buildBanner(resolved, ctx.cwd, event.reason, frozenBannerSection(ctx));
      const sessionId = ctx.sessionManager.getSessionId();
      if (sessionId.length === 0) return;
      mirror = {
        target: {
          baseUrl: resolved.baseUrl, // registry default: https://petbox.3po.su
          project: resolved.project,
          sessionId,
          apiKey: resolved.apiKey,
          agent: "pi",
          timeoutMs: FETCH_TIMEOUT_MS,
        },
        lastOrdinal: null,
      };
    } catch (e) {
      // Registration must never fail the session: an unregistered cwd is a no-op by design,
      // and anything unexpected here is a Class-Б trace rather than a startup error.
      wireLog("pi", `session_start failed: ${e instanceof Error ? e.message : String(e)}`);
    }
  });

  // Fires on EVERY run (docs/extensions.md: "A run proceeds from input and
  // before_agent_start"), so the assignment must be idempotent — it is: a plain overwrite of
  // one sections key from the per-session memo, and the memo means the definition/canon/
  // protocol work of session_start is never redone per turn. A SECTION (not the
  // result.systemPrompt full-replacement) is what pi diffs into a transcript delta, so an
  // unchanged banner costs no prompt churn after the first run.
  pi.on("before_agent_start", async (event) => {
    try {
      const memo = banner;
      if (!memo) return; // unregistered cwd / no session yet — no banner by design
      const state = await memo;
      // Gate, decided ONCE on the session's first run — not per run, deliberately. A per-run
      // check would let a late MCP connection add the section to a live transcript, which is the
      // same mid-conversation injection frozenBannerSection exists to prevent; a first-run
      // decision makes the session's prompt shape final in both directions. The cost is the
      // mirror image of the benefit: if the direct MCP tools really are missing on run one (a
      // slow connect), this session runs without a banner — which is why the abstention is
      // traced to wire.log rather than silent.
      if (bannerAllowed === null) {
        bannerAllowed = petboxToolsAreCallable(pi);
        if (!bannerAllowed) {
          wireLog(
            "pi",
            `no petbox MCP tool (${PETBOX_TOOL_PREFIX}*) is declared in this session — the protocol is not injected here (a subagent's strict tool allowlist is the usual reason)`,
          );
        }
      }
      if (!bannerAllowed) {
        // Nothing NEW goes into a session that cannot act on the protocol. A banner the transcript
        // ALREADY carries is a different case and is kept standing byte-for-byte: not setting it
        // would not be silence — pi reads "absent from the options" as a section DELETION and drops
        // it mid-conversation.
        if (state.frozen && state.text !== null) event.systemPromptOptions.sections[BANNER_SECTION] = state.text;
        return;
      }
      // Set when there IS a banner; otherwise leave the transcript alone. NOTHING is deleted:
      // a `null` text means the build failed for a project that IS wired, and pushing "no
      // section" would carve a hole in the prompt now and pour the whole banner back into it
      // on the next successful run — the flip this file exists to prevent. An unregistered cwd
      // never gets here at all (`memo` is null), so a stale section cannot be left behind.
      if (state.text !== null) event.systemPromptOptions.sections[BANNER_SECTION] = state.text;
      if (state.nudge.length > 0 && !nudgeSent) {
        nudgeSent = true;
        // A MESSAGE, not a section: it is appended to the transcript once and never diffed, so
        // the resumed session gets the kit's recall line without the banner being re-rendered.
        // `display: false` keeps it out of the TUI — it is context for the model, not chrome
        // for the user, exactly like the other harnesses' SessionStart additional-context.
        return { message: { customType: NUDGE_CUSTOM_TYPE, content: state.nudge, display: false } };
      }
      return;
    } catch (e) {
      // Belt and braces: buildBanner never rejects, but a handler error must still only
      // degrade the banner, never the run.
      wireLog("pi", `before_agent_start banner injection failed: ${e instanceof Error ? e.message : String(e)}`);
    }
  });

  pi.on("turn_end", async (_event, ctx) => {
    const state = mirror;
    if (state) await pushNow(state, ctx, "turn_end"); // pushNow itself never throws
  });

  pi.on("session_shutdown", async (_event, ctx) => {
    const state = mirror;
    mirror = null; // idempotent cleanup: a repeated shutdown sees null and pushes nothing
    banner = null;
    if (state) await pushNow(state, ctx, "session_shutdown");
  });
}
