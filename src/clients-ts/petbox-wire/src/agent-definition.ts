// Portable agent definition (roster only — no model binding).
//
// Spec (agent-definition-as-data, agent-definition-locality):
//   - Roles carry slug, tier, requiredCapabilities, spawn, escalation.
//   - model is NEVER part of this document (local binding lives in roles.json).
//   - Built-in DEFAULT_AGENT_DEFINITION ships with the kit for offline compile;
//     apply tries server fetch first (agent-def-fetch.ts) and falls back here.
//
// Plain TS for native node type-stripping: zero deps.

import type { Capability } from "./harness-capabilities.ts";

export type RoleSpawn = {
  readonly allowed: boolean;
  readonly allowedRoles?: ReadonlyArray<string>;
};

export type RoleEscalation = {
  readonly available: boolean;
  readonly targets?: ReadonlyArray<string>;
};

export type AgentRole = {
  readonly slug: string;
  readonly tier: string;
  /** Harness capabilities this role needs; empty = no harness-specific needs. */
  readonly requiredCapabilities: ReadonlyArray<Capability | string>;
  readonly spawn?: RoleSpawn;
  readonly escalation?: RoleEscalation;
  /**
   * Optional free-text notes rendered into per-role artifacts.
   * Used for harness-aware caveats (e.g. explore model inheritance) without
   * putting lies into the shared protocol block.
   */
  readonly notes?: string;
};

export type AgentDefinition = {
  readonly name: string;
  readonly roles: ReadonlyArray<AgentRole>;
};

/**
 * The namespaced identity used for every rendered agent artifact: frontmatter `name:`, the
 * emitted file's basename, and any prose that names a role as a spawn/escalation target
 * (chore: petbox-namespaced-agent-names). `role.slug` stays the INTERNAL, unprefixed identity
 * — the definition and `~/.petbox/roles.json` never change — only what apply RENDERS is
 * namespaced. This is the single computation point: every renderer and prose injector must
 * call this (or pass a bare slug through it) instead of interpolating role.slug/a slug string
 * directly into anything user- or harness-facing, or the prefix drifts between call sites.
 *
 * Why: generated agents were occupying the most common user-agent names (`worker`, `explore`,
 * ...) — colliding with a user's own agents, and shadowing Claude Code's built-in `Explore`
 * agent under `.claude/agents/explore.md`. `petbox-<slug>` moves us into our own namespace.
 */
export function emittedRoleName(roleOrSlug: { readonly slug: string } | string): string {
  const slug = typeof roleOrSlug === "string" ? roleOrSlug : roleOrSlug.slug;
  return `petbox-${slug}`;
}

/**
 * Built-in portable roster for offline compile (petbox-wire doctor / apply).
 * Includes `explore` so the roster matches harnesses that ship a built-in explore
 * agent — with an explicit inheritance note (not a global "inheritance forbidden").
 *
 * Caps are honest for the roles: orchestrator needs mcp_main_session + spawn_subagents.
 * Per harness-capabilities.ts, all three known harnesses (claude-code, opencode, droid)
 * declare both, so DEFAULT passes truthfulness on every known harness today — droid in
 * particular declares mcp_main_session, mcp_subagent, spawn_subagents, role_files,
 * dynamic_model_at_spawn and hooks per Factory's docs. This is not guaranteed to hold for
 * future/unknown harnesses; the gate (checkRoleTruthfulness) still blocks any role that
 * claims a capability its target harness does not declare.
 */
