// Renders codex's `model_providers.*` / `model_provider` / `model` / `model_catalog_json` config
// — and the model catalog file that last key points at — as PRINTED TEXT for the owner to paste
// into `$CODEX_HOME/config.toml` (and save alongside it) by hand, and detects when the LIVE
// config+catalog have drifted from what the kit's own roster (roles.json's ACTIVE-profile codex
// bindings) expects.
//
// WHY THIS EXISTS (task wire-codex-config-print-fragment, owner decision 09.09.2026 verbatim:
// «и назад тоже. все унифицировать»): the same scope boundary already applied to qwen
// (qwen-config-fragment.ts) applied BACKWARDS to codex. wire.ts's installGlobalHooks used to
// write all four keys plus a whole `$CODEX_HOME/petbox-model-catalog.json` on every run. It now
// writes none of them: the kit's share of codex is the hook block, the MCP entry and the role
// files, and provider/model/window/header config belongs to the human.
//
// THE TRAP THIS MODULE EXISTS TO DEFUSE (task body, "Главная ловушка"): for codex the catalog is
// not merely cosmetic sizing. A slug ABSENT from the catalog silently loses the `apply_patch`
// tool entirely (9 tools instead of 10 — measured, task wire-support-codex-qwen) and silently
// gets a 272000 context window from codex's fallback metadata. Exit 0, no warning, from codex or
// from anything else. So "the kit simply stops writing it" would have degraded every codex role
// on the owner's machine quietly — the exact failure class this whole line of work exists against.
// Two consequences, both requirements rather than niceties:
//   1. the print is LOUD (console.error at the call site in wire.ts, consequence spelled out), and
//   2. a role bound to a slug the LIVE catalog does not carry WARNS, naming apply_patch and the
//      window — findCodexConfigDivergence below.
//
// The static `x-opencode-session` uuid the kit used to mint (one per install, written into
// `model_providers.opencode-go.http_headers`) is deliberately NOT reproduced here: a fixed value
// collapses every conversation on the machine into one prompt-cache bucket (deepseek-harness#5495
// — cache invalidation, higher cost, slower). Codex sends its own native session header and the
// gateway accepts it (measured: `session-id` → 200, task wire-codex-config-print-fragment). The
// printed block says so and leaves the choice to the human.
//
// Plain TS for native node type-stripping: zero deps beyond roles.ts / codex-model-catalog.ts /
// codex-toml.ts.

import { resolveAgentRoles, CODEX_ROLE_MODEL_SEED, type RolesFile } from "./roles.ts";
import { CODEX_MODEL_PROVIDER } from "./binding-provider.ts";
import {
  buildCodexModelCatalogFromData,
  CODEX_UNCATALOGUED_CONTEXT_WINDOW,
  codexContextWindowFor,
  collectActiveCodexRoleModelSlugsFromData,
} from "./codex-model-catalog.ts";
import {
  getRootScalar,
  parseTomlBlocks,
  buildDeepseekProviderBlock,
  findBlock,
  getBlockScalar,
  tomlLiteralString,
  tomlString,
} from "./codex-toml.ts";

/** The catalog file name the kit recommends inside `$CODEX_HOME` — historically the exact file
 * the kit used to WRITE there, kept identical so an existing install's `model_catalog_json`
 * pointer keeps resolving after the kit stops maintaining the file. */
export const CODEX_CATALOG_FILE_NAME = "petbox-model-catalog.json";

/** `model_provider` for a bare `codex`/`codex exec` run. Codex pins ONE provider per process (a
 * role file's own `model_provider` field is accepted and silently dropped — measured, task
 * wire-support-codex-qwen), so this is a single machine-wide choice, not a per-role one.
 *
 * The literal itself lives in binding-provider.ts, because the SAME value is what every codex role
 * binding's `provider` label resolves to (roles.json format v2): a codex model value is a bare slug
 * that cannot express a provider, so the process-level choice is the only truthful answer, and two
 * copies of it would drift. Aliased rather than moved so this module's own name keeps working. */
