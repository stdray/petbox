// Unit tests for agent-def-fetch (parser + fetch + LKG cache resolution).
//
// Run: node --test src/agent-def-fetch.test.ts   (Node >= 23.6 native TS)

import assert from "node:assert/strict";
import { mkdtempSync, readFileSync, rmSync, existsSync, writeFileSync, mkdirSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { test } from "node:test";
import {
  AGENT_DEF_OFFLINE_STALE_MARKER,
  AGENT_DEF_STALE_MARKER,
  agentDefCacheDir,
  agentDefCachePath,
  agentDefinitionBannerNote,
  DEFAULT_DEFINITION_KEY,
  fetchAgentDefinition,
  parseAgentDefinitionResponse,
  readAgentDefCache,
  resolveAgentDefinitionWithLkg,
  writeAgentDefCache,
  type ResolvedAgentDefinition,
} from "./agent-def-fetch.ts";
import { DEFAULT_AGENT_DEFINITION, validateAgentDefinition } from "./agent-definition.ts";
import { planOpencodeApply } from "./apply-artifacts.ts";
import { readWireLogTail } from "./wire-log.ts";

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
  const [firstRole] = got.definition.roles;
  assert.ok(firstRole);
  assert.equal(firstRole.slug, "orchestrator");
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
  const calls: Array<{ url: string; headers: RequestInit["headers"] }> = [];
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
  assert.equal(calls.length, 1);
  const [call] = calls;
  assert.ok(call);
  assert.equal(call.url, "https://petbox.example/api/%24system/agent-defs/default");
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
    // Genuine network failure — never reached the server, so this is NOT a 404.
    assert.equal(got.notFoundOnServer, undefined);
  } finally {
    rmSync(home, { recursive: true, force: true });
  }
});

// Regression for the misleading-message bug (agent-def-404-not-offline): a project with no
// own agent-definition document gets a plain 404 from a reachable server — that is NORMAL
// (fresh project), not an offline/unreachable condition. resolve must still fall through to
// DEFAULT (unchanged functional behavior) but flag notFoundOnServer so the caller (wire.ts's
// resolveApplyDefinition) can say so instead of claiming "no server".
test("resolve: fetch 404 + no LKG → DEFAULT, flagged notFoundOnServer (server reachable, just no definition)", async () => {
  const home = freshHome();
  try {
    const got = await resolveAgentDefinitionWithLkg({
      offline: false,
      definitionKey: "default",
      projectKey: "proj",
      baseUrl: "https://petbox.example",
      apiKey: "k",
      homeDir: home,
      fetchImpl: async () => new Response("not found", { status: 404 }),
    });
    assert.equal(got.source, "default");
    assert.equal(got.stale, false);
    assert.equal(got.definition, DEFAULT_AGENT_DEFINITION);
    assert.equal(got.notFoundOnServer, true);
  } finally {
    rmSync(home, { recursive: true, force: true });
  }
});

