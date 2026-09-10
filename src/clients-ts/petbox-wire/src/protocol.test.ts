// Protocol inject: memory always; spawn prescriptions when harness has spawn_subagents.
//
// Run: node --test src/protocol.test.ts

import assert from "node:assert/strict";
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { test } from "node:test";
import { DEFAULT_AGENT_DEFINITION, type AgentDefinition } from "./agent-definition.ts";
import { resolveDefinitionForSession } from "./definition-source.ts";
import {
  buildProtocol,
  mcpPetboxTool,
  orchestrationPrescriptionsAllowed,
} from "./protocol.ts";

const project = "demo";

function freshHome(): string {
  return mkdtempSync(join(tmpdir(), "petbox-protocol-"));
}

/** A project layer that REPLACES the orchestrator's prose — the shape a real operator uses. */
function writeProjectLayer(root: string, notes: string): void {
  const dir = join(root, ".petbox", "agents");
  mkdirSync(dir, { recursive: true });
  writeFileSync(dir + "/layer.json", JSON.stringify({ name: "project", mode: "overlay" }), "utf8");
  writeFileSync(join(dir, "petbox-orchestrator.md"), notes, "utf8");
}

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

// --- protocol-block-unowned-budget-hog: the protocol block is the banner's LARGEST fixed
// consumer (it was 6378 B of a 9400 B budget — 68% — and drifted ~170 B between edits, more than
// the whole remaining margin, so the canon leg was silently dropped on session after session).
// The owner's decision (intake canon-trim-budget-decision) is a DIAGNOSTIC ceiling, not a write
// gate: SESSION_BANNER_BUDGET_BYTES and the 10 000 B hard limit stay untouched, and nothing
// rejects a canon write — this assert is the ceiling's teeth. It is measured on BOTH source legs
// because `resume`/`compact` append a suffix and are therefore the worst case (that ~94 B is
// exactly what pushed the live 2026-07-26 resume banner 4 B over budget). ---
//
// card protocol-ceiling-gate-blind-to-published-kit-version-length (owner decisions, 2026-09-10) —
// this SUPERSEDES the number above, it does not just add to it:
//
//   1. Ceiling raised 5_400 -> 5_450. The `kit v${KIT_VERSION}` label's length is legitimate part
//      of the block's cost — the owner ruled it counts, not a bug to hide — and the extra 50 B is
//      headroom for the build-number component of that version string growing over time.
//   2. The gate had been measuring against the CHECKOUT's KIT_VERSION ("0.0.0", 5 chars), never
//      against what actually ships: CI's TsWirePack task stamps package.json with GitVersion's
//      real semver (e.g. "0.1.0-ci.2386", 13 chars, growing) before `npm publish`, and
//      agent-definition.ts's loadKitVersion() reads that same package.json field — so the
//      published kit's banner is ~8 B+ longer than anything this test ever measured, and the gate
//      was structurally blind to it (measured live: repo 5397/5398 B vs published 5405/5406 B on
//      resume/compact, both over the OLD 5_400 ceiling). Fixed by measuring against a worst-case
//      version string (`kitVersion` override in ProtocolOpts, production code path untouched) so
//      the gate now catches this on the commit that grows the block, not after a publish already
//      shipped it.
//
// Net: PROTOCOL_BLOCK_CEILING_BYTES is a decided, current number (5_450) with headroom of ~43 B
// against the worst-case measurement below — still do NOT raise it again without a fresh owner
// decision.

export const PROTOCOL_BLOCK_CEILING_BYTES = 5_450;

// Worst-case KIT_VERSION for the ceiling measurement (NOT the checkout's "0.0.0" placeholder) —
// see the comment on PROTOCOL_BLOCK_CEILING_BYTES above for why. Longer than any version CI has
// stamped so far (14 chars vs. the observed "0.1.0-ci.2386"'s 13), so the gate has margin against
// the build counter growing before anyone revisits this.
const WORST_CASE_KIT_VERSION = "0.1.0-ci.99999";

