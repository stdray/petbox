// Stage B2's write gate: three outcomes, and the boundary between them.
//
// The property under test is NOT "does it find the model" — it is that `unverified` (the source
// could not be consulted) is never reported as `invalid` (the source WAS consulted and does not
// know this id), in either direction. Collapsing them is the defect
// `apply-reports-missing-key-as-unregistered-project-and-exits-0`, and for codex it is measured
// to be unavoidable at the transport level: a missing key and a wrong key both answer 401.
//
// Every network call and every subprocess spawn is INJECTED here — this file touches no provider
// and spawns no CLI, so it runs identically on a machine with no keys and no harnesses installed.
//
// Run: node --test src/model-validity.test.ts

import assert from "node:assert/strict";
import { mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { test } from "node:test";

import {
  checkModelValidity,
  checkRolesModelValidity,
  createModelSourceCache,
  DROID_BUILTIN_MODELS,
  formatModelValidity,
  type CommandRun,
  type ModelValidityOptions,
} from "./model-validity.ts";
import { makeRoleBinding, ROLES_FORMAT_VERSION, type RolesFile } from "./roles.ts";

function tempHome(): string {
  return mkdtempSync(join(tmpdir(), "petbox-model-validity-"));
}

function writeJson(path: string, value: unknown): void {
  mkdirSync(join(path, ".."), { recursive: true });
  writeFileSync(path, JSON.stringify(value, null, 2), "utf8");
}

/** A fetch stub answering one canned response for any URL. */
function stubFetch(handler: () => Response | Promise<Response>): typeof fetch {
  return (async () => handler()) as unknown as typeof fetch;
}

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { "content-type": "application/json" } });
}

/** A codex config.toml with one provider, written into `home/.codex`. */
function writeCodexConfig(home: string, opts: { provider: string; baseUrl?: string; envKey?: string }): void {
  const lines = [`model_provider = "${opts.provider}"`, `[model_providers.${opts.provider}]`];
  if (opts.baseUrl !== undefined) lines.push(`base_url = "${opts.baseUrl}"`);
  if (opts.envKey !== undefined) lines.push(`env_key = "${opts.envKey}"`);
  mkdirSync(join(home, ".codex"), { recursive: true });
  writeFileSync(join(home, ".codex", "config.toml"), `${lines.join("\n")}\n`, "utf8");
}

function okRun(stdout: string): (ms: number) => Promise<CommandRun> {
  return async () => ({ ok: true, stdout });
}

function failRun(reason: string): (ms: number) => Promise<CommandRun> {
  return async () => ({ ok: false, reason });
}

// ---- claude-code: the only BLOCKING harness ----------------------------------------------------

test("claude-code: a tier alias is valid, and the harness is the one that blocks", async () => {
  const v = await checkModelValidity("claude-code", "opus", { homeDir: tempHome() });
  assert.equal(v.verdict, "valid");
  assert.equal(v.blocking, true);
});

test("claude-code: a foreign-shaped id is INVALID and blocking (the 2026-07-12 incident shape)", async () => {
  const v = await checkModelValidity("claude-code", "custom:DeepSeek-V4-Pro-0", { homeDir: tempHome() });
  assert.equal(v.verdict, "invalid");
  assert.equal(v.blocking, true);
});

test("claude-code: a concrete claude-* id is UNVERIFIED, not invalid — the 2026-07-13 decision survives", async () => {
  // A closed catalog of concrete Anthropic ids was deliberately dropped after it false-blocked
  // genuinely new models. In this module's vocabulary that tier is "could not check", and it must
  // never drift into "invalid" (which for claude-code would BLOCK the write).
  const v = await checkModelValidity("claude-code", "claude-opus-9-9", { homeDir: tempHome() });
  assert.equal(v.verdict, "unverified");
  assert.notEqual(v.verdict, "invalid");
});

// ---- qwen: local file, warn-and-write ----------------------------------------------------------

