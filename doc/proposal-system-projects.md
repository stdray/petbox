# Proposal: `$system` hosts internal PetBox services as projects

**Status:** proposed, not implemented. Author response to "Conf, Log, Data, Tasks стоит сделать отдельным проектами или сервисами в $system" (2026-05-27).

## Context

Today, **modules** (Conf, Log, Data, Dashboard, Tasks-to-be) live in code as feature-flagged subsystems registered in `Program.cs`. They have:
- A FeatureFlag (`Features:Config: true`)
- Service registrations + endpoint mappings
- Their own runtime config (e.g., `RetentionOptions`)

There is **no first-class entity** representing a module in the PetBox data model. Self-logging already writes to `$system` workspace, but the *project* layer underneath that is sparse — there's one `$system` project, no per-module breakout.

Result: the user can't see "what's the health of the Config module?" or "which logs did the Log ingestion subsystem produce?" via the normal IA. Modules are invisible to the navigation that's the heart of the product.

## Proposal

Inside the `$system` workspace, create one project per module:

```
$system workspace
├── $system project       (petbox shell / web — the existing one)
├── conf project
├── log project
├── data project
└── tasks project          (when the module ships)
```

Each module project has:
- One or more **services** (e.g., `conf/resolver`, `conf/migrator`, `log/ingest`, `log/retention-sweep`)
- Its own **config bindings** (auto-loaded by the module to override `appsettings.json`)
- Its own **logs** (the module writes here via the same internal API agents would use)
- Its own **health** (does the retention sweep run? is the ingest queue draining?)
- Its own **retention policy** (the Log module's own logs probably want short retention)

`$system` workspace stays undeletable. Its projects are also undeletable (created automatically by a migration). User-created projects in `$system` stay blocked.

## What this buys

1. **Observability via the normal IA.** "Is the resolver healthy?" → `/ui/$system/conf` → look at Logs/Status. No special UI needed.
2. **Per-module config decoupled from `appsettings.json`.** Today `RetentionOptions` is read once at startup. With per-module config bindings, runtime changes become possible — same model as user pets.
3. **Self-documenting boundaries.** The list of modules is the list of projects in `$system`. No `Features` config + `MapXxxEndpoints` archaeology.
4. **Agent ergonomics.** `/agent/` endpoints — see [[project-url-conventions]] — can offer agent keys scoped to a module (e.g., "give me a key for `$system/log` to ingest").
5. **Disaster diagnosis.** When petbox itself is misbehaving, its logs land in `$system/{module}` — a known place — not a mystery service.

## What this does NOT change

- **Modules stay code-level subsystems.** Feature flags still gate registration. This proposal is about *representation*, not *decomposition*.
- **Architectural boundaries.** No new microservices, no new processes.
- **User pets.** Regular workspaces and projects work exactly as before.

## Implementation sketch

### A. Migration

```csharp
// M00N_SystemProjects.cs
// Creates: $system/conf, $system/log, $system/data, $system/dashboard projects.
// Each with one initial service named after its primary subsystem.
```

### B. Module → project wiring

Each module's host (e.g., `MapConfigEndpoints`) registers itself against its `$system/{module}` project:
- Logs use `ServiceKey = "$system/conf/resolver"` (or similar) when self-logging
- Config bindings under tag `project:$system-conf` (note `$system-conf` not `conf` to avoid collisions with user projects named `conf` — but `conf` is in the reserved-name list anyway)
- Health pollers register with the corresponding service

### C. UI guards

- `$system` workspace project list shows the module projects, marked with a badge (`sys`, `internal`, similar).
- User-created projects in `$system` already blocked (see the `Cannot create projects in $system` form lock).
- Deleting module projects: blocked similarly.

### D. Reserved project keys

The reserved set already includes `config`, `data`, `tasks`, `admin`, etc. For module projects inside `$system`, we use the bare name (`conf`, `log`, `data`, `tasks`) since the workspace context disambiguates URLs.

## Risks / open questions

- **Bootstrapping order.** Modules register before migrations run. If a module wants to self-log into `$system/log` and the `$system/log` project doesn't exist yet, the first few log lines either go nowhere or to a sentinel service. Plan: keep a `$system/petbox-web` catch-all service for early logs.
- **Reserved-name collision.** `conf` and `log` are short and might collide in URL paths (e.g. `/ui/$system/conf` already overlaps with `/ui/{ws}/config` if someone types `config` literally). The `/ui/{ws}/{key}/config` pattern is unambiguous because the workspace key comes first, so `$system` workspace's `conf` project sits at `/ui/$system/conf` cleanly. Verify after implementation.
- **Health source for modules.** "Is the resolver healthy?" requires a heartbeat or last-success timestamp. Each module needs to write one. That's part of the same Dashboard module-maturity work blocked elsewhere — health collection.
- **Mismatch with existing $system project.** Today `$system` workspace has one `$system` project. Migration needs to decide: does the existing `$system` project stay as "petbox shell", and new module projects are siblings? Probably yes.

## Recommendation

Worth doing, **after the Tasks module ships**. Reason: Tasks is the next-priority module and its design will surface most of the patterns module-projects need (per-module config, per-module logs, per-module agent endpoints). Building module-projects with one real consumer (Tasks) avoids over-engineering for hypothetical ones.

Until then: leave `$system` as-is (single project + scattered services), but tighten the "user can't create projects in `$system`" rule (already done in this iteration).
