# AGENTS.md

Guidance for coding agents working in this repository.
Invariants: see [doc/invariants.md](doc/invariants.md). Status tracked in `my/prj/petbox/invariant-status.md`.

## Status: Phase 0 — Scaffold

Empty repository. Template files copied from `wiki/cross-project/templates/dotnet/`. No code yet.

## Build entry points

- **Local:** `./build.sh --target=Test` (bash) or `pwsh ./build.ps1 -Target Test` (PowerShell). Bootstrap restores Cake + GitVersion tools, then runs the Cake task.
- **Cake tasks:** Clean → Restore → Version (GitVersion) → Build → Test → Docker → DockerSmoke → DockerPush.
- **Dev loop:** `bun run dev` (ts + css watchers via concurrently) and `dotnet watch` in parallel.
- **CI:** mirrors `./build.sh --target=CI` (format verify + test). On `main` push + `deploy` tag push also runs `--target=DockerPush` with ghcr.io credentials.
- **Deploy:** manual tag `deploy` only. `git tag deploy && git push origin deploy --force`.

## MCP access during development

`.mcp.json` at repo root registers the running petbox dev server as an MCP
target named `petbox`. Tools exposed: `data.*` (7 tools) + `log.query`.
Useful during dev for inspecting logs without leaving the editor:
`log.query` with KQL like `events | where Level >= 4` shows what errored.

Setup (one-time, per machine):
1. Set env var `PETBOX_DEV_APIKEY` to a `$system`-scoped ApiKey with
   `logs:query,data:read,data:write,data:schema` scopes. Create one at
   `/ui/admin/ws/$system/projects/$system/info`.
2. Start the dev server (`dotnet watch` in `src/PetBox.Web/`).
3. Claude Code (or any MCP-aware agent) picks up `.mcp.json` automatically.

Inspect via `mcp__petbox__log_query` calls; the key never lands in the
config file because `.mcp.json` references `${PETBOX_DEV_APIKEY}`.

## Documents — what goes where