test("the protocol block stays under its diagnostic ceiling on every source leg, measured on the worst-case (longest) published version string", () => {
  for (const source of ["startup", "resume", "compact"] as const) {
    const text = buildProtocol(project, mcpPetboxTool, {
      harness: "claude-code",
      source,
      kitVersion: WORST_CASE_KIT_VERSION,
    });
    const bytes = Buffer.byteLength(text, "utf8");
    assert.ok(
      bytes <= PROTOCOL_BLOCK_CEILING_BYTES,
      `protocol block for source=${source} (kitVersion=${WORST_CASE_KIT_VERSION}) is ${bytes} B, over the ` +
        `${PROTOCOL_BLOCK_CEILING_BYTES} B ceiling — it is crowding the canon out of the banner. Trim the block ` +
        "or the orchestrator notes; do NOT raise this number without an owner decision " +
        "(card protocol-ceiling-gate-blind-to-published-kit-version-length supersedes the earlier " +
        "intake canon-trim-budget-decision).",
    );
  }
});

// The load-bearing rules must survive any future trim of the notes: each of these is a rule that
// exists ONLY in this banner (the spawned subagent never runs the SessionStart hook), so losing
// one loses it everywhere. Anchors are phrase-level on purpose — reword them consciously.
test("the trimmed protocol block still carries every load-bearing orchestrator rule", () => {
  const text = buildProtocol(project, mcpPetboxTool, { harness: "claude-code" });
  const required: ReadonlyArray<readonly [string, RegExp]> = [
    ["no model at spawn", /Never pass a model at spawn/],
    ["escalate by ROLE, not model", /a different ROLE, never a spawn argument/],
    // The tier criterion carried ONE axis ("expensive") from the role's birth until 2026-09-06,
    // and a worker was picked for work whose failure mode was the other one: a plausible number
    // acceptance cannot re-derive. Both axes are pinned so a future trim cannot drop one silently.
    ["tier criterion names the cost axis", /would be expensive/],
    ["tier criterion names the unnoticed axis", /would pass unnoticed/],
    ["delegate by default", /Delegate by default/],
    ["no errand role", /no errand role/],
    ["reserve triggers are events", /Reserve triggers on EVENTS/],
    ["never dictate a self-intro", /Never dictate a subagent's self-intro/],
    ["never accept unseen verification", /Never accept a verification you did not see/],
    ["tools lie about remote state", /tools LIE about remote state/],
    ["never self-set Done", /Never self-set Done\/accepted/],
    ["lane axis is conditional", /tasks_methodology_rules_get/],
    ["stale InProgress circuit breaker", /Circuit breaker for stale `InProgress`/],
    // NOT the tier criterion's two axes ("would be expensive" / "would pass unnoticed") — a flat
    // presence check on the WHOLE banner cannot tell "both axes in the same rule" apart from
    // "each axis somewhere in the document", which is the exact gap rule-agent-facing-text-
    // names-both-axes exists to close. That pair now has its own COMPOSITION test below.
  ];
  for (const [name, re] of required) {
    assert.match(text, re, `load-bearing rule missing from the banner: ${name}`);
  }
});

// --- rule-agent-facing-text-names-both-axes (R1, root of umbrella-agent-text-names-both-axes) ---
//
// CRITERION. A rule delivered to an agent is broken if it states only ONE half of a governing
// pair — a trigger without its non-trigger, a prohibition without the obligation that survives
// it, a benefit without the invariant that overrides it — even when the OTHER half is written
// down SOMEWHERE ELSE in the very same document. An agent acts on the text in front of it at the
// moment of the act; an axis it cannot see in THAT text does not exist for that act, no matter
// where else it is named. (Scope note, not a trailing override — attach it here, to the clause it
// qualifies, not at the end where it would read as walking the whole rule back: a rule stating one
// plain fact, not a trigger/prohibition/tradeoff, has no second axis to name and is simply out of
// scope for this pairing; forcing one in would be theater, not compliance.)
//
// `assertRuleNamesBothAxes` is the checkable form of that criterion: it locates the rule's OWN
// block (see `extractRuleBlock`) and requires BOTH axis patterns to occur INSIDE that one block —
// never merely inside the surrounding document. Two independent `assert.match(wholeText, axisA)` /
// `assert.match(wholeText, axisB)` calls do NOT satisfy this: they pass just as happily when axis B
// sits in a wholly different, unrelated rule. That is precisely the failure mode that let the tier
// criterion below run one-axis ("expensive" only) from the worker-highstakes role's birth until
// 2026-09-06 (memory m-7961c8269e5840c79643b0c2600693a2) — three misses before anyone noticed,
// because the ratchet of the day pinned the rule's PRESENCE, never its COMPOSITION. The red-proof
// test further down constructs exactly that shape of mutant: a presence-only check lets it through,
// this one does not.
//
// Ratchet scope, stated honestly: this file only reaches text that flows through `buildProtocol`
// (role `notes` in default-agents.json, the banner it renders into). It says nothing about SKILL.md
// prose, which never passes through here — that surface needs its own composition ratchet
// (tracked separately: skills-audit-against-both-axes-criterion). Do not read a green run of THIS
// file as proof that skills satisfy the criterion.

/** Absence has its OWN representation: "the rule itself is gone" must never read as "one axis of
 * a present rule is missing" — those are different defects with different fixes. */
function extractRuleBlock(text: string, anchor: RegExp): string {
  const lines = text.split("\n");
  const line = lines.find((l) => anchor.test(l));
  assert.ok(line !== undefined, `rule anchor ${anchor} not found at all in text (the whole rule is missing, not just one axis):\n${text}`);
  return line!;
}

function assertRuleNamesBothAxes(
  text: string,
  anchor: RegExp,
  axisA: RegExp,
  axisB: RegExp,
  label: string,
): void {
  const block = extractRuleBlock(text, anchor);
  assert.match(block, axisA, `${label}: axis A (${axisA}) missing from the rule's OWN block: "${block}"`);
  assert.match(
    block,
    axisB,
    `${label}: axis B (${axisB}) missing from the rule's OWN block (rule-agent-facing-text-names-both-axes: ` +
      `both axes must live in the SAME rule, not merely somewhere in the same document) — rule text: "${block}"`,
  );
}

test("orchestrator tier criterion names BOTH axes in its OWN rule block, not merely somewhere in the banner", () => {
  const text = buildProtocol(project, mcpPetboxTool, { harness: "claude-code" });
  assertRuleNamesBothAxes(
    text,
    /Never pass a model at spawn/,
    /would be expensive/,
    /would pass unnoticed/,
    "tier criterion",
  );
});

test("RED-PROOF: assertRuleNamesBothAxes rejects a mutant whose second axis was moved to an unrelated rule, which a presence-only ratchet cannot tell apart from a correctly-composed one", () => {
  const mutant: AgentDefinition = {
    name: "mutant-one-axis",
    roles: [
      {
        slug: "orchestrator",
        tier: "orchestrator",
        requiredCapabilities: [],
        notes:
          "1. Never pass a model at spawn: escalate to worker-highstakes when a wrong result would be expensive.\n" +
          "2. Unrelated rule: a different mistake, in a different place, would pass unnoticed if nobody checked the logs.",
      },
    ],
  };
  const text = buildProtocol(project, mcpPetboxTool, { harness: "claude-code", definition: mutant });

  // CONTROL — proves the fixture actually models the bug this test exists to catch: a
  // presence-only ratchet (the OLD shape, two independent assert.match calls over the WHOLE
  // banner) sees both phrases and reports success. If either of these two lines ever fails, the
  // fixture stopped modeling the one-axis mutant and the "rejects" assertion below would be
  // proving nothing.
  assert.match(text, /would be expensive/, "control: presence-only check would find axis A anywhere");
  assert.match(text, /would pass unnoticed/, "control: presence-only check would find axis B anywhere");

  // The COMPOSITION ratchet must still catch it: axis B lives in rule 2, not rule 1.
  assert.throws(
    () =>
      assertRuleNamesBothAxes(
        text,
        /Never pass a model at spawn/,
        /would be expensive/,
        /would pass unnoticed/,
        "mutant tier criterion",
      ),
    /axis B .* missing from the rule's OWN block/,
    "composition ratchet must reject an axis named in a DIFFERENT rule, not just anywhere in the banner",
  );
});

test("RED-PROOF: assertRuleNamesBothAxes rejects a mutant where the whole rule is gone, and says so distinctly from a missing-axis failure", () => {
  const mutant: AgentDefinition = {
    name: "mutant-rule-deleted",
    roles: [
      {
        slug: "orchestrator",
        tier: "orchestrator",
        requiredCapabilities: [],
        notes: "1. A totally different rule, nothing about model tiers at all.",
      },
    ],
  };
  const text = buildProtocol(project, mcpPetboxTool, { harness: "claude-code", definition: mutant });
  assert.throws(
    () =>
      assertRuleNamesBothAxes(
        text,
        /Never pass a model at spawn/,
        /would be expensive/,
        /would pass unnoticed/,
        "deleted tier criterion",
      ),
    /not found at all in text \(the whole rule is missing, not just one axis\)/,
    "absence of the whole rule must get its OWN message, distinct from a missing-axis message",
  );
});

// --- role-notes-name-the-obligation-axis (L2, under umbrella-agent-text-names-both-axes) ---
//
// Two more rules carried exactly the one-axis shape R1 exists to catch. Both got a COMPOSITION
// test here rather than a second flat presence string in the `required` list above — a presence
// check cannot tell "both axes in the same rule" from "each axis somewhere in the document",
// which is the gap that let the tier criterion run one-axis for a month.

test("orchestrator's Done/accepted-freeze rule names BOTH axes in its OWN rule block: the prohibition AND the obligation that survives it", () => {
  const text = buildProtocol(project, mcpPetboxTool, { harness: "claude-code" });
  assertRuleNamesBothAxes(
    text,
    /Never self-set Done\/accepted/,
    /stop one status short of Done\/accepted/,
    /left sitting in its starting status.*is a defect/,
    "Done/accepted freeze rule",
  );
});

test("RED-PROOF: the Done/accepted-freeze composition check rejects a mutant where the obligation half moved to an unrelated rule", () => {
  const mutant: AgentDefinition = {
    name: "mutant-status-freeze",
    roles: [
      {
        slug: "orchestrator",
        tier: "orchestrator",
        requiredCapabilities: [],
        notes:
          "6. Never self-set Done/accepted: stop one status short of Done/accepted and hand over.\n" +
          "9. Unrelated bookkeeping rule: a card left sitting in its starting status after the work already moved is a defect, filed on the observations board.",
      },
    ],
  };
  const text = buildProtocol(project, mcpPetboxTool, { harness: "claude-code", definition: mutant });

  // CONTROL — proves the fixture models the bug: a presence-only check finds both phrases.
  assert.match(text, /stop one status short of Done\/accepted/, "control: axis A found anywhere");
  assert.match(text, /left sitting in its starting status.*is a defect/, "control: axis B found anywhere");

  assert.throws(
    () =>
      assertRuleNamesBothAxes(
        text,
        /Never self-set Done\/accepted/,
        /stop one status short of Done\/accepted/,
        /left sitting in its starting status.*is a defect/,
        "mutant status-freeze rule",
      ),
    /axis B .* missing from the rule's OWN block/,
    "composition ratchet must reject an obligation phrase living in a different rule",
  );
});

// Worker/worker-highstakes notes never flow through buildProtocol (protocol.ts only reads the
// orchestrator role's notes into the banner — the worker role is looked up only for its emitted
// NAME, never its prose). So this checks the raw role notes from the definition directly, the
// same way the by-number cross-reference test above reads DEFAULT_AGENT_DEFINITION.roles rather
// than buildProtocol's output. Both worker roles are checked: worker-highstakes point 9 asserts
// "you are the same worker... no execution rule changes" — leaving its point 3 one-axis while
// fixing only `worker`'s would make that claim false the moment this file's edit landed.
for (const workerSlug of ["worker", "worker-highstakes"] as const) {
  test(`${workerSlug} notes' ambiguous-brief rule names BOTH axes in its OWN rule block: unclear-brief proceeds, wrong-brief stops`, () => {
    const notes = DEFAULT_AGENT_DEFINITION.roles.find((r) => r.slug === workerSlug)?.notes ?? "";
    assertRuleNamesBothAxes(
      notes,
      /Ambiguous brief/,
      /minimal reasonable assumption, state it, proceed/,
      /you believe is WRONG.*is a stop/,
      `${workerSlug} ambiguous/wrong-brief rule`,
    );
  });
}

test("RED-PROOF: the ambiguous/wrong-brief composition check rejects a mutant where the wrong-brief obligation moved to an unrelated rule", () => {
  const mutant: AgentDefinition = {
    name: "mutant-brief-axis",
    roles: [
      {
        slug: "worker",
        tier: "worker",
        requiredCapabilities: [],
        notes:
          "3. Do ONLY the delegated task. Ambiguous brief -> minimal reasonable assumption, state it, proceed.\n" +
          "7. Unrelated rule: a brief you believe is WRONG is a stop, filed separately from this one.",
      },
    ],
  };
  const notes = mutant.roles.find((r) => r.slug === "worker")?.notes ?? "";

  // CONTROL — proves the fixture models the bug: a presence-only check finds both phrases.
  assert.match(notes, /minimal reasonable assumption, state it, proceed/, "control: axis A found anywhere");
  assert.match(notes, /you believe is WRONG.*is a stop/, "control: axis B found anywhere");

  assert.throws(
    () =>
      assertRuleNamesBothAxes(
        notes,
        /Ambiguous brief/,
        /minimal reasonable assumption, state it, proceed/,
        /you believe is WRONG.*is a stop/,
        "mutant ambiguous/wrong-brief rule",
      ),
    /axis B .* missing from the rule's OWN block/,
    "composition ratchet must reject a wrong-brief obligation living in a different rule",
  );
});

// The self-intro block points at the delegate-by-default rule BY NUMBER ("Orchestrator notes,
// point N") — the one numbered cross-reference in the kit. Renumbering the notes without
// updating it silently aims the pointer at a different rule.
test("the by-number cross-reference in the self-intro resolves to the delegate-by-default point", () => {
  const text = buildProtocol(project, mcpPetboxTool, { harness: "claude-code" });
  const ref = /Orchestrator notes, point (\d+)\./.exec(text);
  assert.ok(ref, `self-intro must carry the numbered delegation pointer:\n${text}`);
  const notes = DEFAULT_AGENT_DEFINITION.roles.find((r) => r.slug === "orchestrator")?.notes ?? "";
  const target = notes.split("\n").find((l) => l.startsWith(`${ref![1]}. `));
  assert.ok(target, `orchestrator notes have no point ${ref![1]}`);
  assert.match(
    target!,
    /Delegate by default/,
    `point ${ref![1]} is not the delegation rule — renumbering the notes broke the cross-reference`,
  );
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
  for (const harness of ["claude-code", "opencode", "droid", "codex", "qwen"] as const) {
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
  for (const harness of ["claude-code", "opencode", "droid", "codex", "qwen"] as const) {
    const text = buildProtocol(project, mcpPetboxTool, { harness });
    assert.ok(
      !text.includes("write their self-intro into the brief"),
      `${harness}: kit must not instruct dictating a subagent's self-intro:\n${text}`,
    );
  }
});

test("buildProtocol does not reference a nonexistent 'worker preamble'", () => {
  for (const harness of ["claude-code", "opencode", "droid", "codex", "qwen"] as const) {
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

// --- end-to-end: resolveDefinitionForSession (the FILE cascade base < user < project) feeding
// buildProtocol, the same wiring pull-memory.ts / droid-pull-memory.ts / opencode-plugin.ts use.
// No fake server anywhere in here, and that is the point: after card
// wire-stops-fetching-definition there is no server leg on this path to fake. ---

test("SessionStart banner renders the project layer's orchestrator prose over the kit base", () => {
  const home = freshHome();
  const root = freshHome();
  try {
    writeProjectLayer(root, "LAYER-AUTHORED-ORCHESTRATOR-NOTES-xyz789");
    const got = resolveDefinitionForSession({ root, homeDir: home });
    assert.equal(got.note, "", "a clean cascade must add no marker line");
    assert.equal(got.degraded, false);
    const text = buildProtocol("proj", mcpPetboxTool, {
      harness: "claude-code",
      definition: got.definition,
    });
    assert.match(text, /LAYER-AUTHORED-ORCHESTRATOR-NOTES-xyz789/);
    assert.match(text, /PetBox memory active/);
  } finally {
    rmSync(home, { recursive: true, force: true });
    rmSync(root, { recursive: true, force: true });
  }
});

test("SessionStart banner renders the kit base when no layer directory exists (absence is no opinion, never a crash and never empty)", () => {
  const home = freshHome(); // fresh dirs → guaranteed no user/project layer
  const root = freshHome();
  try {
    const got = resolveDefinitionForSession({ root, homeDir: home });
    assert.equal(got.note, "");
    assert.equal(got.degraded, false);
    assert.deepEqual(
      got.definition.roles.map((r) => r.slug),
      DEFAULT_AGENT_DEFINITION.roles.map((r) => r.slug),
    );

    const text = buildProtocol("proj", mcpPetboxTool, {
      harness: "claude-code",
      definition: got.definition,
    });
    assert.ok(text.length > 0, "banner must never be empty");
    assert.match(text, /PetBox memory active/);
    assert.match(text, /orchestrator/i);
  } finally {
    rmSync(home, { recursive: true, force: true });
    rmSync(root, { recursive: true, force: true });
  }
});
