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
        "1. **ROLE and MODEL are independent axes.** Role = what the agent may DO (spawn? edit files?). Model = thinking power. A worker on the strongest model is still a leaf that edits files; reserve on any model never edits files.\n" +
        "2. **Never pass a model at spawn.** Every role rides its roster binding — read them with `petbox-wire roles`, change them with `petbox-wire model set <role> <model>`. A different tier means a different ROLE or a deliberate roster edit, never a spawn argument. Exception: the harness's own built-in types (`general-purpose`, `Explore`, `Plan`) carry no pin and ride the parent session's model — the harness default, not a violation.\n" +
        "3. **Reserve when STUCK, not when the work is hard.** About to attack the same problem the same way a second time? Call reserve instead of taking a third swing. Signals: the bug won't reproduce; facts destroyed your hypothesis and the next guess comes from the SAME head; two defensible architectures with an expensive rollback.\n" +
        "4. **Never dictate a subagent's self-intro line.** It states the model it ACTUALLY runs as — your only evidence of what ran; dictating turns the signal into an echo.\n" +
        "5. **Never accept a verification you did not see.** \"The run is still going, I'll report when it finishes\" reports NOTHING — that process died with its turn. Re-run it yourself in the worker's worktree (`git status` there first — the work is usually intact but uncommitted). Same distrust for tools reporting remote state: confirm against the live system.\n" +
        "6. **Ceiling is Review.** The maintainer moves things to Done/accepted.",
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
        "6. **Run the verification in the FOREGROUND and block on its exit code.** The gate, the build, the test run — whatever proves your work — must finish INSIDE your turn. A run started in the background dies with your turn: ending with \"it is still running, I'll report when it completes\" means the result never arrives and the task is wasted. Your report MUST carry the actual exit status. If the run is slow, wait for it — waiting IS the job, not an interruption of it.\n" +
        "7. **Stuck? Say so.** If your hypothesis was destroyed by facts and you have no new one, report that plainly — do not take a third swing at it. Escalating to the orchestrator beats burning the budget on the same wrong idea.\n" +
        "8. **Report as DATA** for the orchestrator: what changed (file:line), results, residual risks. Not a human-facing essay.",
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
        "The second pair of eyes. Called when the orchestrator is STUCK — not merely when the work is hard (a hard task still goes to a worker on its roster binding; stuck is a different failure entirely).\n\n" +
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

/** Count top-level numbered rules in a role's notes (lines like "1. **...**"). A proxy for
 * "how many protocol rules does this role carry" without depending on prose wording. */
function countRules(notes: string | undefined): number {
  if (!notes) return 0;
  const matches = notes.match(/^\d+\.\s/gm);
  return matches ? matches.length : 0;
}

/**
 * By-SUBSTANCE diff between the built-in offline default (DEFAULT_AGENT_DEFINITION) and a live
 * server definition — used by `doctor` (bug: builtin-definition-drifts-no-catchup) to name what
 * changed in terms an operator can act on (which role, what disagrees) rather than dumping a raw
 * text/byte diff. Deliberately coarse: rule COUNT and exact notes-text equality, not a line-level
 * diff — good enough to say "the orchestrator has 7 rules vs 8 live" without becoming its own
 * maintenance burden every time prose is reworded without changing meaning.
 */
export function diffAgentDefinitions(
  builtin: AgentDefinition,
  live: AgentDefinition,
): ReadonlyArray<string> {
  const diffs: string[] = [];
  const builtinBySlug = new Map(builtin.roles.map((r) => [r.slug, r] as const));
  const liveBySlug = new Map(live.roles.map((r) => [r.slug, r] as const));

  for (const slug of liveBySlug.keys()) {
    if (!builtinBySlug.has(slug)) {
      diffs.push(`role '${slug}' exists in the live definition but not in the built-in default`);
    }
  }
  for (const slug of builtinBySlug.keys()) {
    if (!liveBySlug.has(slug)) {
      diffs.push(`role '${slug}' exists in the built-in default but not in the live definition`);
    }
  }

  for (const [slug, builtinRole] of builtinBySlug) {
    const liveRole = liveBySlug.get(slug);
    if (!liveRole) continue;
    if (builtinRole.tier !== liveRole.tier) {
      diffs.push(`role '${slug}': tier "${builtinRole.tier}" (built-in) vs "${liveRole.tier}" (live)`);
    }
    const builtinRuleCount = countRules(builtinRole.notes);
    const liveRuleCount = countRules(liveRole.notes);
    if (builtinRuleCount !== liveRuleCount) {
      diffs.push(
        `role '${slug}': built-in default has ${builtinRuleCount} rule(s), live definition has ${liveRuleCount}`,
      );
    } else if ((builtinRole.notes ?? "") !== (liveRole.notes ?? "")) {
      diffs.push(
        `role '${slug}': notes text differs from the live definition (same rule count: ${builtinRuleCount})`,
      );
    }
  }

  return diffs;
}
