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

## Phase 2: Port yobalog Log [DONE]

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
- [-] Remote auth: run against remote instance → validates, caches, 401 on invalid — SKIPPED, нет второго инстанса. Логика покрыта unit-тестами через `RemoteAuthHandler`

---

## Phase 3: Test parity with yobaconf + yobalog [DONE]

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

## Phase 4: Dashboard + /admin route fix [DONE]

Goal: fix 404 on /admin, create dashboard page.

### 4.1 — Fix /admin route

- [x] `YobaBox.Web/Pages/Admin/Index.cshtml` — это страница `/ui/admin/sys` (sys overview) после Phase 24, не редирект
- [x] `YobaBox.Web/Pages/Admin/Index.cshtml.cs` — имеет `[Authorize]` (унаследовано от `_AdminLayout` policy chain)

### 4.2 — Dashboard page

- [x] `YobaBox.Web/Pages/Dashboard/Index.cshtml` — workspace status на `/ui/{ws}`, карточки проектов + сервисы. Видимая после Phase 21 IA rework.
- [x] `YobaBox.Web/Pages/Dashboard/Index.cshtml.cs` — `[Authorize]`, queries Projects + Services from YobaBoxDb
- [x] `YobaBox.Web/Pages/Index.cshtml` — `OnGetAsync` редиректит на workspace status (с учётом `UiSettings.DefaultHome`)
- [x] `YobaBox.Web/Pages/Shared/_Layout.cshtml` — sidebar содержит workspace + projects nav (sidebar IS the dashboard nav)

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

## Phase 7: Remaining UI + polish [DONE]

### 7.1 — Layout fixes

- [x] `Pages/Shared/_Layout.cshtml` — sidebar links: workspace switcher + projects + sysadmin button — все работают
- [x] Footer: `APP_VERSION` + `GIT_SHORT_SHA` из env (`data-testid="footer-version"`)

### 7.2 — Auth polish

- [x] Username в nav (`data-testid="nav-username"` → ссылка на `/ui/me/account`)
- [x] Sign-out form в nav (`data-testid="nav-signout"` → POST `/api/auth/logout`)

### 7.3 — E2E test expansion

- [x] `tests/YobaBox.E2ETests/DashboardTests.cs` — workspace status renders projects/services
- [x] `tests/YobaBox.E2ETests/LogsPageTests.cs` — KQL input + interactions
- [x] `tests/YobaBox.E2ETests/ConfigPageTests.cs` — экзист, тесты skipped pending UI rework (см. test skip comments)

---

## Phase 8: E2E — KpVotes real-world flow [DONE]

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

## Phase 9: Config resolve priority tests [DONE]

Test file: `tests/YobaBox.E2ETests/ConfigResolvePriorityTests.cs`

- [x] Create bindings A(timeout=30), B(timeout=15), C(timeout=5) with different tag specificity
- [x] `project:kpvotes` → 30
- [x] `project:kpvotes,service:kpvotes-bot` → 15
- [x] `project:kpvotes,service:kpvotes-bot,env:staging` → 5
- [x] `project:kpvotes,service:kpvotes-web` → 30 (fallback to project-level)
- [x] `project:other` → 30 (0 matching tags → lowest Id wins)

---

## Phase 10: ApiKey scope enforcement tests [DONE]

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

## Phase 21: IA rework — workspace-first URLs + project tabs [DONE]

Goal: replace `/ui/dashboard/...`, `/ui/admin/...`, `/ui/logs/{key}`, `/ui/config/{ws}` with a single workspace-first scheme `/ui/{ws}/{key}/...`. Source plan: `~/.claude/plans/proud-waddling-naur.md`.

Session output: 11 commits, 214 unit/integration pass, 29 E2E pass + 10 skipped (legacy Editor UI flow), 0 fail. See `doc/user-stories.md` for the resulting flows.

### 21.1 — Foundation

- [x] `src/YobaBox.Web/Routes.cs` — static helper with `Workspace(ws)`, `Project(ws,k)`, `ProjectLogs/Traces/Config/Settings`, `SharedConfig(ws)`, `Sys()`, etc.
- [x] `src/YobaBox.Config/ResolvePipeline.cs` — subset semantics (`binding.tags ⊆ request.tags`, rank by `|binding.tags|`, ties throw `AmbiguousConfigException`)
- [x] `src/YobaBox.Config/AmbiguousConfigException.cs` — new exception with candidate IDs
- [x] `src/YobaBox.Config/ConfigApi.cs` — auto-injects `ws:{workspaceKey}` into resolve request, returns 409 on ambiguity
- [x] `src/YobaBox.Web/Pages/Config/Preview.cshtml(.cs)` — uses `ResolveDetailed`, displays ambiguity in red
- [x] `tests/YobaBox.Tests/Config/ResolvePipelineTests.cs` — 13 unit tests for the new semantics
- [x] `tests/YobaBox.E2ETests/ConfigResolvePriorityTests.cs` — `AssertNotFound` for "no match" (was buggy "first by Id")

### 21.2 — Layout V2

- [x] `_Layout.cshtml` rewritten: workspace switcher moved into sidebar header (always visible), 4 top-level items (Status / Logs(all) / Shared config / Workspace), flat project list, minimal top-bar (brand + sysadmin icon + username + signout)
- [x] No health dots, no pinning, no Tasks placeholder in sidebar (per "явное > неявное")
- [x] `Pages/Shared/_ProjectTabs.cshtml` — 3-tab strip (Logs · Config · Settings), Data tab feature-flagged
- [x] `Pages/Index.cshtml(.cs)` — server-side `Redirect(Routes.Workspace(currentWs))`, no meta-refresh, no cookie magic
- [x] `Pages/Dashboard/Project.cshtml(.cs)` deleted — `/ui/{ws}/{key}` IS the Logs view

### 21.3 — @page directive migration

- [x] All Razor Pages routes migrated to `/ui/{workspaceKey}/[{projectKey}/]...` form (route param names kept as `workspaceKey`/`projectKey` to avoid PageModel churn)
- [x] `NavigationContext.IsProjectRoute()` updated to recognize new patterns
- [x] `WorkspaceSwitchEndpoint` default redirect → `Routes.Workspace(ws)`
- [x] Old `/ui/dashboard/...`, `/ui/admin/...`, `/ui/logs/{key}`, `/ui/config/{ws}` URLs deleted (no 308 redirects per user)

### 21.4 — Routing extras via `AddPageRoute`

- [x] `/ui/{ws}/logs` → `Logs/Index` cross-project mode
- [x] `/ui/{ws}/traces` → `Logs/Traces` cross-project mode
- [x] `/ui/{ws}/{key}/config[/...]` → `Config/Index|Editor|History|Preview` with auto project filter
- [x] `/ui/{ws}/admin` → `Admin/WorkspaceUsers` alias landing

### 21.5 — Editor tag format

- [x] `Config/Editor.cshtml(.cs)` canonicalizes tags to `key:value` (was `key=value`), accepts both on input; validates `ws:{workspaceKey}` instead of `ws=...`

### 21.6 — E2E migration

- [x] All E2E test classes ported to new URLs
- [x] `KpVotesOnboardingTests` rewritten end-to-end against new IA (S-1..S-4 via API + UI)
- [x] Legacy Editor UI flow tests skipped with clear reasons (covered semantically by API-driven tests)
- [x] DashboardTests locators tightened (was too broad — matched sidebar nav too)

### 21.7 — Docs

- [x] `doc/user-stories.md` — source of truth for E2E coverage (S-1..S-13)
- [x] `doc/ui-conventions.md` — canonical daisyUI recipes + htmx/Alpine boundary
- [x] `doc/tasks-mcp/` — bench for future Tasks module: records of plan/memory ops
- [x] `AGENTS.md` — "Recording plan/memory actions" section added

### 21.8 — Follow-ups [→ Phase 25 Polish]

Все 6 пунктов перенесены в Phase 25 (UI navigation polish + validation polish + session-plan items).

---

## Phase 16: Data module rework [Wave 1, 2, 4 DONE; Wave 3 deferred к pet-repo]

Goal: replace local pet-side SQLite files с per-project-per-db remote SQLite через yobabox REST API + auto-migrations. Уйти от mount'ов, ручных backup'ов, copy-paste файлов между машинами.

**Source of truth**: `~/.claude/plans/noble-sniffing-bear.md` — полный session-plan с discovery / critique / решениями. doc/plan.md держит summary + waves.

### Resolved decisions (2026-05-28)

