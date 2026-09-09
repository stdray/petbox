// Unit tests for codex-config-fragment.ts — the printed replacement for the codex config keys the
// kit stopped writing (task wire-codex-config-print-fragment, owner decision 09.09.2026 «и назад
// тоже. все унифицировать»), and the divergence check that keeps the switch from degrading a role
// in silence.
//
// The acceptance criteria these map to are numbered on the card:
//   #3 — the fragment pasted into an empty config yields a WORKING role: apply_patch present
//        (`apply_patch_tool_type: "freeform"`, the thing that makes it 10 tools instead of 9) and
//        a context window equal to the model's REAL one (measured, not the old kit-chosen 128000).
//   #4 — the fragment is derived from roles.json: a rebinding changes the printed text.
//   #5 — a role bound to a slug the LIVE catalog does not carry WARNS, naming the consequence.
//   #6 — the catalog comes from the ACTIVE profile, not the union across all of them.
//
// Run: node --test src/codex-config-fragment.test.ts   (Node >= 23.6 native TS type-stripping)

import assert from "node:assert/strict";
import { test } from "node:test";
import {
  buildCodexDefaultModelFragment,
  findCodexConfigDivergence,
  readCodexCatalogPathFromConfig,
  renderCodexConfigFragmentText,
  unquoteTomlScalar,
} from "./codex-config-fragment.ts";
import { makeRoleBinding, type Profile, ROLES_FORMAT_VERSION, type RolesFile } from "./roles.ts";

const CATALOG_PATH = "C:\\Users\\o\\.codex\\petbox-model-catalog.json";

function rolesWith(
  roles: Record<string, string>,
  opts: { activeProfile?: string; other?: Record<string, string> } = {},
): RolesFile {
  const profiles: Record<string, Profile> = {
    default: {
      agents: { codex: { roles: Object.fromEntries(Object.entries(roles).map(([r, m]) => [r, makeRoleBinding("codex", m)])) } },
    },
  };
  if (opts.other) {
    profiles["alt"] = {
      agents: {
        codex: {
          roles: Object.fromEntries(
            Object.entries(opts.other).map(([r, m]) => [r, makeRoleBinding("codex", m)]),
          ),
        },
      },
    };
  }
  return {
    formatVersion: ROLES_FORMAT_VERSION,
    activeProfile: opts.activeProfile ?? "default",
    profiles,
  };
}

/** A live config.toml that already carries everything the fragment recommends. */
function goodConfigToml(model = "deepseek-v4-pro"): string {
  return [
    `model_provider = "deepseek"`,
    `model = "${model}"`,
    `model_catalog_json = '${CATALOG_PATH}'`,
    "",
    "[model_providers.deepseek]",
    `name = "DeepSeek"`,
    `base_url = "https://api.deepseek.com/v1"`,
    `env_key = "DEEPSEEK_API_KEY"`,
    `wire_api = "responses"`,
    "",
  ].join("\n");
}

function goodCatalog(slugs: readonly string[], contextWindow = 1048576): unknown {
  return {
    models: slugs.map((slug) => ({
      slug,
      apply_patch_tool_type: "freeform",
      context_window: contextWindow,
    })),
  };
}

// ---- rendering (acceptance #3, #4, #6) ------------------------------------------------------

test("acceptance #3: the printed fragment carries a catalog whose entries have apply_patch_tool_type=freeform and the MEASURED context window, not the kit's old 128000", () => {
  const fragment = renderCodexConfigFragmentText(
    rolesWith({ orchestrator: "deepseek-v4-pro", worker: "deepseek-v4-flash" }),
    CATALOG_PATH,
  );
  assert.deepEqual(fragment.slugs, ["deepseek-v4-flash", "deepseek-v4-pro"]);
  for (const m of fragment.catalog.models) {
    assert.equal((m as Record<string, unknown>)["apply_patch_tool_type"], "freeform");
    assert.equal((m as Record<string, unknown>)["context_window"], 1048576);
  }
  assert.ok(!fragment.text.includes("128000"), `the retired kit-chosen window must not appear:\n${fragment.text}`);
  assert.deepEqual(fragment.unmeasuredSlugs, []);
});

test("the printed config.toml half puts root scalars BEFORE any [table] header (a bare key after a header belongs to that table in TOML)", () => {
  const { text } = renderCodexConfigFragmentText(rolesWith({ orchestrator: "deepseek-v4-pro" }), CATALOG_PATH);
  const part2 = text.slice(text.indexOf("--- part 2/2"));
  const firstTable = part2.indexOf("[model_providers.");
  assert.ok(firstTable !== -1, part2);
  for (const key of ["model_provider =", "model =", "model_catalog_json ="]) {
    const at = part2.indexOf(key);
    assert.ok(at !== -1, `${key} missing from:\n${part2}`);
    assert.ok(at < firstTable, `${key} must precede the first [table] header:\n${part2}`);
  }
  // The catalog path is a TOML LITERAL string — a Windows path in a basic string aborts codex's
  // load of the WHOLE config.toml on the first backslash escape (defect codex-mcp-inert-untrusted-project).
  assert.ok(text.includes(`model_catalog_json = '${CATALOG_PATH}'`), text);
});

