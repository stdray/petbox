// Renders the qwen `modelProviders`/`providerProtocol`/`agents.modelGrades`/`outboundCorrelation`/
// `model.name` config as PRINTED TEXT for the owner to paste into `$QWEN_HOME/settings.json` by
// hand, and detects when a live settings.json has drifted from what the kit's own roster (qwen-
// model-catalog.ts + roles.json role→model bindings) expects.
//
// `model.name` (task qwen-model-name-into-fragment, owner decision 09.09.2026) is a SEPARATE class
// from the other four keys above: those are pure kit-derived config with no user-facing meaning of
// their own, but `model.name` is qwen's OWN user-choice key — persisted by qwen itself, paired with
// `model.baseUrl`, every time the owner picks a model via `/model`. The kit used to overwrite only
// the `name` half of that pair every `wire` run, silently reverting the owner's own pick (measured:
// a stale `model.baseUrl` then matches no provider, and qwen falls back to a hardcoded default
// leg). So `model.name` is PRINTED (a recommendation, derived from the qwen `orchestrator` role's
// current binding) exactly like the other four, and never written.
//
// WHY THIS EXISTS (task wire-print-config-fragment, owner decision 09.09.2026): wire.ts's
// installGlobalHooks used to WRITE these keys into $QWEN_HOME/settings.json every run
// (`modelProviders`/`providerProtocol` merge as REPLACE) — on a hand-configured machine this
// silently destroyed a working three-leg layout: `${session_id}` collapsed to a static uuid,
// `contextWindowSize` vanished (~1M context fell back to a 200k default), and `modelGrades`
// shrank to two ids. The fix is not "merge more carefully" — it is to stop writing these keys at
// all and print a ready-to-paste fragment instead, so the human stays the only writer of their
// own provider/model/header/context layout (see wiki `qwen-three-provider-legs-howto` for the
// full manual howto this fragment is the compiled, roles.json-driven form of).
//
// Plain TS for native node type-stripping: zero deps beyond roles.ts and qwen-model-catalog.ts.

import { QWEN_ROLE_MODEL_SEED, resolveAgentRoles, type RolesFile } from "./roles.ts";
import {
  collectActiveQwenRoleModelIdsFromData,
  findQwenModelEntry,
  qwenRegisteredModelIds,
  QWEN_DEEPSEEK_MODELS,
  QWEN_OPENCODE_GO_MODELS,
  type QwenModelEntry,
} from "./qwen-model-catalog.ts";

// The opencode-go gateway 400s on every request without a session header (measured, opencode-go
// docs); `${session_id}` is qwen's OWN per-request template placeholder (QwenLM/qwen-code#11282,
// merged 2026-09-08, shipping in ≥0.23.1) — NEVER a resolved uuid here, unlike codex's static-uuid
// workaround (wire.ts's opencodeSessionUuid), because qwen can actually resolve this dynamically
// per conversation and a static value would collapse every conversation onto one prompt-cache
// bucket (this task's whole motivating harm #1).
const SESSION_HEADER_TEMPLATE = "${session_id}";

/** `security.outboundCorrelation.allowDynamicHeaderValues` — the consent flag `${session_id}`
 * expansion is gated behind (no host allowlist exists upstream, so qwen requires an explicit
 * opt-in). Measured live 09.09.2026 (wiki `qwen-three-provider-legs-howto` §2): without it qwen
 * prints a startup warning and drops the header entirely, and the gateway then 400s. */
export const QWEN_OUTBOUND_CORRELATION_FRAGMENT = { allowDynamicHeaderValues: true } as const;

function providerBaseUrl(providerKey: "deepseek" | "opencode-go"): string {
  return providerKey === "deepseek" ? "https://api.deepseek.com/v1" : "https://opencode.ai/zen/go/v1";
}

function providerEnvKey(providerKey: "deepseek" | "opencode-go"): string {
  return providerKey === "deepseek" ? "DEEPSEEK_API_KEY" : "OPENCODE_GO_API_KEY";
}