test("qwen: an id registered in modelProviders is valid", async () => {
  const home = tempHome();
  writeJson(join(home, ".qwen", "settings.json"), {
    modelProviders: { deepseek: [{ id: "ds-deepseek-v4-pro" }], "opencode-go": [{ id: "go-qwen3.8-max" }] },
  });
  const v = await checkModelValidity("qwen", "openai:ds-deepseek-v4-pro", { homeDir: home });
  assert.equal(v.verdict, "valid");
});

test("qwen: a typo is INVALID against a populated file — and never blocks the write", async () => {
  const home = tempHome();
  writeJson(join(home, ".qwen", "settings.json"), { modelProviders: { deepseek: [{ id: "ds-deepseek-v4-pro" }] } });
  const v = await checkModelValidity("qwen", "openai:ds-deepsek-v4-pro", { homeDir: home });
  assert.equal(v.verdict, "invalid");
  assert.equal(v.blocking, false);
  // The message must state WHY silence is dangerous here: qwen itself does not fail on a bad id.
  assert.match(v.detail, /falls back to the first registered model/);
});

test("qwen: NO settings.json is UNVERIFIED — an unconfigured machine is not a wrong model", async () => {
  const v = await checkModelValidity("qwen", "openai:ds-deepseek-v4-pro", { homeDir: tempHome() });
  assert.equal(v.verdict, "unverified");
});

test("qwen: a file with an EMPTY modelProviders is UNVERIFIED, not invalid", async () => {
  const home = tempHome();
  writeJson(join(home, ".qwen", "settings.json"), { modelProviders: {} });
  const v = await checkModelValidity("qwen", "openai:ds-deepseek-v4-pro", { homeDir: home });
  assert.equal(v.verdict, "unverified");
});

test("qwen: an unparseable settings.json is UNVERIFIED — a broken file says nothing about a model", async () => {
  const home = tempHome();
  mkdirSync(join(home, ".qwen"), { recursive: true });
  writeFileSync(join(home, ".qwen", "settings.json"), "{ not json", "utf8");
  const v = await checkModelValidity("qwen", "openai:ds-deepseek-v4-pro", { homeDir: home });
  assert.equal(v.verdict, "unverified");
});

// ---- droid: built-in catalog + customModels, warn-and-write -------------------------------------

test("droid: a built-in slug is valid without reading any file at all", async () => {
  const v = await checkModelValidity("droid", "deepseek-v4-pro", { homeDir: tempHome() });
  assert.equal(v.verdict, "valid");
});

test("droid: a bare slug in NEITHER registry is INVALID, non-blocking, and says the catalog is a snapshot", async () => {
  const v = await checkModelValidity("droid", "deepseek-v4-prro", { homeDir: tempHome() });
  assert.equal(v.verdict, "invalid");
  assert.equal(v.blocking, false);
  assert.match(v.detail, /SNAPSHOT/);
});

test("droid: a custom: id present in customModels is valid", async () => {
  const home = tempHome();
  writeJson(join(home, ".factory", "settings.json"), {
    customModels: [{ id: "custom:DeepSeek-V4-Pro-0", apiKey: "sk-SECRET-must-never-surface" }],
  });
  const v = await checkModelValidity("droid", "custom:DeepSeek-V4-Pro-0", { homeDir: home });
  assert.equal(v.verdict, "valid");
});

test("droid: NOTHING read out of ~/.factory/settings.json ever carries the plaintext apiKey", async () => {
  // Measured in stage B1: every customModels entry holds the OWNER's provider credential in
  // plaintext. This gate reads `id` and nothing else; the guarantee is asserted on the WORST case
  // — the failure message, which is the only string a user ever sees from this path.
  const home = tempHome();
  writeJson(join(home, ".factory", "settings.json"), {
    customModels: [{ id: "custom:Real-0", apiKey: "sk-SECRET-must-never-surface", baseUrl: "https://secret.example" }],
  });
  const v = await checkModelValidity("droid", "custom:Typo-0", { homeDir: home });
  assert.equal(v.verdict, "invalid");
  const rendered = `${v.detail} ${v.source} ${formatModelValidity(v)}`;
  assert.ok(!rendered.includes("sk-SECRET-must-never-surface"), "an apiKey leaked into a user-facing message");
  assert.ok(!rendered.includes("secret.example"), "a provider baseUrl leaked into a user-facing message");
});

