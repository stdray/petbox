// Unit tests for the roles.json v2 binding schema (task role-model-bindings-review-refactor,
// stage 1): the binding ORIGIN (kit vs owner), the PROVIDER label derived from the harness's own
// model grammar, and the one-shot v1 -> v2 migration that attributes every legacy binding.
//
// The fixtures are not invented shapes: the migration cases below are the owner's real
// ~/.petbox/roles.json as measured on 2026-09-09 (three profiles; ten stale qwen bindings and two
// stale codex `reserve` bindings, all of them values a past version of this kit seeded; alongside
// qwen bindings on `go-*` ids the owner picked himself). That file is the acceptance case for this
// stage, so it is the fixture.
//
// Run: node --test src/binding-schema.test.ts

import assert from "node:assert/strict";
import { test } from "node:test";
import { CODEX_MODEL_PROVIDER, deriveBindingProvider } from "./binding-provider.ts";
import {
  CODEX_ROLE_MODEL_SEED,
  DEFAULT_ROLE_MODEL_SEED,
  findBindingProviderInconsistencies,
  formatResolvedBinding,
  HARNESS_ROLE_MODEL_SEEDS,
  isHistoricalKitSeed,
  makeRoleBinding,
  migrateRolesFile,
  normalizeRoles,
  QWEN_ROLE_MODEL_SEED,
  ROLES_FORMAT_VERSION,
  rewrittenByMigration,
  seedMissingRoleBindings,
  setRoleModel,
  type RolesFile,
} from "./roles.ts";

// ---- provider derivation, one case per harness grammar ----------------------------------------

test("provider: opencode reads the segment before the first '/', and a value with no prefix derives nothing rather than a guess", () => {
  assert.equal(deriveBindingProvider("opencode", "deepseek/deepseek-v4-pro").provider, "deepseek");
  assert.equal(deriveBindingProvider("opencode", "opencode-go/glm-5.3-flash").provider, "opencode-go");
  // Further slashes belong to the model id, not to the provider (lmstudio/openai/gpt-oss-20b).
  assert.equal(deriveBindingProvider("opencode", "lmstudio/openai/gpt-oss-20b").provider, "lmstudio");
  const bare = deriveBindingProvider("opencode", "deepseek-v4-pro");
  assert.equal(bare.provider, null);
  assert.match(bare.reason, /no '<provider>\/' prefix/);
});

test("provider: qwen recovers the modelProviders key from the id itself — the authType prefix is never a provider", () => {
  // Both of the kit's provider keys speak protocol `openai`, so the prefix cannot tell them apart;
  // only the id decoration can.
  assert.equal(deriveBindingProvider("qwen", "openai:ds-deepseek-v4-pro").provider, "deepseek");
  assert.equal(deriveBindingProvider("qwen", "openai:go-glm-5.3-flash").provider, "opencode-go");
  // An id the kit does not register is honest "unknown", never a default.
  assert.equal(deriveBindingProvider("qwen", "openai:deepseek-v4-pro").provider, null);
});

test("provider: codex is the process-level model_provider the kit pins — the value itself can never express it", () => {
  const d = deriveBindingProvider("codex", "deepseek-v4-pro");
  assert.equal(d.provider, CODEX_MODEL_PROVIDER);
  assert.match(d.reason, /ONE model_provider per process/);
});

test("provider: droid names the REGISTRY that resolves the id (custom BYOK vs Factory's built-in), which is all the value can truthfully say", () => {
  assert.equal(deriveBindingProvider("droid", "custom:DeepSeek-V4-Pro-0").provider, "custom");
  assert.equal(deriveBindingProvider("droid", "deepseek-v4-pro").provider, "factory");
});