test("the fragment never emits a static x-opencode-session value — only a commented explanation of why the choice is now the owner's", () => {
  const { text } = renderCodexConfigFragmentText(rolesWith({ orchestrator: "deepseek-v4-pro" }), CATALOG_PATH);
  for (const line of text.split("\n")) {
    if (line.includes("x-opencode-session")) {
      assert.ok(line.trimStart().startsWith("#"), `an uncommented static session header line leaked: ${line}`);
    }
  }
  assert.ok(text.includes("prompt-cache bucket"), text);
});

test("acceptance #4: rebinding a codex role changes the printed text — both the catalog and the root `model` default", () => {
  const before = renderCodexConfigFragmentText(rolesWith({ orchestrator: "deepseek-v4-pro" }), CATALOG_PATH);
  const after = renderCodexConfigFragmentText(rolesWith({ orchestrator: "deepseek-v4-flash" }), CATALOG_PATH);
  assert.notEqual(after.text, before.text);
  assert.deepEqual(after.slugs, ["deepseek-v4-flash"]);
  assert.ok(before.text.includes(`model = "deepseek-v4-pro"`), before.text);
  assert.ok(after.text.includes(`model = "deepseek-v4-flash"`), after.text);
});

test("acceptance #6: the printed catalog is the ACTIVE profile's bindings, and a stale binding parked in another profile never appears", () => {
  const data = rolesWith({ orchestrator: "deepseek-v4-pro" }, { other: { reserve: "grok-4.6" } });
  const fragment = renderCodexConfigFragmentText(data, CATALOG_PATH);
  assert.deepEqual(fragment.slugs, ["deepseek-v4-pro"]);
  assert.ok(!fragment.text.includes("grok-4.6"), fragment.text);
});

test("a slug with no endpoint measurement is called out in the printed notes instead of shipping a silent guess", () => {
  const { text, unmeasuredSlugs } = renderCodexConfigFragmentText(
    rolesWith({ orchestrator: "deepseek-v4-pro", reserve: "grok-4.6" }),
    CATALOG_PATH,
  );
  assert.deepEqual(unmeasuredSlugs, ["grok-4.6"]);
  assert.match(text, /NOTES:/);
  assert.match(text, /context_window for grok-4\.6 is NOT a measurement/);
});

test("buildCodexDefaultModelFragment: the active profile's own orchestrator binding, falling back to the kit's seed", () => {
  assert.equal(buildCodexDefaultModelFragment(rolesWith({ orchestrator: "glm-5.3-flash" })), "glm-5.3-flash");
  assert.equal(buildCodexDefaultModelFragment(rolesWith({ worker: "deepseek-v4-flash" })), "deepseek-v4-pro");
});

// ---- reading a live config -----------------------------------------------------------------

test("readCodexCatalogPathFromConfig / unquoteTomlScalar: literal and basic strings both round-trip a Windows path", () => {
  assert.equal(readCodexCatalogPathFromConfig(`model_catalog_json = '${CATALOG_PATH}'`), CATALOG_PATH);
  assert.equal(
    readCodexCatalogPathFromConfig(`model_catalog_json = "C:\\\\Users\\\\o\\\\.codex\\\\cat.json"`),
    "C:\\Users\\o\\.codex\\cat.json",
  );
  assert.equal(readCodexCatalogPathFromConfig("model_provider = \"deepseek\""), undefined);
  assert.equal(unquoteTomlScalar('"deepseek"'), "deepseek");
});

// ---- divergence (acceptance #5) --------------------------------------------------------------

test("a live config+catalog that already deliver the roster produce ZERO warnings (nothing to paste)", () => {
  const data = rolesWith({ orchestrator: "deepseek-v4-pro", worker: "deepseek-v4-flash" });
  const { warnings } = findCodexConfigDivergence(
    { configText: goodConfigToml(), catalog: goodCatalog(["deepseek-v4-pro", "deepseek-v4-flash"]) },
    data,
  );
  assert.deepEqual(warnings, []);
});

