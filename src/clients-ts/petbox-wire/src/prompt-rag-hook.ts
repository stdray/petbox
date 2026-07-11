// Install-time decision + settings surgery for the GLOBAL prompt-RAG UserPromptSubmit hook.
//
// Lives in its own side-effect-free module (like wire-exit.ts / posix-env.ts / wire-identity.ts)
// precisely so it is importable by a test: wire.ts runs main() at import time and cannot be.
//
// The toggle is a STICKY TRI-STATE, and it must be sticky for BOTH halves of the wiring:
//   --prompt-rag    → registry promptRag.enabled = true  + INSTALL the global hook
//   --no-prompt-rag → registry promptRag.enabled = false + prune the hook IF it is now dead weight
//   neither         → KEEP: the registry flag AND the installed hook are left exactly as found.
//
// The old code passed `promptRag === true` into the installer, collapsing "sticky" into "off", so
// EVERY plain re-run (`npx petbox-wire <dir> <project>` to refresh an MCP config) silently pruned
// the hook while the project's registry flag stayed `true` — flag on, machine not running it,
// nobody reporting the mismatch. `decidePromptRagHook` restores the third state.
//
// Global hook vs per-project flag. There is ONE hook per machine (~/.claude/settings.json,
// ~/.factory/settings.json) and one flag per project (~/.petbox/projects.json). The hook self-gates
// per project at runtime (prompt-rag.ts reads the matched entry's `promptRag.enabled`), so an
// installed hook + a disabled project is a CORRECT, silent state. Therefore `--no-prompt-rag` on
// project A must NOT rip the hook out from under project B, which explicitly opted in: we prune only
// when NO registry entry is left enabled (the hook could then only ever no-op). Anything else would
// reproduce the very bug this fixes — a per-project command with a global, silent side effect.
//
// Plain TS for native node type-stripping: no enum/namespace/parameter-properties, zero deps.

import type { RegistryEntry } from "./registry.ts";

// What this run should do to the global prompt-RAG hook in a settings file.
//   install — ensure exactly one hook entry with this command (idempotent)
//   prune   — remove every hook entry targeting prompt-rag.ts
//   keep    — touch nothing (the sticky no-op; also the reason a re-wire is safe)
export type PromptRagHookAction = "install" | "prune" | "keep";

export type PromptRagHookDecision = {
  action: PromptRagHookAction;
  reason: string;
};

export type PromptRagHookInput = {
  // The parsed tri-state flag: true = --prompt-rag, false = --no-prompt-rag, undefined = neither.
  requested: boolean | undefined;
  // Does the stable kit (~/.petbox/wire/) actually ship prompt-rag.ts? A downgraded kit must never
  // leave a hook pointing at a file it no longer ships (the recurring version-skew SyntaxError).
  kitShipsHook: boolean;
  // Is prompt-RAG still enabled for ANY registered project? Evaluated AFTER the registry upsert, so
  // the project being wired is already counted with its NEW flag.
  anyProjectEnabled: boolean;
};

// The whole install-time policy in one pure function.
export function decidePromptRagHook(input: PromptRagHookInput): PromptRagHookDecision {
  // Version-skew self-heal outranks everything: a hook command pointing at a missing file crashes
  // every prompt, so it goes even when this run says nothing about prompt-RAG.
  if (!input.kitShipsHook) {
    return {
      action: "prune",
      reason:
        "the stable kit does not ship prompt-rag.ts — pruning any hook that targets it (version-skew guard)",
    };
  }
  if (input.requested === true) {
    return { action: "install", reason: "--prompt-rag" };
  }
  if (input.requested === undefined) {
    return {
      action: "keep",
      reason: "neither --prompt-rag nor --no-prompt-rag — sticky: the hook is left exactly as found",
    };
  }
  // --no-prompt-rag: this project's flag is already off in the registry. The global hook still
  // serves any OTHER project that opted in (it self-gates), so removing it would silently disable
  // them. Prune only when nothing is left for it to do.
  if (input.anyProjectEnabled) {
    return {
      action: "keep",
      reason:
        "--no-prompt-rag: this project's flag is off, but another registered project still has prompt-RAG on — " +
        "the global hook stays (it self-gates per project)",
    };
  }
  return {
    action: "prune",
    reason: "--no-prompt-rag: no registered project has prompt-RAG enabled — the global hook is removed",
  };
}

// True when ANY registry entry has prompt-RAG enabled. Malformed `promptRag` values degrade to
// "not enabled" (same polarity as registry.resolveProject).
export function anyProjectPromptRagEnabled(entries: RegistryEntry[]): boolean {
  return entries.some(
    (e) => !!e && typeof e.promptRag === "object" && e.promptRag !== null && e.promptRag.enabled === true,
  );
}

// ---- settings surgery (pure w.r.t. the hooks object; the caller does the file I/O) ----------

// The one event both Claude Code and Droid fire for prompt-RAG (no matcher — CC docs).
export const PROMPT_RAG_EVENT = "UserPromptSubmit";
// The kit file every prompt-RAG hook command targets, on either agent.
export const PROMPT_RAG_FILE = "prompt-rag.ts";

