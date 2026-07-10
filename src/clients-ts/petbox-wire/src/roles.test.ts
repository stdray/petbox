// Unit tests for the local role→model binding store (roles.ts).
//
// Run: node --test src/roles.test.ts   (Node >= 23.6 native TS type-stripping; no build step)

import assert from "node:assert/strict";
import { existsSync, mkdtempSync, readFileSync, rmSync, writeFileSync, mkdirSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { test } from "node:test";
import {
  exportRolesBootstrap,
  formatResolvedBinding,
  isEmptyRoles,
  loadRoles,
  resolveAgentRoles,
  resolveObservedBinding,
  rolesPath,
  saveRoles,
  useProfile,
  type RolesFile,
} from "./roles.ts";

function freshHome(): string {
  return mkdtempSync(join(tmpdir(), "petbox-wire-roles-"));
}

const SAMPLE: RolesFile = {
  activeProfile: "default",
  profiles: {
    default: {
      agents: {
        "claude-code": {
          roles: {
            orchestrator: { model: "claude-opus-4" },
            worker: { model: "claude-sonnet-4" },
          },
        },
        opencode: {
          roles: {
            orchestrator: { model: "deepseek-chat" },
            worker: { model: "deepseek-coder" },
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

test("save/load roundtrip under temp HOME", () => {
  const home = freshHome();
  try {
    saveRoles(SAMPLE, home);
    assert.equal(existsSync(rolesPath(home)), true);
    const loaded = loadRoles(home);
    assert.equal(loaded.activeProfile, "default");
    assert.equal(
      loaded.profiles.default.agents["claude-code"].roles.orchestrator.model,
      "claude-opus-4",
    );
    assert.equal(loaded.profiles.default.agents.opencode.roles.worker.model, "deepseek-coder");
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
    assert.ok(data.profiles.work);
    assert.deepEqual(data.profiles.work.agents, {});
    saveRoles(data, home);

    // switching again keeps the shell and updates active
    data = useProfile(loadRoles(home), "default");
    assert.equal(data.activeProfile, "default");
    assert.ok(data.profiles.work, "prior profile shell retained");
    saveRoles(data, home);

    const reloaded = loadRoles(home);
    assert.equal(reloaded.activeProfile, "default");
    assert.ok(reloaded.profiles.work);
  } finally {
    rmSync(home, { recursive: true, force: true });
  }
});

test("export shape is bootstrap-safe RolesFile (no secrets field)", () => {
  const exported = exportRolesBootstrap(SAMPLE);
  assert.equal(exported.activeProfile, "default");
  assert.equal(
    exported.profiles.default.agents["claude-code"].roles.worker.model,
    "claude-sonnet-4",
  );
  // no accidental secret-looking top-level keys
  const keys = Object.keys(exported).sort();
  assert.deepEqual(keys, ["activeProfile", "profiles"]);
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
    // unknown agent → null observation
    assert.equal(resolveObservedBinding("factory-droid", home), null);
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
                  orchestrator: { model: "ok-model" },
                  broken: { notModel: true },
                  empty: { model: "  " },
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
