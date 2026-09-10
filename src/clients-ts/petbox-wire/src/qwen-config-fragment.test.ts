// Unit tests for qwen-config-fragment.ts — the printed-fragment renderer and live-config
// divergence check that replaced wire.ts's old REPLACE-merge write of `modelProviders`/
// `agents.modelGrades` (task wire-print-config-fragment, owner decision 09.09.2026).
//
// Run: node --test src/qwen-config-fragment.test.ts   (Node >= 23.6 native TS type-stripping)

import assert from "node:assert/strict";
import { test } from "node:test";
import {
  buildQwenModelGradesFragment,
  buildQwenModelNameFragment,
  buildQwenModelProvidersFragment,
  buildQwenProviderProtocolFragment,
  findQwenConfigDivergence,
  findQwenConfigFragmentLossage,
  renderQwenConfigFragmentText,
  QWEN_OUTBOUND_CORRELATION_FRAGMENT,
} from "./qwen-config-fragment.ts";
import { makeRoleBinding, type Profile, ROLES_FORMAT_VERSION, type RolesFile } from "./roles.ts";

function rolesWith(qwenRoles: Record<string, string>, opts: { other?: Record<string, string> } = {}): RolesFile {
  const profiles: Record<string, Profile> = {
    default: {
      agents: {
        qwen: {
          roles: Object.fromEntries(Object.entries(qwenRoles).map(([r, m]) => [r, makeRoleBinding("qwen", m)])),
        },
      },
    },
  };
  if (opts.other) {
    profiles["alt"] = {
      agents: {
        qwen: {
          roles: Object.fromEntries(Object.entries(opts.other).map(([r, m]) => [r, makeRoleBinding("qwen", m)])),
        },
      },
    };
  }
  return { formatVersion: ROLES_FORMAT_VERSION, activeProfile: "default", profiles };
}

// ---- fragment content --------------------------------------------------------------------

test("buildQwenModelProvidersFragment: both provider keys, measured contextWindowSize per model, customHeaders template only on opencode-go", () => {
  const fragment: any = buildQwenModelProvidersFragment();
  const deepseekPro = fragment.deepseek.find((m: any) => m.id === "ds-deepseek-v4-pro");
  assert.equal(deepseekPro.generationConfig.contextWindowSize, 1048576);
  assert.equal(deepseekPro.generationConfig.extra_body.model, "deepseek-v4-pro");
  assert.equal(deepseekPro.generationConfig.customHeaders, undefined);

  const glmFlash = fragment["opencode-go"].find((m: any) => m.id === "go-glm-5.3-flash");
  assert.equal(glmFlash.generationConfig.contextWindowSize, 1048576);
  assert.equal(glmFlash.generationConfig.customHeaders["x-opencode-session"], "${session_id}");

  const qwenMax = fragment["opencode-go"].find((m: any) => m.id === "go-qwen3.8-max");
  assert.equal(qwenMax.generationConfig.contextWindowSize, 983616);
});

test("buildQwenProviderProtocolFragment: both keys map to openai", () => {
  assert.deepEqual(buildQwenProviderProtocolFragment(), { deepseek: "openai", "opencode-go": "openai" });
});

test("QWEN_OUTBOUND_CORRELATION_FRAGMENT: consent flag is true", () => {
  assert.equal(QWEN_OUTBOUND_CORRELATION_FRAGMENT.allowDynamicHeaderValues, true);
});

// ---- modelGrades reacts to roles.json (acceptance #4) ------------------------------------

test("buildQwenModelGradesFragment: lists exactly the bound ids, self-keyed, 'inherit'/empty skipped", () => {
  const data = rolesWith({ orchestrator: "openai:ds-deepseek-v4-pro", worker: "openai:go-glm-5.3-flash", reserve: "inherit", explore: "" });
  const { grades, unrecognizedIds } = buildQwenModelGradesFragment(data);
  assert.deepEqual(grades, {
    "openai:ds-deepseek-v4-pro": "openai:ds-deepseek-v4-pro",
    "openai:go-glm-5.3-flash": "openai:go-glm-5.3-flash",
  });
  assert.deepEqual(unrecognizedIds, []);
});

