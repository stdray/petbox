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
- [x] `YobaBox.Web/Pages/Index.cshtml` `[NEW]` — redirect to /dashboard
- [x] `YobaBox.Web/Pages/Error.cshtml` `[PORT yobaconf/src/YobaConf.Web/Pages/Error.cshtml]`

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
- [ ] `docker build` succeeds
- [ ] `docker run` → `/health` returns 200 within 30s
- [ ] `GET /api/auth/validate` with test key returns 200

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

- [ ] Create binding, resolve with different tag sets → correct override
- [ ] Config UI: create, edit, delete binding
- [ ] ApiKey scopes: config:read can resolve, cannot write; config:write can write
- [ ] Admin: create project, service, key; revoke key

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

- [ ] `YobaBox.Web/Pages/Logs/Index.cshtml` `[ADAPT yobalog/src/YobaLog.Web/Pages/]` — project+service selector. Table: timestamp, level badge, service, message. htmx poll auto-refresh.
- [ ] `YobaBox.Web/Pages/Logs/Detail.cshtml` `[ADAPT yobalog detail/expand]` — row expand: full message, properties, exception.
- [ ] `YobaBox.Web/Pages/Logs/Filters.cshtml` `[ADAPT yobalog filters]` — level, service, text search, date range.
- [x] `YobaBox.Web/ts/logs.ts` `[PORT yobalog/src/YobaLog.Web/ts/admin.ts log sections]` — Alpine.js: filters, auto-refresh, row expand.
- [x] `YobaBox.Log.Core/LogModule.cs` `[NEW]` — registers ingestion, KQL API, FeatureFlags.

### 2.3 — Self-logging via `$system` `[NEW]`

- [x] `YobaBox.Core/Data/Migrations/M004_SeedSystem.cs` — creates `$system` project + api key for self-logging
- [x] `YobaBox.Web/Program.cs` — configure Seq.E.Logging → own `/ingest/clef` when LogModule enabled. Fallback: console.
- [ ] OTel traces → OTLP endpoint

### 2.4 — Remote Auth API `[NEW]`

- [x] `YobaBox.Core/Auth/RemoteAuthHandler.cs` — validates via HTTP to `RemoteUrl/api/auth/validate`. Caches.
- [x] `YobaBox.Core/Auth/AuthConfiguration.cs` — binds `Auth` config section
- [x] Log-only instance config sample

### 2.5 — Verify

- [ ] Ingest CLEF → appears in KQL results
- [ ] KQL: `where Level == "Error" | project Timestamp, Message | take 10` → correct
- [ ] Log UI: table renders, filters work, row expand
- [ ] Self-logging: error in ConfigModule → `$system/yobabox-config`
- [ ] Remote auth: validates against main, caches, 401 on invalid
