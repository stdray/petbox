// Reads `$QWEN_HOME/settings.json` → `modelProviders` LIVE — the ONE place that answers "what
// does THIS MACHINE actually have registered, and under which `modelProviders` key" for qwen.
//
// WHY THIS IS ITS OWN LEAF (defect `qwen-binding-provider-null-for-live-registered-id`, fixed
// same day as the v2 `provider` field landed): model-validity.ts's qwen gate already read this
// file to answer "is this id known on this machine". binding-provider.ts answered a DIFFERENT
// question — "which subscription serves it" — from a SEPARATE, hardcoded id list
// (qwen-model-registry.ts) that has never seen an operator-registered id. The owner registered
// `ds-deepseek-v4-pro-max` / `go-glm-5.3-flash-low`; the validity gate correctly called them
// "verified — registered on this machine", and `provider` still came back `null`, because the
// gate and the deriver were reading two different sources about the same file. This module is the
// ONE live reader both now share, so they cannot diverge again.
//
// Sync, local-file-only (~0ms, per model-validity.ts's own B1 measurement) — no network, no
// spawn. Kept dependency-light (fs/path/qwen-paths.ts only, no roles.ts) on purpose: roles.ts
// imports binding-provider.ts (qwen-model-registry.ts's header explains why), so a module
// binding-provider.ts imports must not import anything that imports roles.ts back — this one
// doesn't.
//
// Plain TS for native node type-stripping: zero deps beyond node's own stdlib + qwen-paths.ts.

import { readFileSync } from "node:fs";
import { join } from "node:path";

import { qwenHomeDir } from "./qwen-paths.ts";

export type LiveQwenProviders =
  | { readonly ok: true; readonly idToProviderKey: ReadonlyMap<string, string>; readonly path: string }
  | { readonly ok: false; readonly path: string; readonly reason: string };

/**
 * `$QWEN_HOME/settings.json` → every registered bare model id mapped to the `modelProviders` key
 * that registers it.
 *
 * `ok: false` covers every "not configured (yet)" shape uniformly — missing file, unreadable
 * file, unparseable JSON, no `modelProviders` key, or one that registers zero ids — because a
 * caller must treat all of them the same way: "nothing was learned", never "no providers exist".
 * That distinction (unverified vs. invalid) is model-validity.ts's own rule; this module only
 * supplies the one fact both call sites need, not the verdict.
 */
export function readLiveQwenProviders(homeDir: string): LiveQwenProviders {
  const path = join(qwenHomeDir(homeDir), "settings.json");
  let text: string;
  try {
    text = readFileSync(path, "utf8");
  } catch (e) {
    const code = (e as NodeJS.ErrnoException).code;
    return {
      ok: false,
      path,
      reason: code === "ENOENT" ? "the file does not exist" : `it could not be read (${code ?? (e instanceof Error ? e.message : String(e))})`,
    };
  }
  let parsed: unknown;
  try {
    parsed = JSON.parse(text);
  } catch (e) {
    return { ok: false, path, reason: `it is not valid JSON (${e instanceof Error ? e.message : String(e)})` };
  }
  const providers = (parsed as { modelProviders?: unknown } | null)?.modelProviders;
  if (providers === undefined || providers === null || typeof providers !== "object") {
    return { ok: false, path, reason: "it declares no `modelProviders` key at all — not configured yet" };
  }
  const idToProviderKey = new Map<string, string>();
  for (const [key, entries] of Object.entries(providers as Record<string, unknown>)) {
    if (!Array.isArray(entries)) continue;
    for (const entry of entries) {
      const id = (entry as { id?: unknown } | null)?.id;
      if (typeof id === "string" && id.trim()) idToProviderKey.set(id.trim(), key);
    }
  }
  if (idToProviderKey.size === 0) {
    return { ok: false, path, reason: "`modelProviders` registers no model ids yet — not configured yet" };
  }
  return { ok: true, idToProviderKey, path };
}