test("provider: claude-code is anthropic for every value its grammar can express, and null for inherit", () => {
  assert.equal(deriveBindingProvider("claude-code", "opus").provider, "anthropic");
  assert.equal(deriveBindingProvider("claude-code", "fable").provider, "anthropic");
  assert.equal(deriveBindingProvider("claude-code", "claude-opus-4-8[1m]").provider, "anthropic");
  assert.equal(deriveBindingProvider("claude-code", "inherit").provider, null);
});

test("provider: an unbound value or an unknown harness makes NO claim", () => {
  assert.equal(deriveBindingProvider("droid", "inherit").provider, null);
  assert.equal(deriveBindingProvider("qwen", "   ").provider, null);
  const unknown = deriveBindingProvider("some-future-harness", "whatever");
  assert.equal(unknown.provider, null);
  assert.match(unknown.reason, /unknown harness/);
});

// ---- the historical seed table is the migration's only evidence --------------------------------

test("HISTORICAL_ROLE_MODEL_SEEDS contains every CURRENT seed value — the ratchet that stops a seed change from silently orphaning the machines still holding the old one", () => {
  for (const [harness, seed] of Object.entries(HARNESS_ROLE_MODEL_SEEDS)) {
    for (const [role, model] of Object.entries(seed)) {
      assert.ok(
        isHistoricalKitSeed(harness, role, model),
        `current seed ${harness}/${role} = '${model}' is missing from HISTORICAL_ROLE_MODEL_SEEDS — ` +
          `add it (history is append-only; never replace an entry)`,
      );
    }
  }
});

test("HISTORICAL_ROLE_MODEL_SEEDS matches per harness AND role, so a string that is a seed for one role is not treated as one for another", () => {
  assert.equal(isHistoricalKitSeed("codex", "orchestrator", "deepseek-v4-pro"), true);
  // `deepseek-v4-pro` has never been the seed for codex/worker (that is deepseek-v4-flash), so an
  // operator who bound it there deliberately keeps it.
  assert.equal(isHistoricalKitSeed("codex", "worker", "deepseek-v4-pro"), false);
  assert.equal(isHistoricalKitSeed("opencode", "worker", "deepseek/deepseek-v4-pro"), false);
});

// ---- the migration, on the owner's real file ---------------------------------------------------