test("acceptance #5: a role bound to a slug the LIVE catalog does not carry warns, naming the role, apply_patch AND the 272000 window", () => {
  const data = rolesWith({ orchestrator: "deepseek-v4-pro", worker: "glm-5.3-flash" });
  const { warnings } = findCodexConfigDivergence(
    { configText: goodConfigToml(), catalog: goodCatalog(["deepseek-v4-pro"]) },
    data,
  );
  const hit = warnings.find((w) => w.includes("glm-5.3-flash"));
  assert.ok(hit, `expected a warning about the uncatalogued slug. Got:\n${warnings.join("\n")}`);
  assert.match(hit!, /role\(s\) worker/);
  assert.match(hit!, /apply_patch/);
  assert.match(hit!, /272000/);
});

test("acceptance #5: model_catalog_json absent entirely is the loudest case — every bound slug named, consequence spelled out", () => {
  const data = rolesWith({ orchestrator: "deepseek-v4-pro" });
  const configText = goodConfigToml().replace(/^model_catalog_json = .*$/m, "");
  const { warnings } = findCodexConfigDivergence({ configText, catalog: undefined }, data);
  const hit = warnings.find((w) => w.startsWith("model_catalog_json: absent"));
  assert.ok(hit, warnings.join("\n"));
  assert.match(hit!, /deepseek-v4-pro/);
  assert.match(hit!, /apply_patch/);
  assert.match(hit!, /272000/);
});

test("a live catalog still carrying the kit's retired 128000 window warns against the measured value", () => {
  const data = rolesWith({ orchestrator: "deepseek-v4-pro" });
  const { warnings } = findCodexConfigDivergence(
    { configText: goodConfigToml(), catalog: goodCatalog(["deepseek-v4-pro"], 128000) },
    data,
  );
  const hit = warnings.find((w) => w.includes("context_window"));
  assert.ok(hit, warnings.join("\n"));
  assert.match(hit!, /128000/);
  assert.match(hit!, /1048576/);
});

test("a live catalog entry that lost apply_patch_tool_type warns even though the slug IS present", () => {
  const data = rolesWith({ orchestrator: "deepseek-v4-pro" });
  const { warnings } = findCodexConfigDivergence(
    {
      configText: goodConfigToml(),
      catalog: { models: [{ slug: "deepseek-v4-pro", apply_patch_tool_type: null, context_window: 1048576 }] },
    },
    data,
  );
  assert.ok(
    warnings.some((w) => w.includes("apply_patch_tool_type") && w.includes("9 tools instead of 10")),
    warnings.join("\n"),
  );
});

test("acceptance #6 on the check side: a stale binding in a NON-active profile produces no warning at all", () => {
  const data = rolesWith({ orchestrator: "deepseek-v4-pro" }, { other: { reserve: "grok-4.6" } });
  const { warnings } = findCodexConfigDivergence(
    { configText: goodConfigToml(), catalog: goodCatalog(["deepseek-v4-pro"]) },
    data,
  );
  assert.deepEqual(warnings, []);
});

test("an unreadable catalog file is a warning about running uncatalogued, not a crash", () => {
  const data = rolesWith({ orchestrator: "deepseek-v4-pro" });
  const { warnings } = findCodexConfigDivergence(
    { configText: goodConfigToml(), catalog: undefined, catalogError: "ENOENT: no such file" },
    data,
  );
  assert.ok(
    warnings.some((w) => w.includes("ENOENT") && w.includes("apply_patch")),
    warnings.join("\n"),
  );
});

test("a surviving static x-opencode-session in the live config is a NOTE, not a warning — the kit no longer owns that value", () => {
  const data = rolesWith({ orchestrator: "deepseek-v4-pro" });
  const configText =
    goodConfigToml() +
    [
      "[model_providers.opencode-go]",
      `name = "opencode Zen Go"`,
      `base_url = "https://opencode.ai/zen/go/v1"`,
      `env_key = "OPENCODE_GO_API_KEY"`,
      `wire_api = "responses"`,
      `http_headers = { "x-opencode-session" = "11111111-2222-3333-4444-555555555555" }`,
      "",
    ].join("\n");
  const { warnings, notes } = findCodexConfigDivergence(
    { configText, catalog: goodCatalog(["deepseek-v4-pro"]) },
    data,
  );
  assert.deepEqual(warnings, []);
  assert.ok(
    notes.some((n) => n.includes("x-opencode-session") && n.includes("prompt-cache bucket")),
    notes.join("\n"),
  );
});

test("an empty config.toml (fresh machine) warns on every key the fragment provides", () => {
  const data = rolesWith({ orchestrator: "deepseek-v4-pro" });
  const { warnings } = findCodexConfigDivergence({ configText: "", catalog: undefined }, data);
  for (const expected of ["model_providers.deepseek", "model_provider", "model:", "model_catalog_json"]) {
    assert.ok(
      warnings.some((w) => w.startsWith(expected)),
      `expected a warning starting with "${expected}". Got:\n${warnings.join("\n")}`,
    );
  }
});