test("buildQwenModelGradesFragment: a rebind (petbox-wire model set) changes the printed grades", () => {
  const before = buildQwenModelGradesFragment(rolesWith({ worker: "openai:ds-deepseek-v4-flash" })).grades;
  const after = buildQwenModelGradesFragment(rolesWith({ worker: "openai:go-qwen3.8-max" })).grades;
  assert.notDeepEqual(before, after);
  assert.ok("openai:go-qwen3.8-max" in after);
  assert.ok(!("openai:ds-deepseek-v4-flash" in after));
});

test("buildQwenModelGradesFragment: a bound id outside the kit's catalog is still graded, but flagged unrecognized", () => {
  const { grades, unrecognizedIds } = buildQwenModelGradesFragment(rolesWith({ worker: "openai:totally-bogus-model" }));
  assert.ok("openai:totally-bogus-model" in grades);
  assert.deepEqual(unrecognizedIds, ["totally-bogus-model"]);
});

// role-model-bindings-review-refactor, remainder E: mirrors codex-config-fragment.test.ts's
// "acceptance #6 on the check side" — a binding that lives only in a NON-active profile must never
// leak into the printed fragment or its divergence check (the same defect #6 shape codex's own
// catalog already had fixed; this file's grades builder still had it until this change).
test("acceptance #6 on the check side (qwen): a stale binding in a NON-active profile produces no grade and no warning", () => {
  const data = rolesWith({ orchestrator: "openai:ds-deepseek-v4-pro" }, { other: { reserve: "openai:go-qwen3.8-max" } });
  const { grades, unrecognizedIds } = buildQwenModelGradesFragment(data);
  assert.deepEqual(grades, { "openai:ds-deepseek-v4-pro": "openai:ds-deepseek-v4-pro" });
  assert.deepEqual(unrecognizedIds, []);
  const { warnings } = findQwenConfigDivergence(
    {
      modelProviders: buildQwenModelProvidersFragment(),
      providerProtocol: buildQwenProviderProtocolFragment(),
      outboundCorrelation: QWEN_OUTBOUND_CORRELATION_FRAGMENT,
      agents: { modelGrades: grades },
      model: { name: "ds-deepseek-v4-pro" },
    },
    data,
  );
  assert.deepEqual(warnings, []);
});

// ---- model.name reacts to the orchestrator's own binding (task qwen-model-name-into-fragment) --

test("buildQwenModelNameFragment: bare id of the active profile's qwen orchestrator binding", () => {
  const data = rolesWith({ orchestrator: "openai:ds-deepseek-v4-flash" });
  assert.equal(buildQwenModelNameFragment(data), "ds-deepseek-v4-flash");
});

test("buildQwenModelNameFragment: rebinding the orchestrator role (petbox-wire model set) changes the printed value", () => {
  const before = buildQwenModelNameFragment(rolesWith({ orchestrator: "openai:ds-deepseek-v4-pro" }));
  const after = buildQwenModelNameFragment(rolesWith({ orchestrator: "openai:go-qwen3.8-max" }));
  assert.notEqual(before, after);
  assert.equal(after, "go-qwen3.8-max");
});

test("buildQwenModelNameFragment: falls back to the kit's default seed when the active profile has no orchestrator binding", () => {
  const data = rolesWith({ worker: "openai:ds-deepseek-v4-flash" }); // no orchestrator role at all
  assert.equal(buildQwenModelNameFragment(data), "ds-deepseek-v4-pro");
});

// ---- rendered text --------------------------------------------------------------------

test("renderQwenConfigFragmentText: valid JSON containing modelProviders/providerProtocol/security/agents/model", () => {
  const text = renderQwenConfigFragmentText(rolesWith({ orchestrator: "openai:ds-deepseek-v4-pro" }));
  const jsonStart = text.indexOf("{");
  const parsed = JSON.parse(text.slice(jsonStart));
  assert.ok(parsed.modelProviders.deepseek.length === 2);
  assert.ok(parsed.modelProviders["opencode-go"].length === 2);
  assert.deepEqual(parsed.providerProtocol, { deepseek: "openai", "opencode-go": "openai" });
  // Top-level key, NOT nested under `security` — qwen's own settingsSchema.ts has no such nested
  // key at all (defect qwen-outbound-correlation-warning-checks-wrong-nesting-level).
  assert.equal(parsed.outboundCorrelation.allowDynamicHeaderValues, true);
  assert.deepEqual(parsed.agents.modelGrades, { "openai:ds-deepseek-v4-pro": "openai:ds-deepseek-v4-pro" });
  assert.deepEqual(parsed.model, { name: "ds-deepseek-v4-pro" });
});