/** The owner's live roles.json as measured 2026-09-09, in its v1 (pre-origin) on-disk shape. */
function liveRolesJsonV1(): unknown {
  const claudeCode = {
    roles: {
      orchestrator: { model: "opus" },
      worker: { model: "sonnet" },
      reserve: { model: "fable" },
      explore: { model: "haiku" },
      "worker-highstakes": { model: "opus" },
    },
  };
  const droid = {
    roles: {
      orchestrator: { model: "custom:DeepSeek-V4-Pro-0" },
      worker: { model: "custom:DeepSeek-V4-Pro-0" },
      reserve: { model: "custom:Qwen3.7-Max-[1M-ctx-·-orchestrator]-0" },
      explore: { model: "custom:DeepSeek-V4-Flash-1" },
      "worker-highstakes": { model: "custom:DeepSeek-V4-Pro-0" },
    },
  };
  const staleQwen = {
    roles: {
      orchestrator: { model: "openai:deepseek-v4-pro" },
      worker: { model: "openai:glm-5.3-flash" },
      "worker-highstakes": { model: "openai:deepseek-v4-pro" },
      explore: { model: "openai:glm-5.3-flash" },
      reserve: { model: "openai:qwen3.8-max" },
    },
  };
  const staleCodex = {
    roles: {
      orchestrator: { model: "deepseek-v4-pro" },
      worker: { model: "deepseek-v4-flash" },
      "worker-highstakes": { model: "deepseek-v4-pro" },
      explore: { model: "deepseek-v4-flash" },
      reserve: { model: "grok-4.6" },
    },
  };
  return {
    activeProfile: "opencode-main",
    profiles: {
      "opencode-main": {
        agents: {
          opencode: {
            roles: {
              orchestrator: { model: "deepseek/deepseek-v4-pro" },
              worker: { model: "opencode-go/glm-5.3-flash" },
              "worker-highstakes": { model: "deepseek/deepseek-v4-pro" },
              explore: { model: "opencode-go/glm-5.3-flash" },
              reserve: { model: "opencode-go/qwen3.8-max" },
            },
          },
          droid,
          "claude-code": claudeCode,
          codex: {
            roles: {
              orchestrator: { model: "deepseek-v4-pro" },
              worker: { model: "deepseek-v4-flash" },
              "worker-highstakes": { model: "deepseek-v4-pro" },
              explore: { model: "deepseek-v4-flash" },
              reserve: { model: "deepseek-v4-pro" },
            },
          },
          // The owner's OWN qwen layout: ds-* on two roles (which happen to equal the kit seed)
          // and go-* on three, which no kit version ever seeded.
          qwen: {
            roles: {
              orchestrator: { model: "openai:ds-deepseek-v4-pro" },
              worker: { model: "openai:go-glm-5.3-flash" },
              "worker-highstakes": { model: "openai:ds-deepseek-v4-pro" },
              explore: { model: "openai:go-glm-5.3-flash" },
              reserve: { model: "openai:go-qwen3.8-max" },
            },
          },
        },
      },
      "opencode-go-max": {
        agents: {
          opencode: {
            roles: {
              orchestrator: { model: "deepseek/deepseek-v4-pro" },
              worker: { model: "opencode-go/glm-5.3-flash" },
              "worker-highstakes": { model: "opencode-go/deepseek-v4-pro" },
              explore: { model: "opencode-go/glm-5.3-flash" },
              reserve: { model: "opencode-go/qwen3.8-max" },
            },
          },
          droid,
          "claude-code": claudeCode,
          codex: staleCodex,
          qwen: staleQwen,
        },
      },
      "opencode-direct": {
        agents: {
          opencode: {
            roles: {
              orchestrator: { model: "deepseek/deepseek-v4-pro" },
              worker: { model: "deepseek/deepseek-v4-flash" },
              "worker-highstakes": { model: "deepseek/deepseek-v4-pro" },
              explore: { model: "deepseek/deepseek-v4-flash" },
              reserve: { model: "opencode-go/qwen3.8-max" },
            },
          },
          droid,
          "claude-code": claudeCode,
          codex: staleCodex,
          qwen: staleQwen,
        },
      },
    },
  };
}

test("migration on the owner's real v1 file: exactly the 12 cells that hold a past kit seed are updated, and nothing else moves", () => {
  const parsed = normalizeRoles(liveRolesJsonV1());
  assert.equal(parsed.formatVersion, 1, "an on-disk file with no formatVersion key is version 1");

  const { data, changed, migrations } = migrateRolesFile(parsed);
  assert.equal(changed, true);
  assert.equal(data.formatVersion, ROLES_FORMAT_VERSION);

  const rewritten = rewrittenByMigration(migrations).map(
    (m) => `${m.profile}/${m.agent}/${m.role}: ${m.modelBefore} -> ${m.modelAfter}`,
  );
  assert.deepEqual(rewritten.sort(), [
    "opencode-direct/codex/reserve: grok-4.6 -> deepseek-v4-pro",
    "opencode-direct/qwen/explore: openai:glm-5.3-flash -> openai:ds-deepseek-v4-flash",
    "opencode-direct/qwen/orchestrator: openai:deepseek-v4-pro -> openai:ds-deepseek-v4-pro",
    "opencode-direct/qwen/reserve: openai:qwen3.8-max -> openai:ds-deepseek-v4-pro",
    "opencode-direct/qwen/worker-highstakes: openai:deepseek-v4-pro -> openai:ds-deepseek-v4-pro",
    "opencode-direct/qwen/worker: openai:glm-5.3-flash -> openai:ds-deepseek-v4-flash",
    "opencode-go-max/codex/reserve: grok-4.6 -> deepseek-v4-pro",
    "opencode-go-max/qwen/explore: openai:glm-5.3-flash -> openai:ds-deepseek-v4-flash",
    "opencode-go-max/qwen/orchestrator: openai:deepseek-v4-pro -> openai:ds-deepseek-v4-pro",
    "opencode-go-max/qwen/reserve: openai:qwen3.8-max -> openai:ds-deepseek-v4-pro",
    "opencode-go-max/qwen/worker-highstakes: openai:deepseek-v4-pro -> openai:ds-deepseek-v4-pro",
    "opencode-go-max/qwen/worker: openai:glm-5.3-flash -> openai:ds-deepseek-v4-flash",
  ]);
});