function buildProviderModelObject(m: QwenModelEntry, providerKey: "deepseek" | "opencode-go"): Record<string, unknown> {
  const generationConfig: Record<string, unknown> = {
    extra_body: { model: m.wireModel },
    contextWindowSize: m.contextWindowSize,
  };
  if (providerKey === "opencode-go") {
    generationConfig["customHeaders"] = { "x-opencode-session": SESSION_HEADER_TEMPLATE };
  }
  return {
    id: m.id,
    name: m.name,
    baseUrl: providerBaseUrl(providerKey),
    envKey: providerEnvKey(providerKey),
    generationConfig,
  };
}

/** The full `modelProviders` object — always BOTH provider keys and all four registered ids,
 * independent of roles.json bindings: this is what "paste into an empty config and get a working
 * three-leg layout" (acceptance #3) requires — a role binding only ever narrows which of these a
 * role USES, never which providers exist. */
export function buildQwenModelProvidersFragment(): Record<string, unknown> {
  return {
    deepseek: QWEN_DEEPSEEK_MODELS.map((m) => buildProviderModelObject(m, "deepseek")),
    "opencode-go": QWEN_OPENCODE_GO_MODELS.map((m) => buildProviderModelObject(m, "opencode-go")),
  };
}

export function buildQwenProviderProtocolFragment(): Record<string, string> {
  return { deepseek: "openai", "opencode-go": "openai" };
}

/** `agents.modelGrades`, self-keyed (grade name == the value it resolves to), listing EXACTLY the
 * ids currently bound in roles.json's ACTIVE PROFILE — not the whole catalog, and not the union
 * across every profile (task role-model-bindings-review-refactor, remainder E: the same defect #6
 * shape codex's catalog already had fixed — a stale binding in a profile nobody has selected has
 * no business shaping the fragment qwen actually reads). This is what makes the printed fragment
 * react to `petbox-wire model set <role> <id> --agent qwen` (acceptance #4): rebind a role in the
 * active profile, the next fragment print changes. Bound ids the catalog doesn't recognize still
 * get a self-keyed grade (a role can be pointed at a model the kit doesn't know about; the grade
 * gate needs the entry either way) but are called out separately for the caller to notice. */
export function buildQwenModelGradesFragment(data: RolesFile): {
  readonly grades: Record<string, string>;
  readonly unrecognizedIds: readonly string[];
} {
  const ids = collectActiveQwenRoleModelIdsFromData(data);
  const grades: Record<string, string> = {};
  const unrecognizedIds: string[] = [];
  for (const id of ids) {
    grades[`openai:${id}`] = `openai:${id}`;
    if (!findQwenModelEntry(id)) unrecognizedIds.push(id);
  }
  return { grades, unrecognizedIds };
}

/** Bare id `model.name` should be set to for a plain `qwen` run (no `-m` flag) to resolve through
 * the SAME `modelProviders` this fragment recommends (task qwen-model-name-into-fragment, owner
 * decision 09.09.2026) — the active profile's own qwen `orchestrator` role binding, stripped of
 * its `openai:` authType prefix (`model.name` is matched against the bare
 * `modelProviders.<key>[].id`, never the `authType:id` form a role .md file's `model:`
 * frontmatter uses — same rule qwen-model-catalog.ts's `bareQwenModelId` documents). Falls back to
 * the kit's own default seed (`QWEN_ROLE_MODEL_SEED.orchestrator`) when the active profile has no
 * orchestrator binding yet (belt-and-suspenders: step 7c seeds one before this is ever read on a
 * full `wire` run, but `apply`/a hand-edited roles.json could still lack it).
 *
 * PRINTED ONLY, never written by the kit: `model.name` is qwen's OWN user-choice key — it persists
 * it itself as a pair with `model.baseUrl` on every `/model` picker use — and the kit used to
 * clobber half that pair every `wire` run, silently reverting the owner's own pick (defect
 * qwen-dead-default-model's fix over-corrected into a NEW defect this task closes). This function
 * only ever produces a RECOMMENDATION for the pasted fragment. */
export function buildQwenModelNameFragment(data: RolesFile): string {
  // Bracket access (not dot) + a literal final fallback: both maps are plain index-signature
  // records, so under this package's noUncheckedIndexedAccess every lookup types as
  // `string | undefined` — the literal keeps `bound` provably `string` without an unsafe assertion.
  const bound: string =
    resolveAgentRoles(data, "qwen")["orchestrator"] ??
    QWEN_ROLE_MODEL_SEED["orchestrator"] ??
    "ds-deepseek-v4-pro";
  const i = bound.indexOf(":");
  return i === -1 ? bound : bound.slice(i + 1);
}