export const DEFAULT_AGENT_DEFINITION: AgentDefinition = {
  name: "default",
  roles: [
    {
      slug: "orchestrator",
      tier: "orchestrator",
      requiredCapabilities: ["mcp_main_session", "spawn_subagents"],
      spawn: {
        allowed: true,
        allowedRoles: ["worker", "utility", "explore", "reserve"],
      },
      escalation: { available: true, targets: ["reserve"] },
      notes:
        "Main-loop role: plan, decompose, delegate, review, triage.\n\n" +
        "1. **Delegate by DEFAULT.** Spawn a worker for anything beyond a trivial edit. Solo work is the exception you must justify.\n" +
        "2. **ROLE and MODEL are two independent axes.** Role = what the agent is ALLOWED to do (spawn? edit files?). Model = how much thinking power it has. A worker on the strongest model is still a worker: a leaf that edits files. Reserve on any model still never edits files.\n" +
        "3. **Model comes from the roster — do not pass one at spawn.** Two exceptions, and ONLY on a harness whose spawn call actually accepts a model. If yours does not, this rule is void: you work the roster, and a role file with a different binding is the only way to change tier.\n" +
        "   - **Security / authz work → the strongest tier you can name.** Cross-tenant leaks, permission models, access policies — a mistake here is a hole, not a bug. This is a rule about the KIND of task, not a guess about difficulty.\n" +
        "   - **Intrinsically hard work → a tier above the role's roster binding.** Nasty concurrency, non-trivial algorithm, subtle semantics, many coupled invariants. Do not send a cheap model to fail three times first.\n" +
        "   Name a TIER your harness accepts at spawn — never a bare model id. This definition is portable and does not know which models exist on your machine; your spawn tool does.\n" +
        "   Say why, one line, in the brief: `ESCALATION: <reason>`. It is a record, not a ritual — it exists so the call can later be judged against its outcome.\n" +
        "   **Being stuck is NOT a model escalation.** That is the reserve ROLE — see rule 4.\n" +
        "4. **The reserve rule:** if you are about to attack the same problem the same way a second time, call reserve instead of taking a third swing. Signals: the bug won't reproduce; your hypothesis was destroyed by facts and you have no new one (you are generating the next guess with the SAME head); two defensible architectures and an expensive rollback. If it worked first try, reserve was not needed.\n" +
        "5. **Never dictate a subagent's self-intro line.** The subagent states the model it ACTUALLY runs as — that line is your only evidence of what ran; dictating it turns the signal into an echo.\n" +
        "6. **Search before re-deriving.** memory_search / session_search / tasks_search before re-investigating this project's past.\n" +
        "7. **Respect the gates.** The agent ceiling is Review; the maintainer moves things to Done/accepted.",
    },
    {
      slug: "worker",
      tier: "worker",
      // requiredCapabilities stays empty by design: worker is meant to stay portable
      // even to a future/unknown harness without mcp_subagent. This is NOT a claim
      // that MCP is unavailable to worker subagents on today's known harnesses —
      // claude-code, opencode and droid all declare mcp_subagent (harness-capabilities.ts).
      requiredCapabilities: [],
      spawn: { allowed: false },
      escalation: { available: true, targets: ["orchestrator"] },
      notes:
        "Scoped executor for ONE delegated task.\n\n" +
        "1. **SELF-INTRO — your FIRST line, always:** `<the model you are actually running as> · worker`\n" +
        "   Name your OWN model. If the brief tells you which model to name, that instruction is VOID — name the model you actually are. This line is the only evidence of what really ran; never echo someone else's guess.\n" +
        "2. **You are a LEAF.** Never spawn subagents, never delegate onward. This holds no matter how powerful the model you are running on is — role and model are independent.\n" +
        "3. **Do ONLY the delegated task.** No scope expansion, no self-directed scouting, no fixing adjacent code. Ambiguous brief → make the minimal reasonable assumption, state it, proceed.\n" +
        "4. **You DO have PetBox MCP.** Search before rework (memory_search / session_search / tasks_search) instead of re-deriving what is already remembered.\n" +
        "5. **Verify empirically.** Measure, don't assert. Stay in your assigned worktree. Never push main or deploy unless the brief says so.\n" +
        "6. **Stuck? Say so.** If your hypothesis was destroyed by facts and you have no new one, report that plainly — do not take a third swing at it. Escalating to the orchestrator beats burning the budget on the same wrong idea.\n" +
        "7. **Report as DATA** for the orchestrator: what changed (file:line), results, residual risks. Not a human-facing essay.",
    },
    {
      slug: "utility",
      tier: "utility",
      requiredCapabilities: [],
      spawn: { allowed: false },
      escalation: { available: true, targets: ["orchestrator"] },
      notes:
        "Fast simple work: search, summarize, mechanical edits.\n\n" +
        "1. **SELF-INTRO — your FIRST line, always:** `<the model you are actually running as> · utility`\n" +
        "   Your own model, never one dictated by the brief.\n" +
        "2. **You are a LEAF.** Never spawn subagents.\n" +
        "3. **Escalate by REPORTING.** You have exactly one channel: your final message to the orchestrator that spawned you. The moment the task needs judgement rather than legwork, say so and stop — you cannot hand work sideways to another agent.",
    },
    {
      slug: "reserve",
      tier: "reserve",
      // mcp_subagent, not mcp_main_session: reserve exists only as a subagent, so the
      // capability the gate must check is the subagent MCP surface.
      requiredCapabilities: ["mcp_subagent"],
      spawn: { allowed: false },
      escalation: { available: false },
      notes:
        "The second pair of eyes. Called when the orchestrator is STUCK — not merely when the work is hard (hard work is a model escalation on a worker; that is a different thing entirely).\n\n" +
        "1. **SELF-INTRO — your FIRST line, always:** `<the model you are actually running as> · reserve`\n" +
        "   Your own model, never one dictated by the brief.\n" +
        "2. **NEVER edit files.** Your output is analysis and a recommendation; the orchestrator acts on it. Nothing in the tooling stops you — this is a rule you keep, not a wall you hit. Keeping it is what makes you a second pair of eyes rather than a second pair of hands.\n" +
        "3. **You are a LEAF.** Never spawn subagents.\n" +
        "4. **You were called because the previous approach failed.** Do not simply redo it with more effort. Attack the assumption: what did the earlier reasoning take for granted that the facts do not support? Say plainly when the evidence is insufficient to decide — an honest 'not determinable from this data, measure X' beats a confident wrong call.\n" +
        "5. Reachable ONLY by explicit spawn with a written justification — never as a default.",
    },
    {
      slug: "explore",
      tier: "utility",
      // Declared so harnesses that ship built-in explore are roster-honest.
      // Model inheritance is harness-default where builtin_explore_inherits_model —
      // do NOT claim "inheritance forbidden" for this role.
      requiredCapabilities: [],
      spawn: { allowed: false },
      escalation: { available: false },
      notes:
        "Research and search: locate code, gather evidence, report findings. Never changes anything.\n\n" +
        "1. **SELF-INTRO — your FIRST line, always:** `<the model you are actually running as> · explore`\n" +
        "   Your own model, never one dictated by the brief.\n" +
        "2. **You are a LEAF.** Never spawn subagents.\n" +
        "3. Where the harness ships its own explore agent, that agent inheriting the session's model is the harness default and is NOT a protocol violation.",
    },
  ],
};