test("migration attributes the OWNER's own bindings and leaves them byte-for-byte — including three that sit in the same agent block as kit-seeded siblings", () => {
  const { data } = migrateRolesFile(normalizeRoles(liveRolesJsonV1()));
  const qwen = data.profiles["opencode-main"]?.agents["qwen"]?.roles;
  assert.ok(qwen);

  // The picked case: `worker` and `explore` and `reserve` are on `go-*` ids the kit has never
  // seeded — the owner's second subscription. They must survive untouched, while `orchestrator`
  // and `worker-highstakes`, one key away in the same object, are recognised as the kit's own.
  assert.equal(qwen["worker"]?.model, "openai:go-glm-5.3-flash");
  assert.equal(qwen["worker"]?.origin, "owner");
  assert.equal(qwen["worker"]?.provider, "opencode-go");
  assert.equal(qwen["explore"]?.model, "openai:go-glm-5.3-flash");
  assert.equal(qwen["explore"]?.origin, "owner");
  assert.equal(qwen["reserve"]?.model, "openai:go-qwen3.8-max");
  assert.equal(qwen["reserve"]?.origin, "owner");
  assert.equal(qwen["orchestrator"]?.origin, "kit");
  assert.equal(qwen["worker-highstakes"]?.origin, "kit");

  // opencode has never been seeded at all, so every one of its bindings is the owner's.
  for (const profile of Object.values(data.profiles)) {
    for (const binding of Object.values(profile.agents["opencode"]?.roles ?? {})) {
      assert.equal(binding.origin, "owner");
    }
  }
  // droid's live bindings are `custom:*` BYOK ids; the kit only ever seeded the literal `inherit`.
  for (const profile of Object.values(data.profiles)) {
    for (const binding of Object.values(profile.agents["droid"]?.roles ?? {})) {
      assert.equal(binding.origin, "owner");
      assert.equal(binding.provider, "custom");
    }
  }
});

test("migration stamps a provider on every binding it can name one for, straight from the value", () => {
  const { data } = migrateRolesFile(normalizeRoles(liveRolesJsonV1()));
  const main = data.profiles["opencode-main"]?.agents;
  assert.ok(main);
  assert.equal(main["opencode"]?.roles["orchestrator"]?.provider, "deepseek");
  assert.equal(main["opencode"]?.roles["worker"]?.provider, "opencode-go");
  assert.equal(main["claude-code"]?.roles["orchestrator"]?.provider, "anthropic");
  assert.equal(main["codex"]?.roles["orchestrator"]?.provider, CODEX_MODEL_PROVIDER);
  assert.equal(main["qwen"]?.roles["orchestrator"]?.provider, "deepseek");
  // Nothing is left unlabelled on this file — the acceptance shape for "roles.json now says which
  // subscription serves each role".
  for (const profile of Object.values(data.profiles)) {
    for (const agent of Object.values(profile.agents)) {
      for (const [role, binding] of Object.entries(agent.roles)) {
        assert.notEqual(binding.provider, null, `${role} -> ${binding.model} has no provider`);
      }
    }
  }
});