/** Render the complete, ready-to-paste JSON fragment text (acceptance #3). Not a diff, not a
 * merge instruction — the literal keys/values the owner drops into `$QWEN_HOME/settings.json`
 * top level, replacing whatever those same keys currently hold. */
export function renderQwenConfigFragmentText(data: RolesFile): string {
  const { grades, unrecognizedIds } = buildQwenModelGradesFragment(data);
  const fragment = {
    modelProviders: buildQwenModelProvidersFragment(),
    providerProtocol: buildQwenProviderProtocolFragment(),
    security: { outboundCorrelation: QWEN_OUTBOUND_CORRELATION_FRAGMENT },
    agents: { modelGrades: grades },
    model: { name: buildQwenModelNameFragment(data) },
  };
  const lines = [
    "Paste these top-level keys into $QWEN_HOME/settings.json (they REPLACE whatever those same",
    "keys currently hold — this is not a patch):",
    "",
    JSON.stringify(fragment, null, 2),
  ];
  if (unrecognizedIds.length > 0) {
    lines.push(
      "",
      `NOTE: agents.modelGrades above includes id(s) not in the kit's own qwen-model-catalog.ts ` +
        `(bound by a role but unrecognized): ${unrecognizedIds.join(", ")}. Add a matching ` +
        `modelProviders entry yourself, or rebind the role.`,
    );
  }
  return lines.join("\n");
}

// ---- live-config divergence -------------------------------------------------

function deepEqual(a: unknown, b: unknown): boolean {
  return JSON.stringify(a) === JSON.stringify(b);
}

export type QwenConfigDivergence = {
  /** Concrete mismatches — the live config does not deliver what the roster requires. A
   * non-empty list means the printed fragment should be pasted (or reconciled) by the owner. */
  readonly warnings: readonly string[];
  /** Informational-only observations that are not mismatches (e.g. an extra hand-added grade) —
   * never counted toward "does the live config already match the roster". */
  readonly notes: readonly string[];
};

/**
 * Compare a live, parsed `$QWEN_HOME/settings.json` object against what the kit's roster
 * (qwen-model-catalog.ts + roles.json bindings) expects, field by field. `warnings` is empty when
 * the live config already matches (acceptance #3's "kit says it's present, does not touch" case)
 * — `notes` alone never trips that. Never mutates `liveSettings`.
 */
