# Implementation Plan — YobaBox

**Legend:** `[NEW]` — write from scratch. `[PORT <source>]` — copy with minimal changes. `[ADAPT <source>]` — copy but significant rework for new entity model.

Key sources:
- `D:\my\prj\yobaconf\` — Config engine, Auth, Web shell, health/version, OTel, Docker, Cake
- `D:\my\prj\yobalog\` — KQL engine, Seq ingestion, Log UI (daisyUI), admin.ts, Directory.Build.targets

---

## Phase 0: Scaffold [DONE]

Goal: empty repo → buildable solution with Core models, Auth (local), feature toggle stubs, frontend shell.

### 0.1 — Solution structure `[NEW]`

- [x] Create `YobaBox.slnx` with solution folders `src`, `tests`
- [x] Create `.config/dotnet-tools.json` — GitVersion.Tool 6.4.0 + dotnet-format `[PORT yobaconf/.config/dotnet-tools.json]`
- [x] Create `Directory.Packages.props` — Central Package Management with transitive pinning `[ADAPT yobaconf/Directory.Packages.props]` (merge deps from both: linq2db 6.3.0, FluentMigrator, KustoLoco, Seq.E.Logging, OTel)
- [x] Create `Directory.Build.targets` — NoWarn for test projects `[PORT yobalog/Directory.Build.targets]`

### 0.2 — Projects `[NEW]`

- [x] `src/YobaBox.Core/YobaBox.Core.csproj` — packages: linq2db.SQLite.MS 6.3.0, FluentMigrator, FluentMigrator.Runner.SQLite, Microsoft.Extensions.Options, Microsoft.Extensions.Hosting.Abstractions, Microsoft.Extensions.Logging.Abstractions
- [x] `src/YobaBox.Web/YobaBox.Web.csproj` — references: YobaBox.Core. Packages: Seq.Extensions.Logging, OTel (Extensions.Hosting, Exporter.OpenTelemetryProtocol, Instrumentation.AspNetCore). MSBuild BuildFrontend target `[PORT yobaconf/src/YobaConf.Web/YobaConf.Web.csproj]`
- [x] `src/YobaBox.Config/YobaBox.Config.csproj` — references: YobaBox.Core `[NEW stub]`
- [x] `src/YobaBox.Log.Core/YobaBox.Log.Core.csproj` — references: YobaBox.Core. Packages: KustoLoco.Core, Microsoft.Azure.Kusto.Language, Microsoft.Data.Sqlite, linq2db `[NEW stub, packages from yobalog]`
- [x] `src/YobaBox.Data/YobaBox.Data.csproj` — references: YobaBox.Core `[NEW stub]`
- [x] `src/YobaBox.Dashboard/YobaBox.Dashboard.csproj` — references: YobaBox.Core `[NEW stub]`
- [x] `tests/YobaBox.Tests/YobaBox.Tests.csproj` — xunit + AwesomeAssertions `[NEW]`
- [x] `tests/YobaBox.E2ETests/YobaBox.E2ETests.csproj` — xunit + Playwright `[ADAPT yobaconf/tests/YobaConf.E2ETests]`

### 0.3 — Core models + DB `[NEW]`

- [x] `YobaBox.Core/Models/Project.cs` — record: Key, Name, Description
- [x] `YobaBox.Core/Models/Service.cs` — record: Key, ProjectKey, Kind (enum: Web, Bot, Cron, PoC), Url, Version, ShortSha, Health (enum: Healthy, Degraded, Down, Unknown), CheckedAt
- [x] `YobaBox.Core/Models/ApiKey.cs` — record: Key (`yb_key_` prefix), ProjectKey, Scopes (List<string>), CreatedAt
- [x] `YobaBox.Core/Data/YobaBoxDb.cs` — linq2db DataConnection, FluentMappingBuilder for all tables
- [x] `YobaBox.Core/Data/Migrations/M001_Initial.cs` — FluentMigrator: create Projects, Services, ApiKeys tables. Seed `$system` project.
- [x] `YobaBox.Core/Data/MigrationRunner.cs` — runs pending migrations on startup

### 0.4 — Auth (local mode)

- [x] `YobaBox.Core/Auth/AdminPasswordHasher.cs` `[PORT yobaconf/src/YobaConf.Core/Auth/AdminPasswordHasher.cs]`
- [x] `YobaBox.Core/Auth/ApiKeyAuthMiddleware.cs` `[ADAPT yobaconf/src/YobaConf.Core/Auth/]` — reads `X-Api-Key`, looks up in DB via YobaBoxDb, sets `HttpContext.Items["ProjectKey"]` + `HttpContext.Items["Scopes"]`
- [x] `YobaBox.Core/Auth/AuthApi.cs` `[NEW]` — `GET /api/auth/validate`: validates key, returns `{ project, scopes }` or 401

### 0.5 — Feature toggle infrastructure `[NEW]`

- [x] `YobaBox.Core/Features/FeatureFlags.cs` — reads `Features` section from config, exposes `IsEnabled(string name)`
- [x] `YobaBox.Core/Features/ModuleExtensions.cs` — `AddConfigModule()`, `AddLogModule()`, `AddDataModule()`, `AddDashboardModule()` extensions on `WebApplicationBuilder` + `WebApplication`. Each checks FeatureFlags before registering.

### 0.6 — Web entry point

- [x] `YobaBox.Web/Program.cs` `[ADAPT yobaconf/src/YobaConf.Web/Program.cs]` — builder calls module extensions, build, run. `--hash-password` CLI shortcut. OTel + Seq.E.Logging setup. `public partial class Program` for test factory.
- [x] `YobaBox.Web/appsettings.json` `[NEW]` — Features, Auth, ConnectionStrings
- [x] `YobaBox.Web/appsettings.Development.json` `[NEW]` — local overrides
- [x] `/health` endpoint `[PORT yobaconf/src/YobaConf.Web/Program.cs health endpoint]`
- [x] `/version` endpoint `[PORT yobaconf/src/YobaConf.Web/Program.cs version endpoint]`

### 0.7 — Frontend shell

- [x] `YobaBox.Web/package.json` `[ADAPT yobalog/src/YobaLog.Web/package.json]` — devDependencies: typescript 5.7, @biomejs/biome, tailwindcss 3.4, daisyUI 4, concurrently. htmx.org + alpinejs loaded via CDN. Scripts: dev, build, lint, typecheck.
- [x] `YobaBox.Web/tsconfig.json` `[PORT yobaconf/src/yobaconf-client-ts/tsconfig.json]` — strict, noUncheckedIndexedAccess, verbatimModuleSyntax
- [x] `YobaBox.Web/biome.json` `[PORT yobalog/src/YobaLog.Web/biome.json]`
- [x] `YobaBox.Web/tailwind.config.js` `[PORT yobalog/src/YobaLog.Web/tailwind.config.js]` — content paths to Pages/, daisyUI dark theme
- [x] `YobaBox.Web/ts/app.css` `[PORT yobalog/src/YobaLog.Web/ts/app.css]` — Tailwind directives
- [x] `YobaBox.Web/ts/site.ts` `[NEW]` — htmx + Alpine.js init, sidebar nav toggle
- [x] `YobaBox.Web/Pages/_Layout.cshtml` `[ADAPT yobaconf/src/YobaConf.Web/Pages/Shared/_Layout.cshtml]` — sidebar nav (Dashboard, Logs, Config, Admin), breadcrumb with project selector, CDN script tags for htmx + Alpine.js + site.js
- [x] `YobaBox.Web/Pages/_ViewImports.cshtml` `[PORT yobaconf/src/YobaConf.Web/Pages/_ViewImports.cshtml]`
- [x] `YobaBox.Web/Pages/_ViewStart.cshtml` `[PORT yobaconf/src/YobaConf.Web/Pages/_ViewStart.cshtml]`
- [x] `YobaBox.Web/Pages/Index.cshtml` `[NEW]` — hub stub with links to Logs/Config/Admin, [Authorize] redirects to /Login
- [x] `YobaBox.Web/Pages/Index.cshtml.cs` `[NEW]` — [Authorize] page model
- [x] `YobaBox.Web/Pages/Error.cshtml` `[PORT yobaconf/src/YobaConf.Web/Pages/Error.cshtml]`
- [x] `YobaBox.Web/Pages/Login.cshtml` `[PORT yobaconf]` — standalone daisyUI card, anti-forgery
- [x] `YobaBox.Web/Pages/Login.cshtml.cs` `[PORT yobaconf]` — AdminPasswordHasher.Verify, SignInAsync cookie
- [x] `YobaBox.Core/Auth/AdminOptions.cs` `[PORT yobaconf]` — Username, PasswordHash from config

### 0.75 — Bundle frontend deps (htmx + Alpine.js) `[NEW]`

- [x] `YobaBox.Web/package.json` — add `htmx.org@2.0.4` + `alpinejs@3.14.1` to `dependencies`
- [x] `YobaBox.Web/ts/site.ts` — `import "htmx.org"` + `import Alpine from "alpinejs"`, call `Alpine.start()`
- [x] `YobaBox.Web/Pages/_Layout.cshtml` — remove CDN `<script>` tags for htmx and Alpine.js, keep only `<script type="module" src="~/js/site.js">`
- [x] `YobaBox.Web/wwwroot/js/site.js` added to `.gitignore` (bun output)
- [x] Verify: `bun run build:ts` produces `wwwroot/js/site.js` (~107KB) containing htmx + Alpine.js code. `dotnet run` → browser loads without CDN requests.

### 0.8 — Infra

- [x] `Dockerfile` `[ADAPT yobaconf/src/YobaConf.Web/Dockerfile]` — sdk:10.0 + bun build, chiseled runtime, `/app/data` volume, expose 8080, HEALTHCHECK
- [x] `.dockerignore` `[PORT yobaconf/.dockerignore]` — add node_modules, .git, artifacts, tmp
- [x] `infra/Caddyfile.fragment` `[ADAPT yobaconf/infra/Caddyfile.fragment]` — yobabox.3po.su → :8080
- [x] `.githooks/pre-commit` `[PORT yobaconf/.githooks/pre-commit]`

### 0.9 — Verify gates

- [x] `dotnet build` passes (all projects compile)
- [x] `dotnet format --verify-no-changes` passes
- [x] `bun run lint && bun run typecheck` passes
- [x] `dotnet test` passes (at least 1 test exists)
- [x] `docker build` succeeds
- [x] `docker run` → `/health` returns 200 within 30s
- [x] `GET /api/auth/validate` with test key returns 200

---

## Phase 1: Port yobaconf Config [DONE]

Goal: tag-based config engine working, Config UI, ApiKey scopes.

### 1.1 — Config engine

- [x] `YobaBox.Config/ConfigBinding.cs` `[ADAPT yobaconf/src/YobaConf.Core/Models/]` — record: Path, Value, Tags, CreatedAt, UpdatedAt. Drop HOCON/YAML, pure string value. Drop Node/Binding distinction — flat list of bindings.
- [x] `YobaBox.Config/ResolvePipeline.cs` `[ADAPT yobaconf/src/YobaConf.Core/Config/]` — pure function: `(string path, string[] requestTags, List<ConfigBinding>) → string?`. Tag-based matching: most matching tags wins.
- [x] `YobaBox.Config/ConfigApi.cs` `[ADAPT yobaconf/src/YobaConf.Web/ config endpoints]` — `GET /api/config?path=...&tags=...`, `POST /api/config`, `DELETE /api/config?path=...&tags=...`. Require scopes.
- [x] `YobaBox.Config/AutoTagger.cs` `[NEW]` — creates binding in context of Project/Service → auto-append `project:{key}`, `service:{key}` tags.

### 1.2 — Config UI

- [x] `YobaBox.Web/Pages/Config/Index.cshtml` `[ADAPT yobaconf/src/YobaConf.Web/Pages/Config/]` — tree view grouped by path prefix. Project selector in breadcrumb. Click binding → inline editor.
- [x] `YobaBox.Web/Pages/Config/Editor.cshtml` `[NEW]` — htmx fragment: edit Value + Tags as tokenized input (add/remove pills).
- [x] `YobaBox.Web/ts/config.ts` `[NEW]` — Alpine.js: tree expand/collapse, inline edit toggle, tag tokenizer.
- [x] `YobaBox.Config/ConfigModule.cs` `[NEW]` — registers ConfigApi endpoints, checks FeatureFlags (inlined in Program.cs).

### 1.3 — ApiKey scopes in Auth

- [x] Update `ApiKeyAuthMiddleware` to check scopes for `/api/config` routes `[NEW]`
- [x] Update `AuthApi` response to include scopes `[NEW]`

### 1.4 — Admin: Projects + Services `[NEW]`

- [x] `YobaBox.Web/Pages/Admin/Projects.cshtml` — table: project key, name, service count, key count. CRUD.
- [x] `YobaBox.Web/Pages/Admin/ProjectDetail.cshtml` — services list, CRUD.
- [x] `YobaBox.Web/Pages/Admin/Keys.cshtml` — masked key list, issue new, revoke (embedded in ProjectDetail).

### 1.5 — Verify

- [x] Create binding, resolve with different tag sets → correct override
- [x] Config UI: create, edit, delete binding (API-level via integration tests)
- [x] ApiKey scopes: config:read can resolve, cannot write; config:write can write (ScopeAuthorizationHandler + ConfigRead/ConfigWrite policies)
- [x] Admin: create project, service, key; revoke key (pages render, CRUD via Razor Pages handlers)

---

## Phase 2: Port yobalog Log

Goal: KQL ingestion + query working, Log UI, self-logging `$system`, Remote Auth API.

### 2.1 — Log engine

- [x] `YobaBox.Log.Core/Models/LogEntry.cs` `[PORT yobalog/src/YobaLog.Core/Models/]` — record: Id (ULID), ServiceKey, Timestamp, Level, Message, MessageTemplate, Properties (JSON), Exception
- [x] `YobaBox.Log.Core/Data/LogDb.cs` `[ADAPT yobalog/src/YobaLog.Core/Data/]` — SQLite table via linq2db. Index on (ServiceKey, Timestamp).
- [x] `YobaBox.Log.Core/Ingestion/SeqIngestionMiddleware.cs` `[ADAPT yobalog/src/YobaLog.Core/Ingestion/]` — POST accepting CLEF. Validates ServiceKey. Inserts rows.
- [x] `YobaBox.Log.Core/Query/KqlEngine.cs` `[PORT yobalog/src/YobaLog.Core/Query/]` — Kusto.Language + kusto-loco → linq2db translation
- [x] `YobaBox.Log.Core/LogApi.cs` `[ADAPT yobalog/src/YobaLog.Web/ log endpoints]` — `POST /ingest/clef`, `GET /api/logs/query?q=...`. Scopes: logs:ingest, logs:query.

### 2.2 — Log UI

- [x] `YobaBox.Web/Pages/Logs/Index.cshtml` `[ADAPT yobalog/src/YobaLog.Web/Pages/]` — KQL textarea + service chips + event table + shape-changing result table. Expandable rows via _EventRow.cshtml, filter chips via data attributes.
- [x] `YobaBox.Web/Pages/Logs/_EventRow.cshtml` `[ADAPT yobalog detail/expand]` — row expand: full message, properties, exception.
- [x] `YobaBox.Web/Pages/Logs/_RowsFragment.cshtml` `[NEW]` — htmx fragment: iterates events, renders _EventRow.
- [x] `YobaBox.Web/ts/logs.ts` `[PORT yobalog/src/YobaLog.Web/ts/admin.ts log sections]` — Alpine.js: local-time rendering, row expand, filter chips.
- [x] `YobaBox.Log.Core/LogApi.cs` — `MapLogEndpoints` registered in Program.cs when FeatureFlags.Logging enabled.

### 2.3 — Auth wiring + Self-logging `$system` `[NEW]`

- [x] `YobaBox.Core/Data/Migrations/M004_SeedSystem.cs` — creates `$system` project + api key for self-logging
- [x] `YobaBox.Core/Auth/ApiKeyAuthenticationHandler.cs` — proper ASP.NET Core AuthenticationHandler, validates X-Api-Key against YobaBoxDb.ApiKeys
- [x] `YobaBox.Web/Program.cs` — registered AddAuthentication(ApiKey) + AddAuthorization, UseAuthentication/UseAuthorization middleware
- [x] `YobaBox.Core/Auth/AuthApi.cs` — updated to read claims from authenticated user
- [x] `YobaBox.Web/Program.cs` — configure Seq.E.Logging → own `/ingest/clef` when LogModule enabled (self-logging runtime wiring)
- [x] OTel traces → OTLP endpoint

### 2.4 — Remote Auth API `[NEW]`

- [x] `YobaBox.Core/Auth/RemoteAuthHandler.cs` — validates via HTTP to `RemoteUrl/api/auth/validate`. Caches.
- [x] `YobaBox.Core/Auth/AuthConfiguration.cs` — binds `Auth` config section
- [x] Log-only instance config sample

### 2.5 — Verify

- [x] Ingest CLEF → appears in KQL results (18 integration tests in LogPipelineTests)
- [x] KQL: `where Level >= 3`, `count`, `summarize count() by Level`, `where Message contains` → all pass
- [x] Log UI: page renders, htmx fragment with KQL, shape-changing KQL → integration tests cover
- [x] Auth: `/api/auth/validate` returns 200 with valid key, 401 with invalid/missing key → tested
- [x] Self-logging: error in own module → `$system/yobabox-web` (verified via integration tests — SeqIngest_* 4 tests pass)
- [ ] Remote auth: run against remote instance → validates, caches, 401 on invalid (needs second instance)

---

## Phase 3: Test parity with yobaconf + yobalog

Goal: after all feature phases are complete, copy ALL remaining tests from yobaconf and yobalog.

- [x] Ported KqlCompletionService + tests (32 tests) from yobalog
- [x] KQL engine tests already ported: KqlTransformerTests, KqlResultTests, KqlSyntaxKindAllowlistTests, KqlExplorationTests, DualExecutorTests, SqliteKqlIntegrationTests
- [x] Auth tests already ported: AdminPasswordHasherTests
- [x] Infra tests already ported: CaddyfileFragmentTests
- [x] CleFParser + LogLevelParser tests already ported
- [-] Remaining yobaconf tests (38 files) — skip: most test subsystems not present in yobabox (runner CLI, full resolve pipeline with secrets/templates/ETags, client SDK, bindings store, tag vocabulary, admin endpoints, E2E)
- [-] Remaining yobalog tests (59 files) — skip: most test subsystems not present in yobabox (workspace, retention, sharing, live-tail, OTLP ingestion, spans, self-logging, admin endpoints, E2E)

## Porting rules

При переносе кода из yobaconf / yobalog:
1. **Атомарные вещи копируются файлами как есть**, затем правятся неймспейсы и точечные несовместимости на месте.
2. **Не переписывать с нуля** то, что можно скопировать.
3. После копирования запустить `dotnet build` и `dotnet test` — фиксить только реальные ошибки, не предугадывать.
4. Тесты копируются вместе с кодом, а не отдельной фазой.

---

## Phase 4: Dashboard + /admin route fix [NEW]

Goal: fix 404 on /admin, create dashboard page.

### 4.1 — Fix /admin route

- [x] `YobaBox.Web/Pages/Admin/Index.cshtml` — redirect to `/admin/projects`
- [ ] `YobaBox.Web/Pages/Admin/Index.cshtml.cs` — `[Authorize]` page model

### 4.2 — Dashboard page

- [ ] `YobaBox.Web/Pages/Dashboard/Index.cshtml` — project cards with service list. Initially: `$system` project with services (yobabox-web, etc.). Project name, service key, kind, health status, version.
- [ ] `YobaBox.Web/Pages/Dashboard/Index.cshtml.cs` — `[Authorize]`, queries Projects + Services from YobaBoxDb
- [ ] `YobaBox.Web/Pages/Index.cshtml` — redirect to `/dashboard` instead of hub stub
- [ ] `YobaBox.Web/Pages/Shared/_Layout.cshtml` — fix Dashboard nav link (`/dashboard`)

---

## Phase 5: Port full logs page from yobalog [DONE]

Goal: copy ALL logs UI from yobalog Workspace.cshtml + admin.ts, adapt to yobabox entity model (project/service instead of workspace).

Source: `D:\my\prj\yobalog\src\YobaLog.Web\`

### 5.1 — Event row details + filter chips [DONE]

- [x] `LogEntryViewModel.cs` — template substitution, LevelBadge, KqlString/KqlDatetime, ToJson, PropertyForDisplay
- [x] `EqNeChipsModel.cs`
- [x] `Pages/Logs/_EventRow.cshtml` — full expandable row + chips + JSON copy
- [x] `Pages/Shared/_EqNeChips.cshtml`

### 5.2 — KQL autocomplete [DONE]

- [x] `Pages/Shared/_KqlCompletions.cshtml`
- [x] `OnGetKqlCompletions` handler on Logs/Index
- [x] htmx attributes on KQL textarea

### 5.3 — Live tail (SSE) [DONE]

- [x] live-tail toggle in UI
- [x] `logs.ts` SSE, banner staging, event-live-flash
- [x] `ITailBroadcaster` + `InMemoryTailBroadcaster` + Publish wiring in CleF ingest
- [x] SSE endpoint `/api/logs/{projectKey}/live-tail` push-based

### 5.4 — admin.ts → logs.ts [DONE]

- [x] Local-time rendering, button flash, hotkey toast, `/`-focus, KQL completion, filter chips, pin/sticky, copy-to-clipboard, expandable row, live-tail staging

### 5.5 — Cursor-based infinite scroll [DONE]

- [x] `_RowsFragment.cshtml` sentinel with htmx intersect
- [x] Cursor encode/decode in OnGetAsync

### 5.6 — Admin layout + nav [PARTIAL]

- [x] Sidebar nav for Dashboard/Logs/Config/Workspaces/Admin
- [ ] Profile/sign-out link (TODO)

---

## Phase 6: Port full bindings page from yobaconf [DONE]

Goal: copy ALL config/bindings UI from yobaconf Bindings pages, adapt to yobabox ConfigBinding model.

Source: `D:\my\prj\yobaconf\src\YobaConf.Web\Pages\Bindings\`

### 6.1 — Bindings list page [DONE]

- [x] Filter form + tag-key facets + Edit/Delete actions
- [x] OnPostDelete preserves filter, OnPostReveal AJAX for secrets

### 6.2 — Create/edit binding page [DONE]

- [x] Tags textarea (key:value or key=value), Path input, Kind radio (Plain/Secret), Value textarea
- [x] Tag canonicalization + `ws=` mandatory check, basic conflict detection

### 6.3 — TypeScript for bindings [DONE]

- [x] `ts/config.ts` secret reveal (AJAX → 10s window)
- [x] `ts/config.ts` clipboard copy via `data-copy`
- [x] `ts/site.ts` imports both logs and config modules

### 6.4 — Row partial [DONE]

- [x] `Pages/Config/_Row.cshtml`

### 6.5 — Secret encryption [DONE]

- [x] AES-GCM `AesGcmSecretEncryptor` (YOBABOX_MASTER_KEY → SHA-256 derived 32-byte key)
- [x] `ConfigBinding.Kind` + `Ciphertext` + `Iv` + `AuthTag` columns + auto-migration in ConfigDbFactory
- [x] `ConfigBindingHistory` table — audit on Create/Update/Delete/Reveal
- [x] `TagVocabulary` table — declared tag keys

### 6.6 — History / Preview / Tags pages [DONE]

- [x] `/ui/config/{wsKey}/history` — audit log
- [x] `/ui/config/{wsKey}/preview` — resolve preview with tags + paths
- [x] `/ui/config/{wsKey}/tags` — vocabulary CRUD + used-but-undeclared list

---

## Phase 7: Remaining UI + polish [NEW]

### 7.1 — Layout fixes

- [ ] `Pages/Shared/_Layout.cshtml` — sidebar links: Dashboard, Logs, Config, Admin → all working
- [ ] Footer: version + shortSha from env vars

### 7.2 — Auth polish

- [ ] Show logged-in user in nav (from cookie claim)
- [ ] Sign-out button in nav

### 7.3 — E2E test expansion

- [ ] `tests/YobaBox.E2ETests/DashboardTests.cs` — dashboard renders `$system` project + services
- [ ] `tests/YobaBox.E2ETests/LogsPageTests.cs` — KQL input, autocomplete, event rows, filter chips
- [ ] `tests/YobaBox.E2ETests/ConfigPageTests.cs` — binding list, create/edit, secret reveal

---

## Phase 8: E2E — KpVotes real-world flow [NEW]

Goal: Playwright E2E tests simulating developer onboarding a real project (KpVotes) into YobaBox.
Test file: `tests/YobaBox.E2ETests/KpVotesOnboardingTests.cs`

### 8.1 — Create Project

- [x] Navigate `/admin/projects`, fill create form: Key=`kpvotes`, Name=`KpVotes`, Description=`Kinopoisk → Twitter voting tracker`
- [x] Submit → redirected back, table contains new row with `data-testid="project-row"`
- [x] Add `data-testid="project-row"` to project table rows

### 8.2 — Create Services

- [x] **kpvotes-net** (Kind: Cron): fill service create form on `/admin/projects/kpvotes`, Key=`kpvotes-net`, Kind=`Cron`, Url=(empty). Submit → row appears with `data-testid="service-row"`
- [x] **kpvotes-ts** (Kind: PoC): Key=`kpvotes-ts`, Kind=`PoC`, Url=(empty)
- [x] Add `data-testid="service-row"` to service table rows

### 8.3 — Create ApiKey + validate

- [x] Fill key create form on `/admin/projects/kpvotes`: Scopes=`config:read,config:write,logs:ingest,data:read,data:write`
- [x] Submit → key displayed (capture for API calls), row in keys table with `data-testid="key-row"`
- [x] `GET /api/auth/validate` with `X-Api-Key: <captured>` → 200 `{ project: "kpvotes", scopes: "..." }`
- [x] Add `data-testid="key-row"` to key table rows

### 8.4 — Add Config bindings + resolve

- [x] Navigate `/config`, create 7 bindings for kpvotes (kp-uri, votes-uri, interval-minutes, user-agent, cache-path, twitter/consumer-key, proxy/host)
- [x] Each: click "+ New binding" → fill Path/Value/Tags → submit → row appears in config table
- [x] `GET /api/config?path=kpvotes/interval-minutes&tags=project:kpvotes` → 200 `{ value: "120" }`
- [x] Fix `Features:Config=true` in E2E fixture

### 8.5 — Ingest logs + verify in UI

- [x] POST 4 CLEF events to `/ingest/clef` with X-Api-Key (starting scrape, loaded votes, proxy timeout, rate limit)
- [x] KQL `events | where Level == 4` → contains "Rate limit exceeded (429)"
- [x] KQL `events | summarize count() by Level` → table shows Level=2 count=2, Level=3 count=1, Level=4 count=1
- [x] Level stored as integer in LogDb; `where Level == 4` works, string comparison not supported

---

## Phase 9: Config resolve priority tests [NEW]

Test file: `tests/YobaBox.E2ETests/ConfigResolvePriorityTests.cs`

- [x] Create bindings A(timeout=30), B(timeout=15), C(timeout=5) with different tag specificity
- [x] `project:kpvotes` → 30
- [x] `project:kpvotes,service:kpvotes-bot` → 15
- [x] `project:kpvotes,service:kpvotes-bot,env:staging` → 5
- [x] `project:kpvotes,service:kpvotes-web` → 30 (fallback to project-level)
- [x] `project:other` → 30 (0 matching tags → lowest Id wins)

---

## Phase 10: ApiKey scope enforcement tests [NEW]

Test file: `tests/YobaBox.E2ETests/ApiKeyScopeTests.cs`

- [x] Key with only `config:read` → `POST /api/config` → 403, `DELETE /api/config` → 403
- [x] Key with only `config:write` → `GET /api/config` → 403
- [x] Key with only `logs:ingest` → `GET /api/config` → 403
- [x] Key with `logs:ingest` → `POST /ingest/clef` → 200
- [x] Key with `admin` → `GET /api/auth/validate` → 200 (any valid key), `GET /api/config` → 403
- [x] Revoked key → `GET /api/auth/validate` → 401

---

## Phase 11: Workspace foundation [NEW]

Goal: workspace organizational layer above projects. Foundation for DB isolation and multi-user.

### 11.1 — Workspace model + migration

- [ ] `YobaBox.Core/Models/Workspace.cs` — record: Key, Name, Description, CreatedAt
- [ ] `Project.WorkspaceKey` — FK to Workspace
- [ ] `YobaBox.Core/Data/Migrations/M008_Workspaces.cs` — create `Workspaces` table, add `WorkspaceKey` column to `Projects`. Seed `$system` workspace.
- [ ] `YobaBox.Core/Data/YobaBoxDb.cs` — add `ITable<Workspace>`, configure mapping

### 11.2 — Workspace admin UI

- [ ] `YobaBox.Web/Pages/Admin/Workspaces.cshtml` + `.cs` — list, create, delete workspaces
- [ ] `YobaBox.Web/Pages/Admin/WorkspaceDetail.cshtml` + `.cs` — workspace projects + users
- [ ] Project create form: add Workspace selector

### 11.3 — Seed data

- [ ] M004/M008: `$system` workspace + `$system` project in it

---

## Phase 12: User + WorkspaceMember [NEW]

Goal: multi-user auth with workspace-level roles.

### 12.1 — Models + migration

- [ ] `YobaBox.Core/Models/User.cs` — record: Id, Username, PasswordHash, CreatedAt
- [ ] `YobaBox.Core/Models/WorkspaceMember.cs` — record: UserId, WorkspaceKey, Role (Admin|Member|Viewer)
- [ ] M009: `Users` + `WorkspaceMembers` tables

### 12.2 — Auth integration

- [ ] Login: validate username/password, set cookie with UserId + WorkspaceKey claims
- [ ] Authorization: check WorkspaceMember role for workspace-scoped pages
- [ ] ApiKey: stays per-project as-is

### 12.3 — Workspace user management UI

- [ ] `/admin/workspaces/{key}/users` — list members, invite, change role, remove

---

## Phase 13: LogDb per-project isolation [NEW]

Goal: separate SQLite per project for log storage.

### 13.1 — LogDbFactory

- [ ] `YobaBox.Log.Core/Data/LogDbFactory.cs` — `GetLogDb(projectKey)` → opens/creates `data/logs/{projectKey}.db`
- [ ] Auto-migration: `CREATE TABLE IF NOT EXISTS LogEntries (...)` on first open
- [ ] Interface: `ILogDbFactory` with `(projectKey, serviceKey?)` for future per-service

### 13.2 — Ingestion + query

- [ ] Ingestion: `X-Service-Key` → lookup Service → WorkspaceKey? No, ProjectKey → LogDbFactory(projectKey)
- [ ] KQL query: accept `projectKey`, open LogDb via factory
- [ ] Live tail SSE: per-project polling
- [ ] Remove `LogDb` from core DI — LogDbFactory replaces it

### 13.3 — UI updates

- [ ] `/ui/logs/{projectKey}` — Razor page, projectKey from path
- [ ] `/ui/logs/{projectKey}/{svcKey}` — page shell (no per-service DB yet)

---

## Phase 14: ConfigDb per-workspace isolation [NEW]

Goal: separate SQLite per workspace for config storage. `ws` tag mandatory.

### 14.1 — ConfigDbFactory

- [ ] `YobaBox.Config/Data/ConfigDbFactory.cs` — `GetConfigDb(workspaceKey)` → `data/config/{workspaceKey}.db`
- [ ] Auto-migration: `CREATE TABLE IF NOT EXISTS ConfigBindings (...)`
- [ ] ConfigBinding model stays in YobaBox.Core (shared, but table lives in workspace DB)

### 14.2 — Resolve pipeline update

- [ ] Remove `ConfigBindings` from `YobaBoxDb`
- [ ] `ConfigApi`: accept `workspaceKey` in path, use ConfigDbFactory
- [ ] ResolvePipeline: load bindings from workspace's ConfigDb
- [ ] `ws` tag mandatory — create/update validates presence of `ws:{workspaceKey}`

### 14.3 — UI

- [ ] `/ui/config/{workspaceKey}` — Razor page, workspaceKey from path

---

## Phase 15: /api and /ui prefix routing [NEW]

Goal: clean separation of API and UI URL namespaces.

### 15.1 — UI prefix

- [ ] Move all Razor Pages to `/ui` via `@page` directives + conventions in Program.cs
- [ ] `/ui/login`, `/ui/dashboard`, `/ui/dashboard/{projectKey}`, `/ui/logs/{projectKey}`, `/ui/config/{workspaceKey}`
- [ ] `/ui/admin/projects`, `/ui/admin/workspaces`

### 15.2 — API prefix

- [ ] Group all API endpoints under `/api` via `MapGroup("/api")`
- [ ] `/api/auth/validate`, `/api/auth/login`, `/api/auth/logout`
- [ ] `/api/ingest/clef`, `/api/events/raw`
- [ ] `/api/logs/{projectKey}/query`, `/api/logs/{projectKey}/services`, `/api/logs/{projectKey}/live-tail`
- [ ] `/api/config/{workspaceKey}/resolve`, `/api/config/{workspaceKey}/bindings`

### 15.3 — Backward compat (optional)

- [ ] Redirect old paths or keep until E2E tests updated

---

## Phase 17: Sharing module [DONE]

- [x] `ShareLink` model + M009 migration
- [x] AES-salted `ValueMasker`, `FieldMaskingPolicy`, `MaskMode`
- [x] `TsvExporter`
- [x] POST `/api/share` + public `/ui/share/{token}` page + `/api/share/{token}/tsv`

## Phase 18: Retention [DONE]

- [x] `RetentionPolicy` model (per-project override) + M010 migration
- [x] `RetentionService` BackgroundService — hourly sweep of `LogEntries` and expired `ShareLinks`
- [x] `/ui/admin/retention` page — set/clear per-project policies

## Phase 19: Tracing/Spans [DONE]

- [x] `SpanRecord` + Spans table in per-project LogDb (auto-migration in factory)
- [x] `/ui/logs/{projectKey}/traces` — trace list with root span, duration, status
- [x] `/ui/logs/{projectKey}/traces/{traceId}` + `_TraceWaterfall.cshtml` — recursive waterfall view

## Phase 20: Cleanup [DONE]

- [x] `doc/spec.md` ported from vault, updated under workspaces / factories / /ui-/api / User-model
- [x] Dead refs removed (`admin.js`, empty Program.cs Phase 1 if, ConfigBindings from YobaBoxDb)
- [x] `ts/config.ts` filled (was 6-line stub)
- [x] User-table login via AdminBootstrapper; M008 no longer seeds empty admin
- [x] `WorkspaceAdmin/Member/Viewer` policies + `WorkspaceRoleRequirement` handler

## Phase 16: Data module rework [BLOCKED]

Goal: fix DataTable / DataApi design. Requires user clarification.
Current implementation is wrong — to be revisited.

- [ ] Clarify: where do user data tables live? Separate `data/databases/{name}.db` per project?
- [ ] Clarify: API shape — PostgREST-compatible? CRUD?
- [ ] Clarify: how DataTables map to projects/workspaces?

### 8.6 — Create DataTable for votes cache (after log + config flows)

- [x] Navigate `/admin/projects/kpvotes/data` (new page or section)
- [x] Create table `votes_cache` with columns: id TEXT PK, film_uri TEXT NOT NULL, vote_value TEXT, cached_at TEXT
- [x] Set Read=true, Write=true, Delete=true → submit → table appears in list
- [x] `GET /api/data/votes_cache` with X-Api-Key → 200 `[]` (empty)
- [x] Build `/admin/projects/{key}/data` page + `/api/data/{table}` endpoint

### 8.7 — Dashboard (after DataTable)

- [x] Navigate `/dashboard` → project card "KpVotes" visible with services
- [x] Shows 2 services: kpvotes-net (Cron), kpvotes-ts (PoC)
- [x] Navigate `/dashboard/kpvotes` → service list with health badges
- [x] Build `/dashboard/{project}` page

Test file: `tests/YobaBox.E2ETests/ApiKeyScopeTests.cs`

- [ ] Create key with only `config:read`
- [ ] `GET /api/config?...` → 200
- [ ] `POST /api/config` → 403
- [ ] `DELETE /api/config?...` → 403
- [ ] `POST /ingest/clef` → 403
- [ ] `GET /api/auth/validate` → 200 (no scope needed)