test("migration is idempotent: a second pass changes nothing and reports nothing, and a third produces the identical object graph", () => {
  const first = migrateRolesFile(normalizeRoles(liveRolesJsonV1()));
  const second = migrateRolesFile(first.data);
  assert.equal(second.changed, false);
  assert.deepEqual(second.migrations, []);
  assert.equal(second.data, first.data, "an already-migrated file is returned by reference");

  // A full round trip through the JSON on-disk form is also a fixed point.
  const roundTripped = normalizeRoles(JSON.parse(JSON.stringify(first.data)));
  const third = migrateRolesFile(roundTripped);
  assert.equal(third.changed, false);
  assert.deepEqual(third.data, first.data);
});

test("migration never touches a v2 file, whatever it holds — the version, not the content, decides", () => {
  const alreadyV2: RolesFile = {
    formatVersion: ROLES_FORMAT_VERSION,
    activeProfile: "default",
    profiles: {
      // A value that IS a historical kit seed, but labelled as the owner's: the migration must not
      // re-open the question.
      default: { agents: { codex: { roles: { reserve: makeRoleBinding("codex", "grok-4.6", "owner") } } } },
    },
  };
  const { data, changed } = migrateRolesFile(alreadyV2);
  assert.equal(changed, false);
  assert.equal(data.profiles["default"]?.agents["codex"]?.roles["reserve"]?.model, "grok-4.6");
});

// ---- what the origin field buys once the file is at v2 -----------------------------------------

test("seedMissingRoleBindings refreshes a KIT binding when the kit's default moves, and refuses to touch the identical value when the OWNER set it", () => {
  const stale = "openai:deepseek-v4-pro"; // a past kit seed for qwen/orchestrator
  const asKit: RolesFile = {
    formatVersion: ROLES_FORMAT_VERSION,
    activeProfile: "p",
    profiles: { p: { agents: { qwen: { roles: { orchestrator: makeRoleBinding("qwen", stale, "kit") } } } } },
  };
  const asOwner: RolesFile = {
    formatVersion: ROLES_FORMAT_VERSION,
    activeProfile: "p",
    profiles: { p: { agents: { qwen: { roles: { orchestrator: makeRoleBinding("qwen", stale, "owner") } } } } },
  };

  const kitResult = seedMissingRoleBindings(asKit);
  assert.equal(
    kitResult.data.profiles["p"]?.agents["qwen"]?.roles["orchestrator"]?.model,
    QWEN_ROLE_MODEL_SEED["orchestrator"],
  );
  assert.deepEqual(
    kitResult.refreshed.map((r) => `${r.role}: ${r.modelBefore} -> ${r.modelAfter}`),
    ["orchestrator: openai:deepseek-v4-pro -> openai:ds-deepseek-v4-pro"],
  );

  const ownerResult = seedMissingRoleBindings(asOwner);
  assert.equal(ownerResult.data.profiles["p"]?.agents["qwen"]?.roles["orchestrator"]?.model, stale);
  assert.deepEqual(ownerResult.refreshed, []);
});

test("a binding still labelled kit but holding a value this kit never shipped is re-attributed to the owner, value kept — a hand edit is never silently reverted", () => {
  const handEdited: RolesFile = {
    formatVersion: ROLES_FORMAT_VERSION,
    activeProfile: "p",
    profiles: {
      p: {
        agents: {
          qwen: { roles: { orchestrator: makeRoleBinding("qwen", "openai:go-qwen3.8-max", "kit") } },
        },
      },
    },
  };
  const first = seedMissingRoleBindings(handEdited);
  const binding = first.data.profiles["p"]?.agents["qwen"]?.roles["orchestrator"];
  assert.equal(binding?.model, "openai:go-qwen3.8-max", "the edit survives");
  assert.equal(binding?.origin, "owner", "and the label is corrected to match reality");
  assert.deepEqual(
    first.reattributed.map((r) => `${r.role}: ${r.model}`),
    ["orchestrator: openai:go-qwen3.8-max"],
  );
  // Settled: a second pass has nothing to say.
  assert.deepEqual(seedMissingRoleBindings(first.data).reattributed, []);
});