// ---- divergence detection (acceptance #5) ------------------------------------------------

function expectedLiveSettings(data: RolesFile): any {
  const text = renderQwenConfigFragmentText(data);
  return JSON.parse(text.slice(text.indexOf("{")));
}

test("findQwenConfigDivergence: a live config exactly matching the roster has zero warnings", () => {
  const data = rolesWith({ orchestrator: "openai:ds-deepseek-v4-pro" });
  const live = expectedLiveSettings(data);
  const { warnings, notes } = findQwenConfigDivergence(live, data);
  assert.deepEqual(warnings, []);
  assert.deepEqual(notes, []);
});

test("findQwenConfigDivergence: completely empty live config -> one warning per missing provider entry/protocol/flag/grade", () => {
  const data = rolesWith({ orchestrator: "openai:ds-deepseek-v4-pro" });
  const { warnings } = findQwenConfigDivergence({}, data);
  assert.ok(warnings.some((w) => w.includes('modelProviders.deepseek: missing entry id="ds-deepseek-v4-pro"')));
  assert.ok(warnings.some((w) => w.includes('modelProviders.opencode-go: missing entry id="go-glm-5.3-flash"')));
  assert.ok(warnings.some((w) => w.includes("providerProtocol.deepseek")));
  assert.ok(warnings.some((w) => w.includes("outboundCorrelation.allowDynamicHeaderValues")));
  assert.ok(warnings.some((w) => w.includes("agents.modelGrades: missing grade")));
});

test("findQwenConfigDivergence: this task's exact motivating harm — static uuid instead of ${session_id} template, and a shrunk contextWindowSize", () => {
  const data = rolesWith({ orchestrator: "openai:ds-deepseek-v4-pro" });
  const live = expectedLiveSettings(data);
  live.modelProviders.deepseek[0].generationConfig.contextWindowSize = 200000; // qwen's own default fallback
  live.modelProviders["opencode-go"][0].generationConfig.customHeaders["x-opencode-session"] =
    "b6f1c2b0-0000-4a11-8888-abcdefabcdef"; // resolved-at-install-time, never changes again
  const { warnings } = findQwenConfigDivergence(live, data);
  assert.ok(warnings.some((w) => w.includes("contextWindowSize: live=200000 expected=1048576")));
  const headerWarning = warnings.find((w) => w.includes("x-opencode-session"));
  assert.ok(headerWarning);
  assert.ok(headerWarning!.includes("one prompt-cache bucket"));
});

// ---- outboundCorrelation nesting (acceptance #3, defect
// qwen-outbound-correlation-warning-checks-wrong-nesting-level) -----------------------------

test("findQwenConfigDivergence: outboundCorrelation is read at the TOP LEVEL, not under security — a live file shaped like qwen's own settingsSchema.ts (security holding only auth) trips no false warning", () => {
  const data = rolesWith({ orchestrator: "openai:ds-deepseek-v4-pro" });
  const live = expectedLiveSettings(data);
  // Mirror the owner's real ~/.qwen/settings.json shape measured live: `security` never gets an
  // `outboundCorrelation` child at all — qwen itself only ever writes/reads the flag top-level.
  live.security = { auth: { selectedType: "openai" } };
  const { warnings } = findQwenConfigDivergence(live, data);
  assert.deepEqual(
    warnings,
    [],
    `outboundCorrelation lives top-level in qwen's own schema; a live file shaped that way (with ` +
      `no nested security.outboundCorrelation at all) must not trip a false "every request 400s" ` +
      `warning. Got:\n${JSON.stringify(warnings, null, 2)}`,
  );
});