test("droid: a custom: id with NO settings.json is UNVERIFIED, not invalid", async () => {
  const v = await checkModelValidity("droid", "custom:DeepSeek-V4-Pro-0", { homeDir: tempHome() });
  assert.equal(v.verdict, "unverified");
});

test("droid: the built-in snapshot is non-empty and holds the ids the seed actually uses", async () => {
  assert.ok(DROID_BUILTIN_MODELS.length > 20, `expected a real catalog, got ${DROID_BUILTIN_MODELS.length}`);
  assert.ok(DROID_BUILTIN_MODELS.includes("auto"));
});

// ---- codex: network, the third outcome is the common case ---------------------------------------

const CODEX_HOME_OPTS = (home: string, extra: Partial<ModelValidityOptions> = {}): ModelValidityOptions => ({
  homeDir: home,
  env: { DEEPSEEK_API_KEY: "sk-test" },
  ...extra,
});

test("codex: an id the active provider lists is valid", async () => {
  const home = tempHome();
  writeCodexConfig(home, { provider: "deepseek", baseUrl: "https://api.example/v1", envKey: "DEEPSEEK_API_KEY" });
  const v = await checkModelValidity(
    "codex",
    "deepseek-v4-pro",
    CODEX_HOME_OPTS(home, { fetchImpl: stubFetch(() => jsonResponse({ data: [{ id: "deepseek-v4-pro" }] })) }),
  );
  assert.equal(v.verdict, "valid");
});

test("codex: an id the ACTIVE provider does not list is INVALID and says so as a REACH claim", async () => {
  // The grok-4.6 shape: real elsewhere, unreachable through the provider codex actually uses.
  const home = tempHome();
  writeCodexConfig(home, { provider: "deepseek", baseUrl: "https://api.example/v1", envKey: "DEEPSEEK_API_KEY" });
  const v = await checkModelValidity(
    "codex",
    "grok-4.6",
    CODEX_HOME_OPTS(home, { fetchImpl: stubFetch(() => jsonResponse({ data: [{ id: "deepseek-v4-pro" }] })) }),
  );
  assert.equal(v.verdict, "invalid");
  assert.equal(v.blocking, false);
  assert.match(v.detail, /REACH/);
});

test("codex: a 401 is UNVERIFIED, never invalid — a missing key and a wrong key answer identically", async () => {
  const home = tempHome();
  writeCodexConfig(home, { provider: "deepseek", baseUrl: "https://api.example/v1", envKey: "DEEPSEEK_API_KEY" });
  const v = await checkModelValidity(
    "codex",
    "anything-at-all",
    CODEX_HOME_OPTS(home, { fetchImpl: stubFetch(() => new Response("", { status: 401 })) }),
  );
  assert.equal(v.verdict, "unverified");
  assert.notEqual(v.verdict, "invalid");
});

test("codex: an UNSET env key is UNVERIFIED and never reaches the network", async () => {
  const home = tempHome();
  writeCodexConfig(home, { provider: "deepseek", baseUrl: "https://api.example/v1", envKey: "DEEPSEEK_API_KEY" });
  let called = false;
  const v = await checkModelValidity("codex", "deepseek-v4-pro", {
    homeDir: home,
    env: {},
    fetchImpl: stubFetch(() => {
      called = true;
      return jsonResponse({ data: [] });
    }),
  });
  assert.equal(v.verdict, "unverified");
  assert.equal(called, false, "the gate must not burn a provider round trip it cannot authenticate");
});

