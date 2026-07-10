---
name: petbox-agent-factory
description: On-demand compile of per-harness agent artifacts from portable PetBox definitions + local role→model bindings. Use after role/profile changes — not every session; never invent models; never put this procedure into canon.
---

# Agent factory (on-demand skill)

Factory is an **on-demand skill**, not session canon. Run it when definitions or local
bindings change; do **not** re-run every session and do **not** paste this procedure into
hooks, protocol, or memory canon.

## Axis

| What | Where | Notes |
| --- | --- | --- |
| Portable definition | PetBox (`agent_def_*` / REST agent-defs) | Roles, instructions, capabilities — **no models** |
| Local binding | `~/.petbox/roles.json` (owner = `$HOME`) | Profile → role → **model** only lives here |
| Compiled artifacts | Project harness dirs (e.g. `.opencode/agent/`) | Output of `apply` |

Never invent a model id. If a role has no binding, leave model unset and report it —
do not guess. Owner axis is `$HOME`; definitions are portable across machines, models are not.

## When to run

- After editing role→model bindings or switching `activeProfile`
- After a definition change you intend to materialize into harness files
- When doctor reports a truthfulness failure you just fixed

Skip on ordinary coding sessions that only need PetBox tasks/memory/session tools.

## Procedure

1. **Read local binding**
   ```bash
   npx petbox-wire roles
   ```
   Inspect `~/.petbox/roles.json` if you need the raw file. Empty store is OK — never invent defaults.

2. **Switch profile** (optional)
   ```bash
   npx petbox-wire profile use <name>
   ```
   Creates an empty profile shell if missing. Does **not** compile artifacts.

3. **Check truthfulness**
   ```bash
   npx petbox-wire doctor
   ```
   Offline gate: definition capabilities vs each known harness. Exit 1 on violations — fix those before apply.

4. **Compile artifacts**
   ```bash
   npx petbox-wire apply
   ```
   Offline. Uses the built-in / portable definition + local binding. Writes per-harness agent
   files (e.g. `.opencode/agent/<role>.md`). `model:` frontmatter only when bound.

## Do not confuse with `update`

| Command | Effect |
| --- | --- |
| `npx petbox-wire apply` | Compile per-harness agent artifacts from definition + binding |
| `npx petbox-wire update` | Refresh only the stable kit under `~/.petbox/wire/` (hooks/scripts/templates) |

`update` does **not** rebuild agent artifacts. After a kit-text change that includes this
skill template, re-run a **full wire** to reinstall skill files into the project.

## Boundaries

- Factory procedure stays in this skill — not in `protocol.ts`, SessionStart canon, or AGENTS.md.
- Portable defs ship without models; local `roles.json` is the only model source.
- Prefer reporting missing bindings over inventing them.