- **Storage**: `data/db/{projectKey}/{dbName}.db` — multiple DBs per project, явно создаются (не auto-create on first touch). WAL mode + `PRAGMA max_page_count` (quota) при создании.
- **API**: REST raw SQL pass-through через `/api/data/{p}/{db}/query|exec|schema`. Не PostgREST, не KQL.
- **Архитектура клиент-сервер**: pet пишет свои POCO + linq2db локально → клиент извлекает скомпилированный parameterized SQL + параметры через `IExpressionQuery.GetSqlQueries(SqlGenerationOptions?)` → `IReadOnlyList<QuerySql>` (multi-statement support для InsertOrReplace/CreateTable; Wave 0.3 PoC verified `ToSqlQuery` который под капотом тот же `GetSqlQueries[0]`, но truncates to one). POST `{statements: [{sql, params: [{name, dbType, clrType, value}, ...]}, ...]}` JSON → **сервер dumb pass-through**: использует `DataConnection.Execute(sql, DataParameter[])` из `LinqToDB.Data` namespace (ADO.NET binding + error wrapping + reader iteration уже встроено). Никакого `MappingSchema` sharing, никакого `SqlStatement` AST. Wave 0.6 re-investigation: `LinqToDB.Remote.*` ничего не даёт для этого паттерна — оно coupled к AST + MappingSchema. Никакого Remote.HttpClient end-to-end (отклонён в Wave 0.4: ships expression tree, требует shared assemblies, не поддерживает cross-request transactions).
- **Транзакции**: НЕ поддерживаем в MVP. KpVotes (`D:\my\prj\KpVotes`) их не использует. Появятся pets с transaction needs → отдельная задача.
- **Маппинг**: per-project-per-db. Один проект может иметь N DataDbs. ApiKey project-level видит все DataDbs данного проекта (нет per-DB allow-list).
- **Auth scopes**: `data:read` (query/list/describe), `data:write` (exec/DML), `data:schema` (apply/create_db/delete_db).
- **Schema management**: DbUp + custom `SqliteHashingJournal`. Pet POST'ит named SQL migration; hash conflict → 409. **Hash canonicalization через `SQL.Formatter` NuGet** — нормализует whitespace/case/CRLF перед `sha256`. Спец формат фиксируется в Wave 1.
- **WAL background checkpoint**: Wave 1 добавляет hosted service который раз в N минут вызывает `PRAGMA wal_checkpoint(TRUNCATE)` per active DB. Иначе `.wal` растёт unbounded на hot writers с короткоживущими connections.
- **Существующая `DataTables` таблица**: НЕ трогаем в M013 (dead schema, удалим cosmetic миграцией позже).
- **MCP**: Wave 4 — shared host `/mcp` через `ModelContextProtocol.AspNetCore` SDK. Закрывает 22.8 Agent surface.

### Waves

#### Wave 0 — Pre-implementation critique gate + linq2db PoC [DONE]
- [x] Snapshot плана как baseline (`noble-sniffing-bear.v1.md`)
- [x] Skeptical critique agent — RED1 hash + RED2 WAL → YELLOW (см. resolved decisions); RED3 transactions → DROPPED из MVP (kpvotes не использует)
- [x] linq2db SQL extraction PoC — `LinqExtensions.ToSqlQuery(query)` верифицирован end-to-end. Артефакт `.tmp/linq2db-poc/`. Для Wave 1 используем `GetSqlQueries()` (multi-statement support)
- [x] Explore `LinqToDB.Remote.HttpClient.Server` — NO-GO для end-to-end (expression tree + shared assemblies + no cross-request transactions). Оставлено в Wave 5+ как option, dispatch hook известен
- [x] Re-investigation `LinqToDB/Remote` (base ns) — ничего полезного для dumb-server модели. Coupled к AST + MappingSchema. **Server использует `DataConnection.Execute(sql, DataParameter[])` из `LinqToDB.Data` namespace** (ADO.NET binding + error wrapping встроены, без AST)
- [x] Зафиксировать в `doc/decision-log.md`