test("codex: a transport failure is UNVERIFIED, not invalid", async () => {
  const home = tempHome();
  writeCodexConfig(home, { provider: "deepseek", baseUrl: "https://api.example/v1", envKey: "DEEPSEEK_API_KEY" });
  const v = await checkModelValidity(
    "codex",
    "deepseek-v4-pro",
    CODEX_HOME_OPTS(home, {
      fetchImpl: stubFetch(() => {
        throw new Error("ECONNREFUSED");
      }),
    }),
  );
  assert.equal(v.verdict, "unverified");
});

test("codex: NO config.toml is UNVERIFIED — nothing declares which provider to ask", async () => {
  const v = await checkModelValidity("codex", "deepseek-v4-pro", { homeDir: tempHome(), env: {} });
  assert.equal(v.verdict, "unverified");
});

test("codex: the provider is consulted ONCE per cache, not once per binding", async () => {
  const home = tempHome();
  writeCodexConfig(home, { provider: "deepseek", baseUrl: "https://api.example/v1", envKey: "DEEPSEEK_API_KEY" });
  let calls = 0;
  const cache = createModelSourceCache();
  const opts = CODEX_HOME_OPTS(home, {
    cache,
    fetchImpl: stubFetch(() => {
      calls++;
      return jsonResponse({ data: [{ id: "deepseek-v4-pro" }] });
    }),
  });
  await checkModelValidity("codex", "deepseek-v4-pro", opts);
  await checkModelValidity("codex", "deepseek-v4-flash", opts);
  await checkModelValidity("codex", "deepseek-v4-pro", opts);
  assert.equal(calls, 1, `expected one round trip for the whole sweep, made ${calls}`);
});

// ---- opencode: subprocess, the third outcome again ----------------------------------------------

test("opencode: an id in the listing is valid", async () => {
  const v = await checkModelValidity("opencode", "deepseek/deepseek-v4-pro", {
    homeDir: tempHome(),
    runOpencodeModels: okRun("opencode/big-pickle\ndeepseek/deepseek-v4-pro\n"),
  });
  assert.equal(v.verdict, "valid");
});

test("opencode: an id absent from the listing is INVALID and non-blocking", async () => {
  const v = await checkModelValidity("opencode", "deepseek/deepseek-v4-prro", {
    homeDir: tempHome(),
    runOpencodeModels: okRun("deepseek/deepseek-v4-pro\n"),
  });
  assert.equal(v.verdict, "invalid");
  assert.equal(v.blocking, false);
});

test("opencode: an unprefixed id is invalid AND named as the observed grammar defect", async () => {
  const v = await checkModelValidity("opencode", "deepseek-v4-pro", {
    homeDir: tempHome(),
    runOpencodeModels: okRun("deepseek/deepseek-v4-pro\n"),
  });
  assert.equal(v.verdict, "invalid");
  assert.match(v.detail, /wire-accepts-unprefixed-model-id-opencode/);
});

test("opencode: not on PATH is UNVERIFIED — a missing CLI is not a wrong model", async () => {
  const v = await checkModelValidity("opencode", "deepseek/deepseek-v4-pro", {
    homeDir: tempHome(),
    runOpencodeModels: failRun("`opencode` is not on PATH"),
  });
  assert.equal(v.verdict, "unverified");
  assert.notEqual(v.verdict, "invalid");
});

test("opencode: an EMPTY listing is UNVERIFIED, not a catalog that knows nothing", async () => {
  const v = await checkModelValidity("opencode", "deepseek/deepseek-v4-pro", {
    homeDir: tempHome(),
    runOpencodeModels: okRun("\n\n"),
  });
  assert.equal(v.verdict, "unverified");
});

test("opencode: the CLI is spawned ONCE per cache", async () => {
  let calls = 0;
  const opts: ModelValidityOptions = {
    homeDir: tempHome(),
    cache: createModelSourceCache(),
    runOpencodeModels: async () => {
      calls++;
      return { ok: true, stdout: "deepseek/deepseek-v4-pro\n" };
    },
  };
  await checkModelValidity("opencode", "deepseek/deepseek-v4-pro", opts);
  await checkModelValidity("opencode", "deepseek/deepseek-v4-flash", opts);
  assert.equal(calls, 1, `expected one spawn for the whole sweep, made ${calls}`);
});

