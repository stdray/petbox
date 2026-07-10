// Unit tests for agent-def-fetch (response parser + fetch null-on-failure).
//
// Run: node --test src/agent-def-fetch.test.ts   (Node >= 23.6 native TS)

import assert from "node:assert/strict";
import { test } from "node:test";
import {
  DEFAULT_DEFINITION_KEY,
  fetchAgentDefinition,
  parseAgentDefinitionResponse,
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

test("parseAgentDefinitionResponse maps camelCase envelope + definition", () => {
  const got = parseAgentDefinitionResponse(VALID_BODY);
  assert.ok(got);
  assert.equal(got.key, "default");
  assert.equal(got.version, 3);
  assert.equal(got.definition.name, "proj-default");
  assert.equal(got.definition.roles.length, 2);
  assert.equal(got.definition.roles[0].slug, "orchestrator");
  assert.deepEqual(got.definition.roles[0].requiredCapabilities, ["mcp_main_session"]);
  assert.deepEqual(got.definition.roles[0].spawn, {
    allowed: true,
    allowedRoles: ["worker"],
  });
  assert.equal(got.definition.roles[0].notes, "main loop");
  assert.equal(got.definition.roles[1].spawn?.allowed, false);
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
  assert.equal(
    parseAgentDefinitionResponse({
      key: "default",
      version: 1,
      definition: { name: "", roles: [{ slug: "a", tier: "t", requiredCapabilities: [] }] },
    }),
    null,
  );
  assert.equal(
    parseAgentDefinitionResponse({
      key: "default",
      definition: {
        name: "x",
        roles: [{ slug: "a", tier: "t", requiredCapabilities: [] }],
      },
    }),
    null,
  );
});

test("parseAgentDefinitionResponse tolerates string version and missing optional spawn", () => {
  const got = parseAgentDefinitionResponse({
    key: "custom",
    version: "7",
    definition: {
      name: "n",
      roles: [{ slug: "solo", tier: "worker", requiredCapabilities: [] }],
    },
  });
  assert.ok(got);
  assert.equal(got.version, 7);
  assert.equal(got.definition.roles[0].spawn, undefined);
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
  assert.equal(got.definition.name, "proj-default");
  assert.equal(calls.length, 1);
  assert.equal(calls[0].url, "https://petbox.example/api/%24system/agent-defs/default");
  const headers = new Headers(calls[0].headers);
  assert.equal(headers.get("X-Api-Key"), "k");
});

test("fetchAgentDefinition returns null on 404 / network / bad body (never throws)", async () => {
  const notFound = await fetchAgentDefinition({
    baseUrl: "https://petbox.example",
    projectKey: "p",
    apiKey: "k",
    fetchImpl: async () => new Response("nope", { status: 404 }),
  });
  assert.equal(notFound, null);

  const network = await fetchAgentDefinition({
    baseUrl: "https://petbox.example",
    projectKey: "p",
    apiKey: "k",
    fetchImpl: async () => {
      throw new Error("ECONNREFUSED");
    },
  });
  assert.equal(network, null);

  const badBody = await fetchAgentDefinition({
    baseUrl: "https://petbox.example",
    projectKey: "p",
    apiKey: "k",
    fetchImpl: async () =>
      new Response(JSON.stringify({ key: "default", version: 1 }), {
        status: 200,
        headers: { "Content-Type": "application/json" },
      }),
  });
  assert.equal(badBody, null);

  const missingCreds = await fetchAgentDefinition({
    baseUrl: "",
    projectKey: "p",
    apiKey: "k",
    fetchImpl: async () => {
      throw new Error("should not be called");
    },
  });
  assert.equal(missingCreds, null);
});

test("offline DEFAULT_AGENT_DEFINITION still compiles (truthfulness + plan green)", () => {
  validateAgentDefinition(DEFAULT_AGENT_DEFINITION);
  const plan = planOpencodeApply(DEFAULT_AGENT_DEFINITION, {});
  assert.equal(plan.violations.length, 0);
  assert.ok(plan.files.length >= 1);
  assert.ok(plan.files.every((f) => f.relativePath.startsWith(".opencode/agent/")));
});
