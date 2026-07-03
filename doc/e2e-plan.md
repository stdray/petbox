> **HISTORICAL — frozen on 2026-07-03.** This file is kept as a decision record; the current state lives in the $system boards (tasks/memory/sessions).

# PetBox E2E test plan (Playwright)

Infrastructure copied from `D:\my\prj\yobaconf\tests\YobaConf.E2ETests\Infrastructure\`:
- `KestrelAppHost` — boots Kestrel on port 0, exposes `BaseUrl`, creates temp `data/` dir
- `WebAppFixture` — one-time admin login via `/Login`, saves `storageState`, exposes `NewContextAsync(authenticated)`
- `TraceArtifact` — attaches Playwright trace on test failure
- CDN no longer needs stubbing — htmx + Alpine.js are bundled into `site.js`

Fixture wiring: `AssemblyMetadata("PetBoxWebProjectDir")` in `.csproj` → `KestrelAppHost` resolves `wwwroot/`.

---

## Flow 1: Register KpVotes project from scratch

Simulates a developer onboarding an existing project into PetBox.
KpVotes is a background cron agent (no HTTP server). Uses PetBox for config, logs,
and a simple cache table.

### 1.1 Create Project

```
POST /admin/projects/create
  → redirect /admin/projects
  → page shows "KpVotes" row
```

- Navigate `/admin/projects`
- Click "New Project"
- Fill: Key=`kpvotes`, Name=`KpVotes`, Description=`Kinopoisk → Twitter voting tracker`
- Submit → redirected back, table contains new row with `data-testid="project-row"`

### 1.2 Create Services

Service list inside `/admin/projects/kpvotes`:

**kpvotes-net** (Kind: Cron, the .NET production instance)
- Click "Add Service"
- Key=`kpvotes-net`, Kind=`Cron`, Url=(empty — no HTTP), Version=`10.0.0`
- Submit → row appears with `data-testid="service-row"`

**kpvotes-ts** (Kind: PoC, the TypeScript port)
- Same flow, Key=`kpvotes-ts`, Kind=`PoC`, Url=(empty)

### 1.3 Create ApiKey

Inside `/admin/projects/kpvotes` → Keys tab:
- Click "Issue Key"
- Scopes: `config:read`, `config:write`, `logs:ingest`, `data:read`, `data:write`
- Key is displayed once (masked after) → capture for API calls in 1.3b
- Verify new row in keys table with `data-testid="key-row"`

### 1.3b Verify ApiKey via `/api/auth/validate`

```
GET /api/auth/validate
  X-Api-Key: <captured>
  → 200 { project: "kpvotes", scopes: "config:read,config:write,..." }
```

### 1.4 Add Config bindings

KpVotes uses YobaConf for all runtime settings (TS version already does, .NET to follow).
Navigate `/config?project=kpvotes`:

| Path | Value | Tags |
|------|-------|------|
| `kpvotes/kp-uri` | `https://www.kinopoisk.ru/film/...` | `project:kpvotes` |
| `kpvotes/votes-uri` | `https://www.kinopoisk.ru/film/.../votes` | `project:kpvotes` |
| `kpvotes/interval-minutes` | `120` | `project:kpvotes` |
| `kpvotes/user-agent` | `KpVotes/1.0` | `project:kpvotes` |
| `kpvotes/cache-path` | `data/votes.json` | `project:kpvotes` |
| `kpvotes/twitter/consumer-key` | `***` | `project:kpvotes` (masked in UI) |
| `kpvotes/proxy/host` | `proxy.corp.local` | `project:kpvotes, service:kpvotes-net` |

For each binding:
- Click "Add Binding"
- Fill Path, Value, Tags (auto-suggested tag pills from Project/Service context)
- Submit → row appears in config table

Verify resolve:
```
GET /api/config?path=kpvotes/interval-minutes&tags=project:kpvotes
  → 200 { path: "kpvotes/interval-minutes", value: "120" }
```

### 1.5 Create DataTable for votes cache

Navigate `/admin/projects/kpvotes/data`:
- Click "Create Table"
- Table name: `votes_cache`
- Columns (JSON or form rows):
  - `id` TEXT PK
  - `film_uri` TEXT NOT NULL
  - `vote_value` TEXT
  - `cached_at` TEXT (ISO 8601)
