// Protocol inject: memory always; spawn prescriptions when harness has spawn_subagents.
//
// Run: node --test src/protocol.test.ts

import assert from "node:assert/strict";
import { mkdtempSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { test } from "node:test";
import { resolveAgentDefinitionForSession } from "./agent-def-fetch.ts";
import { DEFAULT_AGENT_DEFINITION, type AgentDefinition } from "./agent-definition.ts";
import {
  buildProtocol,
  mcpPetboxTool,
  orchestrationPrescriptionsAllowed,
} from "./protocol.ts";

const project = "demo";

function freshHome(): string {
  return mkdtempSync(join(tmpdir(), "petbox-protocol-"));
}

const SERVER_BODY = {
  key: "default",
  version: 7,
  definition: {
    name: "server-roster",
    roles: [
      {
        slug: "orchestrator",
        tier: "orchestrator",
        requiredCapabilities: ["mcp_main_session", "spawn_subagents"],
        spawn: { allowed: true, allowedRoles: ["worker"] },
        escalation: { available: true, targets: ["reserve"] },
        notes: "SERVER-AUTHORED-ORCHESTRATOR-NOTES-xyz789",
      },
    ],
  },
};

test("orchestrationPrescriptionsAllowed tracks spawn_subagents for all three harnesses", () => {
  assert.equal(orchestrationPrescriptionsAllowed("claude-code"), true);
  assert.equal(orchestrationPrescriptionsAllowed("opencode"), true);
  // Factory Task tool on main session — https://docs.factory.ai/cli/configuration/custom-droids
  assert.equal(orchestrationPrescriptionsAllowed("droid"), true);
  assert.equal(orchestrationPrescriptionsAllowed(undefined), false);
  assert.equal(orchestrationPrescriptionsAllowed("unknown"), false);
});

// Card usage-delivery-mixes-machine-traffic: the canon-fallback memory_get call is the one
// real automated (non-deliberate) pull that exists in the wiring kit — pull-memory.ts /
// droid-pull-memory.ts fetch canon over REST and never touch this instruction at all, so this
// protocol text is the ONLY place that can mark it. It must mandate usageSource:"machine"
// unambiguously (not merely mention it as optional), or the delivery event silently defaults
// to "deliberate" and the deliberate/machine split stays permanently zero on the machine side.
test("buildProtocol's canon-fallback memory_get instruction MANDATES usageSource machine", () => {
  const text = buildProtocol(project, mcpPetboxTool, { harness: "claude-code" });
  const fallbackLine = text.split("\n").find((l) => l.includes("No canon section below"));
  assert.ok(fallbackLine, `protocol must carry the canon-fallback line:\n${text}`);
  assert.match(fallbackLine!, /usageSource:\s*"machine"/);
  // "MUST" (or equivalently strong, non-optional wording) — not just a passing mention, so an
  // agent cannot read the argument as take-it-or-leave-it.
  assert.match(fallbackLine!, /MUST pass/);
});

test("buildProtocol for droid CONTAINS spawn/orchestrator prose (Factory spawns via Task)", () => {
  const text = buildProtocol(project, mcpPetboxTool, { harness: "droid" });
  const lower = text.toLowerCase();
  assert.ok(
    lower.includes("spawn workers") || lower.includes("delegate by default"),
    `droid protocol must prescribe spawn/delegate:\n${text}`,
  );
  assert.match(text, /orchestrator/i);
  assert.match(text, /PetBox memory active/);
  assert.match(text, /search before rework/i);
  // Must not fall back to "· main" no-spawn self-intro
  assert.ok(!lower.includes("· main"), `droid must not use no-spawn main intro:\n${text}`);
});

test("buildProtocol for claude-code / opencode MUST contain spawn/delegate language", () => {
  for (const harness of ["claude-code", "opencode"] as const) {
    const text = buildProtocol(project, mcpPetboxTool, { harness });
    const lower = text.toLowerCase();
    assert.ok(
      lower.includes("spawn workers") || lower.includes("delegate by default"),
      `${harness} protocol must prescribe spawn/delegate:\n${text}`,
    );
    assert.match(text, /orchestrator/i);
    assert.match(text, /plan, decompose, delegate|Orchestrator notes/i);
  }
});

test("buildProtocol without harness omits spawn prescriptions (safe default)", () => {
  const text = buildProtocol(project, mcpPetboxTool);
  const lower = text.toLowerCase();
  assert.ok(!lower.includes("spawn workers"));
  assert.ok(!lower.includes("delegate by default"));
});

// --- petbox-namespaced-agent-names: the literal spawn-target string in the orchestrator
// self-intro must be the COMPUTED, namespaced identity (apply-artifacts.ts's emittedRoleName),
// never a hardcoded `worker` constant — that was exactly the bug at protocol.ts:62 (a third
// source of truth that could drift from the actual .claude/agents/petbox-worker.md apply emits). ---

test("buildProtocol's orchestrator self-intro tells it to spawn the NAMESPACED worker name, not a bare `worker` constant", () => {
  for (const harness of ["claude-code", "opencode", "droid"] as const) {
    const text = buildProtocol(project, mcpPetboxTool, { harness });
    assert.match(text, /Spawn as `petbox-worker`/, `${harness}: must render the computed emitted name`);
    assert.ok(!text.includes("Spawn as `worker`"), `${harness}: must not hardcode the bare slug`);
  }
});

test("buildProtocol computes the spawn-target name from the SUPPLIED definition, not a constant", () => {
  // A definition whose worker role has a different internal slug still renders correctly —
  // proves the name is computed per-definition, not baked into protocol.ts as a literal string.
  const custom: AgentDefinition = {
    name: "custom-roster",
    roles: [
      { slug: "orchestrator", tier: "orchestrator", requiredCapabilities: [] },
      { slug: "worker", tier: "worker", requiredCapabilities: [] },
    ],
  };
  const text = buildProtocol(project, mcpPetboxTool, { harness: "claude-code", definition: custom });
  assert.match(text, /Spawn as `petbox-worker`/);
});

test("buildProtocol falls back to petbox-worker when the supplied definition has no worker role (never crashes)", () => {
  const noWorker: AgentDefinition = {
    name: "no-worker",
    roles: [{ slug: "orchestrator", tier: "orchestrator", requiredCapabilities: [] }],
  };
  const text = buildProtocol(project, mcpPetboxTool, { harness: "claude-code", definition: noWorker });
  assert.match(text, /Spawn as `petbox-worker`/);
});

// --- kit-prose-contradicts-server-definition: the kit banner must DELIVER role/model
// semantics from the server definition's notes, never assert its own competing version.
// Regression guard for two prose bugs that contradicted the definition inside the SAME
// injected banner: (1) telling the orchestrator to dictate a subagent's self-intro, which
// the definition's rule 5 explicitly forbids (the subagent's own self-report is the only
// evidence of what actually ran — a dictated line turns signal into echo); (2) pointing at
// a "worker preamble" that doesn't exist anywhere else in the kit. ---

test("buildProtocol's orchestrator self-intro no longer tells the orchestrator to dictate a subagent's self-intro", () => {
  for (const harness of ["claude-code", "opencode", "droid"] as const) {
    const text = buildProtocol(project, mcpPetboxTool, { harness });
    assert.ok(
      !text.includes("write their self-intro into the brief"),
      `${harness}: kit must not instruct dictating a subagent's self-intro:\n${text}`,
    );
  }
});

test("buildProtocol does not reference a nonexistent 'worker preamble'", () => {
  for (const harness of ["claude-code", "opencode", "droid"] as const) {
    const text = buildProtocol(project, mcpPetboxTool, { harness });
    assert.ok(
      !/worker preamble/i.test(text),
      `${harness}: kit must not promise a "worker preamble" the kit never defines:\n${text}`,
    );
  }
});

// --- definition-driven banner (wiring-startup-symmetry: main-loop banner must resolve the
// portable definition the same way `apply` does — server → LKG → built-in default) ---

test("buildProtocol renders orchestrator notes from an explicitly-supplied definition", () => {
  const custom: AgentDefinition = {
    name: "custom-roster",
    roles: [
      {
        slug: "orchestrator",
        tier: "orchestrator",
        requiredCapabilities: [],
        notes: "CUSTOM-SERVER-NOTES-abc123",
      },
    ],
  };
  const text = buildProtocol(project, mcpPetboxTool, { harness: "claude-code", definition: custom });
  assert.match(text, /CUSTOM-SERVER-NOTES-abc123/);
  const defaultOrchestratorNotes = DEFAULT_AGENT_DEFINITION.roles.find(
    (r) => r.slug === "orchestrator",
  )?.notes;
  assert.ok(
    !text.includes(defaultOrchestratorNotes ?? " "),
    "must not also carry the built-in default's notes once a definition is supplied",
  );
});

test("buildProtocol falls back to the built-in DEFAULT_AGENT_DEFINITION notes when no definition is supplied", () => {
  const text = buildProtocol(project, mcpPetboxTool, { harness: "claude-code" });
  const defaultOrchNotes = DEFAULT_AGENT_DEFINITION.roles.find((r) => r.slug === "orchestrator")?.notes;
  assert.ok(defaultOrchNotes, "fixture assumption: DEFAULT_AGENT_DEFINITION has orchestrator notes");
  assert.ok(text.includes(defaultOrchNotes!));
});

// --- end-to-end: resolveAgentDefinitionForSession (server → LKG → DEFAULT) feeding buildProtocol,
// the same wiring pull-memory.ts / droid-pull-memory.ts / opencode-plugin.ts now use. ---

test("SessionStart banner renders server-supplied definition when the server answers", async () => {
  const home = freshHome();
  try {
    const got = await resolveAgentDefinitionForSession(
      { project: "proj", baseUrl: "https://petbox.example", apiKey: "k" },
      {
        homeDir: home,
        fetchImpl: async () =>
          new Response(JSON.stringify(SERVER_BODY), {
            status: 200,
            headers: { "Content-Type": "application/json" },
          }),
      },
    );
    assert.equal(got.source, "server");
    const text = buildProtocol("proj", mcpPetboxTool, {
      harness: "claude-code",
      definition: got.definition,
    });
    assert.match(text, /SERVER-AUTHORED-ORCHESTRATOR-NOTES-xyz789/);
    assert.match(text, /PetBox memory active/);
  } finally {
    rmSync(home, { recursive: true, force: true });
  }
});

test("SessionStart banner falls back to the built-in default when neither server nor LKG cache is available (never crashes, never empty)", async () => {
  const home = freshHome(); // fresh dir → guaranteed no LKG cache file
  try {
    const got = await resolveAgentDefinitionForSession(
      { project: "proj", baseUrl: "https://petbox.example", apiKey: "k" },
      {
        homeDir: home,
        fetchImpl: async () => {
          throw new Error("ECONNREFUSED");
        },
      },
    );
    assert.equal(got.source, "default");
    assert.equal(got.definition, DEFAULT_AGENT_DEFINITION);

    const text = buildProtocol("proj", mcpPetboxTool, {
      harness: "claude-code",
      definition: got.definition,
    });
    assert.ok(text.length > 0, "banner must never be empty");
    assert.match(text, /PetBox memory active/);
    assert.match(text, /orchestrator/i);
  } finally {
    rmSync(home, { recursive: true, force: true });
  }
});
