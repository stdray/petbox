// Settings surgery for GLOBAL agent hook files (~/.claude/settings.json, ~/.factory/settings.json).
//
// Why this module exists at all: prompt-RAG (a global UserPromptSubmit hook that injected exact-match
// context) has been REMOVED from the kit. But a machine that ever ran `petbox-wire --prompt-rag` has
// the hook command WRITTEN INTO those two settings files, pointing at `<STABLE>/prompt-rag.ts`. The
// kit no longer ships that file, so the leftover hook would fail on EVERY prompt. Deleting the kit
// files without pruning the settings is therefore not a removal — it is a breakage.
//
// So `wire` runs the prune UNCONDITIONALLY (there is no flag left to gate it on) and idempotently:
// it removes only hook entries whose command targets prompt-rag.ts (both the plain Claude Code
// variant and the `--agent droid` one), leaves every other hook untouched, and a second run is a
// byte-identical no-op (the caller only rewrites the file when something was actually removed).
//
// Lives in its own side-effect-free module (like wire-exit.ts / posix-env.ts / wire-identity.ts)
// precisely so it is importable by a test: wire.ts runs main() at import time and cannot be.
//
// Plain TS for native node type-stripping: no enum/namespace/parameter-properties, zero deps.

// The kit file every legacy prompt-RAG hook command targets, on either agent. Both variants are
//   node "<...>/prompt-rag.ts"                (Claude Code)
//   node "<...>/prompt-rag.ts" --agent droid  (Factory Droid)
// so matching the QUOTED basename catches both without caring where the stable kit lives.
export const LEGACY_PROMPT_RAG_FILE = "prompt-rag.ts";

// Remove EVERY hook (across all events) whose command targets the given STABLE kit file, then drop
// the groups THIS call emptied (and an event key it emptied entirely). Mutates hooksObj in place;
// returns the count pruned. Deliberately conservative:
//   - a non-array event value is left exactly as found (we never rewrite a shape we do not own),
//   - a group that was already empty before this call is left alone (it is not ours to clean),
// so on a settings file with no prompt-rag hook this is a pure read: returns 0, mutates nothing.
export function pruneHooksTargeting(hooksObj: any, fileBasename: string): number {
  if (!hooksObj || typeof hooksObj !== "object") return 0;
  let removed = 0;
  const needle = `${fileBasename}"`;
  for (const event of Object.keys(hooksObj)) {
    if (!Array.isArray(hooksObj[event])) continue;
    const groups: any[] = hooksObj[event];
    const emptiedHere = new Set<any>();
    for (const g of groups) {
      if (!g || !Array.isArray(g.hooks)) continue;
      const before = g.hooks.length;
      g.hooks = g.hooks.filter(
        (h: any) => !(typeof h?.command === "string" && h.command.includes(needle)),
      );
      const gone = before - g.hooks.length;
      removed += gone;
      if (gone > 0 && g.hooks.length === 0) emptiedHere.add(g);
    }
    if (emptiedHere.size === 0) continue;
    const kept = groups.filter((g) => !emptiedHere.has(g));
    if (kept.length === 0) delete hooksObj[event];
    else hooksObj[event] = kept;
  }
  return removed;
}

// Does this settings `hooks` object carry a hook targeting the given kit file? (Quoted-basename
// match, so it is agnostic to where the stable kit lives.) Read-only.
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

// The migration itself: drop every dead prompt-RAG hook from one agent's `hooks` object.
// Returns the number removed (0 = nothing to do = the caller must not rewrite the file).
export function pruneDeadPromptRagHooks(hooksObj: any): number {
  return pruneHooksTargeting(hooksObj, LEGACY_PROMPT_RAG_FILE);
}

// Does this settings `hooks` object still carry a dead prompt-RAG hook?
export function hasDeadPromptRagHook(hooksObj: any): boolean {
  return hasHookTargeting(hooksObj, LEGACY_PROMPT_RAG_FILE);
}