test("findQwenConfigDivergence: a live file with the OLD wrong nesting (security.outboundCorrelation, no top-level key) is correctly flagged as missing", () => {
  const data = rolesWith({ orchestrator: "openai:ds-deepseek-v4-pro" });
  const live = expectedLiveSettings(data);
  delete live.outboundCorrelation;
  live.security = { outboundCorrelation: QWEN_OUTBOUND_CORRELATION_FRAGMENT }; // the pre-fix shape
  const { warnings } = findQwenConfigDivergence(live, data);
  assert.ok(
    warnings.some((w) => w.includes("outboundCorrelation.allowDynamicHeaderValues") && w.includes('live=undefined')),
    `a flag nested under security (qwen does not read it there) must still warn — it is genuinely ` +
      `absent from the top level qwen's schema actually checks. Got:\n${JSON.stringify(warnings, null, 2)}`,
  );
});

test("findQwenConfigDivergence: an extra hand-added modelGrade is informational, not a warning", () => {
  const data = rolesWith({ orchestrator: "openai:ds-deepseek-v4-pro" });
  const live = expectedLiveSettings(data);
  live.agents.modelGrades["openai:some-extra-model"] = "openai:some-extra-model";
  const { warnings, notes } = findQwenConfigDivergence(live, data);
  assert.deepEqual(warnings, []);
  assert.ok(notes.some((n) => n.includes("openai:some-extra-model")));
});

// ---- model.name drift (task qwen-model-name-into-fragment, acceptance #4) ----------------

test("findQwenConfigDivergence: model.name UNSET is not a mismatch (fresh machine — qwen falls back to the first registered model, same as with it set)", () => {
  const data = rolesWith({ orchestrator: "openai:ds-deepseek-v4-pro" });
  const live = expectedLiveSettings(data);
  delete live.model; // simulate a live settings.json that never had model.name at all
  const { warnings } = findQwenConfigDivergence(live, data);
  assert.deepEqual(warnings, []);
});

test("findQwenConfigDivergence: model.name matching a registered id (e.g. the owner's own /model pick) has zero warnings", () => {
  const data = rolesWith({ orchestrator: "openai:ds-deepseek-v4-pro" });
  const live = expectedLiveSettings(data);
  live.model.name = "go-qwen3.8-max"; // owner picked a DIFFERENT registered model via /model
  const { warnings } = findQwenConfigDivergence(live, data);
  assert.deepEqual(warnings, []);
});

test("findQwenConfigDivergence: model.name set to a stale/unregistered value warns, and the fragment is not mutated to fix it", () => {
  const data = rolesWith({ orchestrator: "openai:ds-deepseek-v4-pro" });
  const live = expectedLiveSettings(data);
  live.model.name = "coder-model"; // defect qwen-dead-default-model's exact stale value
  const before = JSON.stringify(live);
  const { warnings } = findQwenConfigDivergence(live, data);
  assert.ok(warnings.some((w) => w.includes('model.name: live="coder-model"') && w.includes("does NOT fix this")));
  assert.equal(JSON.stringify(live), before); // never mutated
});

// ---- fragment loss on paste (acceptance #1, defect
// qwen-printed-fragment-silently-reverts-effort-and-maxtokens) --------------------------------
//
// Fixture SHAPED like the owner's real ~/.qwen/settings.json (measured live 2026-09-10, six
// modelProviders entries): two owner-registered effort variants (`ds-deepseek-v4-pro-max`,
// `go-glm-5.3-flash-low`) the kit's catalog does not know, and every one of the six entries
// carries `extra_body.reasoning_effort` + `generationConfig.samplingParams.max_tokens` the
// fragment's own buildProviderModelObject never reproduces.
function sixEntryOwnerFixture(data: RolesFile): any {
  const live = expectedLiveSettings(data);
  const withEffortAndTokens = (entry: any, effort: string, maxTokens: number) => ({
    ...entry,
    generationConfig: {
      ...entry.generationConfig,
      extra_body: { ...entry.generationConfig.extra_body, reasoning_effort: effort },
      samplingParams: { max_tokens: maxTokens },
    },
  });
  const dsPro = live.modelProviders.deepseek.find((m: any) => m.id === "ds-deepseek-v4-pro");
  const dsFlash = live.modelProviders.deepseek.find((m: any) => m.id === "ds-deepseek-v4-flash");
  const dsProMax = { ...withEffortAndTokens(dsPro, "max", 65536), id: "ds-deepseek-v4-pro-max", name: "DeepSeek V4 Pro (direct, effort=max)" };
  live.modelProviders.deepseek = [withEffortAndTokens(dsPro, "high", 65536), dsProMax, withEffortAndTokens(dsFlash, undefined as any, 65536)];
  delete live.modelProviders.deepseek[2].generationConfig.extra_body.reasoning_effort; // measured: flash has none

  const goFlash = live.modelProviders["opencode-go"].find((m: any) => m.id === "go-glm-5.3-flash");
  const goMax = live.modelProviders["opencode-go"].find((m: any) => m.id === "go-qwen3.8-max");
  const goFlashLow = { ...withEffortAndTokens(goFlash, "low", 131072), id: "go-glm-5.3-flash-low", name: "GLM 5.3 Flash (opencode-go, effort=low)" };
  live.modelProviders["opencode-go"] = [
    withEffortAndTokens(goFlash, "high", 131072),
    goFlashLow,
    withEffortAndTokens(goMax, "xhigh", 131072),
  ];
  return live;
}