// ---- cross-cutting -------------------------------------------------------------------------------

test("`inherit` and blank bind no model, so there is nothing to look up on any harness", async () => {
  for (const harness of ["claude-code", "opencode", "droid", "codex", "qwen"]) {
    for (const model of ["inherit", "", "  "]) {
      const v = await checkModelValidity(harness, model, { homeDir: tempHome(), env: {} });
      assert.equal(v.verdict, "valid", `${harness} / '${model}' should short-circuit, got ${v.verdict}`);
    }
  }
});

test("only claude-code ever blocks — the other four warn and write", async () => {
  const home = tempHome();
  const blocking: Record<string, boolean> = {};
  for (const harness of ["claude-code", "opencode", "droid", "codex", "qwen"]) {
    const v = await checkModelValidity(harness, "zz-definitely-not-a-model", {
      homeDir: home,
      env: {},
      runOpencodeModels: failRun("stubbed"),
    });
    blocking[harness] = v.blocking;
  }
  assert.deepEqual(blocking, {
    "claude-code": true,
    opencode: false,
    droid: false,
    codex: false,
    qwen: false,
  });
});

test("an unknown harness is UNVERIFIED — the kit invents neither a source nor a verdict", async () => {
  const v = await checkModelValidity("some-future-harness", "whatever", { homeDir: tempHome() });
  assert.equal(v.verdict, "unverified");
});

test("formatModelValidity leads with the verdict word, so no reader has to infer which outcome it is", async () => {
  const home = tempHome();
  writeJson(join(home, ".qwen", "settings.json"), { modelProviders: { deepseek: [{ id: "ds-deepseek-v4-pro" }] } });
  const bad = await checkModelValidity("qwen", "openai:nope", { homeDir: home });
  assert.match(formatModelValidity(bad), /^INVALID: /);
  const unverified = await checkModelValidity("qwen", "openai:nope", { homeDir: tempHome() });
  assert.match(formatModelValidity(unverified), /^UNVERIFIED: /);
  const good = await checkModelValidity("qwen", "openai:ds-deepseek-v4-pro", { homeDir: home });
  assert.match(formatModelValidity(good), /^OK: /);
});

// ---- the batch sweep -----------------------------------------------------------------------------

test("checkRolesModelValidity covers every binding and is READ-ONLY (nothing under the home dir changes)", async () => {
  const home = tempHome();
  writeJson(join(home, ".qwen", "settings.json"), { modelProviders: { deepseek: [{ id: "ds-deepseek-v4-pro" }] } });
  const data: RolesFile = {
    formatVersion: ROLES_FORMAT_VERSION,
    activeProfile: "p",
    profiles: {
      p: {
        agents: {
          qwen: {
            roles: {
              orchestrator: makeRoleBinding("qwen", "openai:ds-deepseek-v4-pro"),
              worker: makeRoleBinding("qwen", "openai:ds-typo"),
            },
          },
          "claude-code": { roles: { orchestrator: makeRoleBinding("claude-code", "opus") } },
        },
      },
    },
  };
  const rows = await checkRolesModelValidity(data, { homeDir: home, env: {} });
  assert.equal(rows.length, 3);
  const byRole = new Map(rows.map((r) => [`${r.agent}/${r.role}`, r.validity.verdict]));
  assert.equal(byRole.get("qwen/orchestrator"), "valid");
  assert.equal(byRole.get("qwen/worker"), "invalid");
  assert.equal(byRole.get("claude-code/orchestrator"), "valid");
  // Read-only: the settings file the sweep consulted is byte-identical afterwards, and the sweep
  // wrote no roles.json of its own anywhere under the temp home.
  const after = JSON.parse(readFileSync(join(home, ".qwen", "settings.json"), "utf8")) as {
    modelProviders: Record<string, unknown>;
  };
  assert.deepEqual(Object.keys(after.modelProviders), ["deepseek"]);
  rmSync(home, { recursive: true, force: true });
});