test("resolve: fetch 500 + no LKG → DEFAULT, NOT flagged notFoundOnServer (server error, not a clean 404)", async () => {
  const home = freshHome();
  try {
    const got = await resolveAgentDefinitionWithLkg({
      offline: false,
      definitionKey: "default",
      projectKey: "proj",
      baseUrl: "https://petbox.example",
      apiKey: "k",
      homeDir: home,
      fetchImpl: async () => new Response("boom", { status: 500 }),
    });
    assert.equal(got.source, "default");
    assert.equal(got.definition, DEFAULT_AGENT_DEFINITION);
    assert.equal(got.notFoundOnServer, undefined);
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
  // Every default role needs a binding now — apply refuses an unbound declared role outright
  // (reserve-unbound-inherits-session-model) — so this offline-compile smoke test binds all of
  // them, mirroring wire.ts's DEFAULT_ROLE_MODEL_SEED, rather than exercising that refusal here.
  const plan = planOpencodeApply(DEFAULT_AGENT_DEFINITION, {
    orchestrator: "opus",
    worker: "sonnet",
    "worker-highstakes": "opus",
    utility: "haiku",
    explore: "haiku",
    reserve: "fable",
  });
  assert.equal(plan.violations.length, 0);
  assert.ok(plan.files.length >= 1);
});

test("readAgentDefCache: corrupt JSON → null (treated as no cache)", () => {
  const home = freshHome();
  try {
    mkdirSync(agentDefCacheDir(home), { recursive: true });
    writeFileSync(agentDefCachePath("proj", home), "{ not valid json", "utf8");
    assert.equal(readAgentDefCache("proj", home), null);
  } finally {
    rmSync(home, { recursive: true, force: true });
  }
});

test("resolve: tampered cache (role.model injected) is rejected → DEFAULT, no injection", async () => {
  const home = freshHome();
  try {
    // Well-formed envelope but a poisoned role carrying a model binding — a portable
    // definition must never carry model. The read path must reject it, and resolve must
    // fall through to DEFAULT rather than compile the attacker's roster.
    mkdirSync(agentDefCacheDir(home), { recursive: true });
    const poisoned = {
      key: "default",
      version: 9,
      fetchedAt: "2026-07-10T00:00:00.000Z",
      definition: {
        name: "attacker-roster",
        roles: [
          {
            slug: "orchestrator",
            tier: "orchestrator",
            requiredCapabilities: [],
            model: "attacker/evil-model",
          },
        ],
      },
    };
    writeFileSync(agentDefCachePath("proj", home), JSON.stringify(poisoned), "utf8");

    // Direct read rejects the tampered record.
    assert.equal(readAgentDefCache("proj", home), null);

    // And resolve (offline, so cache is the only non-DEFAULT source) falls to DEFAULT,
    // never the attacker roster.
    const got = await resolveAgentDefinitionWithLkg({
      offline: true,
      definitionKey: "default",
      projectKey: "proj",
      homeDir: home,
    });
    assert.equal(got.source, "default");
    assert.equal(got.definition, DEFAULT_AGENT_DEFINITION);
    assert.notEqual(got.definition.name, "attacker-roster");
  } finally {
    rmSync(home, { recursive: true, force: true });
  }
});

// ---- wire-silent-failures-invisible: 403 (scope) must be distinguishable from offline --------
//
// Evidence 2026-07-26 (card comment): 403 fell into the exact same bucket as a genuine
// network/timeout failure — both produced source:"default", stale:false, notFoundOnServer
// undefined — so the caller's message said "offline default definition (no server, no LKG
// cache)" for a key that was flatly refused by a server that WAS reachable. `forbidden` closes
// that gap.

test("resolve: fetch 403 + no LKG → DEFAULT, flagged forbidden (server reachable, refused — a scope problem, not offline)", async () => {
  const home = freshHome();
  try {
    const got = await resolveAgentDefinitionWithLkg({
      offline: false,
      definitionKey: "default",
      projectKey: "proj",
      baseUrl: "https://petbox.example",
      apiKey: "k",
      homeDir: home,
      fetchImpl: async () => new Response("forbidden", { status: 403 }),
    });
    assert.equal(got.source, "default");
    assert.equal(got.definition, DEFAULT_AGENT_DEFINITION);
    assert.equal(got.forbidden, true);
    // Distinct from the 404 case: this is NOT "no definition yet", it's "refused".
    assert.equal(got.notFoundOnServer, undefined);
  } finally {
    rmSync(home, { recursive: true, force: true });
  }
});

test("resolve: fetch 401 behaves the same as 403 (both are 'refused', not offline)", async () => {
  const home = freshHome();
  try {
    const got = await resolveAgentDefinitionWithLkg({
      offline: false,
      definitionKey: "default",
      projectKey: "proj",
      baseUrl: "https://petbox.example",
      apiKey: "k",
      homeDir: home,
      fetchImpl: async () => new Response("unauthorized", { status: 401 }),
    });
    assert.equal(got.forbidden, true);
  } finally {
    rmSync(home, { recursive: true, force: true });
  }
});

test("resolve: genuine network failure (ECONNREFUSED) is NOT flagged forbidden — stays the offline case", async () => {
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
    assert.equal(got.forbidden, undefined);
  } finally {
    rmSync(home, { recursive: true, force: true });
  }
});

