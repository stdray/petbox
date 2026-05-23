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

## Phase 5: Port full logs page from yobalog [NEW]

Goal: copy ALL logs UI from yobalog Workspace.cshtml + admin.ts, adapt to yobabox entity model (project/service instead of workspace).

Source: `D:\my\prj\yobalog\src\YobaLog.Web\`

### 5.1 — Event row details + filter chips

- [ ] `EventRowViewModel.cs` `[PORT yobalog]` — IsLive, RenderedMessage (template substitution), LevelBadge, KqlString/KqlDatetime helpers, ToJson(), PropertyForDisplay
- [ ] `EqNeChipsModel.cs` `[PORT yobalog]` — `record (string Field, string KqlLiteral)`
- [ ] `Pages/Logs/_EventRow.cshtml` `[ADAPT yobalog/Shared/_EventRow.cshtml]` — full expandable row: Time/Level/Message/Trace columns, hover chips (✓/✗ for Timestamp, Level, TraceId), message template rendering (`<mark class="msg-sub">`), JSON copy button, exception display, details row with all fields
- [ ] `Pages/Shared/_EqNeChips.cshtml` `[PORT yobalog]` — filter chip partial: ✓ (eq) and ✗ (ne) chips with data-filter-field/op/value attrs

### 5.2 — KQL autocomplete

- [ ] `Pages/Shared/_KqlCompletions.cshtml` `[PORT yobalog]` — htmx fragment: suggestion list with display text + kind, grid layout, data-before/data-after for insertion
- [ ] Wire `/api/kql/completions` endpoint in LogApi (KqlCompletionService already ported)
- [ ] `Logs/Index.cshtml` — add htmx attributes to KQL textarea: `hx-get="/api/kql/completions"`, `hx-trigger="keyup changed delay:250ms"`, `hx-target="#kql-completions"`

### 5.3 — Live tail (SSE)

- [ ] `Logs/Index.cshtml` — live tail toggle checkbox, liveTail hidden form field
- [ ] `ts/logs.ts` — SSE reconnect logic, live-tail banner staging (accumulate events when scrolled away, click-to-flush), `event-live-flash` animation class
- [ ] `LogApi.cs` — SSE endpoint for live tail (or defer if SSE infrastructure too complex)
- [ ] `ts/app.css` — `.event-live` animation keyframe, `.msg-sub` style

### 5.4 — TypeScript: admin.ts [PORT yobalog]

- [ ] `ts/logs.ts` `[ADAPT yobalog/ts/admin.ts]` — sections to port:
  - Local-time rendering (`.local-time` → `YYYY-MM-DD HH:mm:ss.SSS` in local TZ)
  - Button press flash animation (`.btn-flash`)
  - Hotkey toast system (bottom-right notifications)
  - Global `/` focus shortcut → `#kql-textarea`
  - KQL completion (click insert, keyboard navigation, dot re-trigger)
  - Hover filter chips (✓/✗ → inject `where field ==/!= value`)
  - Pin search panel (sticky on scroll, localStorage)
  - Copy-to-clipboard (`data-copy` attribute)
  - Expandable event row (click to toggle `.event-details`)
  - Live-tail banner staging + flush

### 5.5 — Cursor-based infinite scroll

- [ ] `Pages/Logs/_RowsFragment.cshtml` `[ADAPT yobalog/Shared/_RowsFragment.cshtml]` — htmx `intersect once` trigger, sentinel row with loading spinner
- [ ] `Pages/Logs/Index.cshtml.cs` — add cursor property, encode/decode cursor in OnGetAsync, pass NextCursor to fragment view

### 5.6 — Admin layout + nav

- [ ] `Pages/Shared/_Layout.cshtml` — update sidebar nav to match logs dashboard structure, add user/sign-out section
- [ ] Add profile/sign-out link in top nav

---

## Phase 6: Port full bindings page from yobaconf [NEW]

Goal: copy ALL config/bindings UI from yobaconf Bindings pages, adapt to yobabox ConfigBinding model.

Source: `D:\my\prj\yobaconf\src\YobaConf.Web\Pages\Bindings\`

### 6.1 — Bindings list page

- [ ] `Pages/Config/Index.cshtml` `[ADAPT yobaconf/Bindings/Index.cshtml]` — full rewrite:
  - Filter form: key path text input (glob `*` support), tag key facet dropdowns (populated from all bindings)
  - Table: TagSet badges, Key path, Value (masked), Updated timestamp, Edit/Delete actions
  - Delete via POST form with confirmation dialog, preserves filter state
  - "New binding" link → `/Config/Editor`
  - All `data-testid` attributes
- [ ] `Pages/Config/Index.cshtml.cs` `[ADAPT yobaconf/Bindings/Index.cshtml.cs]` — OnGet with tag facet filtering + key query, OnPostDelete, OnPostReveal (AJAX secret reveal)

### 6.2 — Create/edit binding page

- [ ] `Pages/Config/Editor.cshtml` `[ADAPT yobaconf/Bindings/Edit.cshtml]` — full rewrite:
  - Tags textarea (`key=value` per line)
  - Key path input
  - Kind radio (Plain/Secret)
  - Value textarea with JSON validation hint
  - Error/conflict/unknown-tag warnings
  - Save/Cancel buttons
- [ ] `Pages/Config/Editor.cshtml.cs` `[ADAPT yobaconf/Bindings/Edit.cshtml.cs]` — OnGet, OnPost: tag parsing, key path validation, conflict detection

### 6.3 — TypeScript for bindings

- [ ] `ts/config.ts` `[ADAPT yobaconf/ts/bindings-reveal.ts]` — secret reveal via AJAX POST, show for 10s then re-mask, antiforgery token handling
- [ ] `ts/config.ts` `[ADAPT yobaconf/ts/copy-token.ts]` — clipboard copy utility (`data-copy` attribute)
- [ ] `ts/admin.ts` → `ts/site.ts` — entry point importing config + logs modules

### 6.4 — Row partial (in-tree editor)

- [ ] `Pages/Config/Row.cshtml` `[ADAPT yobaconf]` — single binding row fragment for htmx inline editing

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
