# AGENTS.md

Guidance for coding agents working in this repository.

## What PetBox is

PetBox is a **module monolith** (.NET 10, Razor Pages SSR + htmx + Alpine) that
provides shared infrastructure for pet projects: one service with feature-toggled
subsystems. It runs live at `petbox.3po.su`. Modules:

- **Config** — tag-based configuration engine + resolve pipeline (`PetBox.Config`).
- **Log** — log ingestion (Seq-compatible + OTLP) and a KQL query engine
  (Kusto.Language + KustoLoco) (`PetBox.Log.Core`).
- **Data** — per-project SQLite databases with a raw-SQL pass-through REST API and
  DbUp-style schema migration (`PetBox.Data`).
- **Tasks** — task boards with a user-defined methodology engine
  (idea → spec → work → intake) (`PetBox.Tasks`).
- **Memory** — durable, temporal, searchable agent memory stores (`PetBox.Memory`).
- **Sessions** — working-session transcripts + digests + episodic search
  (`PetBox.Sessions`; gated under the `Tasks` feature).
- **LLM router** — a routed chat / embed / rerank facade over LLM providers
  (`PetBox.LlmRouter`, `PetBox.LlmRouter.Contract`).
- **Dashboard** — health polling, CI polling, dashboard UI (`PetBox.Dashboard`).
- **Deploy** — deployment tracking (`PetBox.Deploy`).
- **MCP surface** — an MCP tool surface over all of the above, in `PetBox.Web/Mcp`.

`PetBox.Core` holds auth, the entity models, the SQLite context + migrations, search,
and the feature-toggle infrastructure. `PetBox.Web` is the single entry point (Program.cs,
Razor Pages, frontend assets, MCP endpoint). Client SDKs live under `src/clients-net`,
`src/clients-ts`, `src/clients-py`.

## Where the living truth lives

**The live plan, status, and specification are NOT in this repo.** They live in the
PetBox `$system` project's methodology boards, reached over the `petbox` MCP server
(see "MCP access" below):

- **ideas** — deliberations (raw → exploring → review → accepted; or rejected).
- **spec** — the defined requirements tree.
- **work** — technical tasks with a status lifecycle.
- **intake** — the inbox (raw requests → triage).

Canon for how these fit together: **[doc/methodology.md](doc/methodology.md)** (+
[doc/methodology-engine.md](doc/methodology-engine.md) for the engine). The markdown
files under `doc/` (`plan.md`, `spec.md`, `decision-log.md`, …) are **historical
records**, not the working plan — do not treat them as current state.

## Process contract (binding for ALL agents)

1. **No code before a card:** every change starts from a work item on `$system`
   (intake issue → promoted task, or accepted idea → spec → work). Create/move the
   card BEFORE editing code.