- Check: Read=true, Write=true, Delete=true (the KpVotes agent updates rows)
- Submit → table appears in list

Verify via API:
```
GET /api/data/votes_cache
  X-Api-Key: <captured>
  → 200 [] (empty, table just created)
```

### 1.6 Ingest logs

Simulate KpVotes agent sending CLEF log events:
```
POST /ingest/clef
  X-Api-Key: <captured>
  Content-Type: application/vnd.serilog.clef

  {"@t":"2026-05-23T12:00:00Z","@l":"Information","@mt":"Starting scrape cycle {FilmUri}", "FilmUri":"https://...","ServiceKey":"kpvotes-net"}
  {"@t":"2026-05-23T12:00:05Z","@l":"Information","@mt":"Loaded {Count} cached votes","Count":142,"ServiceKey":"kpvotes-net"}
  {"@t":"2026-05-23T12:00:30Z","@l":"Warning","@mt":"Proxy timeout, retrying","ServiceKey":"kpvotes-net"}
  {"@t":"2026-05-23T12:01:00Z","@l":"Error","@mt":"Twitter API 429 rate limit","ServiceKey":"kpvotes-net"}
```

Verify in Log UI:
- Navigate `/logs?project=kpvotes`
- Service chips show `kpvotes-net` + `kpvotes-ts`
- KQL textarea: `where Level == "Error"` → 1 row (429 rate limit)
- KQL: `summarize count() by Level` → table: Information=2, Warning=1, Error=1
- Click row expand → shows full message + properties

### 1.7 Dashboard

Navigate `/dashboard`:
- Project card "KpVotes" visible with health indicator
- Shows 2 services: `kpvotes-net` (degraded — has errors), `kpvotes-ts` (unknown — no data yet)

Navigate `/dashboard/kpvotes`:
- Service list with health badges
- `kpvotes-net` shows: version=10.0.0, last error="Twitter API 429 rate limit", last checked=<now>
- `kpvotes-ts` shows: status=Unknown

---

## Flow 2: Config resolve with service override

Config engine: most specific tag-set wins.

```
Binding A: path=timeout, value=30,    tags=project:kpvotes
Binding B: path=timeout, value=15,    tags=project:kpvotes,service:kpvotes-bot
Binding C: path=timeout, value=5,     tags=project:kpvotes,service:kpvotes-bot,env:staging
```

| Request tags | Expected result |
|---|---|
| `project:kpvotes` | `30` (A — only match) |
| `project:kpvotes,service:kpvotes-bot` | `15` (B — more specific than A) |
| `project:kpvotes,service:kpvotes-bot,env:staging` | `5` (C — most specific) |
| `project:kpvotes,service:kpvotes-web` | `30` (A — service mismatch, fallback to project-level) |
| `project:other` | 404 (no match at all) |

---

## Flow 3: ApiKey scope enforcement

Create key with only `config:read`:
- `GET /api/config?path=...&tags=...` → 200
- `POST /api/config` → 403
- `DELETE /api/config?...` → 403
- `POST /ingest/clef` → 403
- `GET /api/auth/validate` → always 200 (no scope needed)

---

## Test file structure (in `tests/PetBox.E2ETests/`)

```
tests/PetBox.E2ETests/
├── Infrastructure/
│   ├── KestrelAppHost.cs          [PORT yobaconf]
│   ├── WebAppFixture.cs           [ADAPT yobaconf — no CDN stubs, add ApiKey seed]
│   ├── TraceArtifact.cs           [PORT yobaconf]
│   └── UiCollection.cs            [PORT yobaconf]
├── KpVotesOnboardingTests.cs      [NEW] — Flow 1.1–1.7
├── ConfigResolvePriorityTests.cs  [NEW] — Flow 2
├── ApiKeyScopeTests.cs            [NEW] — Flow 3
└── PetBox.E2ETests.csproj        [adapt — add AssemblyMetadata + PetBox.Web ref]
```

## Verification checklist

- [ ] `dotnet test PetBox.E2ETests` — all green
- [ ] No CDN fetches in Playwright trace (bundled htmx+Alpine.js)
- [ ] Seeds use `X-Api-Key` header, not cookie (no login page in PetBox — auth is key-based for API, browser pages are admin-only with cookie auth)
- [ ] Temp DB cleaned between test runs
- [ ] Playwright traces saved on failure