export function findQwenConfigDivergence(liveSettings: unknown, data: RolesFile): QwenConfigDivergence {
  const warnings: string[] = [];
  const notes: string[] = [];
  const live = (liveSettings && typeof liveSettings === "object" ? liveSettings : {}) as Record<string, unknown>;

  const liveProviders = (live["modelProviders"] && typeof live["modelProviders"] === "object"
    ? (live["modelProviders"] as Record<string, unknown>)
    : {}) as Record<string, unknown>;

  const checkProviderKey = (providerKey: "deepseek" | "opencode-go", entries: readonly QwenModelEntry[]) => {
    const liveArr = Array.isArray(liveProviders[providerKey]) ? (liveProviders[providerKey] as any[]) : [];
    for (const expected of entries) {
      const liveModel = liveArr.find((m) => m && m.id === expected.id);
      if (!liveModel) {
        warnings.push(`modelProviders.${providerKey}: missing entry id="${expected.id}"`);
        continue;
      }
      const liveWireModel = liveModel.generationConfig?.extra_body?.model;
      if (liveWireModel !== expected.wireModel) {
        warnings.push(
          `modelProviders.${providerKey}[id="${expected.id}"].generationConfig.extra_body.model: ` +
            `live="${liveWireModel}" expected="${expected.wireModel}"`,
        );
      }
      const liveCtx = liveModel.generationConfig?.contextWindowSize;
      if (liveCtx !== expected.contextWindowSize) {
        warnings.push(
          `modelProviders.${providerKey}[id="${expected.id}"].generationConfig.contextWindowSize: ` +
            `live=${JSON.stringify(liveCtx)} expected=${expected.contextWindowSize}`,
        );
      }
      if (providerKey === "opencode-go") {
        const liveHeader = liveModel.generationConfig?.customHeaders?.["x-opencode-session"];
        if (liveHeader !== SESSION_HEADER_TEMPLATE) {
          warnings.push(
            `modelProviders.opencode-go[id="${expected.id}"].generationConfig.customHeaders.` +
              `x-opencode-session: live=${JSON.stringify(liveHeader)} expected="${SESSION_HEADER_TEMPLATE}"` +
              (typeof liveHeader === "string" && liveHeader !== "" && liveHeader !== SESSION_HEADER_TEMPLATE
                ? " (a resolved/static value here pins every conversation to one prompt-cache bucket)"
                : ""),
          );
        }
      }
    }
  };
  checkProviderKey("deepseek", QWEN_DEEPSEEK_MODELS);
  checkProviderKey("opencode-go", QWEN_OPENCODE_GO_MODELS);

  const liveProtocol = (live["providerProtocol"] && typeof live["providerProtocol"] === "object"
    ? (live["providerProtocol"] as Record<string, unknown>)
    : {}) as Record<string, unknown>;
  for (const [key, expected] of Object.entries(buildQwenProviderProtocolFragment())) {
    if (liveProtocol[key] !== expected) {
      warnings.push(`providerProtocol.${key}: live=${JSON.stringify(liveProtocol[key])} expected="${expected}"`);
    }
  }

  const liveFlag = (live["security"] as any)?.outboundCorrelation?.allowDynamicHeaderValues;
  if (liveFlag !== QWEN_OUTBOUND_CORRELATION_FRAGMENT.allowDynamicHeaderValues) {
    warnings.push(
      `security.outboundCorrelation.allowDynamicHeaderValues: live=${JSON.stringify(liveFlag)} expected=true ` +
        `(without it every opencode-go request 400s: the header is dropped at startup)`,
    );
  }

  const { grades: expectedGrades } = buildQwenModelGradesFragment(data);
  const liveGrades = ((live["agents"] as any)?.modelGrades ?? {}) as Record<string, unknown>;
  const expectedKeys = Object.keys(expectedGrades);
  const liveKeys = Object.keys(liveGrades);
  const missing = expectedKeys.filter((k) => !(k in liveGrades));
  const extra = liveKeys.filter((k) => !(k in expectedGrades));
  if (missing.length > 0) {
    warnings.push(`agents.modelGrades: missing grade(s) for currently-bound role model(s): ${missing.join(", ")}`);
  }
  for (const k of expectedKeys) {
    if (k in liveGrades && !deepEqual(liveGrades[k], expectedGrades[k])) {
      warnings.push(`agents.modelGrades["${k}"]: live=${JSON.stringify(liveGrades[k])} expected="${expectedGrades[k]}"`);
    }
  }
  if (extra.length > 0) {
    // Not a mismatch — an extra grade (e.g. a model the owner registered by hand) does not block
    // anything and must never trip the "already matches" check on its own.
    notes.push(`agents.modelGrades: extra grade(s) the roster does not require (informational): ${extra.join(", ")}`);
  }

  // model.name (task qwen-model-name-into-fragment, acceptance #4) — WARN ONLY, never touched: an
  // UNSET model.name is not a mismatch (a fresh machine has none, and qwen then falls back to the
  // FIRST modelProviders entry, which this kit's own printed order already puts first — measured
  // byte-identical result, this task's "why zero loss on a fresh machine" section). Only a value
  // that is SET and resolves to nothing is a real drift: qwen silently falls back to the first
  // registered model instead of erroring, exactly the failure mode defect qwen-dead-default-model
  // first found. The kit does not rewrite it — model.name is the owner's own `/model`-picker
  // choice, never the kit's to fix (this is why `notes`/`warnings` here never feed an auto-fix
  // anywhere in this module).
  const liveModelName = (live["model"] as any)?.name;
  if (typeof liveModelName === "string" && liveModelName.trim() !== "") {
    const registered = qwenRegisteredModelIds();
    if (!registered.includes(liveModelName)) {
      warnings.push(
        `model.name: live="${liveModelName}" matches no modelProviders entry the kit registers ` +
          `(${registered.join(", ")}) — a plain 'qwen' run (no -m flag) silently falls back to ` +
          `the FIRST registered model instead. The kit does NOT fix this (it is the owner's own ` +
          `/model choice) — see this fragment's own "model" recommendation below.`,
      );
    }
  }

  return { warnings, notes };
}