export const CODEX_DEFAULT_PROVIDER = CODEX_MODEL_PROVIDER;

/** A TOML value for a filesystem path: literal (single-quoted) so Windows backslashes are never
 * read as escape introducers — codex aborts loading the WHOLE config.toml on the first invalid
 * one (defect codex-mcp-inert-untrusted-project). Falls back to a basic escaped string for the
 * one shape a literal string cannot represent (a path containing a single quote). */
function tomlPathValue(path: string): string {
  return path.includes("'") ? tomlString(path) : tomlLiteralString(path);
}

/** Strip TOML quoting off a scalar read back out of a config file: `'literal'` verbatim,
 * `"basic"` with its backslash/quote escapes undone. Anything else is returned as-is. */
export function unquoteTomlScalar(raw: string): string {
  const v = raw.trim();
  if (v.length >= 2 && v.startsWith("'") && v.endsWith("'")) return v.slice(1, -1);
  if (v.length >= 2 && v.startsWith('"') && v.endsWith('"')) {
    return v
      .slice(1, -1)
      .replace(/\\n/g, "\n")
      .replace(/\\r/g, "\r")
      .replace(/\\t/g, "\t")
      .replace(/\\"/g, '"')
      .replace(/\\\\/g, "\\");
  }
  return v;
}

/** The path a live `$CODEX_HOME/config.toml` currently points `model_catalog_json` at, or
 * undefined when the key is absent entirely (which is itself the loudest divergence there is —
 * see findCodexConfigDivergence). The caller resolves it against `$CODEX_HOME` and reads it; this
 * module never touches disk. */
export function readCodexCatalogPathFromConfig(configText: string): string | undefined {
  const raw = getRootScalar(parseTomlBlocks(configText), "model_catalog_json");
  if (raw === undefined) return undefined;
  const path = unquoteTomlScalar(raw);
  return path.trim() === "" ? undefined : path;
}

/** Bare slug the root `model` key should carry — the ACTIVE profile's own codex `orchestrator`
 * binding, so `petbox-wire model set orchestrator <slug> --agent codex` changes the printed
 * fragment (acceptance #4). Falls back to the kit's own seed when the active profile binds no
 * orchestrator yet. Mirrors qwen-config-fragment.ts's buildQwenModelNameFragment exactly. */
export function buildCodexDefaultModelFragment(data: RolesFile): string {
  return (
    resolveAgentRoles(data, "codex")["orchestrator"] ??
    CODEX_ROLE_MODEL_SEED["orchestrator"] ??
    "deepseek-v4-pro"
  );
}

/** The `[model_providers.opencode-go]` body. NO `http_headers`: see this file's header for why the
 * kit no longer mints a static `x-opencode-session` uuid. The commentary rides along in the
 * printed text (renderCodexConfigFragmentText), not in the block, so the block stays paste-able
 * as-is. */
function opencodeGoProviderLinesWithoutHeaders(): string[] {
  return [
    `name = ${tomlString("opencode Zen Go")}`,
    `base_url = ${tomlString("https://opencode.ai/zen/go/v1")}`,
    `env_key = ${tomlString("OPENCODE_GO_API_KEY")}`,
    `wire_api = ${tomlString("responses")}`,
  ];
}

export type CodexConfigFragment = {
  /** The complete printable text (both parts + notes). */
  readonly text: string;
  /** The catalog object part 1 tells the owner to save — exposed so callers/tests can assert on
   * it without re-parsing the printed text. */
  readonly catalog: { readonly models: Record<string, unknown>[] };
  readonly slugs: readonly string[];
  readonly unmeasuredSlugs: readonly string[];
};

/**
 * Render the complete, ready-to-paste codex fragment (acceptance #3): the catalog file's exact
 * JSON, then the `config.toml` keys, then any honesty notes. Not a diff and not a merge
 * instruction — the literal text the owner saves and pastes.
 *
 * `catalogPath` is the absolute path the printed `model_catalog_json` will point at (the caller
 * knows `$CODEX_HOME`; this module does not).
 */
export function renderCodexConfigFragmentText(data: RolesFile, catalogPath: string): CodexConfigFragment {
  const built = buildCodexModelCatalogFromData(data);
  const defaultModel = buildCodexDefaultModelFragment(data);

  const lines: string[] = [
    "=== CODEX CONFIG FRAGMENT — the kit does NOT write these (owner decision 09.09.2026) ===",
    "",
    "Codex gets the `apply_patch` tool and its real context window ONLY from a model catalog.",
    "A slug missing from the catalog silently loses `apply_patch` (9 tools instead of 10,",
    `measured) and runs on codex's ${CODEX_UNCATALOGUED_CONTEXT_WINDOW} fallback window — exit 0, no warning from codex.`,
    "Save BOTH parts, or every codex role below runs degraded and silent.",
    "",
    `--- part 1/2: save this file as ${catalogPath}`,
    JSON.stringify(built.catalog, null, 2),
    "",
    "--- part 2/2: merge these keys into $CODEX_HOME/config.toml",
    "# Root scalars go FIRST: in TOML a bare `key = value` written after a [table] header belongs",
    "# to that table, not to the root.",
    `model_provider = ${tomlString(CODEX_DEFAULT_PROVIDER)}`,
    `model = ${tomlString(defaultModel)}`,
    `model_catalog_json = ${tomlPathValue(catalogPath)}`,
    "",
    "[model_providers.deepseek]",
    ...buildDeepseekProviderBlock(),
    "",
    "[model_providers.opencode-go]",
    ...opencodeGoProviderLinesWithoutHeaders(),
    "# No `http_headers` here on purpose. The gateway needs a session header, but a SINGLE static",
    "# uuid per install (what the kit used to mint) collapses every conversation on this machine",
    "# into one prompt-cache bucket — cache invalidation, higher cost, slower (deepseek-harness#5495).",
    "# Codex sends its own native session header and the gateway accepts it (measured: session-id → 200).",
    "# If you want an explicit one anyway, it is now your choice to add:",
    '#   http_headers = { "x-opencode-session" = "<your value>" }',
  ];

  const notes: string[] = [];
  if (built.source === "fallback") {
    notes.push(
      `the active profile ("${data.activeProfile}") binds no codex model at all, so the catalog ` +
        `above is the kit's historical 3-slug default (${built.slugs.join(", ")}) rather than your ` +
        `own bindings. \`petbox-wire model set <role> <slug> --agent codex\` and re-run to derive it.`,
    );
  }
  if (built.unmeasuredSlugs.length > 0) {
    notes.push(
      `context_window for ${built.unmeasuredSlugs.join(", ")} is NOT a measurement — the kit has ` +
        `no endpoint measurement for these slugs, so they carry codex's own uncatalogued fallback ` +
        `(${CODEX_UNCATALOGUED_CONTEXT_WINDOW}). The catalog entry still buys them \`apply_patch\`; the window is a ` +
        `placeholder you may want to correct.`,
    );
  }
  if (notes.length > 0) {
    lines.push("", "NOTES:");
    for (const n of notes) lines.push(`  - ${n}`);
  }

  return { text: lines.join("\n"), catalog: built.catalog, slugs: built.slugs, unmeasuredSlugs: built.unmeasuredSlugs };
}

// ---- live-config divergence -------------------------------------------------

export type CodexLiveConfig = {
  /** Raw `$CODEX_HOME/config.toml` text — "" when the file does not exist. */
  readonly configText: string;
  /** The catalog JSON parsed from wherever the live `model_catalog_json` points, or undefined
   * when the key is absent / the file is missing or unparsable (`catalogError` then says which). */
  readonly catalog: unknown;
  readonly catalogError?: string | undefined;
};

export type CodexConfigDivergence = {
  /** Concrete mismatches — the live config does not deliver what the roster requires. Non-empty
   * means the printed fragment should be pasted (or reconciled) by the owner. */
  readonly warnings: readonly string[];
  /** Informational-only observations that are not mismatches — never counted toward "does the
   * live config already match the roster". */
  readonly notes: readonly string[];
};

type LiveCatalogEntry = { slug?: unknown; apply_patch_tool_type?: unknown; context_window?: unknown };

function liveCatalogEntries(catalog: unknown): LiveCatalogEntry[] {
  if (!catalog || typeof catalog !== "object") return [];
  const models = (catalog as Record<string, unknown>)["models"];
  return Array.isArray(models) ? (models as LiveCatalogEntry[]) : [];
}

/** Which ACTIVE-profile codex roles are bound to each slug — so the missing-slug warning can name
 * the roles that actually degrade, not just the slug. */
function rolesBySlug(data: RolesFile): ReadonlyMap<string, string[]> {
  const out = new Map<string, string[]>();
  for (const [role, model] of Object.entries(resolveAgentRoles(data, "codex"))) {
    const slug = model.trim();
    if (!slug || slug === "inherit") continue;
    const list = out.get(slug);
    if (list) list.push(role);
    else out.set(slug, [role]);
  }
  return out;
}

const UNCATALOGUED_CONSEQUENCE =
  "codex then runs it UNCATALOGUED: the `apply_patch` tool disappears entirely (9 tools instead " +
  `of 10, measured) and the context window silently becomes ${CODEX_UNCATALOGUED_CONTEXT_WINDOW} — exit 0, no warning`;

/**
 * Compare a live `$CODEX_HOME/config.toml` + the catalog it points at against what the kit's
 * roster (roles.json's ACTIVE-profile codex bindings) expects. `warnings` empty means the live
 * config already delivers it and nothing needs pasting. Never touches disk, never mutates input.
 */
export function findCodexConfigDivergence(live: CodexLiveConfig, data: RolesFile): CodexConfigDivergence {
  const warnings: string[] = [];
  const notes: string[] = [];
  const blocks = parseTomlBlocks(live.configText);

  // --- providers -------------------------------------------------------------------------
  const deepseekExpected: Readonly<Record<string, string>> = {
    base_url: tomlString("https://api.deepseek.com/v1"),
    env_key: tomlString("DEEPSEEK_API_KEY"),
    wire_api: tomlString("responses"),
  };
  if (!findBlock(blocks, "model_providers.deepseek")) {
    warnings.push(
      "model_providers.deepseek: missing — with no provider table, codex cannot reach the direct " +
        "DeepSeek subscription every codex role is bound to.",
    );
  } else {
    for (const [key, expected] of Object.entries(deepseekExpected)) {
      const liveValue = getBlockScalar(blocks, "model_providers.deepseek", key);
      if (liveValue !== expected) {
        warnings.push(`model_providers.deepseek.${key}: live=${liveValue ?? "(absent)"} expected=${expected}`);
      }
    }
  }
  if (!findBlock(blocks, "model_providers.opencode-go")) {
    // NOT a warning: no role binds through this leg today — it documents the owner's second
    // subscription so a future rebinding is one config edit, not a rewrite.
    notes.push("model_providers.opencode-go: absent (informational — no codex role binds through it today)");
  } else {
    const header = getBlockScalar(blocks, "model_providers.opencode-go", "http_headers");
    if (header && /x-opencode-session/.test(header)) {
      notes.push(
        "model_providers.opencode-go.http_headers pins a static x-opencode-session value. That is " +
          "yours to keep or drop now (the kit no longer writes it) — note a fixed value collapses " +
          "every conversation on this machine into one prompt-cache bucket; codex's own native " +
          "session header is accepted by the gateway on its own (measured: session-id → 200).",
      );
    }
  }

  // --- root scalars ----------------------------------------------------------------------
  const liveProvider = getRootScalar(blocks, "model_provider");
  if (liveProvider === undefined) {
    warnings.push(
      `model_provider: absent — codex pins ONE provider per process, so without it every request ` +
        `goes to codex's own built-in default instead of "${CODEX_DEFAULT_PROVIDER}".`,
    );
  } else if (unquoteTomlScalar(liveProvider) !== CODEX_DEFAULT_PROVIDER) {
    notes.push(
      `model_provider: live=${liveProvider} (the kit's fragment recommends ` +
        `"${CODEX_DEFAULT_PROVIDER}") — informational, an operator may deliberately run another leg.`,
    );
  }

  const boundSlugs = collectActiveCodexRoleModelSlugsFromData(data);
  const catalogSlugs = new Set(
    liveCatalogEntries(live.catalog)
      .map((m) => (typeof m.slug === "string" ? m.slug : ""))
      .filter((s) => s !== ""),
  );

  const liveModelRaw = getRootScalar(blocks, "model");
  if (liveModelRaw === undefined) {
    warnings.push(
      "model: absent — a bare `codex`/`codex exec` run then asks the configured provider for " +
        "codex's own built-in default model, which the DeepSeek endpoint does not serve.",
    );
  }

  // --- catalog ---------------------------------------------------------------------------
  const catalogPath = readCodexCatalogPathFromConfig(live.configText);
  if (catalogPath === undefined) {
    warnings.push(
      `model_catalog_json: absent — EVERY codex role on this machine (${boundSlugs.join(", ") || "none bound"}) ` +
        `runs uncatalogued. ${UNCATALOGUED_CONSEQUENCE}.`,
    );
  } else if (live.catalogError !== undefined) {
    warnings.push(
      `model_catalog_json points at ${catalogPath}, which could not be read as a catalog ` +
        `(${live.catalogError}) — codex falls back to uncatalogued metadata for every slug. ` +
        `${UNCATALOGUED_CONSEQUENCE}.`,
    );
  } else {
    if (catalogSlugs.size === 0) {
      warnings.push(
        `the live catalog at ${catalogPath} carries no usable \`models[].slug\` entries at all — ` +
          `${UNCATALOGUED_CONSEQUENCE}.`,
      );
    }
    const byRole = rolesBySlug(data);
    for (const slug of boundSlugs) {
      const roles = byRole.get(slug) ?? [];
      const who = roles.length > 0 ? `role(s) ${roles.join(", ")}` : "an active-profile binding";
      if (!catalogSlugs.has(slug)) {
        warnings.push(
          `live catalog ${catalogPath} has no entry for "${slug}", which ${who} in profile ` +
            `"${data.activeProfile}" is bound to — ${UNCATALOGUED_CONSEQUENCE}.`,
        );
        continue;
      }
      const entry = liveCatalogEntries(live.catalog).find((m) => m.slug === slug)!;
      if (entry.apply_patch_tool_type !== "freeform") {
        warnings.push(
          `live catalog entry "${slug}" has apply_patch_tool_type=` +
            `${JSON.stringify(entry.apply_patch_tool_type)} expected "freeform" — ${who} loses the ` +
            `\`apply_patch\` tool (9 tools instead of 10, measured).`,
        );
      }
      const { contextWindow, measured } = codexContextWindowFor(slug);
      if (measured && entry.context_window !== contextWindow) {
        warnings.push(
          `live catalog entry "${slug}" has context_window=${JSON.stringify(entry.context_window)} ` +
            `expected ${contextWindow} (measured against the provider endpoint itself) — a lower ` +
            `value silently makes codex compact far earlier than the model requires.`,
        );
      }
    }
    for (const slug of catalogSlugs) {
      if (!boundSlugs.includes(slug)) {
        // Not a mismatch: an entry for a model no active-profile role uses costs nothing.
        notes.push(`live catalog ${catalogPath} carries "${slug}", which no active-profile codex role binds (informational)`);
      }
    }
  }

  if (liveModelRaw !== undefined) {
    const liveModel = unquoteTomlScalar(liveModelRaw);
    if (catalogSlugs.size > 0 && !catalogSlugs.has(liveModel)) {
      warnings.push(
        `model: live="${liveModel}" has no entry in the live catalog — a bare \`codex\` run ` +
          `(no role file) then ${UNCATALOGUED_CONSEQUENCE}.`,
      );
    }
  }

  return { warnings, notes };
}
