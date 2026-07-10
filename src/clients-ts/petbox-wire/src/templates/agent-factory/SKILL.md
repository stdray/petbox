---
name: petbox-agent-factory
description: On-demand compile of per-harness agent artifacts from portable PetBox definitions + local role→model bindings. Use after role/profile changes — not every session; never invent models; never put this procedure into canon.
---

# Agent factory (on-demand skill)

Factory is an **on-demand skill**, not session canon. Run it when definitions or local
bindings change; do **not** re-run every session and do **not** paste this procedure into
hooks, protocol, or memory canon.

## Axis

| What | Where |
| --- | --- |
| Portable definition | PetBox (`agent_def_*` / REST agent-defs) — roles/capabilities, **no models** |
| Local binding | `~/.petbox/roles.json` (owner = `$HOME`) — profile → role → **model** only |
| Compiled artifacts | Per-harness agent files written by `apply` |

Never invent a model id. If a role has no binding, leave model unset and report it.
Owner axis is `$HOME`; definitions are portable across machines, models are not.

## Procedure

1. Inspect / switch local binding as needed:
   ```bash
   npx petbox-wire roles
   npx petbox-wire profile use <name>
   ```
2. Gate:
   ```bash
   npx petbox-wire doctor
   ```
   Doctor reports truthfulness violations (role + capability + harness). Exit 1 on failure — fix before apply.
3. Materialize:
   ```bash
   npx petbox-wire apply
   ```
   Apply writes per-harness agent files from the definition + local binding; `model:` only when bound. Any harness violation blocks all writes.

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
- Not canon; on-demand only.
