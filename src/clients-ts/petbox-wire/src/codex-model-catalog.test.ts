// Unit tests for codex-model-catalog.ts — the model_catalog_json builder that used to hardcode
// three slugs disconnected from roles.json (task wire-support-codex-qwen, Change 2). See that
// file's own header for the measured consequence of an uncatalogued slug (apply_patch gone,
// context_window silently wrong).
//
// Run: node --test src/codex-model-catalog.test.ts   (Node >= 23.6 native TS type-stripping)

import assert from "node:assert/strict";
import { mkdtempSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { test } from "node:test";
import {
  buildCodexModelCatalog,
  collectCodexRoleModelSlugs,
  deriveCodexDisplayName,
} from "./codex-model-catalog.ts";
import { CODEX_ROLE_MODEL_SEED, saveRoles, seedMissingRoleBindings, type RolesFile } from "./roles.ts";

function freshHome(): string {
  return mkdtempSync(join(tmpdir(), "petbox-wire-codex-catalog-"));
}

test("buildCodexModelCatalog: no roles.json at all falls back to the kit's 3-model default, source=fallback", () => {
  const home = freshHome();
  try {
    const result = buildCodexModelCatalog(home);
    assert.equal(result.source, "fallback");
    assert.deepEqual(
      result.slugs.slice().sort(),
      ["deepseek-v4-flash", "deepseek-v4-pro", "grok-4.6"],
    );
    assert.equal(result.catalog.models.length, 3);
    for (const m of result.catalog.models) {
      assert.equal((m as Record<string, unknown>)["apply_patch_tool_type"], "freeform");
    }
  } finally {
    rmSync(home, { recursive: true, force: true });
  }
});

test("buildCodexModelCatalog: a codex rebinding to a model ABSENT from the 3 literals produces a catalog entry for it, apply_patch_tool_type=freeform", () => {
  const home = freshHome();
  try {
    const data: RolesFile = {
      activeProfile: "default",
      profiles: {
        default: {
          agents: {
            codex: {
              roles: {
                orchestrator: { model: "deepseek-v4-pro" },
                // Not one of the three historical literals — the whole point of this test.
                worker: { model: "glm-5.3-flash" },
              },
            },
          },
        },
      },
    };
    saveRoles(data, home);

    const result = buildCodexModelCatalog(home);
    assert.equal(result.source, "roles");
    assert.deepEqual(result.slugs, ["deepseek-v4-pro", "glm-5.3-flash"]); // sorted

    const entry = result.catalog.models.find(
      (m) => (m as Record<string, unknown>)["slug"] === "glm-5.3-flash",
    ) as Record<string, unknown> | undefined;
    assert.ok(entry, "expected a catalog entry for the rebound model 'glm-5.3-flash'");
    assert.equal(entry!["apply_patch_tool_type"], "freeform");
    assert.equal(entry!["display_name"], "Glm 5.3 Flash");
    assert.equal(entry!["context_window"], 128000);
    // experimental_supported_tools deliberately untouched — see codex-model-catalog.ts header.
    assert.deepEqual(entry!["experimental_supported_tools"], []);
  } finally {
    rmSync(home, { recursive: true, force: true });
  }
});

test("buildCodexModelCatalog: union spans ALL profiles, not just the active one", () => {
  const home = freshHome();
  try {
    const data: RolesFile = {
      activeProfile: "default",
      profiles: {
        default: {
          agents: { codex: { roles: { orchestrator: { model: "deepseek-v4-pro" } } } },
        },
        alt: {
          agents: { codex: { roles: { reserve: { model: "grok-4.6" } } } },
        },
      },
    };
    saveRoles(data, home);

    const result = buildCodexModelCatalog(home);
    assert.equal(result.source, "roles");
    assert.deepEqual(result.slugs, ["deepseek-v4-pro", "grok-4.6"]);
  } finally {
    rmSync(home, { recursive: true, force: true });
  }
});

test("collectCodexRoleModelSlugs: de-duplicates across roles/profiles and skips inherit/empty bindings", () => {
  const home = freshHome();
  try {
    const data: RolesFile = {
      activeProfile: "default",
      profiles: {
        default: {
          agents: {
            codex: {
              roles: {
                orchestrator: { model: "deepseek-v4-pro" },
                "worker-highstakes": { model: "deepseek-v4-pro" }, // duplicate, collapses
                worker: { model: "inherit" }, // not a concrete model — skipped
              },
            },
            // A non-codex harness binding must never leak into the codex catalog.
            "claude-code": { roles: { orchestrator: { model: "opus" } } },
          },
        },
      },
    };
    saveRoles(data, home);

    const slugs = collectCodexRoleModelSlugs(home);
    assert.deepEqual(slugs, ["deepseek-v4-pro"]);
  } finally {
    rmSync(home, { recursive: true, force: true });
  }
});

test("buildCodexModelCatalog: a freshly-seeded roles.json (CODEX_ROLE_MODEL_SEED, owner decision 2026-09-08: both new harnesses run entirely on the direct DeepSeek subscription) derives a catalog of exactly the two DeepSeek slugs — grok-4.6 does not appear, and both entries keep apply_patch_tool_type=freeform", () => {
  const home = freshHome();
  try {
    const fresh: RolesFile = { activeProfile: "default", profiles: { default: { agents: {} } } };
    const { data: seeded } = seedMissingRoleBindings(fresh);
    saveRoles(seeded, home);

    // Sanity: the seed itself only ever names the two DeepSeek slugs (no grok-4.6 anywhere).
    assert.deepEqual(
      [...new Set(Object.values(CODEX_ROLE_MODEL_SEED))].sort(),
      ["deepseek-v4-flash", "deepseek-v4-pro"],
    );

    const result = buildCodexModelCatalog(home);
    assert.equal(result.source, "roles");
    assert.deepEqual(result.slugs, ["deepseek-v4-flash", "deepseek-v4-pro"]);
    assert.equal(result.catalog.models.length, 2);
    assert.equal(
      result.catalog.models.some((m) => (m as Record<string, unknown>)["slug"] === "grok-4.6"),
      false,
      "grok-4.6 must not appear in a catalog derived from the new binding set",
    );
    for (const m of result.catalog.models) {
      assert.equal(
        (m as Record<string, unknown>)["apply_patch_tool_type"],
        "freeform",
        `${(m as Record<string, unknown>)["slug"]} must keep apply_patch_tool_type=freeform`,
      );
    }
  } finally {
    rmSync(home, { recursive: true, force: true });
  }
});

test("deriveCodexDisplayName: known slugs keep their exact historical name; unknown slugs get a sensible title-cased fallback", () => {
  assert.equal(deriveCodexDisplayName("deepseek-v4-pro"), "DeepSeek V4 Pro");
  assert.equal(deriveCodexDisplayName("deepseek-v4-flash"), "DeepSeek V4 Flash");
  assert.equal(deriveCodexDisplayName("grok-4.6"), "Grok 4.6");
  assert.equal(deriveCodexDisplayName("glm-5.3-flash"), "Glm 5.3 Flash");
  assert.equal(deriveCodexDisplayName("qwen3.8-max"), "Qwen3.8 Max");
});