test("resolve: fetch 403 WITH an LKG cache present still uses the cache, but flags forbidden alongside stale", async () => {
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
      fetchImpl: async () => new Response("forbidden", { status: 403 }),
    });
    assert.equal(got.source, "lkg");
    assert.equal(got.stale, true);
    assert.equal(got.forbidden, true);
  } finally {
    rmSync(home, { recursive: true, force: true });
  }
});

// ---- corrupt LKG cache vs no cache: distinguishable via wire.log (card item 2) ----------------

test("readAgentDefCache: MISSING cache file → null, silent, no wire.log trace (Class A — fresh machine)", () => {
  const home = freshHome();
  try {
    assert.equal(readAgentDefCache("proj", home), null);
    assert.deepEqual(readWireLogTail(20, home), []);
  } finally {
    rmSync(home, { recursive: true, force: true });
  }
});

test("readAgentDefCache: PRESENT but corrupt cache → null, same functional result, but leaves a Class-Б wire.log trace", () => {
  const home = freshHome();
  try {
    mkdirSync(agentDefCacheDir(home), { recursive: true });
    writeFileSync(agentDefCachePath("proj", home), "{ not valid json", "utf8");
    assert.equal(readAgentDefCache("proj", home), null);
    const tail = readWireLogTail(20, home);
    assert.ok(tail.length > 0, "a present-but-corrupt LKG cache must be distinguishable from 'no cache at all'");
    assert.match(tail.join("\n"), /LKG cache.*not valid JSON/i);
  } finally {
    rmSync(home, { recursive: true, force: true });
  }
});

// ---- agentDefinitionBannerNote: the SessionStart banner degradation marker --------------------

const RESOLVED_BASE = {
  definition: DEFAULT_AGENT_DEFINITION,
} as const;

test("agentDefinitionBannerNote: source server → no note (healthy case)", () => {
  const resolved: ResolvedAgentDefinition = { ...RESOLVED_BASE, source: "server", stale: false };
  assert.equal(agentDefinitionBannerNote(resolved), "");
});

test("agentDefinitionBannerNote: source lkg → the existing stale marker", () => {
  const resolved: ResolvedAgentDefinition = {
    ...RESOLVED_BASE,
    source: "lkg",
    stale: true,
    staleMarker: AGENT_DEF_STALE_MARKER,
  };
  assert.equal(agentDefinitionBannerNote(resolved), AGENT_DEF_STALE_MARKER);
});

test("agentDefinitionBannerNote: source lkg + forbidden → names the scope problem, not just 'stale'", () => {
  const resolved: ResolvedAgentDefinition = {
    ...RESOLVED_BASE,
    source: "lkg",
    stale: true,
    staleMarker: AGENT_DEF_STALE_MARKER,
    forbidden: true,
  };
  assert.match(agentDefinitionBannerNote(resolved), /401\/403/);
});

test("agentDefinitionBannerNote: source default (built-in fallback) → the required marker line", () => {
  const resolved: ResolvedAgentDefinition = { ...RESOLVED_BASE, source: "default", stale: false };
  assert.equal(
    agentDefinitionBannerNote(resolved),
    "⚠ definition: built-in fallback (server/LKG unavailable).",
  );
});

