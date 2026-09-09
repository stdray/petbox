// Unit tests for the local role→model binding store (roles.ts).
//
// Run: node --test src/roles.test.ts   (Node >= 23.6 native TS type-stripping; no build step)

import assert from "node:assert/strict";
import { existsSync, mkdtempSync, readFileSync, rmSync, writeFileSync, mkdirSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { test } from "node:test";
import {
  CODEX_ROLE_MODEL_SEED,
  DEFAULT_ROLE_MODEL_SEED,
  formatResolvedBinding,
  HARNESS_ROLE_MODEL_SEEDS,
  isEmptyRoles,
  loadRoles,
  makeRoleBinding,
  QWEN_ROLE_MODEL_SEED,
  resetRoleModelSlice,
  resetRoleModelToKitDefault,
  resolveAgentRoles,
  resolveObservedBinding,
  ROLES_FORMAT_VERSION,
  type RolesFile,
  rolesPath,
  saveRoles,
  seedMissingRoleBindings,
  setRoleModel,
  setRoleModelSlice,
  unsetRoleModel,
  unsetRoleModelSlice,
  useProfile,
  exportRolesBootstrap,
} from "./roles.ts";
import { readWireLogTail } from "./wire-log.ts";

function freshHome(): string {
  return mkdtempSync(join(tmpdir(), "petbox-wire-roles-"));
}

const SAMPLE: RolesFile = {
  formatVersion: ROLES_FORMAT_VERSION,
  activeProfile: "default",
  profiles: {
    default: {
      agents: {
        "claude-code": {
          roles: {
            orchestrator: makeRoleBinding("claude-code", "claude-opus-4"),
            worker: makeRoleBinding("claude-code", "claude-sonnet-4"),
          },
        },
        opencode: {
          roles: {
            orchestrator: makeRoleBinding("opencode", "deepseek-chat"),
            worker: makeRoleBinding("opencode", "deepseek-coder"),
          },
        },
      },
    },
  },
};

test("load missing file → empty shell (never throws)", () => {
  const home = freshHome();
  try {
    assert.equal(existsSync(rolesPath(home)), false);
    const data = loadRoles(home);
    assert.equal(data.activeProfile, "default");
    assert.deepEqual(data.profiles, {});
    assert.equal(isEmptyRoles(data), true);
  } finally {
    rmSync(home, { recursive: true, force: true });
  }
});

test("load corrupt / non-object JSON → empty shell", () => {
  const home = freshHome();
  try {
    mkdirSync(join(home, ".petbox"), { recursive: true });
    writeFileSync(rolesPath(home), "not-json{{{", "utf8");
    const data = loadRoles(home);
    assert.equal(data.activeProfile, "default");
    assert.deepEqual(data.profiles, {});

    writeFileSync(rolesPath(home), "null", "utf8");
    assert.deepEqual(loadRoles(home).profiles, {});
  } finally {
    rmSync(home, { recursive: true, force: true });
  }
});

// wire-silent-failures-invisible taxonomy: a missing file is Class A (fresh machine) and never
// throws even in strict mode; a PRESENT-but-corrupt file is where apply's polarity (strict)
// differs from every other caller (doctor, roles/model CLI, session push).
test("loadRoles strict: missing file stays silent (Class A), never throws even in strict mode", () => {
  const home = freshHome();
  try {
    assert.doesNotThrow(() => loadRoles(home, { strict: true }));
    const data = loadRoles(home, { strict: true });
    assert.deepEqual(data.profiles, {});
    assert.deepEqual(readWireLogTail(20, home), [], "no roles.json at all must never trace to wire.log");
  } finally {
    rmSync(home, { recursive: true, force: true });
  }
});

test("loadRoles strict: corrupt roles.json THROWS (apply's hard-failure polarity — the 2026-07-12 incident shape)", () => {
  const home = freshHome();
  try {
    mkdirSync(join(home, ".petbox"), { recursive: true });
    writeFileSync(rolesPath(home), "not-json{{{", "utf8");
    assert.throws(
      () => loadRoles(home, { strict: true }),
      /corrupt roles\.json/,
      "apply must hard-fail on a corrupt roles.json, never silently compile as if unbound",
    );
  } finally {
    rmSync(home, { recursive: true, force: true });
  }
});

test("loadRoles non-strict: corrupt roles.json stays silent on stdout but leaves a Class-Б trace in wire.log", () => {
  const home = freshHome();
  try {
    mkdirSync(join(home, ".petbox"), { recursive: true });
    writeFileSync(rolesPath(home), "not-json{{{", "utf8");
    // Non-strict (doctor / roles CLI / session push's polarity): behaves exactly as before —
    // empty shell, never throws — but now also traces the event so doctor can surface it.
    const data = loadRoles(home);
    assert.deepEqual(data.profiles, {});
    const tail = readWireLogTail(20, home);
    assert.ok(tail.length > 0, "a present-but-corrupt roles.json must leave a wire.log trace");
    assert.match(tail.join("\n"), /roles.*failed to parse/i);
  } finally {
    rmSync(home, { recursive: true, force: true });
  }
});

test("save/load roundtrip under temp HOME", () => {
  const home = freshHome();
  try {
    saveRoles(SAMPLE, home);
    assert.equal(existsSync(rolesPath(home)), true);
    const loaded = loadRoles(home);
    assert.equal(loaded.activeProfile, "default");
    const defaultProfile = loaded.profiles["default"];
    assert.ok(defaultProfile, "the 'default' profile must round-trip");
    const claudeCode = defaultProfile.agents["claude-code"];
    assert.ok(claudeCode, "claude-code agent bindings must round-trip");
    assert.equal(claudeCode.roles["orchestrator"]?.model, "claude-opus-4");
    const opencode = defaultProfile.agents["opencode"];
    assert.ok(opencode, "opencode agent bindings must round-trip");
    assert.equal(opencode.roles["worker"]?.model, "deepseek-coder");
    // file is pretty-printed JSON
    const raw = JSON.parse(readFileSync(rolesPath(home), "utf8"));
    assert.equal(raw.activeProfile, "default");
  } finally {
    rmSync(home, { recursive: true, force: true });
  }
});

test("profile use: set activeProfile and create shell if missing", () => {
  const home = freshHome();
  try {
    let data = loadRoles(home);
    data = useProfile(data, "work");
    assert.equal(data.activeProfile, "work");
    const workProfile = data.profiles["work"];
    assert.ok(workProfile);
    assert.deepEqual(workProfile.agents, {});
    saveRoles(data, home);

    // switching again keeps the shell and updates active
    data = useProfile(loadRoles(home), "default");
    assert.equal(data.activeProfile, "default");
    assert.ok(data.profiles["work"], "prior profile shell retained");
    saveRoles(data, home);

    const reloaded = loadRoles(home);
    assert.equal(reloaded.activeProfile, "default");
    assert.ok(reloaded.profiles["work"]);
  } finally {
    rmSync(home, { recursive: true, force: true });
  }
});

test("export shape is bootstrap-safe RolesFile (no secrets field)", () => {
  const exported = exportRolesBootstrap(SAMPLE);
  assert.equal(exported.activeProfile, "default");
  const defaultProfile = exported.profiles["default"];
  assert.ok(defaultProfile, "the 'default' profile must round-trip through export");
  const claudeCode = defaultProfile.agents["claude-code"];
  assert.ok(claudeCode, "claude-code agent bindings must round-trip through export");
  assert.equal(
    claudeCode.roles["worker"]?.model,
    "claude-sonnet-4",
  );
  // no accidental secret-looking top-level keys
  const keys = Object.keys(exported).sort();
  assert.deepEqual(keys, ["activeProfile", "formatVersion", "profiles"]);
});

test("resolveAgentRoles / resolveObservedBinding do not invent defaults", () => {
  const home = freshHome();
  try {
    assert.deepEqual(resolveAgentRoles(loadRoles(home), "claude-code"), {});
    assert.equal(resolveObservedBinding("claude-code", home), null);

    saveRoles(SAMPLE, home);
    assert.deepEqual(resolveAgentRoles(loadRoles(home), "claude-code"), {
      orchestrator: "claude-opus-4",
      worker: "claude-sonnet-4",
    });
    const obs = resolveObservedBinding("claude-code", home);
    assert.deepEqual(obs, {
      profile: "default",
      agent: "claude-code",
      roles: { orchestrator: "claude-opus-4", worker: "claude-sonnet-4" },
    });
    // truly unknown agent → null observation
    assert.equal(resolveObservedBinding("not-a-harness", home), null);
  } finally {
    rmSync(home, { recursive: true, force: true });
  }
});

test("factory-droid alias resolves to canonical droid (session agent id)", () => {
  const home = freshHome();
  try {
    saveRoles(
      {
        formatVersion: ROLES_FORMAT_VERSION,
        activeProfile: "default",
        profiles: {
          default: {
            agents: {
              // legacy / display name in roles.json
              "factory-droid": {
                roles: {
                  orchestrator: makeRoleBinding("factory-droid", "deepseek-v4-pro"),
                  worker: makeRoleBinding("factory-droid", "deepseek-v4-pro"),
                },
              },
            },
          },
        },
      },
      home,
    );
    // push-session / droid-push stamps agent:"droid" — must find factory-droid bucket
    assert.deepEqual(resolveAgentRoles(loadRoles(home), "droid"), {
      orchestrator: "deepseek-v4-pro",
      worker: "deepseek-v4-pro",
    });
    assert.deepEqual(resolveAgentRoles(loadRoles(home), "factory-droid"), {
      orchestrator: "deepseek-v4-pro",
      worker: "deepseek-v4-pro",
    });
    const obs = resolveObservedBinding("droid", home);
    assert.deepEqual(obs, {
      profile: "default",
      agent: "droid", // stamp always uses canonical id
      roles: { orchestrator: "deepseek-v4-pro", worker: "deepseek-v4-pro" },
    });
    // looking up via alias still stamps canonical agent id
    assert.equal(resolveObservedBinding("factory-droid", home)?.agent, "droid");
  } finally {
    rmSync(home, { recursive: true, force: true });
  }
});

test("formatResolvedBinding surfaces empty vs populated", () => {
  const home = freshHome();
  try {
    const empty = formatResolvedBinding(loadRoles(home));
    assert.match(empty, /activeProfile: default/);
    assert.match(empty, /no agent role bindings/);

    const filled = formatResolvedBinding(SAMPLE);
    assert.match(filled, /claude-code:/);
    assert.match(filled, /orchestrator: claude-opus-4/);
    assert.match(filled, /opencode:/);
  } finally {
    rmSync(home, { recursive: true, force: true });
  }
});

test("setRoleModel: known alias writes clean, no warning", () => {
  const home = freshHome();
  try {
    const data = loadRoles(home);
    const result = setRoleModel(data, { agent: "claude-code", role: "worker", model: "sonnet" });
    assert.equal(result.ok, true);
    if (!result.ok) return;
    assert.equal(result.warning, undefined);
    saveRoles(result.data, home);
    assert.deepEqual(resolveAgentRoles(loadRoles(home), "claude-code"), { worker: "sonnet" });
  } finally {
    rmSync(home, { recursive: true, force: true });
  }
});

// In-memory empty store for setRoleModel/unsetRoleModel cases that never touch disk (no
// freshHome() needed — these are pure-function tests, not load/save round trips).
const EMPTY_ROLES: RolesFile = {
  formatVersion: ROLES_FORMAT_VERSION,
  activeProfile: "default",
  profiles: {},
};

test("setRoleModel: shape-valid-but-unlisted claude id writes with a warning (unknown tier)", () => {
  const data = EMPTY_ROLES;
  const result = setRoleModel(data, {
    agent: "claude-code",
    role: "orchestrator",
    model: "claude-opus-9000",
  });
  assert.equal(result.ok, true);
  if (!result.ok) return;
  assert.match(result.warning ?? "", /not on the known-alias list/);
  assert.deepEqual(resolveAgentRoles(result.data, "claude-code"), {
    orchestrator: "claude-opus-9000",
  });
});

test("setRoleModel: foreign-shaped id is REFUSED by default (the 2026-07-12 incident shape)", () => {
  const data = EMPTY_ROLES;
  const result = setRoleModel(data, {
    agent: "claude-code",
    role: "worker",
    model: "custom:DeepSeek-V4-Pro-0",
  });
  assert.equal(result.ok, false);
  if (result.ok) return;
  assert.match(result.reason, /looks like another harness's id shape/);
  assert.match(result.reason, /--allow-unknown-model/);
  // nothing written — the store is untouched
  assert.deepEqual(resolveAgentRoles(data, "claude-code"), {});
});

test("setRoleModel: foreign-shaped id writes anyway with --allow-unknown-model, still warns", () => {
  const data = EMPTY_ROLES;
  const result = setRoleModel(data, {
    agent: "claude-code",
    role: "worker",
    model: "custom:DeepSeek-V4-Pro-0",
    allowUnknownModel: true,
  });
  assert.equal(result.ok, true);
  if (!result.ok) return;
  assert.match(result.warning ?? "", /--allow-unknown-model was passed/);
  assert.deepEqual(resolveAgentRoles(result.data, "claude-code"), {
    worker: "custom:DeepSeek-V4-Pro-0",
  });
});

test("setRoleModel: open-policy harness (droid) never blocks, never warns", () => {
  const data = EMPTY_ROLES;
  const result = setRoleModel(data, {
    agent: "droid",
    role: "worker",
    model: "custom:DeepSeek-V4-Pro-0",
  });
  assert.equal(result.ok, true);
  if (!result.ok) return;
  assert.equal(result.warning, undefined);
});

test("setRoleModel: agent alias resolves to the canonical bucket; --profile targets a non-active profile", () => {
  const data = EMPTY_ROLES;
  const result = setRoleModel(data, {
    agent: "factory-droid", // alias for droid
    role: "worker",
    model: "deepseek-v4-pro",
    profile: "work",
  });
  assert.equal(result.ok, true);
  if (!result.ok) return;
  // activeProfile is untouched (still "default") — model set does not switch profiles
  assert.equal(result.data.activeProfile, "default");
  const workProfile = result.data.profiles["work"];
  assert.ok(workProfile, "the named profile is created as a shell");
  assert.equal(workProfile.agents["droid"]?.roles["worker"]?.model, "deepseek-v4-pro");
});

test("setRoleModel: rejects a blank role or model", () => {
  const data = EMPTY_ROLES;
  const blankRole = setRoleModel(data, { agent: "claude-code", role: "  ", model: "sonnet" });
  assert.equal(blankRole.ok, false);
  const blankModel = setRoleModel(data, { agent: "claude-code", role: "worker", model: "  " });
  assert.equal(blankModel.ok, false);
  if (blankModel.ok) return;
  assert.match(blankModel.reason, /model unset/);
});

// ---- slice operations (task role-model-bindings-review-refactor, stage D, defect #5) -----------

const CANONICAL_ROLES = Object.keys(DEFAULT_ROLE_MODEL_SEED);

test("setRoleModelSlice: --all-roles on one agent writes the whole canonical roster to one command, does not touch another agent", () => {
  const data: RolesFile = {
    formatVersion: ROLES_FORMAT_VERSION,
    activeProfile: "default",
    profiles: {
      default: {
        agents: {
          opencode: { roles: { worker: makeRoleBinding("opencode", "deepseek/deepseek-v4-pro") } },
        },
      },
    },
  };
  const result = setRoleModelSlice(data, {
    profiles: ["default"],
    agents: ["droid"],
    roles: "all",
    model: "custom:DeepSeek-V4-Pro-0",
    allowUnknownModel: true,
  });
  assert.equal(result.ok, true);
  if (!result.ok) return;
  // Every canonical role got the same model, for droid only.
  assert.deepEqual(resolveAgentRoles(result.data, "droid"), Object.fromEntries(CANONICAL_ROLES.map((r) => [r, "custom:DeepSeek-V4-Pro-0"])));
  // opencode's own pre-existing binding is byte-for-byte untouched — outside the slice.
  assert.deepEqual(resolveAgentRoles(result.data, "opencode"), { worker: "deepseek/deepseek-v4-pro" });
  assert.equal(result.changes.length, CANONICAL_ROLES.length);
});

test("setRoleModelSlice: one role across --all-agents touches only that role on every canonical agent", () => {
  const result = setRoleModelSlice(EMPTY_ROLES, {
    profiles: ["default"],
    agents: "all",
    roles: ["reserve"],
    model: "fable",
    allowUnknownModel: true,
  });
  assert.equal(result.ok, true);
  if (!result.ok) return;
  for (const agent of ["claude-code", "opencode", "droid", "codex", "qwen"]) {
    assert.deepEqual(resolveAgentRoles(result.data, agent), { reserve: "fable" });
  }
});

test("setRoleModelSlice: --all-profiles applies to every EXISTING profile only, never invents one", () => {
  const data: RolesFile = {
    formatVersion: ROLES_FORMAT_VERSION,
    activeProfile: "default",
    profiles: {
      default: { agents: {} },
      alt: { agents: {} },
    },
  };
  const result = setRoleModelSlice(data, {
    profiles: "all",
    agents: ["droid"],
    roles: ["worker"],
    model: "inherit",
  });
  assert.equal(result.ok, true);
  if (!result.ok) return;
  assert.equal(result.data.profiles["default"]?.agents["droid"]?.roles["worker"]?.model, "inherit");
  assert.equal(result.data.profiles["alt"]?.agents["droid"]?.roles["worker"]?.model, "inherit");
  assert.equal(Object.keys(result.data.profiles).length, 2, "no profile was invented");
});

test("setRoleModelSlice: ALL-OR-NOTHING — one refused cell refuses the whole slice, nothing is written", () => {
  const before = EMPTY_ROLES;
  const result = setRoleModelSlice(before, {
    profiles: ["default"],
    agents: "all",
    roles: ["worker"],
    model: "custom:DeepSeek-V4-Pro-0", // foreign shape for claude-code, refused there
  });
  assert.equal(result.ok, false);
  if (result.ok) return;
  assert.match(result.reason, /refused/);
  // The one bad cell is named...
  assert.ok(result.outcomes.some((o) => !o.ok && o.cell.agent === "claude-code"));
  // ...and even the cells that WOULD have succeeded (droid, open policy) were not applied: the
  // function never returns a `data` to save on refusal, so there is nothing to assert on disk —
  // the caller (wire.ts) only ever calls saveRoles on the ok:true branch.
});

test("unsetRoleModelSlice: --all-roles removes every canonical role for one agent, no-op-safe per absent cell", () => {
  const data: RolesFile = {
    formatVersion: ROLES_FORMAT_VERSION,
    activeProfile: "default",
    profiles: {
      default: {
        agents: {
          droid: { roles: { worker: makeRoleBinding("droid", "inherit") } },
        },
      },
    },
  };
  const result = unsetRoleModelSlice(data, { profiles: ["default"], agents: ["droid"], roles: "all" });
  assert.deepEqual(resolveAgentRoles(result.data, "droid"), {});
  const removed = result.changes.filter((c) => c.removed);
  assert.equal(removed.length, 1, "only the one cell that actually had a binding reports removed:true");
  assert.equal(result.changes.length, CANONICAL_ROLES.length);
});

test("resetRoleModelToKitDefault: overwrites an OWNER binding back to the kit's current default, origin flips to kit", () => {
  const owner = setRoleModel(EMPTY_ROLES, { agent: "claude-code", role: "worker", model: "opus" });
  assert.equal(owner.ok, true);
  if (!owner.ok) return;
  assert.equal(owner.data.profiles["default"]?.agents["claude-code"]?.roles["worker"]?.origin, "owner");
  const reset = resetRoleModelToKitDefault(owner.data, { agent: "claude-code", role: "worker" });
  assert.equal(reset.ok, true);
  if (!reset.ok) return;
  assert.equal(reset.modelBefore, "opus");
  assert.equal(reset.modelAfter, DEFAULT_ROLE_MODEL_SEED["worker"]);
  const cell = reset.data.profiles["default"]?.agents["claude-code"]?.roles["worker"];
  assert.equal(cell?.origin, "kit");
  assert.equal(cell?.model, DEFAULT_ROLE_MODEL_SEED["worker"]);
});

test("resetRoleModelToKitDefault: refuses a cell the kit has never seeded (opencode) — never invents a value", () => {
  const result = resetRoleModelToKitDefault(EMPTY_ROLES, { agent: "opencode", role: "worker" });
  assert.equal(result.ok, false);
  if (result.ok) return;
  assert.match(result.reason, /no kit default/);
});

test("resetRoleModelSlice: --all-roles resets every seeded role and reports (not errors on) cells with no kit default", () => {
  const data: RolesFile = {
    formatVersion: ROLES_FORMAT_VERSION,
    activeProfile: "default",
    profiles: {
      default: {
        agents: {
          opencode: { roles: { worker: makeRoleBinding("opencode", "deepseek/deepseek-v4-pro") } },
        },
      },
    },
  };
  const result = resetRoleModelSlice(data, { profiles: ["default"], agents: ["opencode"], roles: "all" });
  // opencode has NO kit seed at all — matrixRolesFor still walks the canonical roster (plus the
  // one role opencode already has bound), and every one of those cells is skipped, not written.
  const ok = result.changes.filter((c) => c.ok);
  const skipped = result.changes.filter((c) => !c.ok);
  assert.equal(ok.length, 0);
  assert.ok(skipped.length >= CANONICAL_ROLES.length);
  // opencode's own pre-existing binding is completely untouched.
  assert.deepEqual(resolveAgentRoles(result.data, "opencode"), { worker: "deepseek/deepseek-v4-pro" });
});

test("resetRoleModelSlice: --all-roles on a harness the kit DOES seed (droid) resets every canonical role to 'inherit', origin kit", () => {
  const owner = setRoleModel(EMPTY_ROLES, { agent: "droid", role: "reserve", model: "custom:Something-Else-0" });
  assert.equal(owner.ok, true);
  if (!owner.ok) return;
  const result = resetRoleModelSlice(owner.data, { profiles: ["default"], agents: ["droid"], roles: "all" });
  const ok = result.changes.filter((c) => c.ok);
  assert.equal(ok.length, CANONICAL_ROLES.length);
  assert.deepEqual(resolveAgentRoles(result.data, "droid"), Object.fromEntries(CANONICAL_ROLES.map((r) => [r, "inherit"])));
  for (const role of CANONICAL_ROLES) {
    assert.equal(result.data.profiles["default"]?.agents["droid"]?.roles[role]?.origin, "kit");
  }
});

test("unsetRoleModel: removes an existing binding, no-ops when absent", () => {
  const home = freshHome();
  try {
    saveRoles(SAMPLE, home);
    const before = loadRoles(home);
    const result = unsetRoleModel(before, { agent: "claude-code", role: "worker" });
    assert.equal(result.removed, true);
    saveRoles(result.data, home);
    assert.deepEqual(resolveAgentRoles(loadRoles(home), "claude-code"), {
      orchestrator: "claude-opus-4",
    });

    // second unset of the same role is a no-op, not an error
    const again = unsetRoleModel(loadRoles(home), { agent: "claude-code", role: "worker" });
    assert.equal(again.removed, false);

    // unknown agent / unknown role / no bindings at all — all no-op-safe
    const emptyResult = unsetRoleModel(EMPTY_ROLES, { agent: "claude-code", role: "worker" });
    assert.equal(emptyResult.removed, false);
    assert.equal(emptyResult.data, EMPTY_ROLES); // same reference back — genuinely untouched
  } finally {
    rmSync(home, { recursive: true, force: true });
  }
});

test("light validation drops junk role entries without model", () => {
  const home = freshHome();
  try {
    mkdirSync(join(home, ".petbox"), { recursive: true });
    writeFileSync(
      rolesPath(home),
      JSON.stringify({
        activeProfile: "default",
        profiles: {
          default: {
            agents: {
              "claude-code": {
                roles: {
                  orchestrator: makeRoleBinding("claude-code", "ok-model"),
                  broken: { notModel: true },
                  empty: makeRoleBinding("claude-code", "  "),
                },
              },
            },
          },
        },
      }),
      "utf8",
    );
    const data = loadRoles(home);
    assert.deepEqual(resolveAgentRoles(data, "claude-code"), { orchestrator: "ok-model" });
  } finally {
    rmSync(home, { recursive: true, force: true });
  }
});

// seedMissingRoleBindings — regression coverage for bug
// harness-seed-skipped-when-roles-json-exists (task wire-support-codex-qwen): a machine whose
// roles.json predates codex/qwen being added to HARNESS_IDS got NO bindings for either, forever,
// because the old seeder (wire.ts's seedDefaultRoleBindingsIfMissing) only ever ran on a totally
// ABSENT file.

test("seedMissingRoleBindings: pre-existing profile with only the three old harnesses gains codex+qwen with the right models, in every profile, and a user's own binding is untouched", () => {
  const before: RolesFile = {
    formatVersion: ROLES_FORMAT_VERSION,
    activeProfile: "opencode-main",
    profiles: {
      "opencode-main": {
        agents: {
          "claude-code": { roles: { orchestrator: makeRoleBinding("claude-code", "MY-CUSTOM-MODEL") } },
          opencode: { roles: { orchestrator: makeRoleBinding("opencode", "deepseek-chat") } },
          droid: { roles: { orchestrator: makeRoleBinding("droid", "inherit") } },
        },
      },
      // A second profile — seeding must reach every profile in the file, not just active.
      "opencode-go-max": {
        agents: {
          droid: { roles: { worker: makeRoleBinding("droid", "inherit") } },
        },
      },
    },
  };

  const { data: after, changed } = seedMissingRoleBindings(before);
  assert.equal(changed, true);

  for (const profileName of ["opencode-main", "opencode-go-max"] as const) {
    const agents = after.profiles[profileName]!.agents;
    assert.deepEqual(
      Object.fromEntries(Object.entries(agents["codex"]!.roles).map(([r, b]) => [r, b.model])),
      CODEX_ROLE_MODEL_SEED,
      `${profileName}: codex seeded with CODEX_ROLE_MODEL_SEED`,
    );
    assert.deepEqual(
      Object.fromEntries(Object.entries(agents["qwen"]!.roles).map(([r, b]) => [r, b.model])),
      QWEN_ROLE_MODEL_SEED,
      `${profileName}: qwen seeded with QWEN_ROLE_MODEL_SEED`,
    );
  }

  // Pre-existing bindings, byte-identical: the user's own claude-code/opencode/droid values,
  // and opencode itself (not in HARNESS_ROLE_MODEL_SEEDS — intentionally never auto-bound).
  assert.equal(
    after.profiles["opencode-main"]!.agents["claude-code"]!.roles["orchestrator"]!.model,
    "MY-CUSTOM-MODEL",
  );
  assert.deepEqual(
    after.profiles["opencode-main"]!.agents["opencode"],
    before.profiles["opencode-main"]!.agents["opencode"],
  );
  assert.deepEqual(
    after.profiles["opencode-main"]!.agents["droid"],
    before.profiles["opencode-main"]!.agents["droid"],
  );
  assert.deepEqual(
    after.profiles["opencode-go-max"]!.agents["droid"],
    before.profiles["opencode-go-max"]!.agents["droid"],
  );

  // Idempotent: seeding an already-fully-seeded file is a true no-op (same reference back).
  const second = seedMissingRoleBindings(after);
  assert.equal(second.changed, false);
  assert.equal(second.data, after);
});

test("seedMissingRoleBindings: a harness already present but bound for only SOME roles is left completely alone (roles too) — that gap is apply's unbound-role refusal to catch, not this seeder's to paper over", () => {
  const before: RolesFile = {
    formatVersion: ROLES_FORMAT_VERSION,
    activeProfile: "default",
    profiles: {
      default: {
        agents: {
          // codex present, but only "orchestrator" bound — deliberately partial.
          codex: { roles: { orchestrator: makeRoleBinding("codex", "operator-chosen") } },
        },
      },
    },
  };
  const { data: after, changed } = seedMissingRoleBindings(before);
  // qwen was fully absent, so it gets seeded — that IS a change...
  assert.equal(changed, true);
  assert.deepEqual(
    Object.fromEntries(
      Object.entries(after.profiles["default"]!.agents["qwen"]!.roles).map(([r, b]) => [r, b.model]),
    ),
    QWEN_ROLE_MODEL_SEED,
  );
  // ...but codex, already present, is untouched byte-for-byte — no "worker"/"explore"/etc
  // backfilled even though HARNESS_ROLE_MODEL_SEEDS.codex has them.
  assert.deepEqual(after.profiles["default"]!.agents["codex"], before.profiles["default"]!.agents["codex"]);
});

test("seedMissingRoleBindings: HARNESS_ROLE_MODEL_SEEDS excludes opencode (its model space is open/unknowable) — a file with no harnesses at all never gets an opencode entry invented", () => {
  const before: RolesFile = {
    formatVersion: ROLES_FORMAT_VERSION,
    activeProfile: "default",
    profiles: { default: { agents: {} } },
  };
  const { data: after } = seedMissingRoleBindings(before);
  assert.equal("opencode" in after.profiles["default"]!.agents, false);
  assert.equal(Object.keys(HARNESS_ROLE_MODEL_SEEDS).includes("opencode"), false);
});
