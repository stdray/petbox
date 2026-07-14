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
2. **Worktree before edits — ANY tracked file, not just code:** never edit the primary
   checkout; create a git worktree for the change. The rule is about the *file*, not the
   *kind of work*: if `git` tracks it, it is edited in a worktree — source, tests,
   `AGENTS.md` and `doc/*`, build/CI files, `.claude/` skills, hooks and agent definitions,
   `appsettings*.json`. A one-line doc or config fix is exactly the change that "feels too
   small for a worktree", and it is how the shared checkout ends up dirty and how a commit
   ends up on `main`. Only untracked/ignored paths (`.tmp/`, local logs, the scratchpad) may
   be written anywhere. Put every agent's worktree under `.claude/worktrees/<branch-slug>` —
   one dir for CC/opencode/droid alike: it is INSIDE the repo (so opencode, which can only
   work within the project folder, is happy) yet gitignored (so it never dirties
   `git status`), and it is where Claude Code's own worktree tool already lands. Take a
   fresh base without touching the primary: `git fetch origin` + `git worktree add <abs-dir>
   -b <branch> origin/main` — NOT `git pull` in the primary checkout (it is shared state,
   often parked on another session's feature branch with uncommitted edits). Pass an
   ABSOLUTE path to `git worktree add` (a relative path resolves against the cwd).
   **Finding work already in the primary checkout is not permission to continue it
   there** — an inherited diff (a previous session's unfinished edits) is the exact
   trap: nothing is "created", so the rule feels satisfied, and the change ends up
   committed from the shared tree. Move it out FIRST: `git stash push -u` → create the
   worktree → `git stash pop` inside it (the stash is repo-level, so it pops wherever
   you are).
3. **Tests:** `./build.ps1 -Target Test` (or `./build.sh --target=Test`) **is the gate** —
   it is what must be green before a commit is pushed / a card moves to Review. The Cake
   `Test` target depends on `Build`, `FormatVerify`, and `WebTsLint` (`build.cs`), so a
   green Cake `Test` run already proves formatting and the frontend lint, not just the
   .NET suite passing. A filtered `dotnet test` is fine for the FAST INNER LOOP while
   iterating on one failing test — it runs NONE of those dependencies, so being green on
   it proves nothing about mergeability; always finish on the Cake gate before pushing.
   This has bitten main twice from running less than the gate: it went red on formatting
   (`0c768826` "style(tests): format MutationFeedbackPageTests, unbreaking main" — the
   deploy tag run correctly refused to deploy that state), and on 2026-07-13 the deploy
   tag run for `4ceb0d25` passed `test` and `sdk` and died in `publish` (`CSC : error
   CS2001: Source file '/src/BannedSymbols.txt' could not be found`) — a new analyzer
   ban-list file lived at the repo root, declared in `Directory.Build.props`, but the
   Dockerfile copies a curated subset of the tree and nobody had added it there (fixed in
   `57072c38`). **A green local gate still does not prove the image builds**: anything the
   build needs that lives outside the paths the Dockerfile `COPY`s is invisible to every
   local check — the Cake gate builds the whole source tree, the image build does not. If
   a change adds a root-level build input (a new props/analyzer file, a new top-level
   directory), build the image (`docker build .`) before moving the deploy tag.
   Pipe test output to `.tmp/test-run.log` and read the log instead of scrolling the
   console. For readable FAILURE detail (a quiet log drops the assertion messages), add
   `--logger "trx;LogFileName=res.trx"` and render it with **`dotnet trx`** (the
   `dotnet-trx` local tool, in `.config/dotnet-tools.json`; run `dotnet tool restore`
   once). It auto-discovers `*.trx` under the cwd and prints each failed test's message +
   stack (`--path <dir>` to scope, `-v verbose` for all). Set **both** `NO_COLOR=1` and
   `TERM=dumb` for it (PowerShell: `$env:NO_COLOR='1'`, `$env:TERM='dumb'`) — otherwise
   the render carries ANSI color and OSC-8 hyperlink escapes that are pure noise in a
   captured log; `NO_COLOR` alone leaves the hyperlinks. For TIMING analysis (per-class
   totals, wall-clock critical path of a parallel run) use `dotnet run
   scripts/trx-timings.cs -- <run.trx>` — don't re-write that parser.
4. **Finish = branch + commit + push:** completed work is committed on a feature
   branch and pushed BEFORE the card moves to `Review` (add commits to the card via `commits[]`);
   never leave finished edits uncommitted in a working tree. This repo's process
   contract IS the standing request to commit — it overrides any agent-default
   "don't commit unless asked".
5. **Agent ceiling is Review:** mark finished work `Review`, never `Done`; the
   maintainer confirms `Done`.
6. **A card never closes silently — post the verdict:** ANY move to a terminal status
   (`Done`/`Cancelled` on work, `done`/`wontfix`/`duplicate` on intake,
   `accepted`/`rejected` on ideas, `deprecated` on spec) MUST be accompanied by a comment
   on the card carrying the `verdict` artifact tag (`comments_upsert` with
   `tags: ["verdict"]`). The card BODY states the intent; the verdict states the OUTCOME —
   what was actually done, the evidence it works (numbers, commits, live checks), and
   anything the next reader must know that the diff won't tell them. For a cancel/wontfix/
   reject, the verdict is WHY, and what would change the answer. A card closed without a
   verdict is indistinguishable a month later from one abandoned. Tag the comment
   `artifact:verdict` (the `artifact:` prefix is the convention).
   **This is binding on agents but NO LONGER enforced by the engine** — the
   `preconditionArtifact:"verdict"` gate was removed from every terminal transition
   (2026-07-12, intake `verdict-gate-blocks-maintainer`): the UI has no way to author an
   artifact-tagged comment, so a hard gate locked the maintainer — the one person entitled
   to close a card — out of closing anything. It stays off until the UI can write artifacts.
   An idea is exempt on principle, not just for now: its `spec_plan` IS its verdict (it
   states what the acceptance changes), so `review → accepted` must never carry the gate.
   `spec_plan` on `exploring → review` remains enforced.
7. **Deploy only on explicit command** (the `deploy` tag flow); after deploy, run a
   live smoke against production endpoints. **The tag run IS the whole pipeline** —
   pushing the `deploy` tag runs `test` → `publish` (image to ghcr) → `deploy`, so once
   the merge is on `main`, move the tag IMMEDIATELY and watch only the tag run. Do NOT
   wait for `main`'s own CI first: it runs the same tests, and the tag run re-runs them
   anyway (it will not deploy on a red build) — waiting just burns ~6 minutes twice.
   **A smoke NEVER writes into a real project.** Its target is the project `smoke`
   (workspace `$system`), which carries the **`sandbox=true`** flag — that flag, not a
   workspace, is what makes it a legal smoke target; **no workspace named `sandbox`
   exists** (`project_create(workspaceKey: "sandbox", …)` throws `InvalidOperationException:
   Workspace 'sandbox' not found` — verify with `project_list` before trusting this doc).
   A smoke authenticates with a `sandboxOnly` key — never your session's `$system` key,
   never a customer project's. Read-only probes (`/health`, `/version`, a search) may hit
   anything; anything that CREATES a board, project, session, memory entry or task goes
   to `smoke`. This is enforced, not merely asked: a `sandboxOnly` key is rejected against
   any project without the `sandbox` flag, on MCP and REST alike (even a wildcard `*` key
   — the check is on the TARGET's flag, not the claim). The rule exists because it was
   broken: six `smoke-*` boards took up residence next to `work`/`spec` in `$system`, and
   two one-line probes ("Reply with exactly: PONG") landed in the customer project
   `yobapub`, where `SessionDigestJob` tried to digest them and burned ~1462 LLM calls.
   Background jobs run in `smoke` exactly as in production — that is the point, a smoke
   must be able to prove them — so it is a real target, not a null sink: clean up after
   yourself there. There is currently **no `project_delete` MCP tool** (`tool_describe`
   on it returns `unknown tool`) — a project created during a smoke can only be removed
   through the admin UI's Danger zone.
8. **Clean up when the card closes — the worktree's life ends with the card, not with
   the push:** once a card reaches a terminal status (`Done`/`Cancelled`) and its branch
   is merged, remove the worktree (`git worktree remove <dir>`) and delete the branch
   both locally (`git branch -d`) and on the remote (`git push origin --delete`). Rule 2
   creates a worktree per change and rule 4 ends at the push, so nothing ever reaped
   them: they accumulated to ~120 stale worktrees, 115 dead local branches and 14 merged
   remote ones before anyone noticed, and `git worktree list` stopped being readable.
   **Never reap blindly** — a worktree may hold someone else's unfinished work. Delete
   ONLY one that is clean (`git status --porcelain` empty) AND fully merged
   (`git rev-list --count origin/main..HEAD` is 0) AND not `locked`; anything else stays,
   and if it looks abandoned, say so instead of removing it.
9. **Don't silently work around process/doc defects** — file an intake card on
   `$system`.
10. **Delegating to workers (fan-out):** role rules — what a worker/orchestrator/utility/
   reserve may do, the self-intro contract, escalation — live in exactly ONE place: the
   server agent definition (`agent_def_get('default')`, project `$system`). Do not
   hand-copy that prose here; it drifts the moment either side changes (this rule used to
   quote a worker preamble that fell out of sync with the definition — see work
   `agents-md-canon-boundary`). Its compiled, per-harness form is what a spawned subagent
   actually reads at start — for Claude Code that is the PROJECT-LOCAL, gitignored
   `.claude/agents/<role>.md` (produced by `petbox-wire apply`; **not**
   `~/.claude/agents/worker.md` — no role files live at user scope). See
   [doc/agent-wiring.md](doc/agent-wiring.md) §2d for the apply mechanics and the
   harness→path table.
   A spawned subagent does NOT run the SessionStart hook — it never sees the memory
   banner, canon, or the definition automatically — so until `apply` has been run in a
   given checkout, the spawn brief is the only delivery channel: fetch the current
   worker-role notes from the definition and open the brief with them (strengthen per
   task, never weaken; never dictate the model or the self-intro line — the subagent
   states what it actually runs as).

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
  (TS SDK → npmjs), `NpmWirePublish` (the `petbox-wire` kit → npmjs), `PyPiPublish`
  (Python → PyPI) — each fired by its own tag. The version is stamped by GitVersion at
  pack time; never bump `package.json` by hand. `NpmWirePublish` DEPENDS on `TsWireTest`
  + `TsWireTypecheck`, so a red kit cannot reach npm — and until the kit is published,
  no fix reaches anyone who installs it with `npx petbox-wire@latest`.
- **Dev loop:** `./build.ps1 -Target Dev` runs `bun run dev` (ts + css watchers) and
  `dotnet watch run` side by side from `src/PetBox.Web`.
- **CI:** `.github/workflows/ci.yml` on push to `main` runs the `Test` target; a
  parallel job builds and pushes the Docker image to ghcr.io. Tags trigger extra
  jobs: `deploy` (SSH deploy + post-deploy health smoke), `nuget`/`npm`/`npm-wire`/`pypi`
  (client publish). Every publishing tag is moved the same way as `deploy`:
  `git tag -f <tag> <sha> && git push origin <tag> --force`.
- **Deploy:** manual `deploy` tag only — move the tag to the target commit and push
  it (`git tag -f deploy <sha> && git push origin deploy --force`). The tag run is a
  FULL run of `ci.yml`: it re-runs `test`, rebuilds and pushes the image, and only then
  runs the `deploy` job (SSH to prod, pull the new image, health-check the fresh
  container). So the whole outbound flow is: merge → push `main` → move the tag → watch
  the TAG run. The push-to-`main` run and the tag run execute the same tests, so waiting
  for the former before tagging doubles the wall-clock for nothing.

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
entries should include the *why* and *how to apply*. Search before writing, update
over duplicating, delete when wrong (temporal history makes deletes safe). A cold
`tasks_upsert` / `memory_upsert` auto-creates the board/store.

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
  - Локальная отладка UI через скилл `playwright-cli`: только bundled chromium
    (`--browser=chrome|msedge` на Windows оставляет висящее чёрное окно), после работы —
    `npx playwright-cli close-all`. Подробности — в памяти PetBox (workspace).

## Database connections — a hard invariant

**No `DataConnection` is injectable. Ever.** `PetBoxDb` (core.db) and `DeployDb` are not
registered in DI, so they cannot reach you through a ctor, a minimal-API/MCP handler
parameter, or `GetRequiredService`. The only way in is:

```csharp
using var db = _coreDbFactory.Open();   // ICoreDbFactory / IDeployDbFactory
```

The other five (`TasksDb`, `LogDb`, `MemoryDb`, `SessionsDb`, `ConfigDb`) already work this
way via `IScopedDbFactory<T>.NewEnsuredConnection`.

Why: LinqToDB's `DataConnection` is not thread-safe. A request-scoped one shared across
`Task.WhenAll` branches gets its `SqliteCommand` parameters trampled — which surfaced three
times in one day (a fan-out leg, an embed leg one layer down, and a singleton that had
captured a scoped client). Handing every caller its own connection makes that class of bug
**unexpressible**, not merely detectable. `DbInjectionGuardTests` fails the build if a
registration ever comes back.

Two rules that survive the refactor:
- Never construct `PetBoxDb` by hand — the factory preserves the shared `MappingSchema`, and
  a per-connection schema is a ~290 MB production OOM (it has happened).
- Never call another core-db service while holding an open core transaction: core.db runs
  `Cache=Shared`, and the `SQLITE_LOCKED` it raises is not retried by the busy handler.

**The database is visible only in the service layer.** Query it with LinqToDB, reach it
through the factory, and open it *inside a service* — a Razor PageModel, a page filter,
middleware, an `IClaimsTransformation` or an endpoint lambda asks a service, it does not
call `.Open()` itself. `AgentKeyAdminService` is the shape to copy: factory in the ctor,
`using var db` inside, and the ownership predicate welded into the statement rather than
applied by filtering what was rendered.

Two reasons, and the second is the one that bites. A rule that lives in ten pages is a rule
a new page forgets — that is how ten copies of "is this project in this route's workspace?"
drifted into an IDOR nobody noticed. And there is no cache over core.db anywhere: a GET of
a project page opens it 7–9 times, and nothing can be memoised or invalidated while the
readers and writers are scattered across pages. Extracting the service is the precondition
for fixing that, not a matter of taste.

Most of `Pages/**` predates this rule and still opens core.db directly (see
`db-out-of-pages-into-services` on the work board). New code does not add to the pile.

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
- **A Core entity column must be declared in `PetBoxDb`'s Fluent mapping.** `PetBoxDb`
  maps its entities with `FluentMappingBuilder`; a property that exists on the model AND
  as a migration column but is NOT declared there is silently dropped from linq2db's
  schema cache — `InsertAsync` omits it, reads come back NULL, **and the call still
  reports success**. So adding a column takes THREE things: the migration, a
  `.Property(x => x.Foo)…` line in `PetBoxDb.BuildMappingSchema`, and an INSERT→SELECT
  round-trip test. A migration-only test does NOT catch this (the column is in the DB;
  it is the mapping that lost it). `FluentMappingCompletenessTests` enforces it
  mechanically — a property that is genuinely not persisted is opted out with
  `[NotColumn]`. Past scars: `SavedQuery.CreatedAt/UpdatedAt` ("Save as" 500'd),
  `ApiKey.ExpiresAt`, `ApiKey.DefaultProjectKey`, `DataTable.Read/Write/Delete`.
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
- **Never silence the typechecker.** No `!` (non-null assertion), no `as any`, no
  `@ts-expect-error` to make an error go away. Narrow the type honestly — a guard that
  does something real (return, throw, meaningful default). If you cannot say what should
  happen when the value is absent, you have found a bug, not an annoyance.
- **Formatting:** tool defaults — `dotnet format` for C#, biome for TS.
  `.editorconfig` at repo root.

## Commit convention

Conventional Commits. `type(scope): description`, ≤72 chars, imperative mood.
Types: `feat`, `fix`, `refactor`, `test`, `style`, `docs`, `chore`, `build`.
Scopes: `core`, `web`, `config`, `log`, `data`, `dashboard`, `tasks`, `memory`,
`sessions`, `llm`, `deploy`, `auth`, `docs`, `deps`.