test("findQwenConfigDivergence: the gate stays BLIND to the owner's six-entry layout (documents why the loss warning cannot live there)", () => {
  const data = rolesWith({ orchestrator: "openai:ds-deepseek-v4-pro" });
  const live = sixEntryOwnerFixture(data);
  const { warnings } = findQwenConfigDivergence(live, data);
  assert.deepEqual(
    warnings,
    [],
    `the divergence check only compares wireModel/contextWindowSize/customHeaders per catalog id ` +
      `— it must report zero warnings here even though pasting the fragment would destroy two ` +
      `entries and four reasoning_effort/max_tokens settings. Got:\n${JSON.stringify(warnings, null, 2)}`,
  );
});

test("findQwenConfigFragmentLossage: the owner's six-entry copy — two entries dropped outright, four lose reasoning_effort/samplingParams", () => {
  const data = rolesWith({ orchestrator: "openai:ds-deepseek-v4-pro" });
  const live = sixEntryOwnerFixture(data);
  const lossage = findQwenConfigFragmentLossage(live);

  const dropped = lossage.filter((l) => l.entryDropped).map((l) => l.id);
  assert.deepEqual(dropped.sort(), ["ds-deepseek-v4-pro-max", "go-glm-5.3-flash-low"]);

  const shrunk = lossage.filter((l) => !l.entryDropped);
  assert.deepEqual(
    shrunk.map((l) => l.id).sort(),
    ["ds-deepseek-v4-flash", "ds-deepseek-v4-pro", "go-glm-5.3-flash", "go-qwen3.8-max"],
  );
  const dsProLoss = shrunk.find((l) => l.id === "ds-deepseek-v4-pro")!;
  assert.ok(dsProLoss.lostFields.some((f) => f.includes("reasoning_effort") && f.includes("high")));
  assert.ok(dsProLoss.lostFields.some((f) => f.includes("samplingParams")));
});

test("renderQwenConfigFragmentText: with liveSettings, the printed text LOUDLY names every entry/setting a paste would destroy", () => {
  const data = rolesWith({ orchestrator: "openai:ds-deepseek-v4-pro" });
  const live = sixEntryOwnerFixture(data);
  const text = renderQwenConfigFragmentText(data, live);
  assert.match(text, /LOSS WARNING/);
  assert.match(text, /ds-deepseek-v4-pro-max/);
  assert.match(text, /go-glm-5\.3-flash-low/);
  assert.match(text, /reasoning_effort/);
  assert.match(text, /samplingParams/);
  // The warning comes BEFORE the ready-to-paste JSON, not buried after it.
  assert.ok(text.indexOf("LOSS WARNING") < text.indexOf("Paste these top-level keys"));
});

test("renderQwenConfigFragmentText: omitting liveSettings prints no loss warning (fresh machine, nothing to compare against)", () => {
  const data = rolesWith({ orchestrator: "openai:ds-deepseek-v4-pro" });
  const text = renderQwenConfigFragmentText(data);
  assert.doesNotMatch(text, /LOSS WARNING/);
});

test("renderQwenConfigFragmentText: a live file with no extra entries/settings prints no loss warning", () => {
  const data = rolesWith({ orchestrator: "openai:ds-deepseek-v4-pro" });
  const live = expectedLiveSettings(data); // exactly what the fragment itself would produce
  const text = renderQwenConfigFragmentText(data, live);
  assert.doesNotMatch(text, /LOSS WARNING/);
});