2. **Worktree before edits:** never edit the primary checkout; create a git worktree
   for the change. Put every agent's worktree under `.claude/worktrees/<branch-slug>` —
   one dir for CC/opencode/droid alike: it is INSIDE the repo (so opencode, which can only
   work within the project folder, is happy) yet gitignored (so it never dirties
   `git status`), and it is where Claude Code's own worktree tool already lands. Take a
   fresh base without touching the primary: `git fetch origin` + `git worktree add <abs-dir>
   -b <branch> origin/main` — NOT `git pull` in the primary checkout (it is shared state,
   often parked on another session's feature branch with uncommitted edits). Pass an
   ABSOLUTE path to `git worktree add` (a relative path resolves against the cwd).
3. **Tests:** run via Cake (`./build.ps1 -Target Test`, or `./build.sh --target=Test`)
   or a filtered `dotnet test`; pipe output to `.tmp/test-run.log` and read the log
   instead of scrolling the console. For readable FAILURE detail (a quiet log drops the
   assertion messages), add `--logger "trx;LogFileName=res.trx"` and render it with
   **`dotnet trx`** (the `dotnet-trx` local tool, in `.config/dotnet-tools.json`; run
   `dotnet tool restore` once). It auto-discovers `*.trx` under the cwd and prints each
   failed test's message + stack (`--path <dir>` to scope, `-v verbose` for all).
   For TIMING analysis (per-class totals, wall-clock critical path of a parallel run)
   use `dotnet run scripts/trx-timings.cs -- <run.trx>` — don't re-write that parser.
4. **Finish = branch + commit + push:** completed work is committed on a feature
   branch and pushed BEFORE the card moves to `Review` (add commits to the card via `commits[]`);
   never leave finished edits uncommitted in a working tree. This repo's process
   contract IS the standing request to commit — it overrides any agent-default
   "don't commit unless asked".
5. **Agent ceiling is Review:** mark finished work `Review`, never `Done`; the
   maintainer confirms `Done`.
6. **Deploy only on explicit command** (the `deploy` tag flow); after deploy, run a
   live smoke against production endpoints.
7. **Don't silently work around process/doc defects** — file an intake card on
   `$system`. Same rule when sources disagree: the order of truth is
   **maintainer > spec > tests > code**. If you think a spec node is wrong, implement it
   anyway and raise the objection (intake card, or a comment on the node) — never
   silently override the higher source. Unclear which reading is right → take the one
   that is cheaper to undo, and say so.
8. **Delegating to workers (fan-out):** a spawned subagent does NOT run the
   SessionStart hook — it never sees the memory banner, canon, or role rules, so the
   ONLY channel is your spawn brief. Workers that don't get the rules drift: they
   self-scout, broaden scope, and even spawn their own subagents. So EVERY worker
   brief MUST OPEN with the worker preamble (strengthen per task, never weaken):
   > You are a WORKER subagent, not an orchestrator — one scoped task was delegated to
   > you and the orchestrator integrates your result. Rules (non-negotiable): (1) FIRST
   > line exactly `<your model> · worker`, then one sentence of these rules. (2) Do ONLY
   > this brief — no scope expansion, no codebase "exploration"/scouting beyond what the
   > task needs, no fixing adjacent things; ambiguity → minimal assumption + note it,
   > don't go investigate. (3) You are a LEAF — do NOT spawn subagents. (4) Search before
   > rework, verify empirically, stay in your worktree, never push main / global-install
   > / deploy unless told. (5) Report concisely (changes, results, risks) — data for the
   > orchestrator, not an essay; when in doubt do LESS and report.

   For the durable "at start" channel, spawn workers as the `worker` agent type
   (`~/.claude/agents/worker.md`) whose system prompt carries this and which has no
   Agent tool (can't self-spawn) — the brief preamble is then the backstop, not the
   only line of defense.
9. **What NOT to delegate.** Fan-out is the default, but the default is not universal:
   **delegate when verifying the result is cheaper than producing it** —
   `cost(specify) + cost(verify) < cost(do it yourself)`. Judge the task on four flags
   (is a mistake reversible; is the context transferable into a brief; is the result
   mechanically checkable or a matter of judgement; how big it is) and take the FIRST
   rule that fires:
   1. Irreversible (deploy, force-push, anything touching prod or secrets) → do it yourself.
   2. Context you cannot compile into a brief → do it yourself; a worker starved of it
      will scout, drift, and hand back plausible garbage.
   3. Judgement call on a small/medium task → do it yourself (specifying the taste costs
      more than doing the work). A LARGE judgement task may still be delegated as a
      DRAFT, if you will review it.
   4. Otherwise → delegate.

   Never delegate: secrets, irreversible operations, architecture / spec / plan
   decisions, the review of a worker's own output, and edits so small the brief is
   longer than the diff. Retries are bounded: a worker that fails gets ONE stronger
   worker, then you take the task yourself — never a third attempt.

## Build entry points

- **Local:** `./build.ps1 -Target Test` (PowerShell) or `./build.sh --target=Test`
  (bash). The bootstrap restores the Cake script + GitVersion tool, then runs the
  requested Cake target. The Cake build is `build.cs` (a `dotnet run` C# script).
- **Cake targets (build/deploy chain):** `Clean → Restore → Version (GitVersion) →
  Build → Test → Docker → DockerSmoke → DockerPush`. `Test` also installs the
  Playwright chromium binary for the E2E suite. `Default` = `DockerPush`.
- **Verify / CI target:** `Verify` (and its alias `CI`) runs `FormatVerify`
  (`dotnet format --verify-no-changes`) + `Test` + the TS and Python SDK
  lint/typecheck/test targets.
- **Client publish targets:** `NuGetPush` (.NET → nuget.org), `NpmPublish`
  (TS → npmjs), `PyPiPublish` (Python → PyPI) — each fired by its own tag.
- **Dev loop:** `./build.ps1 -Target Dev` runs `bun run dev` (ts + css watchers) and
  `dotnet watch run` side by side from `src/PetBox.Web`.
- **CI:** `.github/workflows/ci.yml` on push to `main` runs the `Test` target; a
  parallel job builds and pushes the Docker image to ghcr.io. Tags trigger extra
  jobs: `deploy` (SSH deploy + post-deploy health smoke), `nuget`/`npm`/`pypi`
  (client publish).
- **Deploy:** manual `deploy` tag only — move the tag to the target commit and push
  it (`git tag -f deploy <sha> && git push origin deploy --force`). The deploy job
  SSHes to prod, pulls the new image, and health-checks the fresh container.

## MCP access during development

`.mcp.json` at the repo root registers the **production** PetBox MCP server:

```json
"petbox": { "type": "http", "url": "https://petbox.3po.su/mcp",
            "headers": { "X-Api-Key": "${PETBOX_API_KEY}" } }
```

It exposes the **full PetBox tool surface** (~72 tools, underscore-named):
`tasks_*`, `memory_*`, `session_*`, `comments_*`, `relations_*`, `config_*`,
`log_*` / `log_query`, `data_*` / `db_*`, `llm_*`, `deploy_*`, `apikey_*`,
`project_*`, `health_search`, `report_issue`, `whoami`. Each tool's visibility is
gated by the calling key's scopes.

Setup (one-time, per machine):
1. Set env var `PETBOX_API_KEY` to an ApiKey with the scopes you need (a
   cross-project `$system` key covers everything). Mint one on the agent-keys admin
   page or via `apikey_create`.
2. Claude Code (or any MCP-aware agent) picks up `.mcp.json` automatically; the key
   is never written into the file (it references `${PETBOX_API_KEY}`).

Useful during dev: `log_query` with KQL like `events | where Level == "Error"` to see
what errored; `tasks_search` / `memory_search` for the live plan and durable notes.

Note: bodies of PetBox nodes and comments render as **GFM markdown** — use `##`
headings and REAL newlines (never a literal `\n`), or the text renders as mush.

## Tasks / Memory / Session — what goes where

When recording state via the MCP tools, pick by lifetime:

- **Session** (`session_*`) — the *current* working plan/thinking. Ephemeral,
  last-write-wins. "Stale next week?" → session.
- **Tasks** (`tasks_*`) — a *unit of work with a status* tracked to Done. "Has a
  status that changes (Pending → Done)?" → task.
- **Memory** (`memory_*`) — a *durable fact* that should outlive the work. "Will a
  future agent need this to avoid re-learning it?" → memory.

Memory entries are typed (`User` | `Feedback` | `Project` | `Reference`) — `type` is
required on a **new** entry (`memory_upsert` with version 0, or `memory_remember`); on
an edit it is optional (an omitted field stays unchanged). Store durable facts not
derivable from code/git/config; do **not** store what the repo/git already records,
transient state, secrets, or actionable work (that's a task). `Feedback`/`Project`
entries should include the *why*, the *how to apply*, and a **revisit trigger** — the
condition under which the fact stops holding and may be reopened ("when the spec is
backfilled", "if we ever ship a second workspace"). Without one a decision is immortal:
a later agent either obeys it forever or breaks it on a whim, and neither is a decision.
Search before writing, update over duplicating, delete when wrong (temporal history makes
deletes safe). A cold `tasks_upsert` / `memory_upsert` auto-creates the board/store.

## Documents — what's here

Live reference docs:
- **[doc/methodology.md](doc/methodology.md)** — how we run PetBox (idea → spec →
  work → intake, the approve gate). Canon. Paired with
  **[doc/methodology-engine.md](doc/methodology-engine.md)**.
- **[doc/ui-conventions.md](doc/ui-conventions.md)** — canonical UI/component recipes
  (Tailwind/daisyUI, htmx/Alpine boundary, modals, tables). Consult before building UI.
- **[doc/settings-taxonomy.md](doc/settings-taxonomy.md)** — where every setting lives
  (entity catalog, storage rules, admin structure). Read before adding a configurable.
- **[doc/agent-interaction-audit.md](doc/agent-interaction-audit.md)** — the weekly
  audit playbook: surface freshness, zombie cards, uncommitted tails, session-archive
  process-violation sampling (incl. false-verify). Run it to audit; it files intake
  cards + an owner report.

Historical (frozen) — a record of how the project got here, not current state:
`doc/plan.md`, `doc/spec.md`, `doc/decision-log.md`, and the older design notes.

## Target stack

- .NET 10 monolith, Razor Pages SSR + htmx 2 + Alpine.js 3
- Tailwind CSS 3.4 + daisyUI 4, TypeScript via bun
- SQLite via linq2db 6.3.0 (Core DB, per-project Data DBs, module stores)
- FluentMigrator for internal (Core/Tasks/Memory/Sessions) migrations
- Seq.Extensions.Logging for structured log ingestion
- Kusto.Language + KustoLoco for the KQL query engine
- OpenTelemetry (AspNetCore + Http instrumentation) for tracing
- Docker: chiseled noble runtime, `/app/data` volume, port 8080
- Cake + GitVersion for build/versioning
- GitHub Actions CI/CD with SSH deploy
- E2E: Playwright (chromium)

## Module architecture

PetBox is a module monolith. Each subsystem is feature-toggled via `appsettings.json`:

```
Features: { Config: true, Logging: true, Data: true, Dashboard: true,
            Tasks: true, Memory: true, LlmRouter: true, Deploy: true }
```

Projects:
- `PetBox.Core` — Auth, Workspace/Project/ApiKey/… models, SQLite context +
  FluentMigrator migrations, search, feature-toggle infrastructure.
- `PetBox.Web` — single entry point (Program.cs), Razor Pages, frontend assets, MCP
  endpoint (`Mcp/`).
- `PetBox.Config` — tag-based config engine, resolve pipeline.
- `PetBox.Log.Core` — KQL engine, Seq/OTLP ingestion.
- `PetBox.Data` — per-project SQLite databases, raw-SQL pass-through REST, schema
  migration + WAL checkpoint / orphan cleanup services.
- `PetBox.Tasks` — task boards + methodology engine.
- `PetBox.Memory` — temporal memory stores + hybrid (FTS + vector) search.
- `PetBox.Sessions` — session transcripts, digests, episodic search.
- `PetBox.LlmRouter` (+ `.Contract`) — routed LLM chat/embed/rerank.
- `PetBox.Dashboard` — HealthPoller, CiPoller, dashboard UI.
- `PetBox.Deploy` — deployment tracking.

## Entity model

Core entities (in `PetBox.Core/Models`):
- **Workspace** — the top-level container; a `WorkspaceMember` grants a `User` a role
  in it.
- **Project** — grouping within a workspace (Key, WorkspaceKey, Name, Description).
- **ApiKey** — auth credential (Key, ProjectKey, Scopes, Name, ExpiresAt, …); the
  `ProjectKey` claim may be the wildcard `*` (a cross-project key).
- **ConfigBinding** — tag-based key-value (Path, Value, Tags) — not FK'd, resolved by
  pure tag matching.
- **HealthEndpoint / HealthReport** — dashboard health polling targets + results.
- **DataDb / DataTable** — per-project Data-module database + table metadata.
- **TaskBoardMeta**, **MemoryStoreMeta**, **Relation** — Tasks/Memory bookkeeping.
- Log records are stored per named log; `LogMeta` is the catalog entry.

`Workspace`/`Project` are organizational layers. ConfigBinding uses tags
(`project:kpvotes`, `service:kpvotes-bot`) — the resolve pipeline is pure tag-based,
most-specific-set wins. (The former standalone `Service` entity was removed —
migration `M019_DropServices`.)

## Hard invariants

- **Feature toggle gating.** Every subsystem checks `Features:<Name>` before
  registering endpoints/middleware/BackgroundServices. Disabled subsystem = zero
  runtime cost.
- **Auth: local and remote modes.** Default local (validate against own DB). Remote
  mode (`Auth:Mode: remote`, `RemoteUrl: …`) delegates validation to a central
  instance.
- **No PetBox self-config via ConfigModule.** PetBox configures itself from
  `appsettings.json` only. ConfigModule serves external consumers.
- **`$system` is the reserved built-in.** Auto-seeded on first migration as both a
  workspace and a project; undeletable.
- **Config resolve is deterministic.** Same tags → same binding; most specific tag
  set wins.
- **ApiKey scopes are an enumerated set, not wildcard scopes.** Scopes are drawn from
  a fixed list (`config:read`/`write`, `logs:ingest`/`query`/`admin`,
  `health:read`/`write`, `data:read`/`write`/`schema`, `tasks:read`/`write`,
  `memory:read`/`write`, `llm:invoke`/`admin`, `deploy:read`/`write`,
  `agent:poll`/`heartbeat`, `admin:provision`). A key's **project claim** may be the
  wildcard `*` (a cross-project key), but its **scopes** are always explicit.
- **Log records are append-only, immutable.** No update or delete of an ingested
  event.
- **English-only UI chrome.** UI strings are plain English literals — no
  Cyrillic/Hebrew/Arabic in chrome text. (No localization infrastructure exists yet:
  no `AddLocalization`/`IStringLocalizer` anywhere — adopting it is a future task;
  don't add ad-hoc localization piecemeal.)
- **`data-testid` for UI selectors.** No text-matching, no CSS class selectors for
  E2E tests.
- **No HTML in `.cs` files.** All markup in Razor (`.cshtml`).
- **No inline JS in Razor.** All client logic in `ts/` files, bundled by bun.
- **Frontend build is Release-only.** Debug uses the `bun run dev` watcher.

## Coding style

- **Immutability and functional approach.** C#: `record`/`readonly record struct`,
  `init`-only, `IReadOnlyList<T>`, `switch` expressions. TS: `const`, `readonly`,
  `ReadonlyArray<T>`.
- **Arrow/expression-bodied when it fits.** Expression-bodied members, `switch`
  expressions. Arrow functions for TS callbacks.
- **Omit implicit access modifiers.** Don't write `internal`, `private`, `public` on
  interface members.
- **Maximum static typing.** No `object`, no `dynamic`, no `any`. Generics and
  discriminated unions.
- **Formatting:** tool defaults — `dotnet format` for C#, biome for TS.
  `.editorconfig` at repo root.

## Commit convention

Conventional Commits. `type(scope): description`, ≤72 chars, imperative mood.
Types: `feat`, `fix`, `refactor`, `test`, `style`, `docs`, `chore`, `build`.
Scopes: `core`, `web`, `config`, `log`, `data`, `dashboard`, `tasks`, `memory`,
`sessions`, `llm`, `deploy`, `auth`, `docs`, `deps`.