// Remove EVERY hook (across all events) whose command targets the given STABLE kit file, then drop
// now-empty groups. Mutates hooksObj in place; returns the count pruned. Commands quote the path
// (`node "<...>/prompt-rag.ts"`, optionally ` --agent droid`), so matching the quoted basename
// catches both the Claude Code and the Droid variant.
export function pruneHooksTargeting(hooksObj: any, fileBasename: string): number {
  if (!hooksObj || typeof hooksObj !== "object") return 0;
  let removed = 0;
  const needle = `${fileBasename}"`;
  for (const event of Object.keys(hooksObj)) {
    const groups: any[] = Array.isArray(hooksObj[event]) ? hooksObj[event] : [];
    for (const g of groups) {
      if (!g || !Array.isArray(g.hooks)) continue;
      const before = g.hooks.length;
      g.hooks = g.hooks.filter(
        (h: any) => !(typeof h?.command === "string" && h.command.includes(needle)),
      );
      removed += before - g.hooks.length;
    }
    hooksObj[event] = groups.filter((g) => !(g && Array.isArray(g.hooks) && g.hooks.length === 0));
  }
  return removed;
}

// Does this settings `hooks` object already carry a hook targeting the given kit file?
// (Command-suffix match, so it is agnostic to where the stable kit lives.) Used by `doctor` to
// surface a flag/hook mismatch on an already-wired machine.
export function hasHookTargeting(hooksObj: any, fileBasename: string): boolean {
  if (!hooksObj || typeof hooksObj !== "object") return false;
  const needle = `${fileBasename}"`;
  for (const event of Object.keys(hooksObj)) {
    const groups: any[] = Array.isArray(hooksObj[event]) ? hooksObj[event] : [];
    for (const g of groups) {
      if (!g || !Array.isArray(g.hooks)) continue;
      if (g.hooks.some((h: any) => typeof h?.command === "string" && h.command.includes(needle))) {
        return true;
      }
    }
  }
  return false;
}

export type ApplyResult = {
  installed: boolean; // a hook entry was added by this call
  pruned: number; // hook entries removed by this call
  changed: boolean; // installed || pruned > 0 — false on an idempotent re-run and on `keep`
};

// Apply a decision to one agent's settings `hooks` object (mutated in place).
//   install — add the exact command under UserPromptSubmit unless byte-identical one is present
//             (idempotent: a second run reports changed=false)
//   prune   — drop every hook targeting prompt-rag.ts (idempotent: nothing left → changed=false)
//   keep    — do nothing at all (the sticky path: an installed hook survives a plain re-wire)
export function applyPromptRagHook(
  hooksObj: any,
  action: PromptRagHookAction,
  command: string,
): ApplyResult {
  if (action === "keep") return { installed: false, pruned: 0, changed: false };
  if (action === "prune") {
    const pruned = pruneHooksTargeting(hooksObj, PROMPT_RAG_FILE);
    return { installed: false, pruned, changed: pruned > 0 };
  }
  const groups: any[] = Array.isArray(hooksObj[PROMPT_RAG_EVENT]) ? hooksObj[PROMPT_RAG_EVENT] : [];
  const already = groups.some(
    (g) => Array.isArray(g?.hooks) && g.hooks.some((h: any) => h?.command === command),
  );
  if (already) {
    hooksObj[PROMPT_RAG_EVENT] = groups;
    return { installed: false, pruned: 0, changed: false };
  }
  groups.push({ hooks: [{ type: "command", command }] });
  hooksObj[PROMPT_RAG_EVENT] = groups;
  return { installed: true, pruned: 0, changed: true };
}

// ---- doctor: flag/hook mismatch on an already-wired machine ---------------------------------

// A project whose registry flag says prompt-RAG is ON while the machine carries no hook to run it
// (the exact state the sticky-prune bug produced), or the inverse. Cheap, offline, advisory.
export type PromptRagMismatch = {
  kind: "flag-on-hook-missing" | "hook-installed-no-project";
  message: string;
};

export function checkPromptRagWiring(input: {
  entries: RegistryEntry[];
  claudeHooks: unknown;
  droidHooks: unknown;
}): PromptRagMismatch[] {
  const enabled = input.entries.filter(
    (e) => !!e && typeof e.promptRag === "object" && e.promptRag !== null && e.promptRag.enabled === true,
  );
  const claude = hasHookTargeting(input.claudeHooks, PROMPT_RAG_FILE);
  const droid = hasHookTargeting(input.droidHooks, PROMPT_RAG_FILE);
  const out: PromptRagMismatch[] = [];
  if (enabled.length > 0 && !claude && !droid) {
    out.push({
      kind: "flag-on-hook-missing",
      message:
        `prompt-RAG: ${enabled.length} project(s) have promptRag.enabled=true ` +
        `(${enabled.map((e) => e.project).join(", ")}) but NO UserPromptSubmit hook targets prompt-rag.ts ` +
        `in ~/.claude/settings.json or ~/.factory/settings.json — the flag is on, nothing runs it. ` +
        `Re-run: npx petbox-wire <dir> <project> --prompt-rag`,
    });
  }
  if (enabled.length === 0 && (claude || droid)) {
    out.push({
      kind: "hook-installed-no-project",
      message:
        "prompt-RAG: the global UserPromptSubmit hook is installed but no registered project has " +
        "promptRag.enabled=true — it self-gates to a silent no-op everywhere (harmless; " +
        "`--no-prompt-rag` on the last enabled project removes it).",
    });
  }
  return out;
}