test("model set stamps the binding as the OWNER's and derives its provider, so a later kit default change cannot move it", () => {
  const empty: RolesFile = { formatVersion: ROLES_FORMAT_VERSION, activeProfile: "p", profiles: {} };
  const result = setRoleModel(empty, {
    agent: "qwen",
    role: "orchestrator",
    model: "openai:go-qwen3.8-max",
  });
  assert.equal(result.ok, true);
  if (!result.ok) return;
  const binding = result.data.profiles["p"]?.agents["qwen"]?.roles["orchestrator"];
  assert.equal(binding?.origin, "owner");
  assert.equal(binding?.provider, "opencode-go");
  assert.deepEqual(seedMissingRoleBindings(result.data).refreshed, []);
});

test("a freshly seeded harness is labelled kit, with the provider its seed value implies", () => {
  const shell: RolesFile = {
    formatVersion: ROLES_FORMAT_VERSION,
    activeProfile: "default",
    profiles: { default: { agents: {} } },
  };
  const { data } = seedMissingRoleBindings(shell);
  const agents = data.profiles["default"]?.agents;
  assert.ok(agents);
  for (const [harness, seed] of Object.entries(HARNESS_ROLE_MODEL_SEEDS)) {
    for (const role of Object.keys(seed)) {
      assert.equal(agents[harness]?.roles[role]?.origin, "kit", `${harness}/${role}`);
    }
  }
  assert.equal(agents["claude-code"]?.roles["orchestrator"]?.model, DEFAULT_ROLE_MODEL_SEED["orchestrator"]);
  assert.equal(agents["claude-code"]?.roles["orchestrator"]?.provider, "anthropic");
  assert.equal(agents["codex"]?.roles["reserve"]?.model, CODEX_ROLE_MODEL_SEED["reserve"]);
  assert.equal(agents["codex"]?.roles["reserve"]?.provider, CODEX_MODEL_PROVIDER);
  // droid seeds the literal `inherit`, which names no provider — an honest null, not "factory".
  assert.equal(agents["droid"]?.roles["worker"]?.provider, null);
});

// ---- the label is checked against the value it claims to describe ------------------------------

test("findBindingProviderInconsistencies is silent on anything the kit wrote, and names a hand-edited label that contradicts its own model", () => {
  const clean = migrateRolesFile(normalizeRoles(liveRolesJsonV1())).data;
  assert.deepEqual(findBindingProviderInconsistencies(clean), []);

  const lying: RolesFile = {
    formatVersion: ROLES_FORMAT_VERSION,
    activeProfile: "p",
    profiles: {
      p: {
        agents: {
          opencode: {
            // Says deepseek, but the value routes through opencode-go.
            roles: { worker: { model: "opencode-go/glm-5.3-flash", origin: "owner", provider: "deepseek" } },
          },
        },
      },
    },
  };
  const issues = findBindingProviderInconsistencies(lying);
  assert.equal(issues.length, 1);
  assert.match(issues[0] ?? "", /role 'worker'/);
  assert.match(issues[0] ?? "", /labelled provider 'deepseek'/);
  assert.match(issues[0] ?? "", /parses to 'opencode-go'/);
});

test("roles output shows, per role, which subscription serves it and who bound it — the question roles.json could not answer before", () => {
  const data = migrateRolesFile(normalizeRoles(liveRolesJsonV1())).data;
  const text = formatResolvedBinding(data);
  assert.match(text, /roles\.json format v2/);
  assert.match(text, /worker: opencode-go\/glm-5\.3-flash {2}\[opencode-go, set by owner\]/);
  assert.match(text, /orchestrator: deepseek-v4-pro {2}\[deepseek, set by kit\]/);
});