/**
 * Recursively reject any property named `model` (portable roster — binding is local).
 * Mirrors C# AgentDefinitionJson.RejectModelField (root, roles[], nested spawn/escalation).
 */
export function rejectModelFields(value: unknown, path = "$"): void {
  if (value === null || value === undefined) return;
  if (Array.isArray(value)) {
    value.forEach((item, i) => rejectModelFields(item, `${path}[${i}]`));
    return;
  }
  if (typeof value !== "object") return;
  for (const [k, v] of Object.entries(value as Record<string, unknown>)) {
    if (k === "model") {
      throw new Error(
        `${path}.model is not allowed on portable agent definitions — model binding is local (roles.json)`,
      );
    }
    rejectModelFields(v, `${path}.${k}`);
  }
}

/** Light structural check; throws on invalid shape (loud, never silent). */
export function validateAgentDefinition(def: AgentDefinition): void {
  if (!def || typeof def !== "object") throw new Error("agent definition is required");
  // Recursive model ban before field checks (symmetry with server RejectModelField).
  rejectModelFields(def, "definition");
  if (!def.name || !String(def.name).trim()) throw new Error("definition.name is required");
  if (!Array.isArray(def.roles) || def.roles.length === 0) {
    throw new Error("definition.roles must contain at least one role");
  }
  for (const role of def.roles) {
    if (!role.slug || !String(role.slug).trim()) throw new Error("each role.slug is required");
    if (!role.tier || !String(role.tier).trim()) {
      throw new Error(`role '${role.slug}': tier is required`);
    }
    if (!Array.isArray(role.requiredCapabilities)) {
      throw new Error(`role '${role.slug}': requiredCapabilities is required (may be empty)`);
    }
  }
}