#### Wave 1 — Foundation + APIs [DONE]
- [x] `DataDbFactory.GetDb*` (две функции: `GetConnectionString` для read, `CreateAsync` для write — pattern проще чем LogDbFactory's cached connections), WAL + max_page_count при создании
- [x] `M013_DataDbs` миграция — только новая `DataDbs(ProjectKey, Name)` таблица
- [x] `dbup-sqlite` + `dbup-core` + `SqliteHashingJournal` + `SchemaRunner` (hash pre-check, 409 maps, WithTransactionPerScript для rollback на failure)
- [x] **SqlParserCS NuGet** (вместо SQL.Formatter, который не нормализует identifier case) — AST roundtrip даёт canonical output, ~15 LOC normalizer вместо ~130 char-walker
- [x] DB lifecycle endpoints: `POST/GET /api/data/{p}/dbs`, `DELETE .../{db}` — row removed immediately, file best-effort
- [x] Orphan cleanup hosted service (1 min interval) — RunOncePassAsync internal для тестов
- [x] Schema push: `POST /api/data/{p}/{db}/schema` через SchemaRunner; 200 Applied/AlreadyApplied, 409 Conflict, 400 ParseError/Failed
- [x] Migration history: `GET /api/data/{p}/{db}/migrations` (dedicated endpoint, не raw /query)
- [x] Query: `POST /api/data/{p}/{db}/query` (ExecuteReader → JSON array; null/long/double/string/bool coercion)
- [x] Exec: `POST /api/data/{p}/{db}/exec` (ExecuteNonQuery, PRAGMA deny-list: writable_schema, temp_store_directory, data_store_directory, trusted_schema; SQLITE_FULL → 507)
- [x] **WAL checkpoint background service** (5 min interval, `PRAGMA wal_checkpoint(TRUNCATE)` per known DataDb)
- [x] Request body size limit per endpoint via Request.ContentLength check (1 MB /query, 10 MB /exec). Test для этого Skipped — WebApplicationFactory's in-memory transport не передаёт Content-Length, но real HTTP clients работают
- [ ] Main-instance-only guard для `/api/data/*` — отложено в polish (Phase 25); single-instance deployment не нуждается
- [x] Unit + integration tests: 73 теста (72 pass + 1 documented skip). SqlNormalizer/DataDbFactory/SchemaRunner/DataDbsApi/SchemaApi/QueryExecApi/OrphanCleanupService/WalCheckpointService покрыты

#### Wave 2 — UI rework + dogfooding [DONE для UI, dogfooding отложено в Wave 3]
- [x] `ProjectData.cshtml(.cs)` rewrite: список DataDbs cards + create form + delete buttons. Auth: `WorkspaceAdmin` policy (sysadmin satisfies via Phase 24 cross-cutting handler)
- [x] `ProjectDataDb.cshtml(.cs)` (NEW) — detail page: PRAGMA introspection of tables, paste-migration form (calls SchemaRunner directly through cookie auth, не HTTP), migration history table
- [x] Старый create-table form удалён вместе со старым `DataApi.cs`. `DataTable` model + M005 остаются для backward-compat — drop отдельной cosmetic миграцией позже
- [ ] `KpVotesOnboardingTests.S5_DataRoundtrip` E2E через REST — **отложено в Wave 3** (выполняется одновременно с реальным pet integration; standalone E2E test = тот же fake gate что предыдущая критика критиковала)

#### Wave 3 — Real pet integration [DEFERRED — outside yobabox repo]

Требует модификаций в `D:\my\prj\KpVotes`. Yobabox-side документация — `doc/data-client-pattern.md` (NEW в этом коммите): pattern + ~30 LOC C# snippet thin client wrapper'а.

Чек-лист для KpVotes когда возвращаемся:
- [ ] Конфиг: `YobaBox:Url`, `YobaBox:ApiKey`, `YobaBox:DbName` вместо local connection string
- [ ] Onboarding: создать DataDb через `POST /api/data/{p}/dbs` (один раз вручную через curl)
- [ ] Залить существующие миграции через `POST /api/data/{p}/{db}/schema` (по одной)
- [ ] Заменить `DataConnection` на thin client wrapper из `doc/data-client-pattern.md`
- [ ] Убедиться что нет multi-statement `BeginTransaction`/`Commit` блоков (yobabox их не поддерживает — переписать в idempotent UPSERT)
- [ ] Latency measurement vs local SQLite

E2E dogfooding-тест — пишем после первого успешного pet-rollover, не до (standalone E2E через сам REST API = "fake gate" по прошлой критике).

#### Wave 4 — MCP server [Data tools DONE; Config/Log tools incrementally]

Subsumes 22.8 Agent surface. Design-решения зафиксированы 2026-05-28:

| Question | Decision | Rationale |
|---|---|---|
| Transport | **HTTP** (через `ModelContextProtocol.AspNetCore` 1.3.0 SDK) | yobabox = remote service. stdio не подходит, агент не запускает yobabox локально |
| Auth | **X-Api-Key** (re-use existing middleware) | Те же scopes (data:*, config:*, logs:*) что REST API. Ноль новой auth infra |
| Tools (MVP) | **Все enabled-feature tools** через один `/mcp` endpoint | Скоупов и feature toggle'ов достаточно для разграничения. Tools namespaced (`data.query`, `config.get`). Агент сам решает что вызывать через client-side skills/profiles |
| Discovery | **C# attributes + reflection** | `[McpTool(name = "data.query")]` на методах + auto-register на boot. Идиоматично для .NET |

- [x] `ModelContextProtocol.AspNetCore` 1.3.0 + `Directory.Packages.props`
- [x] `src/YobaBox.Web/Mcp/DataTools.cs` — 7 Data tools (list_dbs/create_db/delete_db/describe_db/schema_apply/query/exec)
- [x] `Program.cs` wire: `AddMcpServer().WithHttpTransport().WithToolsFromAssembly()`, `app.MapMcp("/mcp")` + ApiKey auth
- [x] `[McpServerTool]` + `[McpServerToolType]` reflection-based registration (built into SDK)
- [x] 4 sanity тест через `McpClient` + `HttpClientTransport`: tools/list discovery, create→migrate→exec→query roundtrip, cross-project rejection, denied PRAGMA blocking
- [ ] Config tools — incrementally в `src/YobaBox.Web/Mcp/ConfigTools.cs` (когда понадобится)
- [ ] Log tools (KQL query — sweet spot для agentic sessions с many-calls pattern) — `LogTools.cs`

#### Wave 5+ — Future (out of MVP)
- Server-side **transaction sessions** (если появится pet с нужной семантикой) — POST /tx/begin → token + TTL, X-Tx-Token header, /tx/commit | /tx/rollback. KpVotes не нужны.
- **Batch endpoint** для массовых INSERT'ов (один HTTP, серверная BEGIN/COMMIT, non-query only)
- **Liquibase XML** через `DbUp.Extensions.WithLiquibaseScriptsFromFileSystem`
- **Per-project quota** (агрегат по всем DataDb проекта, не только per-DB)
- `LinqToDB.Remote.HttpClient` end-to-end (full IQueryable on pet side) — если когда-нибудь потребуется, dispatch hook через `configuration` URL segment + `IDataContextFactory<DataConnection>` известен из Wave 0.4 explore
- hot-backup, per-DB OTEL telemetry, FluentMigrator → DbUp consolidation analysis

### Старые DataTables endpoints (Phase 8.6/8.7 historical)

Эти были построены в onboarding flow до rework'а — оставляем `[x]` для истории, но они исчезнут в Wave 2:

- [x] (8.6) `/admin/projects/kpvotes/data` + create table form + `/api/data/{table}` endpoint
- [x] (8.7) `/dashboard` + `/dashboard/{project}` (Dashboard теперь на `/ui/{ws}` после Phase 21 IA rework)

---

## Phase 22: Доперенести остатки yobaconf + yobalog [NEW]

Цель: закрыть последние пробелы относительно источников, чтобы yobaconf/yobalog можно было архивировать. Lightpanda НЕ берём (остаёмся на Playwright + Chromium).

**Идёт ПЕРЕД Phase 23 (settings taxonomy)** — все новые tunable'ы Phase 22 кладутся стандартным `appsettings.json` + `IOptions<T>`. Phase 23 потом перевезёт их **все разом** в L2. Делать частичную миграцию во время Phase 22 — двойная работа.

Работы разбиты на волны. Внутри волны порядок гибкий.

### Wave 1 — Backend ingest (изолировано от UI/IA)

#### 22.1 — ChannelIngestionPipeline `[PORT yobalog/Ingestion/]` [DONE — `e6b24a0`]

- [x] `YobaBox.Log.Core/Ingestion/IIngestionPipeline.cs`
- [x] `YobaBox.Log.Core/Ingestion/IngestionOptions.cs` (ChannelCapacity, MaxBatchSize) — биндится из `Ingestion:*` в `appsettings.json` через `IOptions<T>`. Phase 23 перевезёт в L2.
- [x] `YobaBox.Log.Core/Ingestion/ChannelIngestionPipeline.cs` — per-project bounded channel + writer-loop с batched `BulkCopyAsync` + Publish в `ITailBroadcaster`. `IHostedService` для graceful drain на shutdown.
- [x] `YobaBox.Log.Core/Ingestion/IngestionLog.cs` — `LoggerMessage` partial для AppendBatchFailed/ShutdownTimedOut
- [x] `YobaBox.Log.Core/Observability/ActivitySources.cs` `[PORT yobalog/Observability/Tracing.cs]` — `ActivitySources.Ingestion` + `.Retention` для OTel span'ов
- [x] `LogApi.IngestClefAsync` + `SeqIngestAsync` — заменить прямой `BulkCopyAsync` на `pipeline.IngestAsync(projectKey, records, ct)`
- [x] Регистрация в `Program.cs` под `Features:Logging` (singleton + hosted service)

#### 22.2 — SystemLogger direct-to-DB `[PORT yobalog/SelfLogging/]` [DONE — `aac7deb`]

- [x] `YobaBox.Log.Core/SelfLogging/SystemLoggerOptions.cs` — ServiceKey, MinLevel, FlushIntervalMs — биндится из `SelfLogging:*` в `appsettings.json`. Phase 23 перевезёт в L2.
- [x] `YobaBox.Log.Core/SelfLogging/SystemLogger.cs` — `ILogger` записывающий в `IIngestionPipeline` напрямую (без HTTP roundtrip)
- [x] `YobaBox.Log.Core/SelfLogging/SystemLoggerProvider.cs`
- [x] `YobaBox.Log.Core/SelfLogging/SystemLogFlusher.cs` — `IHostedService` для финального flush на shutdown
- [x] Регистрация в `Program.cs` под `Features:Logging` + `Seq:SelfLog:Enabled=true`
- [x] Заменили `Seq.Extensions.Logging` → прямой путь через `SystemLogger`

#### 22.3 — OTLP gRPC ingest `[PORT yobalog/Web/Ingestion + Proto/]` [DONE — `60f1462`]

- [x] `src/YobaBox.Web/Proto/opentelemetry/...` — скопировано всё `.proto`-дерево (logs/v1, trace/v1, common/v1, resource/v1, collector/{logs,trace}/v1)
- [x] `YobaBox.Web.csproj` — `Grpc.Tools` + `<Protobuf Include="...">` + `NoWarn=CS8632`
- [x] `YobaBox.Web/Ingestion/OtlpLogsParser.cs` — OTLP logs → `LogEntryCandidate` (TraceId/SpanId hex в Properties JSON)
- [x] `YobaBox.Web/Ingestion/OtlpTracesParser.cs` — OTLP traces → `SpanRecord` (Events/Links JSON-serialized в существующие колонки)
- [x] OTLP HTTP endpoints (protobuf body):
  - [x] `POST /v1/logs` → `OtlpLogsParser` → `IIngestionPipeline.IngestAsync`
  - [x] `POST /v1/traces` → `OtlpTracesParser` → `Spans.BulkCopyAsync`
- [x] Авторизация через `X-Api-Key` (тот же `ApiKey`-policy что и CLEF ingest)
- [x] OTel-сэмплинг: `opts.Filter` исключает `/v1/logs` и `/v1/traces`

### Wave 2 — Config engine (расширяет L3 ConfigBindings)

#### 22.4 — ETag на resolve `[PORT yobaconf]` [DONE — `d81c0c4`]

- [x] `GET /api/config/{workspaceKey}/resolve` возвращает `ETag: "<hash>"` (sha256 от `Path \0 Value`, не Tags — равные значения кэш-эквивалентны)
- [x] Поддержка `If-None-Match` → 304 без тела
- [x] Клиент кэширует значение между poll'ами

#### 22.5 — Binding soft-delete + версионирование `[ADAPT yobaconf/Bindings + Storage]` [DONE — `989eda0`]

- [x] `ConfigBinding`: `IsDeleted` (bool), `DeletedAt` (DateTime?), `Version` (int, start 1), `ContentHash` (sha256 hex)
- [x] Auto-migration `ConfigDbFactory` — `ALTER TABLE ConfigBindings ADD COLUMN ...` гарды
- [x] `OnPostDelete` в `Config/Index.cshtml.cs` — soft (`UPDATE ... SET IsDeleted=1`), не `DELETE`
- [x] Editor.OnPostSave — `Version + 1` + новая `ContentHash`; no-op edits (тот же ContentHash) не бампают версию
- [x] Список биндингов фильтрует `IsDeleted=0`
- [x] ResolvePipeline игнорирует `IsDeleted=1`
- [x] Undelete = редактирование IsDeleted=1 строки в Editor (история пишет `Undelete` action)
- [ ] UI History: кнопка "Undelete" inline на последней удалённой версии (follow-up — пока в Editor приходить руками)

### Wave 3 — Operational (extends L1 entities)

#### 22.6 — Health-poller для Services `[NEW — design adapted]` [DONE — `954bf68`]

- [x] `YobaBox.Dashboard/HealthPoller.cs` — `BackgroundService`, опрашивает `Services` с `HealthModel=Endpoint`
- [x] Для каждого `Service.Url`: `GET {Url}/health` (auto-append /health если нет) с 5s timeout; 2xx → `Healthy`, 5xx → `Degraded`, иное (timeout/connect/4xx) → `Down`
- [x] Для `HealthModel=Push` — `Health=Down` если `CheckedAt` старше TTL (`PushTtlSeconds`, default 5min)
- [x] Обновляет `Service.Health` + `Service.CheckedAt` в `YobaBoxDb`
- [x] Интервал опроса: 30s default (`Dashboard:HealthPollIntervalSeconds` в appsettings) — Phase 23 перевезёт в L2.
- [x] Регистрация под `Features:Dashboard`

#### 22.7 — CompositeApiKeyStore + ConfigApiKeyStore `[ADAPT yobaconf/Auth/]` [DONE — `ccc74f2`]

- [x] `IApiKeyLookup` интерфейс
- [x] `DbApiKeyLookup` — текущая реализация в одном классе
- [x] `ConfigApiKeyLookup` — читает `Auth:ApiKeys[]` из appsettings (массив `{ Key, ProjectKey, Scopes }`)
- [x] `CompositeApiKeyLookup` — пробует config-store первым, затем DB; config wins on collision
- [x] `ApiKeyAuthenticationHandler` — резолв через `IApiKeyLookup` вместо прямого DB

### Wave 4 — SUPERSEDED

#### 22.8 — Agent surface (S-12) [SUPERSEDED by Phase 16 Wave 4]

Решено в discovery 2026-05-28: **MCP-сервер**, не `/agent/` REST endpoint. Реализация в составе Data модуля Wave 4 (shared MCP host через `ModelContextProtocol.AspNetCore` SDK на единый `/mcp` endpoint, tools от всех модулей собираются через DI). Это закрывает изначальную цель: pet/agent discovery + scoped access. REST `/agent/` отклонён как ненужная альтернатива MCP.

- [-] `/agent/` REST endpoint — DROP. Не строим.
- [→] MCP server — см. Phase 16 Wave 4 (Data MCP tools + shared host).
- [→] Tools от других модулей (config resolve, log ingest, log query) — добавятся в shared host инкрементально когда понадобятся; зарезервированный pattern.

### Что НЕ берём из источников

- **Lightpanda** (Playwright + Chromium остаются)
- yobalog `WorkspaceBootstrapper` / `WorkspaceSchema` (заменено `LogDbFactory.CreateSchema`)
- yobalog `IShareLinkStore` / `IKqlShareLinkStore` (тонкий `ShareLink` в `YobaBoxDb` уже достаточен)
- yobaconf `IBindingStoreAdmin` интерфейс (используем `IConfigDbFactory` + прямой linq2db)
- yobaconf `SqliteSchema.cs` (FluentMigrator для main DB + auto-migrate в фабриках)
- `TagSet` typed VO — оставляем строку `Tags` (резолв уже починен под subset-семантику; рефактор сейчас принесёт больше боли, чем пользы)

---

## Phase 23: Settings taxonomy (L1/L2/L3) [NEW]

Источник правды: `doc/settings-taxonomy.md`. Цель — генерик `Settings` таблица + рефлексивный UI вместо точечных таблиц вроде `RetentionPolicies` И вместо разбросанных `IOptions<T>`-секций в `appsettings.json`. После этой фазы любая новая «крутилка» добавляется как `[Setting]`-property на C#-record без миграций.

**Идёт ПОСЛЕ Phase 22.** Все tunable'ы введённые в 22 (`IngestionOptions`, `SystemLoggerOptions`, `Dashboard:HealthPollIntervalSeconds`) перевозятся в L2 разом в одной фазе вместе с уже существующими (`RetentionOptions`). Частичная миграция «по одной» — двойная работа, поэтому мы её избегаем.

### 23.1 — Foundation: Settings table + resolver [DONE]

- [x] `M011_Settings.cs` миграция: `Settings(Scope, ScopeKey, Path, Type, Value, UpdatedAt, UpdatedBy)`, PK `(Scope, ScopeKey, Path)` — inline PK для SQLite compatibility
- [x] `YobaBox.Core/Settings/Scope.cs` — enum `System/Workspace/Project/Service/User/Membership`
- [x] `YobaBox.Core/Settings/SettingAttribute.cs` — `TopLevel`, `Key`, `Description`, `IsSecret`
- [x] `YobaBox.Core/Settings/ISettingsResolver.cs` + реализация:
  - `GetAsync<T>(deepestScope, deepestScopeKey)` — default-init T, для каждого `[Setting]`-свойства ходит вверх до `TopLevel`, первое найденное побеждает
  - `SetAsync<T>(scope, scopeKey, newValues, oldValues, updatedBy)` — пишет только изменённые свойства
  - `ResetAsync<T>(scope, scopeKey, propertyName)` — удаляет override на данном уровне
- [x] Encryption для `IsSecret`-свойств — через `ISecretEncryptor` на read/write пути; в `Value` хранится зашифрованный blob
- [x] Sysadmin claim source — bootstrap admin (username matches `Admin:Username` из appsettings); `SysAdmin` policy + `YobaBoxClaims.IsSysAdmin`

### 23.2 — Reflection-based UI [DONE]

- [x] `Pages/Shared/_SettingsForm.cshtml` partial — принимает `SettingsFormModel(Type RecordType, Scope CurrentScope, string ScopeKey, …)`:
  - Резолвит запись через `ISettingsResolver`
  - Reflects каждое `[Setting]`-property: type → input (number/text/select/checkbox/password+reveal/textarea)
  - Permission gate — скрывает свойства где `TopLevel > currentScope`
  - Submit — diff против резолвнутых значений, пишет только изменения в `Settings` при `currentScope`
- [x] `_SettingsFormFields.cshtml` — fields-only sub-partial для defaults-страниц с несколькими секциями
- [x] `Settings/SettingsFormBinder.cs` — reflection-based form → typed record binding (типизирует form values по property type)

### 23.3 — Mass migration: все tunable'ы из appsettings + RetentionPolicies → L2 [DONE]

Одна фаза перевозит **всё накопленное** на L2 одним проходом.

**Драп точечных хранилищ:**

- [x] `M012_DropRetentionPolicies.cs` — `DROP TABLE RetentionPolicies` (данные не переносим, дефолт стартует заново)
- [x] Удалить `Models/RetentionPolicy.cs` + маппинг в `YobaBoxDb`
- [x] Удалить `Pages/Admin/Retention.cshtml(.cs)` — заменяется leaf-страницей `LogSettings` ниже
- [x] Удалить из `appsettings.json` секции: `Retention:*`, `Ingestion:*`, `Dashboard:HealthPollIntervalSeconds`. SelfLogging оставлен (Seq sink — env-owner)

**Settings-record'ы:**

- [x] `YobaBox.Core/Settings/LogSettings.cs` — `RetentionDays` (TopLevel=Workspace, default 7), `SystemRetainDays` (TopLevel=System, default 30), `RunIntervalSeconds` (TopLevel=System, default 3600)
- [x] `YobaBox.Core/Settings/IngestionSettings.cs` — `ChannelCapacity` (10000), `MaxBatchSize` (1000) — оба TopLevel=System
- [x] `YobaBox.Core/Settings/DashboardSettings.cs` — `HealthPollIntervalSeconds` (30), `RequestTimeoutSeconds` (5), `PushTtlSeconds` (300) — все TopLevel=System
- [skip] SelfLoggingSettings — Seq self-log остался в appsettings (env-owner credentials)

**Переключение consumer'ов:**

- [x] `RetentionService`: читает `LogSettings` per project через `ISettingsResolver`
- [x] `ChannelIngestionPipeline`: snapshots `IngestionSettings` в `StartAsync`
- [x] `HealthPoller`: читает `DashboardSettings` каждую итерацию

**Удалить пустые option-классы:**

- [x] `RetentionOptions.cs`, `IngestionOptions.cs`, `HealthPollerOptions.cs` — удалены

**Что остаётся в appsettings.json (env-owner, не L2):**

- `ConnectionStrings:YobaBox` — connection string
- `Admin:Username`, `Admin:PasswordHash` — bootstrap admin
- `YobaBox:MasterKey` (env `YOBABOX_MASTER_KEY`) — master key
- `Features:Config/Logging/Data/Dashboard` — feature gates
- `Auth:Mode/RemoteUrl/RemoteApiKey` — auth mode + remote
- `OpenTelemetry:Enabled/OtlpEndpoint/ServiceName` — OTel sink
- `Seq:SelfLog:Enabled/ServerUrl/ApiKey/ServiceKey` — Seq sink credentials (если оставляем Seq как fallback)

### 23.4 — Auto-generated defaults pages [DONE]

- [x] `Pages/Admin/SysDefaults.cshtml` — `/ui/sys/defaults`, `[SysAdmin]` policy. Reflection находит все `[Setting]` с `TopLevel >= System`, группирует по типу записи, рендерит `_SettingsFormFields` для каждой группы
- [x] `Pages/Admin/WorkspaceDefaults.cshtml` — `/ui/{ws}/admin/defaults`, `[WorkspaceAdmin]`. То же при `TopLevel >= Workspace`, `scope=workspace`
- [x] `Pages/Admin/ProjectLogSettings.cshtml` — `/ui/{ws}/admin/projects/{key}/log`. Leaf-страница `LogSettings` при `scope=project` — каноническая edit-страница группы

### 23.5 — Admin area separation [DONE]

- [x] `Pages/Shared/_AdminLayout.cshtml` — отдельный layout с admin-sidebar (warning-tinted topbar)
- [x] `Pages/Shared/_AdminSidebar.cshtml` — URL-aware admin nav, секции:
  - WORKSPACE: Info / Members / Projects / Shared config / Defaults
  - PROJECT (когда в project-admin): Info / Log settings / Services / API keys / Data tables
  - SYSTEM (если sysadmin): Workspaces / Users / Defaults
  - ACCOUNT: Profile / Security / Preferences
- [x] Route миграция (см. `doc/settings-taxonomy.md` секция 4):
  - `/ui/{ws}/{key}/settings` → `/ui/{ws}/admin/projects/{key}/info` (ProjectDetail)
  - `/ui/{ws}/{key}/data` → `/ui/{ws}/admin/projects/{key}/data` (ProjectData)
  - `/ui/sys/retention` → `/ui/sys/defaults` (auto-gen)
  - NEW: `/ui/{ws}/admin/defaults`, `/ui/{ws}/admin/projects/{key}/log`
- [x] Удалить `Settings` и `Data` tabs из `_ProjectTabs`; добавить "→ Admin" link (testid `proj-admin-link`) справа сверху на project page
- [x] `_AdminLayout` требует `[Authorize]`; admin-страницы enforce policy (`SysAdmin` / `WorkspaceAdmin`)

### 23.6 — Self-service `/ui/me/*` [partial DONE]

- [x] `Pages/Me/Account.cshtml` — `/ui/me/account` — username + IsSysAdmin badge (read-only)
- [x] `Pages/Me/Security.cshtml` — `/ui/me/security` — смена пароля (форма: old + new + confirm)
- [x] `Pages/Me/Preferences.cshtml` — `/ui/me/preferences` — `UiSettings` (theme, defaultHome) через `_SettingsForm`
- [x] `YobaBox.Core/Settings/UiSettings.cs` — `Theme` (Dark/Light/System), `DefaultHome` (Status/LastProject/AllLogs), оба TopLevel=User
- [x] Применение `UiSettings.Theme` к `data-theme` в `_Layout` + `_AdminLayout` (через DI-resolved injection)
- [→] Применение `DefaultHome.LastProject` — перенесено в Phase 25.3 (нужно MembershipSettings storage)

### 23.7 — Follow-ups [→ Phase 25 Polish]

- Master key rotation CLI → Phase 25.3
- Reserved path prefix validator на `SettingAttribute` → Phase 25.2
- `[SettingsSection]` group attribute → Phase 25.3
- L1 кандидаты-на-перенос-в-L2 (мониторим): пока ничего; шкаф L2 проверяется на будущих фичах

---

## Phase 30: Tasks + Agent Memory modules [DEFERRED]

Goal (original): объединить ведение планов и памяти разных coding-агентов (claude-code, factory droid, opencode, pi) в один MCP-canon store с историей и UI для ревью. Два модуля — `YobaBox.Tasks` (project-plan tree + session-plan blob) и `YobaBox.Memory` (общая 4-типовая память с agent-tag).

**Status:** deferred pending validation experiments. См. `doc/decision-log.md` (запись 2026-05-27) и `doc/proposals/tasks-memory-modules/{proposal,critique}.md`.

### Что делать перед возвращением к фазе

- [ ] **Эксперимент Stop-hook.** Hook на claude-code пишет mock-запись об edit'ах `doc/plan.md` и `~/.claude/plans/*.md`. 2 недели наблюдения. Compliance <70% → модуль строить нельзя, агент не вызовет MCP-tool.
- [ ] **Честный bench.** Одну и ту же задачу прогнать через claude-code / factory droid / opencode / pi. Сравнить artifacts. Понять нужна ли унификация.
- [ ] **Список реальных queries** к plan'у — если все они отвечает `git log` + `grep`, модуль не нужен.
- [ ] **Workflow на двух машинах** — laptop правит plan.md, desktop pull'ит, выяснить что ломается. До дизайна, не после.

### Альтернативы для оценки (по убыванию value)

1. Ничего не делать. plan.md в git, Stop-hook auto-commit, git resolve'ит конфликты.
2. Stop-hook + git auto-commit — 50 строк bash.
3. Готовый `mcp-server-memory` (Anthropic) + git для plan'ов — 0 строк.
4. Минимальный `Notes` модуль: один тип, markdown+frontmatter+tags, git как history.
5. GitHub Issues + sub-issues с gh CLI MCP-обёрткой.

После экспериментов — решить заново: пропозал, минимальный Notes-модуль, или ничего.

### Phases предшественники (порядок не зафиксирован)

- Phase 16 (Data module rework) разблокируется и идёт первым — это prerequisite для kpvotes.
- kpvotes интеграция через Data — следующий dogfooding loop.
- Tasks/Memory — после kpvotes, если эксперименты выше показали смысл.

---

## Phase 24: Admin/Account IA cleanup [DONE]

После Phase 23 admin-страницы были разбросаны по разным URL-пространствам: `/ui/{ws}/admin/*` для workspace, `/ui/sys/*` для sysadmin, `/ui/me/*` для self-service. Сайдбар повторял ту же структуру с генерик-лейблами "Workspace" / "System" / "Account" — последнее путалось со специальным workspace ключом `$system`.

Phase 24 чистит: все administrative страницы переезжают под единый `/ui/admin/*` префикс. Self-service Account выделен в отдельный layout (свой сайдбар, не admin-warning topbar). Project context в сайдбаре теперь появляется отдельным блоком только когда вы внутри проекта.

### 24.1 — URL унификация под `/ui/admin/*` [DONE]

- [x] Workspace admin: `/ui/{ws}/admin/*` → `/ui/admin/ws/{ws}/*`
  - `/admin` (overview), `/admin/members`, `/admin/projects`, `/admin/projects/{key}/info`, `/admin/projects/{key}/log`, `/admin/projects/{key}/data`, `/admin/defaults`, `/admin/info` (бывший `/admin/settings`)
- [x] Sysadmin: `/ui/sys/*` → `/ui/admin/sys/*`
  - `/ui/admin/sys` (overview), `/ui/admin/sys/workspaces`, `/ui/admin/sys/workspaces/{key}`, `/ui/admin/sys/users`, `/ui/admin/sys/defaults`
- [x] Account: остался `/ui/me/*` (account/security/preferences) — НЕ admin-зона
- [x] `Routes.cs`: `AdminPrefix = "/ui/admin"` константа; все методы возвращающие admin URL обновлены; добавлены `MeProfile()` / `MeSecurity()` / `MePreferences()` / `SysDefaults()` / `WorkspaceAdminDefaults()` / `WorkspaceAdminInfo()`; убран `WorkspaceAdminSettings()`
- [x] Legacy URL без redirects (per memory: no legacy redirects, sole user)
- [x] `IsProjectRoute()` упрощён до проверки route value `projectKey` — legacy path-prefix fallback больше не нужен

### 24.2 — `_AccountLayout` + `_AccountSidebar` [DONE]

- [x] `_AccountLayout.cshtml` — копия `_AdminLayout` без warning-tinted топбара (обычная `bg-base-200`)
- [x] `_AccountSidebar.cshtml` — три пункта: Profile / Security / Preferences + блок "Signed in as {username}"
- [x] `Me/Account`, `Me/Security`, `Me/Preferences` переключены на `Layout = "_AccountLayout"`
- [x] `Account` блок удалён из `_AdminSidebar` (account живёт отдельно)

### 24.3 — Плоский `_AdminSidebar` + контекстный блок проекта [SUPERSEDED by 24.5]

Первая итерация: дерево было отвергнуто; победил плоский подход с контекстным блоком. **Но затем пользователь reframed**: "более явного чем полное дерево, ничего нет — давай делать полное дерево". Контекстный блок проблематичен: непонятно как переключить workspace в админке (выходить в обычный UI?). См. 24.5.

- [x] Workspace-секция — плоский список ссылок (Overview / Members / Projects / Shared config / Defaults / Info)
- [x] Project context-блок появляется ТОЛЬКО когда `path` содержит `/ui/admin/ws/{ws}/projects/{key}/...` (Info / Log settings / Data)
- [x] Server administration секция — только если sysadmin (Overview / Workspaces / Users / Defaults)
- [x] Account-блок убран — он теперь в `_AccountSidebar`
- [x] Лейбл "System" переименован в "Server administration" чтобы не путаться с `$system` workspace
- [x] `_WorkspaceAdminTabs`: "Settings" tab переименован в "Info" + URL обновлён под новый `WorkspaceAdminInfo()`

### 24.5 — Полное дерево в `_AdminSidebar` [DONE]

Reframe: tree сам считается "более explicit чем context-block" в navigation. Memory `feedback-explicit-over-implicit` обновлено соответствующе.

- [x] `INavigationContext.ProjectsByWorkspace` — словарь `wsKey → projects[]` для всех available workspace'ов (sysadmin видит все; member — только свои)
- [x] Корневые узлы: `▼ Workspaces (N)` (все доступные ws) + `▼ Server administration` (sysadmin)
- [x] Workspace-узел: collapsible, открыт если это currentWs. Внутри — Overview / Members / Shared config / Defaults / Info + collapsible `▼ Projects (N)`
- [x] Projects-узел: открыт если workspace currentWs. Внутри — "All projects" + список проектов
- [x] Project-узел: collapsible, открыт если currentProject. Внутри — Info / Log settings / Data
- [x] Используется raw `<details>`/`<summary>` + daisyUI menu styling — без JS, без localStorage. Состояние "open" вычисляется server-side из URL
- [x] $system workspace помечен badge `sys`
- [x] Переключение workspace в админке: разворачиваешь другой workspace → клик Overview → URL содержит другой `workspaceKey` → `CurrentWorkspaceKey` обновляется. Сookie `yb_ws` остаётся прежним (это OK — он для non-admin areas; обновляется обычным workspace switcher'ом)

### 24.4 — E2E + verification [DONE]

- [x] Обновлены пути в 7 E2E файлах: `ProjectDetailTests`, `LoginTests`, `KpVotesOnboardingTests`, `ApiKeyScopeTests`, `ConfigResolvePriorityTests`, `DataTableTests`, `Infrastructure/TestWorkspace`
- [x] 29/29 E2E зелёные (10 skipped pre-existing) — для 24.1-24.4 И для 24.5

### 24.6 — Follow-ups [→ Phase 25 Polish]

См. Phase 25 — sidebar tree state persistence перенесён туда.

---

## Phase 25: Polish [DEFERRED]

Goal: единая точка для всех "пора, но не сейчас" задач. Делается после Phase 16 (Data module) + Phase 17 dogfooding с реальным kpvotes.

**Когда браться**: после того как core functionality стабилизировалась и появляется ощущение "везде немного шершаво". До этого — каждый пункт = "smart-поведение" которое маскирует разработку, см. [feedback_explicit_over_implicit].

### 25.1 — UI navigation polish

- [ ] **Sidebar tree state persistence**. Сейчас каждый клик = full page reload → `<details>` сбрасываются на server-rendered defaults. Варианты:
  - **hx-boost** на `_AdminLayout` body — клики через AJAX, меняется только `<main>`, sidebar остаётся в DOM с `open` атрибутами. Standard htmx pattern.
  - **Alpine + localStorage** — каждый `<details>` получает stable id; Alpine синхронизирует open-state. Работает универсально (для tree в `_Layout` тоже).
  - **Оба** — hx-boost для soft-навигации + Alpine для hard reload.
- [ ] **Editor: auto-add `project:{key}` tag** когда на project Config context (step 8 polish из 21.8)
- [ ] **`/ui/admin/ws/{ws}` proper tabbed landing** — Members + Settings sub-tabs (step 10 polish из 21.8)
- [ ] **Cross-project logs annotation** — annotate rows с project (step 9 polish из 21.8)
- [ ] **Health dots в sidebar** когда Dashboard module имеет реальные данные (из 21.8)

### 25.2 — Validation polish

- [ ] **Reserved-name validation для project keys** (`logs`, `traces`, `config`, `admin`, `projects`, `sys`) — из 21.8
- [ ] **Reserved path prefix validator** на `SettingAttribute` (`auth.*`, `sys.*` → sysadmin-only write) — из 23.7

### 25.3 — Auth/Settings polish

- [ ] **Master key rotation CLI**: `dotnet run -- --rotate-master-key <old> <new>` — перешифровка всех `type=secret` строк в `Settings` + всех `BindingKind=Secret` в `ConfigBindings`. Из 23.7.
- [ ] **`UiSettings.DefaultHome.LastProject`** — нужно `MembershipSettings` (`Scope.Membership` storage). Сейчас fallback в `Workspace(ws)` (см. `Index.cshtml.cs`). Из 23.6.
- [ ] **`[SettingsSection]` group attribute** — если рефлексивная группировка по record-type перестанет хватать. Из 23.7.

### 25.4 — Polish from session plans

- [ ] Items из `~/.claude/plans/proud-waddling-naur.md` "Phase 2: UI polish" (если ещё актуальны на момент возврата к фазе)

---

## Phase 26: Clients SDK consolidation [NEW]

Goal: перенести client libraries yobaconf'а (`YobaConf.Client` .NET + `yobaconf-client-ts`) в yobabox repo, переименовать в yobabox namespace, добавить тесты, расширить под все модули (Config + Data + Log). Modular структура: core SDK + framework integrations.

**Драйвер**: prereq для Phase 27 (agentic pet onboarding) и реального kpvotes-ts integration. Без TS yobadata client сценарий "агент создаёт БД и заливает данные" из агентского tooling'а невозможен.

### State входа

- `D:\my\prj\yobaconf\src\YobaConf.Client` (.NET, 309 LOC, 6 файлов) — `IConfigurationProvider` для MEC. **0 unit-тестов.**
- `D:\my\prj\yobaconf\src\yobaconf-client-ts` (TS, 414 LOC, 4 файла) — standalone SDK с ETag polling. Опубликован `@stdray-npm/yobaconf-client@0.1.0-ci.132`. Только `e2e-runner.ts`, **0 unit-тестов.**
- kpvotes-ts уже использует `@stdray-npm/yobaconf-client` (config) + `@datalust/winston-seq` (logs). Data — local JSON, нужно мигрировать на yobabox Data.

### Architecture (модулярная)

**Принцип**: yobabox — drop-in для существующих экосистем там где это возможно. Чем меньше своих пакетов, тем лучше: pet добавляет URL+key к существующим sinks/providers и работает.

- **Logs**: yobabox `/api/ingest/clef` accepts **CLEF/Seq protocol** (это реальный wire standard). Pet использует `Serilog.Sinks.Seq` / `Seq.Extensions.Logging` / `@datalust/winston-seq` / `pino-seq` — никаких yobabox-specific log clients не пишем.
- **Config**: wire-format **свой** (tag-based resolve API, см. memory [[project-config-design]]). MEC — это .NET DI abstraction, не wire protocol; `YobaConf.Client` оборачивает наш формат в `IConfigurationProvider` для consume через стандартный MEC. Аналога нет в TS/Python — нужны свои клиенты. **Возможный апгрейд**: research compat-endpoint под Spring Cloud Config Server или Consul KV — см. 26.8.
- **Data**: wire-format **свой** (raw SQL pass-through, no standard exists). Свои core клиенты обязательны.

- **Core SDK** (свой, нужен): `YobaBox.Client` (.NET) / `@stdray-npm/yobabox-client` (TS) / `yobabox-client` (Python, future) — auth (ApiKey), HTTP transport, raw methods для Data API (`QueryAsync`, `ExecAsync`), Config API (`ResolveAsync`), и Log ingestion (`IngestAsync` через `/api/ingest/clef`)
- **Framework integrations** (свои, нужны где экосистемы нет):
  - `YobaBox.Client.Config` — MEC integration (порт `YobaConf.Client` — exposes config через стандартный `IConfigurationProvider`)
  - `YobaBox.Client.Data.Linq2Db` — linq2db custom provider (Wave 5+ из Phase 16; reference: `LinqToDB.Remote.HttpClient.Server`)
  - `@stdray-npm/yobabox-client-drizzle` — Drizzle integration (TS Data)
- **НЕ делаем** (используется существующая экосистема через Seq protocol):
  - **Logging .NET** — pets настраивают `Serilog.Sinks.Seq` или `Seq.Extensions.Logging` с URL=yobabox + ApiKey. Yobabox `/api/ingest/clef` accepts Seq protocol. Свои adapter'ы не пишем.
  - **Logging TS** — pets используют `@datalust/winston-seq` (например kpvotes-ts) или `pino-seq`. Тот же endpoint. Свой winston transport не пишем.
  - Если в будущем появится pet на языке без Seq sink — может потребоваться minimal yobabox-specific sink, но это reactive по реальной потребности.

### Phasing

#### 26.1 — Move existing yobaconf clients to yobabox repo [DONE — `57c6601`]

- [x] Move `D:\my\prj\yobaconf\src\YobaConf.Client` → `src/clients-net/YobaBox.Client.Config`. Namespace `YobaConf.Client` → `YobaBox.Client.Config`. PackageId `YobaBox.Client.Config`. Classes renamed: YobaConfConfiguration{Provider,Source,Options,Extensions} → YobaBoxConfig{Provider,Source,Options,Extensions}. Extension method `AddYobaConf` → `AddYobaBoxConfig`.
- [x] Move `D:\my\prj\yobaconf\src\yobaconf-client-ts` → `src/clients-ts/yobabox-client`. Package `@stdray-npm/yobaconf-client` → `@stdray/yobabox-client` (GitHub Packages requires scope = github owner). Classes `YobaConfClient` → `YobaBoxConfigClient`, `YobaConfError` → `YobaBoxConfigError`.
- [x] Wire format strings (X-YobaConf-ApiKey header, /v1/conf path) **preserved as-is** — protocol adaptation to yobabox's `/api/config/{ws}/resolve` shape is Phase 26.3.
- [x] Yobaconf репа остаётся как frozen archive — старые опубликованные пакеты не ломаем.
- [x] Update `YobaBox.slnx` — added `YobaBox.Client.Config` + `YobaBox.Client.Config.Tests`.
- [skip] Bun workspace — deferred (каждый TS-клиент собирается независимо со своим node_modules, как было в yobaconf). Добавим если понадобятся shared deps.

#### 26.2 — Unit tests [DONE — `57c6601`]

- [x] `tests/YobaBox.Client.Config.Tests` (.NET) — 14 xunit tests pass: JsonFlattener (8 — object/array/numbers/booleans/null/nested/case-insensitive/empty) + YobaBoxConfigProvider (6 — ctor validation × 2, Load happy path, Optional swallow, non-Optional propagate, WithTag chaining).
- [x] `src/clients-ts/yobabox-client/tests/` — 24 bun tests pass: config.test.ts (16 — ResolvedConfig get/getNumber/getBoolean/toEnv/metadata) + client.test.ts (8 — constructor validation × 3, fetch 200/4xx/optional, headers+query verification).
- [skip] ≥80% coverage target — not measured yet (no coverlet integration в TS, .NET coverlet есть в test csproj но не запущен). Wave 26.4 e2e добавит integration coverage.

#### 26.3 — Core SDK extension (Config + Data raw)

- [ ] `YobaBox.Client` (.NET) — extract auth+HTTP transport из `YobaBox.Client.Config` в общий core. Add `Data` namespace: `QueryAsync`, `ExecAsync` (для `/query` + `/exec` Data API). Add `Log` namespace: `IngestAsync` через `/api/ingest/clef` для use cases где Seq sink не подходит (rare).
- [ ] `@stdray-npm/yobabox-client` (TS) — то же: extract core auth+fetch, add `data` module. Log опциональный raw `ingest()` — основной путь у TS pet'а через `@datalust/winston-seq` напрямую.
- [ ] Existing `Config` сохраняется как specialized provider (MEC), но core SDK даёт raw `ResolveConfigAsync` для use cases без MEC

#### 26.4 — E2E tests

- [ ] `tests/YobaBox.Client.E2ETests` (.NET) — против running yobabox через `WebApplicationFactory<Program>` (in-process) или TestContainers (если нужна real network)
- [ ] `src/clients-ts/yobabox-client/tests/e2e.test.ts` — против running yobabox в `beforeAll` (spawn `dotnet run` process или TestContainers)
- [ ] Покрытие: full round-trip create_db → migrate → exec → query → resolve config → ingest log

#### 26.5 — Versioning + Publishing infra (Cake + GitVersion) [DONE — `7680fd7`, `e28def3`]

- [x] `build.cs` ported from yobaconf `build.cake`: Pack + NuGetPush (publishes to `https://nuget.pkg.github.com/{owner}/index.json` via GITHUB_TOKEN), TsSdkInstall/Typecheck/Lint/Test/Build/Pack/NpmPublish (writes scoped .npmrc with token, publishes to npm.pkg.github.com)
- [x] `GitVersion.yml` уже в yobabox repo идентичен yobaconf (continuous delivery, label=ci on main)
- [x] `.github/workflows/ci.yml` — added `nuget-publish` (triggered на `refs/tags/nuget`) и `npm-publish` (triggered на `refs/tags/npm`) jobs. Existing `publish` (docker) job gated to skip on those tags. Both jobs auth via `GITHUB_TOKEN` + `GITHUB_REPOSITORY_OWNER` (set automatically by GH Actions).
- [x] Verify (CI alias) extended to include TsSdkLint+Typecheck+Test — PR validation теперь покрывает оба языка.
- [ ] Документировать в `doc/clients.md` как pet добавляет dependency: `dotnet add package YobaBox.Client.Config --version 0.x.y-ci.N --source https://nuget.pkg.github.com/{owner}/index.json` / scoped npm registry config. (follow-up — нет блокера для первого publish)
- [x] Debug стадия: GitHub Packages (npm.pkg.github.com + nuget.pkg.github.com) с `0.x.y-ci.N` версиями.

#### 26.6 — kpvotes-ts migration (overlaps с Phase 27 dogfooding)

- [ ] Replace `@stdray-npm/yobaconf-client` → `@stdray-npm/yobabox-client` (config module). API совместим где возможно.
- [ ] **НЕ трогать** `@datalust/winston-seq` — yobalog Seq protocol работает, pet просто меняет URL+key в config'е winston'а. Никакой migration на свой transport.
- [ ] Replace local JSON cache → `@stdray-npm/yobabox-client` data API (`client.data.query/exec`) или `@stdray-npm/yobabox-client-drizzle` если используем Drizzle ORM
- [ ] См. Phase 27 для полного onboarding scenario

#### 26.7 — Publish to npmjs.org / nuget.org (stable)

После 1-2 недель реального usage в kpvotes-ts без incidents:

- [ ] Bump version to `1.0.0`
- [ ] Publish to public registries
- [ ] Update kpvotes-ts deps на public versions

#### 26.8 — Research: Config standards compatibility (research-only, реализация по результату)

- [ ] **Spring Cloud Config Server protocol** — REST `GET /{application}/{profile}` returns key=value tree. Если добавить compat-endpoint на стороне yobabox (`/api/config/spring/{app}/{profile}` → внутри tag query `project:{app}, env:{profile}`) — pet'ы могут использовать готовые `spring-cloud-config-client` (Java/.NET/Node/Python). Минус: tag-flexibility теряется, нужно соглашение application=project, profile=env.
- [ ] **HashiCorp Consul KV** — `GET /v1/kv/{prefix}` flat namespace. Аналогично compat layer возможен, но tag-semantics не лезут.
- [ ] **Etcd KV gRPC** — то же что Consul но gRPC. Самый низкий ROI для pet contexts.
- [ ] **OpenFeature** — narrow scope (feature flags), не подходит для full config.

**Decision after research**: либо (a) добавить compat-endpoint(s) и pet'ы могут выбирать наш client vs стандартный, либо (b) подтвердить что наш tag-based достаточно специфичен — оставить только свои клиенты. Reactive по реальной потребности.

### Repo structure

```
yobabox/
├── src/
│   ├── clients-net/              ← NEW (renamed from src/clients/ implied earlier)
│   │   ├── YobaBox.Client/        — core SDK (auth, HTTP, Data raw, Config raw, Log raw)
│   │   ├── YobaBox.Client.Config/ — MEC IConfigurationProvider (порт YobaConf.Client)
│   │   └── YobaBox.Client.Data.Linq2Db/ — Wave 5+ from Phase 16
│   ├── clients-ts/               ← NEW
│   │   ├── yobabox-client/        — core SDK (TS, bun workspace member)
│   │   └── yobabox-client-drizzle/ — Drizzle integration (Wave 5+)
│   └── clients-py/               ← FUTURE (когда появится первый Python pet)
└── tests/
    ├── YobaBox.Client.Tests/
    ├── YobaBox.Client.Config.Tests/
    └── YobaBox.Client.E2ETests/
```

bun workspace для TS (`yobabox/package.json` с `workspaces`). Python organizationally рядом, но toolchain (poetry/uv/hatch) выбирается когда дойдём.

### Resolved decisions (2026-05-28)

1. **Modular over monolith** — confirmed. Monorepo версионирует все пакеты вместе (Cake + GitVersion), но pet тянет только то что нужно
2. **Bun for TS workspace** — kpvotes-ts споткнулся на bun-lightpanda compat (https://github.com/oven-sh/bun/issues/9911), но мы клиентский кейс — lightpanda не используется. **bun**.
3. **Test framework choice TS** — `bun test` для clients-ts. (kpvotes-ts choice irrelevant — pet выбирает своё.)
4. **Order: точный перенос → потом core SDK extend** — minimal risk path. 26.1+26.2 первыми, потом 26.3.
5. **Drop Logging adapter packages** — Serilog.Sinks.Seq / @datalust/winston-seq работают с yobalog endpoint напрямую. Свои не пишем.

---

## Phase 27: Agentic pet onboarding [NEW]

Goal: pet developer даёт агенту onboarding URL + agent-key. Агент через MCP создаёт project/service/DB/configs, выпускает production ApiKey для pet'а, подключает. После onboarding agent-key экспайрит.

**Драйвер**: kpvotes-ts onboarding scenario — реальный pet, реальный test case для agentic flow. Если работает на нём — паттерн валидирован.

**Prerequisites**: Phase 26 (TS yobabox-client с Data модулем).

### Phasing

#### 27.1 — Agent-key infrastructure

- [ ] `ApiKey` model: добавить `ExpiresAt DateTime?` nullable
- [ ] `M0NN_ApiKeyExpiresAt` миграция: ADD COLUMN с default NULL
- [ ] `ApiKeyAuthenticationHandler` — check expiry, возвращать 401 если `ExpiresAt < UtcNow`
- [ ] `Pages/Admin/Sys/AgentKeys.cshtml(.cs)` — sysadmin блок: list, issue с TTL + scopes, revoke. URL: `/ui/admin/sys/agent-keys`

#### 27.2 — Admin MCP tools

- [ ] `workspace.create_project({workspaceKey, key, name, description?})`
- [ ] `project.create_service({projectKey, key, kind, url?})`
- [ ] `project.create_apikey({projectKey, name, scopes, expiresAt?})` — возвращает raw key (показывается один раз)
- [ ] `project.set_config_binding({workspaceKey, path, value, tags, kind})` — для агента выставить config
- [ ] Auth: только agent-key scope (новый `agent` или существующий `admin`)
- Open fork: один declarative `agent.onboard_pet({...})` tool с polnym payload (idempotent end-to-end) vs набор раздельных tools. **Recommend declarative** — меньше шагов агенту, легче idempotency.

#### 27.3 — Onboarding doc + skill text

- [ ] `doc/agent-onboarding.md` — пошаговая инструкция для агента. Минимальный template: где взять MCP URL, как зарегистрировать в Claude Code (`.mcp.json` entry), какие tools вызывать, в каком порядке
- [ ] `.claude/skills/yobabox-onboard-pet.md` (или эквивалент) — skill text. Trigger: "set up yobabox for this pet" / "onboard pet". Шаги enforced — какие MCP tools в каком порядке
- Open fork: doc location — статичный в репе vs dynamic endpoint `/agent/onboarding/{token}`. **Recommend static** — проще, ниже attack surface.

#### 27.4 — kpvotes-ts dogfooding

- [ ] Создать agent-key с TTL=24h через sysadmin UI
- [ ] Дать агенту `doc/agent-onboarding.md` URL + key
- [ ] Агент: создаёт project `kpvotes`, service `kpvotes-ts`, DB `kpvotes-cache`
- [ ] Агент: applies migration `M001_create_votes_cache` через `data.schema_apply`
- [ ] Агент: batch-INSERT'ит `votes.json` content (~1000 rows) через `data.exec`
- [ ] Агент: выпускает production ApiKey без TTL со scopes `data:read+write`, `config:read`, `logs:ingest`
- [ ] Агент: набирает config bindings через `set_config_binding` для kpvotes (kpUri, votesUri, twitterKeys и т.д.)
- [ ] Pet тулится на yobabox URL + production key. Запускается, читает votes из БД, парсит kinopoisk, постит твиты
- [ ] Lightpanda networking — kpvotes-ts в docker-compose, yobabox на host'е, через `host.docker.internal:5000`. Документировано в onboarding doc.

#### 27.5 — Document gotchas

После dogfooding:
- [ ] Update `doc/agent-onboarding.md` с реальными ошибками агента (если были) — clarifying language
- [ ] Update skill text если агент path-of-least-resistance шёл не туда
- [ ] Compliance check: сколько шагов агент сам сделал vs где требовался human nudge — записать в decision-log

### Open forks (нужны до 27.1)

Сохранены как design choices, требуют отдельного решения когда дойдём до Phase 27:

1. **MCP tool granularity**: declarative `agent.onboard_pet` (одна tool с whole payload) vs набор раздельных (`project.create`, `service.create` и т.д.). Recommend declarative для simpler agent flow.
2. **Agent-key scope model**: новый scope `agent` или просто TTL + существующий `admin`. Recommend TTL + `admin` scope. TTL — отличающий признак.
3. **Onboarding doc location**: static (`doc/agent-onboarding.md`) или dynamic (`/agent/onboarding/{token}`). Recommend static, проще.
4. **Сервисы в проекте**: kpvotes-net + kpvotes-ts оба в `kpvotes` project (per spec — services внутри project'а). → одна `kpvotes` project, два services.

- [x] **`Feature` enum** (`YobaBox.Core.Features.Feature`) заменяет string-based `IsEnabled("Config")` на 27 call sites в 9 файлах. `_ViewImports.cshtml` импортирует namespace без full-qualification в cshtml.
- [x] **CA1848 globally suppressed** в `Directory.Build.targets`. Production code теперь может писать `ILogger.LogInformation(...)` напрямую (попадает в Seq self-log → yobalog → MCP). Hot-path остаётся с `[LoggerMessage]` partial methods deliberately.
- [x] **UI flag-gating** в `_AdminSidebar` и `_ProjectTabs` — Shared config / Log settings / Data link + Logs/Config tabs скрываются когда соответствующий модуль disabled.
- [x] **Workspace switcher dropdown удалён** из main `_Layout`. Был источник проблемы (returnUrl карри'ил старый workspace в URL, cookie-sync middleware откатывал свич). Свич теперь — через admin sidebar workspace tree или прямым URL `/ui/{ws}/...`.
- [x] **Cookie-sync middleware** в `Program.cs` — когда URL имеет `workspaceKey` route value, персистит в `yb_ws` cookie. Membership validation downstream.
- [x] **`WorkspaceSwitchEndpoint.Switch` drops `returnUrl`** — всегда redirects to `Routes.Workspace(newWs)`.

### 25.6 — Follow-ups outside Polish phase

- [ ] **Services placement** — кнопка "Services" сейчас в Logs page header (`/ui/{ws}/{project}` Logs/Index). Логичнее sub-item в admin sidebar под project'ом рядом с Info/Log settings/Data. Move target: `_AdminSidebar.cshtml` + либо отдельная страница `/ui/admin/ws/{ws}/projects/{key}/services`, либо anchor на ProjectDetail.
- [ ] **CA1711** — кейс с enum `Feature` (singular, не `FeatureFlag`). Будущие enums держать в этом паттерне.