test("agentDefinitionBannerNote: source default + notFoundOnServer → the gentler 'no definition yet' note, not a scary one", () => {
  const resolved: ResolvedAgentDefinition = {
    ...RESOLVED_BASE,
    source: "default",
    stale: false,
    notFoundOnServer: true,
  };
  const note = agentDefinitionBannerNote(resolved);
  assert.match(note, /no server-side definition/);
  assert.doesNotMatch(note, /unavailable/);
});

test("agentDefinitionBannerNote: source default + forbidden → names the scope problem, not 'offline'", () => {
  const resolved: ResolvedAgentDefinition = {
    ...RESOLVED_BASE,
    source: "default",
    stale: false,
    forbidden: true,
  };
  const note = agentDefinitionBannerNote(resolved);
  assert.match(note, /401\/403/);
});

// ---- round 2 of doctor-reports-answering-server-unreachable: agentDefinitionBannerNote must
// know about --offline too, not just notFoundOnServer/forbidden/httpError. Reported live: the
// LKG banner said "PetBox unreachable" for a run that had just reached the same server with
// --offline omitted moments earlier — only the CALLER's --offline choice explains the skip.

test("agentDefinitionBannerNote: source lkg + offline → names --offline, never 'unreachable'", () => {
  const resolved: ResolvedAgentDefinition = {
    ...RESOLVED_BASE,
    source: "lkg",
    stale: true,
    staleMarker: AGENT_DEF_OFFLINE_STALE_MARKER,
    offline: true,
  };
  const note = agentDefinitionBannerNote(resolved);
  assert.match(note, /--offline/);
  assert.doesNotMatch(note, /unreachable/i);
});

test("agentDefinitionBannerNote: source lkg + offline with NO staleMarker set → still falls back to the offline-aware default, not the generic 'unreachable' one", () => {
  const resolved: ResolvedAgentDefinition = {
    ...RESOLVED_BASE,
    source: "lkg",
    stale: true,
    offline: true,
  };
  assert.equal(agentDefinitionBannerNote(resolved), AGENT_DEF_OFFLINE_STALE_MARKER);
});

test("agentDefinitionBannerNote: source default + offline (no cache at all) → names --offline, never 'unavailable'/'unreachable'", () => {
  const resolved: ResolvedAgentDefinition = {
    ...RESOLVED_BASE,
    source: "default",
    stale: false,
    offline: true,
  };
  const note = agentDefinitionBannerNote(resolved);
  assert.match(note, /--offline/);
  assert.doesNotMatch(note, /unreachable/i);
  assert.doesNotMatch(note, /unavailable/i);
});

test("resolveAgentDefinitionWithLkg: --offline with LKG cache present sets offline:true and the offline-aware staleMarker (not AGENT_DEF_STALE_MARKER)", async () => {
  const home = freshHome();
  try {
    const fetched = parseAgentDefinitionResponse(VALID_BODY)!;
    writeAgentDefCache("proj", fetched, home);

    const got = await resolveAgentDefinitionWithLkg({
      offline: true,
      definitionKey: "default",
      projectKey: "proj",
      baseUrl: "https://petbox.example",
      apiKey: "k",
      homeDir: home,
      fetchImpl: async () => {
        throw new Error("should not be called");
      },
    });

    assert.equal(got.source, "lkg");
    assert.equal(got.offline, true);
    assert.equal(got.staleMarker, AGENT_DEF_OFFLINE_STALE_MARKER);
    assert.doesNotMatch(got.staleMarker!, /unreachable/i);
  } finally {
    rmSync(home, { recursive: true, force: true });
  }
});

test("resolveAgentDefinitionWithLkg: a genuine (non-offline) network failure with LKG cache present still sets the OLD unreachable-worded marker, and offline stays unset", async () => {
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
      fetchImpl: async () => {
        throw new Error("ECONNREFUSED");
      },
    });

    assert.equal(got.source, "lkg");
    assert.equal(got.offline, undefined);
    assert.equal(got.staleMarker, AGENT_DEF_STALE_MARKER);
  } finally {
    rmSync(home, { recursive: true, force: true });
  }
});
