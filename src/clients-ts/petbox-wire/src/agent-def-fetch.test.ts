// Unit tests for agent-def-fetch (parser + fetch + LKG cache resolution).
//
// Run: node --test src/agent-def-fetch.test.ts   (Node >= 23.6 native TS)

import assert from "node:assert/strict";
import { mkdtempSync, readFileSync, rmSync, existsSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { test } from "node:test";
import {
  AGENT_DEF_STALE_MARKER,
  agentDefCachePath,
  DEFAULT_DEFINITION_KEY,
  fetchAgentDefinition,
  parseAgentDefinitionResponse,
  readAgentDefCache,
  resolveAgentDefinitionWithLkg,
  writeAgentDefCache,
} from "./agent-def-fetch.ts";
import { DEFAULT_AGENT_DEFINITION, validateAgentDefinition } from "./agent-definition.ts";
import { planOpencodeApply } from "./apply-artifacts.ts";

const VALID_BODY = {
  key: "default",
  version: 3,
  created: "2026-01-01T00:00:00Z",
  updated: "2026-01-02T00:00:00Z",
  definition: {
    name: "proj-default",
    roles: [
      {
        slug: "orchestrator",
        tier: "orchestrator",
        requiredCapabilities: ["mcp_main_session"],
        spawn: { allowed: true, allowedRoles: ["worker"] },
        escalation: { available: true, targets: ["reserve"] },
        notes: "main loop",
      },
      {
        slug: "worker",
        tier: "worker",
        requiredCapabilities: [],
        spawn: { allowed: false },
        escalation: { available: false },
      },
    ],
  },
  extraServerField: true,
};

function freshHome(): string {
  return mkdtempSync(join(tmpdir(), "petbox-adef-"));
}

test("parseAgentDefinitionResponse maps camelCase envelope + definition", () => {
  const got = parseAgentDefinitionResponse(VALID_BODY);
  assert.ok(got);
  assert.equal(got.key, "default");
  assert.equal(got.version, 3);
  assert.equal(got.definition.name, "proj-default");
  assert.equal(got.definition.roles.length, 2);
  assert.equal(got.definition.roles[0].slug, "orchestrator");
  validateAgentDefinition(got.definition);
});

test("parseAgentDefinitionResponse rejects role.model", () => {
  const bad = structuredClone(VALID_BODY);
  (bad.definition.roles[0] as { model?: string }).model = "claude-opus";
  assert.equal(parseAgentDefinitionResponse(bad), null);
});

test("parseAgentDefinitionResponse rejects missing roles / name / version", () => {
  assert.equal(parseAgentDefinitionResponse(null), null);
  assert.equal(parseAgentDefinitionResponse({}), null);
  assert.equal(
    parseAgentDefinitionResponse({ key: "default", version: 1, definition: { name: "x", roles: [] } }),
    null,
  );
});

test("fetchAgentDefinition returns mapped definition on 200", async () => {
  const calls: Array<{ url: string; headers: HeadersInit | undefined }> = [];
  const fetchImpl: typeof fetch = async (input, init) => {
    calls.push({ url: String(input), headers: init?.headers });
    return new Response(JSON.stringify(VALID_BODY), {
      status: 200,
      headers: { "Content-Type": "application/json" },
    });
  };

  const got = await fetchAgentDefinition({
    baseUrl: "https://petbox.example/",
    projectKey: "$system",
    apiKey: "k",
    definitionKey: DEFAULT_DEFINITION_KEY,
    fetchImpl,
  });
  assert.ok(got);
  assert.equal(got.key, "default");
  assert.equal(got.version, 3);
  assert.equal(calls[0].url, "https://petbox.example/api/%24system/agent-defs/default");
});

test("fetchAgentDefinition returns null on 404 / network / bad body (never throws)", async () => {
  assert.equal(
    await fetchAgentDefinition({
      baseUrl: "https://petbox.example",
      projectKey: "p",
      apiKey: "k",
      fetchImpl: async () => new Response("nope", { status: 404 }),
    }),
    null,
  );
  assert.equal(
    await fetchAgentDefinition({
      baseUrl: "https://petbox.example",
      projectKey: "p",
      apiKey: "k",
      fetchImpl: async () => {
        throw new Error("ECONNREFUSED");
      },
    }),
    null,
  );
});

test("successful fetch path writeAgentDefCache leaves ~/.petbox/cache/<project>.agent-def.json", () => {
  const home = freshHome();
  try {
    const fetched = parseAgentDefinitionResponse(VALID_BODY)!;
    writeAgentDefCache("$system", fetched, home, () => "2026-07-10T00:00:00.000Z");
    const path = agentDefCachePath("$system", home);
    assert.equal(existsSync(path), true);
    const raw = JSON.parse(readFileSync(path, "utf8"));
    assert.equal(raw.key, "default");
    assert.equal(raw.version, 3);
    assert.equal(raw.fetchedAt, "2026-07-10T00:00:00.000Z");
    assert.equal(raw.definition.name, "proj-default");
    const round = readAgentDefCache("$system", home);
    assert.ok(round);
    assert.equal(round.definition.name, "proj-default");
  } finally {
    rmSync(home, { recursive: true, force: true });
  }
});

test("resolve: fetch null + LKG present → uses cache with stale marker (not DEFAULT)", async () => {
  const home = freshHome();
  try {
    const fetched = parseAgentDefinitionResponse(VALID_BODY)!;
    writeAgentDefCache("proj", fetched, home);

    const got = await resolveAgentDefinitionWithLkg({
      offline: false,
      definitionKey: "default",
      projectKey: "proj",
      baseUrl: "https://petbox.example",
      apiKey: "k",
      homeDir: home,
      fetchImpl: async () => new Response("down", { status: 503 }),
    });

    assert.equal(got.source, "lkg");
    assert.equal(got.stale, true);
    assert.equal(got.staleMarker, AGENT_DEF_STALE_MARKER);
    assert.equal(got.definition.name, "proj-default");
    assert.notEqual(got.definition.name, DEFAULT_AGENT_DEFINITION.name);
    assert.match(got.staleMarker!, /LKG cache|stale/i);
  } finally {
    rmSync(home, { recursive: true, force: true });
  }
});

test("resolve: fetch null + no LKG → DEFAULT", async () => {
  const home = freshHome();
  try {
    const got = await resolveAgentDefinitionWithLkg({
      offline: false,
      definitionKey: "default",
      projectKey: "proj",
      baseUrl: "https://petbox.example",
      apiKey: "k",
      homeDir: home,
      fetchImpl: async () => {
        throw new Error("ECONNREFUSED");
      },
    });
    assert.equal(got.source, "default");
    assert.equal(got.stale, false);
    assert.equal(got.definition, DEFAULT_AGENT_DEFINITION);
  } finally {
    rmSync(home, { recursive: true, force: true });
  }
});

test("resolve: --offline with LKG uses cache (no network)", async () => {
  const home = freshHome();
  try {
    const fetched = parseAgentDefinitionResponse(VALID_BODY)!;
    writeAgentDefCache("proj", fetched, home);

    let fetchCalls = 0;
    const got = await resolveAgentDefinitionWithLkg({
      offline: true,
      definitionKey: "default",
      projectKey: "proj",
      baseUrl: "https://petbox.example",
      apiKey: "k",
      homeDir: home,
      fetchImpl: async () => {
        fetchCalls++;
        throw new Error("should not be called");
      },
    });

    assert.equal(fetchCalls, 0);
    assert.equal(got.source, "lkg");
    assert.equal(got.stale, true);
    assert.equal(got.definition.name, "proj-default");
    assert.ok(got.staleMarker);
  } finally {
    rmSync(home, { recursive: true, force: true });
  }
});

test("resolve: live fetch writes LKG then returns server source", async () => {
  const home = freshHome();
  try {
    const got = await resolveAgentDefinitionWithLkg({
      offline: false,
      definitionKey: "default",
      projectKey: "proj",
      baseUrl: "https://petbox.example",
      apiKey: "k",
      homeDir: home,
      fetchImpl: async () =>
        new Response(JSON.stringify(VALID_BODY), {
          status: 200,
          headers: { "Content-Type": "application/json" },
        }),
    });
    assert.equal(got.source, "server");
    assert.equal(got.stale, false);
    assert.equal(existsSync(agentDefCachePath("proj", home)), true);
    assert.equal(readAgentDefCache("proj", home)?.version, 3);
  } finally {
    rmSync(home, { recursive: true, force: true });
  }
});

test("offline DEFAULT_AGENT_DEFINITION still compiles (truthfulness + plan green)", () => {
  validateAgentDefinition(DEFAULT_AGENT_DEFINITION);
  const plan = planOpencodeApply(DEFAULT_AGENT_DEFINITION, {});
  assert.equal(plan.violations.length, 0);
  assert.ok(plan.files.length >= 1);
});