- **`doc/spec.md`** — pointer to vault spec at `my/idea/petbox/spec.md`.
- **`doc/plan.md`** — phases with checkboxes and progress.
- **`doc/decision-log.md`** — architectural decisions (newest on top).
- **`doc/invariants.md`** — copy of `wiki/cross-project/invariants.md`.
- **`doc/ui-conventions.md`** — canonical UI/component recipes (Tailwind/daisyUI choices, htmx/Alpine boundary, modals, tables, etc.). Consult before building new UI.
- **`doc/settings-taxonomy.md`** — where every setting lives (entity catalog, L1/L2/L3 storage rules, C# records-with-attributes catalog model, admin area structure, extensibility scenarios). Read before adding a new configurable parameter.
- **`doc/tasks-mcp/`** — bench of real plan/memory operations by coding agents. Used as design input for the future Tasks module. See `doc/tasks-mcp/README.md`.

## Recording plan/memory actions

When an agent (claude-code, factory droid, opencode, oh-my-pi, …) creates, edits, or deletes a plan or memory file while working on this repo, it MUST also append a record under `doc/tasks-mcp/` using the file naming and frontmatter format described in `doc/tasks-mcp/README.md`. Triggers:

- Edits to `doc/plan.md` (project plan).
- Edits to session plans in `~/.claude/plans/*.md` (or each agent's equivalent location).
- Edits to memory files in `~/.claude/projects/*/memory/*.md` (or each agent's equivalent location).

One operation per turn → one record file. The point is to accumulate real examples that will inform the design of the PetBox Tasks module.

## Tasks / Memory / Session — what goes where

When recording state via the MCP tools, pick by lifetime:

- **Session** (`session.*`) — the *current* working plan/thinking. Ephemeral, last-write-wins. "Stale next week?" → session.
- **Tasks** (`tasks.*`) — a *unit of work with a status* tracked to Done. "Has a status that changes (Pending→Done)?" → task.
- **Memory** (`memory.*`) — a *durable fact* that should outlive the work. "Will a future agent need this to avoid re-learning it?" → memory.

Memory entries are typed (`user` | `feedback` | `project` | `reference`) — `type` is required on `memory.upsert`. Store durable facts not derivable from code/git/config; do **not** store what the repo/git already records, transient state, secrets, or actionable work (that's a task). `feedback`/`project` entries should include the *why* and *how to apply*. Search before writing, update over duplicating, delete when wrong (temporal history makes deletes safe). A cold `tasks.upsert` / `memory.upsert` auto-creates the board/store.

## Live plan board (petbox `$system`/roadmap)

The **live plan lives in petbox**, not in `doc/plan.md` — the tasks board
`$system`/roadmap (via the `tasks.*` MCP tools) is the source of truth for active
and upcoming work. `doc/plan.md` is kept as the historical/decision record (phases
0–29) and is not the working plan anymore.

Board phases (Phase > Wave > Task):

- **`incoming`** — raw intake. New requests land here. The agent periodically
  re-reads and triages: simple items it implements directly; complex items or ones
  needing discussion get moved into a real phase.
- **`parking`** ("Непонятные") — tasks the user doesn't understand from the
  description or doesn't yet know what to do with. **When the user says "отправь X в
  непонятные" (send X to parking), move/create that task under `parking` with a
  Russian description.** The user reviews these in the UI, thinks, and returns them
  to a real phase when ready.
- **`polish`** — UX/polish doced items (incl. pending Phase 25 items migrated from
  `doc/plan.md`).
- Real work phases (e.g. `phase30`) — tracked features.

Keep board node descriptions in Russian when the user will triage them in the UI
(parking especially). Record durable findings/decisions into `$system`/dogfooding
memory seamlessly (no "should I record this?" prompts) — see the memory rules above.

## Target stack

- .NET 10 monolith, Razor Pages SSR + htmx + Alpine.js
- Tailwind CSS 3.4 + daisyUI 4, TypeScript via bun, tsc for bundling
- SQLite via linq2db 6.3.0 for all data (Core, Logs, Data)
- FluentMigrator for Core DB migrations
- Seq.Extensions.Logging for structured logging ingestion
- Kusto.Language + kusto-loco for KQL query engine
- OpenTelemetry (AspNetCore instrumentation) for tracing
- Docker: chiseled noble, `/app/data` volume, port 8080
- Cake + GitVersion for build/versioning
- GitHub Actions CI/CD with SSH deploy
- E2E: Playwright with Lightpanda browser

## Module architecture

PetBox is a module monolith. Each subsystem is feature-toggled via `appsettings.json`:

```
Features: { Config: true, Logging: true, Data: true, Dashboard: true }
```

Projects:
- `PetBox.Core` — Auth, Project/Service/ApiKey models, SQLite context, localization, feature toggle infrastructure
- `PetBox.Web` — single entry point, Program.cs, Razor Pages, frontend assets
- `PetBox.Config` — tag-based config engine, resolve pipeline
- `PetBox.Log.Core` — KQL engine, Seq ingestion
- `PetBox.Data` — PostgREST HTTP API, linq2db.Remote.Grpc
- `PetBox.Dashboard` — HealthPoller, CiPoller, /api/heartbeat, dashboard UI

## Entity model

Core entities:
- **Project** — top-level grouping (Key, Name, Description)
- **Service** — running process within a project (Key, ProjectKey, Kind, Url, Version, ShortSha, Health)
- **ApiKey** — auth credential (Key, ProjectKey, Scopes, CreatedAt)
- **ConfigBinding** — tag-based key-value (Path, Value, Tags) — not FK'd to Service
- **LogEntry** — immutable log record (Id, ServiceKey, Level, Message, Properties, Timestamp)
- **DataTable** — yobadata table metadata (Name, ProjectKey, Columns, Read/Write/Delete flags)

Project/Service are organizational layers for UI. ConfigBinding uses tags (`project:kpvotes, service:kpvotes-bot`) — resolve pipeline is pure tag-based matching.

## Hard invariants

- **Feature toggle gating.** Every subsystem checks `Features:<Name>` before registering endpoints/middleware/BackgroundServices. Disabled subsystem = zero runtime cost.
- **Auth: local and remote modes.** Default local (validate against own DB). Remote mode (`Auth:Mode: remote, RemoteUrl: ...`) delegates validation to central instance. Log-only instances don't store users or keys.
- **No PetBox self-config via ConfigModule.** PetBox configures itself from `appsettings.json` only. ConfigModule serves external consumers.
- **`$system` project is undeletable.** Auto-created on first start. PetBox logs itself into `$system` services.
- **Config resolve is deterministic.** Same tags → same binding. Tag-based matching: most specific tag set wins.
- **ApiKey scopes are enumerable, not wildcards.** `config:read`, `logs:ingest`, `data:write`, `dashboard:read`, `admin`.
- **LogEntry is append-only, immutable.** No update or delete. Retention policy TBD.
- **Localization from day one.** All user-facing strings through `IStringLocalizer`. No Cyrillic/Hebrew/Arabic in UI strings — English-only chrome text.
- **`data-testid` for UI selectors.** No text-matching, no CSS class selectors for E2E tests.
- **No HTML in `.cs` files.** All markup in Razor (`.cshtml`).
- **No inline JS in Razor.** All client logic in `ts/` files, bundled by bun.
- **Frontend build is Release-only.** Debug uses `bun run dev` watcher.

## Coding style

- **Immutability and functional approach.** C#: `record`/`readonly record struct`, `init`-only, `IReadOnlyList<T>`, `switch` expressions. TS: `const`, `readonly`, `ReadonlyArray<T>`.
- **Arrow/expression-bodied when it fits.** Expression-bodied members, `switch` expressions. Arrow functions for TS callbacks.
- **Omit implicit access modifiers.** Don't write `internal`, `private`, `public` on interface members.
- **Maximum static typing.** No `object`, no `dynamic`, no `any`. Generics and discriminated unions.
- **Formatting:** tool defaults — `dotnet format` for C#, biome for TS. `.editorconfig` at repo root.

## Commit convention

Conventional Commits. `type(scope): description`, ≤72 chars, imperative mood.
Types: `feat`, `fix`, `refactor`, `test`, `style`, `docs`, `chore`, `build`.
Scopes: `core`, `web`, `config`, `log`, `data`, `dashboard`, `auth`, `docs`, `deps`, `scaffold`.
