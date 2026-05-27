# Settings taxonomy

Single source of truth for "where does setting X live, and how is it edited?". Update this doc *before* adding a new setting that doesn't fit the existing patterns.

## 1. Per-entity catalog

Every configurable thing in YobaBox today, grouped by the entity it belongs to.

### YobaBox instance (process-level, restart-only)

| Parameter | Type | Storage | UI | Permission |
|---|---|---|---|---|
| Admin bootstrap (username + password hash) | string | `appsettings.json` | — | env owner |
| Master key | secret | env `YOBABOX_MASTER_KEY` | — | env owner |
| Connection string, OTel endpoint, Seq self-log | various | `appsettings.json` | — | env owner |
| Feature gates (`Features:Config / Logging / Data / Dashboard`) | bool | `appsettings.json` | — | env owner |

### Workspace

| Parameter | Type | Storage | UI | Permission |
|---|---|---|---|---|
| Key | string (immutable) | `Workspaces` table | create form | ws-admin |
| Name | string | `Workspaces` table | `/ui/{ws}/admin/info` | ws-admin |
| Description | string | `Workspaces` table | `/ui/{ws}/admin/info` | ws-admin |
| Members | rows (role per row) | `WorkspaceMembers` table | `/ui/{ws}/admin/members` | ws-admin |
| Defaults (settings with `TopLevel >= Workspace`) | various | `Settings` (scope=workspace) | `/ui/{ws}/admin/defaults` (auto-gen) | ws-admin |
| Shared config bindings | rows | per-ws `ConfigDb` | `/ui/{ws}/admin/config` | ws-member + `config:write` |
| Tag vocabulary | rows | per-ws `ConfigDb` | `/ui/{ws}/admin/config/tags` | ws-member |

### Project (log stream + services + keys)

| Parameter | Type | Storage | UI | Permission |
|---|---|---|---|---|
| Key | string (immutable) | `Projects` table | create form | ws-admin |
| Name | string | `Projects` table | `/ui/{ws}/admin/projects/{key}/info` | ws-admin |
| Description | string | `Projects` table | `/ui/{ws}/admin/projects/{key}/info` | ws-admin |
| `LogSettings.retention.days` | int | `Settings` (scope=project) | `/ui/{ws}/admin/projects/{key}/log` | ws-admin |
| `LogSettings.retention.sizeBytes` | int | `Settings` (scope=project) | `/ui/{ws}/admin/projects/{key}/log` | ws-admin |
| Services | rows | `Services` table | `/ui/{ws}/admin/projects/{key}/services` | ws-admin |
| API keys | rows | `ApiKeys` table | `/ui/{ws}/admin/projects/{key}/keys` | ws-admin |
| Data tables | rows | `DataTables` table | `/ui/{ws}/admin/projects/{key}/data` | ws-admin |
| Saved queries | rows | `SavedQueries` table | inline in `/ui/{ws}/{key}` (data UI) | ws-member |

### Service

| Parameter | Type | Storage | UI | Permission |
|---|---|---|---|---|
| Key | string (immutable) | `Services` table | create form | ws-admin |
| Url | string | `Services` table | service edit | ws-admin |
| HealthModel | enum (Endpoint / Push) | `Services` table | service edit | ws-admin |

### API key

| Parameter | Type | Storage | UI | Permission |
|---|---|---|---|---|
| Key value | string (shown once) | `ApiKeys` table | create form | ws-admin |
| Scopes | csv enum | `ApiKeys` table | create form (immutable after) | ws-admin |

### User

| Parameter | Type | Storage | UI | Permission |
|---|---|---|---|---|
| Username | string | `Users` table | `/ui/me/account` | self |
| Password hash | string | `Users` table | `/ui/me/security` | self |
| `UiSettings.theme` | enum (dark / light / system) | `Settings` (scope=user) | `/ui/me/preferences` | self |
| `UiSettings.defaultHome` | enum (status / last-project / all-logs) | `Settings` (scope=user) | `/ui/me/preferences` | self |
| `AiSettings.personalKey` (future) | secret | `Settings` (scope=user, type=secret) | `/ui/me/ai-providers` | self |

### (User × Workspace) — membership

| Parameter | Type | Storage | UI | Permission |
|---|---|---|---|---|
| Role | enum (Admin / Member / Viewer) | `WorkspaceMembers` table | `/ui/{ws}/admin/members` | ws-admin |
| `MembershipSettings.lastProject` | string | `Settings` (scope=membership) | implicit (auto-set) | self |
| `MembershipSettings.pinned` (future) | json | `Settings` (scope=membership) | `/ui/me/workspaces/{ws}` | self |

### System defaults (auto-generated)

| Parameter | Type | Storage | UI | Permission |
|---|---|---|---|---|
| Settings with `TopLevel = System` | various | `Settings` (scope=system) | `/ui/sys/defaults` (auto-gen) | sysadmin |

### Config binding (external consumers)

| Parameter | Type | Storage | UI | Permission |
|---|---|---|---|---|
| Path / Value / Tags / Kind | various | per-ws `ConfigDb` | `/ui/{ws}/admin/config` + project view | ws-member + `config:read/write` |

## 2. Storage strategy

Three storage classes — every setting belongs to exactly one.

### L1 — Typed entity tables

Use when the setting has **identity**, **referential integrity** (FK), and **dedicated CRUD UI**. Existing tables: `Workspaces`, `Projects`, `Services`, `ApiKeys`, `Users`, `WorkspaceMembers`, `DataTables`, `SavedQueries`, `ShareLinks`.

The previous `RetentionPolicies` table is being **removed** — it had no FK, no rich CRUD, and is a perfect L2 fit (one int per project). Its data does not survive (project-scoped retention starts fresh at L2).

### L2 — Settings table (generic key-value)

```sql
Settings (
  Scope     TEXT NOT NULL,    -- 'system' | 'workspace' | 'project' | 'service' | 'user' | 'membership'
  ScopeKey  TEXT NOT NULL,    -- 'system'='$'; 'workspace'=ws-key; 'project'=project-key;
                              -- 'service'='{projectKey}/{serviceKey}'; 'user'=userId;
                              -- 'membership'='{userId}:{wsKey}'
  Path      TEXT NOT NULL,    -- 'log.retention.days' | 'ui.theme' | ...
  Type      TEXT NOT NULL,    -- 'int' | 'string' | 'bool' | 'enum' | 'secret' | 'json'
  Value     TEXT NOT NULL,    -- string repr; for secret — base64(cipher+iv+tag)
  UpdatedAt TEXT NOT NULL,
  UpdatedBy INTEGER,
  PRIMARY KEY (Scope, ScopeKey, Path)
);
```

Use for everything **not** entity-shaped: tunable knobs attached to one of L1 entities, with optional cascade up to a per-setting cap.

### L3 — ConfigBindings (per-workspace `ConfigDb`, existing, untouched)

Only for **external consumers** — pet-projects reading via the resolve API with tags. Invariant in `AGENTS.md`: YobaBox itself never stores its own settings in `ConfigBindings`.

### Decision rule (three lines)

1. External consumer reads via resolve API? → **L3** (`ConfigBindings`).
2. Setting has identity / FK / dedicated CRUD UI? → **L1** (typed table).
3. Otherwise → **L2** (`Settings` table).

## 3. Settings catalog — C# records with attributes

The catalog lives **in code**, not in the DB. Each logical group is a `record` with `[Setting]`-annotated properties. The property's default-init value is the absolute fallback.

```csharp
public enum Scope { System, Workspace, Project, Service, User, Membership }

public sealed class SettingAttribute : Attribute
{
    public Scope TopLevel { get; set; } = Scope.Workspace;
    public string Key { get; set; } = string.Empty;
    public string? Description { get; set; }
}
```

### Example: log settings (attached to project)

```csharp
public sealed record LogSettings
{
    [Setting(TopLevel = Scope.Workspace, Key = "log.retention.days")]
    public int RetentionDays { get; init; } = 20;

    [Setting(TopLevel = Scope.System, Key = "log.retention.sizeBytes")]
    public long RetentionSize { get; init; } = 40_000_000;
}
```

`RetentionDays` cascades through Project → Workspace (its `TopLevel` is Workspace, so it stops there).
`RetentionSize` cascades through Project → Workspace → System.

### Resolution algorithm

```
Get<T>(deepestScope, deepestScopeKey):
  result = new T()                                  // record's default-init values
  foreach property P with [Setting]:
    foreach scope in [deepestScope, ...up to P.TopLevel]:
      row = Settings.Find(scope, mapKey(scope, deepestScopeKey), P.Key)
      if row is not null:
        result = result with P set from parse(row.Value, P.Type)
        break
    // if nothing found in any scope, P keeps its record default
  return result
```

`mapKey(scope, deepestScopeKey)` walks up the entity tree:

- `Project → projectKey`
- `Workspace → projects.GetWorkspace(projectKey)`
- `System → "$"`
- `Service → "{projectKey}/{serviceKey}"`, then up to Project
- `User → userId`
- `Membership → "{userId}:{ws}"`, then up to User AND Workspace independently

### UI generation via reflection

One Razor partial `_SettingsForm.cshtml` accepts `(record type, scope, scopeKey)`:

1. Loads the record via the resolver — every property has either a value from DB or its default.
2. For each property, reflects: type, current value, `[Setting]` metadata.
3. Renders an input by type: `int`/`long` → number, `string` → text, `enum` → `<select>` populated from the enum values, `bool` → checkbox, `secret` → password input with reveal, `json` → textarea.
4. Permission gate: hides properties whose `TopLevel` is above `currentScope` (you can't override a system-only setting at the project page).
5. Submit handler writes only changed properties to `Settings` at `currentScope`.

### Defaults pages (auto-generated)

`/ui/sys/defaults` — reflection finds all `[Setting]` properties whose `TopLevel >= System`, groups by record type, renders `_SettingsForm` per group at `scope=system`.

`/ui/{ws}/admin/defaults` — same, with `TopLevel >= Workspace`, `scope=workspace`. `LogSettings.RetentionDays` (TopLevel=Workspace) appears here. `LogSettings.RetentionSize` (TopLevel=System) appears here AND on sys defaults.

`/ui/{ws}/admin/projects/{key}/log` — leaf page: shows **all** `LogSettings` properties at `scope=project`. This is the canonical edit page for the group.

### One record → one canonical leaf page

Each `record FooSettings` has exactly one "ownership" page — the deepest scope where any of its properties live. Higher-scope appearances on Defaults pages are reflective subsets, not the source of truth.

## 4. Admin area — full separation

Admin is a separate visual zone with its own layout (`_AdminLayout`) and sidebar. **No editing of settings inside data pages** (Logs, Traces, Config-view, Status). Settings live exclusively in admin.

### Admin sidebar (URL-aware highlighting)

```
┌──────────────────────────────────────────┐
│ ← Back to {workspace name}               │
├──────────────────────────────────────────┤
│ WORKSPACE                                │
│   Info              /ui/{ws}/admin/info  │
│   Members           /ui/{ws}/admin/members│
│   Projects          /ui/{ws}/admin/projects│
│   Shared config     /ui/{ws}/admin/config│
│   Defaults          /ui/{ws}/admin/defaults│
├──────────────────────────────────────────┤
│ PROJECT (when in project admin)          │
│   {projectKey}                           │
│   ├─ Info                                │
│   ├─ Log settings                        │
│   ├─ Services                            │
│   ├─ API keys                            │
│   └─ Data tables                         │
├──────────────────────────────────────────┤
│ SYSTEM (if sysadmin)                     │
│   Workspaces        /ui/sys/workspaces   │
│   Users             /ui/sys/users        │
│   Defaults          /ui/sys/defaults     │
├──────────────────────────────────────────┤
│ ACCOUNT                                  │
│   Profile           /ui/me/account       │
│   Security          /ui/me/security      │
│   Preferences       /ui/me/preferences   │
└──────────────────────────────────────────┘
```

Active section is determined by URL prefix. Sections the user can't access are hidden; if they navigate by URL anyway, they get 403.

### Route changes from current state

| Current | Target |
|---|---|
| `/ui/{ws}/{key}/settings` (project Settings tab) | `/ui/{ws}/admin/projects/{key}/info` + sub-pages |
| `/ui/{ws}/{key}/data` | `/ui/{ws}/admin/projects/{key}/data` |
| `/ui/{ws}/admin/settings` (workspace info) | `/ui/{ws}/admin/info` |
| `/ui/sys/retention` | `/ui/sys/defaults` (auto-gen from catalog) |
| — | `/ui/me/account` · `/security` · `/preferences` (NEW) |
| — | `/ui/{ws}/admin/defaults` (NEW, auto-gen) |
| — | `/ui/{ws}/admin/projects/{key}/log` (LogSettings leaf) (NEW) |

`_ProjectTabs` loses the `Settings` tab. The project page (`/ui/{ws}/{key}`) gets a top-right link "→ Admin" to `/ui/{ws}/admin/projects/{key}/info`.

### Permission gating

- `_AdminLayout` requires `[Authorize]` (logged-in user).
- Each admin page enforces its own policy: `sysadmin` / `ws-admin` / `ws-member` / `self`.
- Sidebar items are hidden when the user lacks permission. Direct URL access still returns 403.

## 5. Extensibility — five worked scenarios

**Scenario A: new behavioural setting (`ui.editor.theme`)**

1. Add a property to existing `record UiSettings`:
   ```csharp
   [Setting(TopLevel = Scope.User, Key = "ui.editor.theme")]
   public EditorTheme EditorTheme { get; init; } = EditorTheme.Default;
   ```
2. Done. The setting appears at `/ui/me/preferences` automatically. No DB migration.

**Scenario B: new group of settings (`AiSettings` for the Tasks module)**

1. Create the record:
   ```csharp
   public sealed record AiSettings
   {
       [Setting(TopLevel = Scope.Workspace, Key = "tasks.ai.provider")]
       public AiProvider Provider { get; init; } = AiProvider.Anthropic;

       [Setting(TopLevel = Scope.User, Key = "tasks.ai.personalKey")]
       public string? PersonalKey { get; init; }
   }
   ```
2. Add an admin page for the workspace-scoped fields (`/ui/{ws}/admin/ai`) and a self page for the user-scoped ones (`/ui/me/ai-providers`).
3. For secrets, declare `type=secret` in the attribute; the read/write path calls `ISecretEncryptor` automatically.

**Scenario C: new entity (`Webhook` subscription per project)**

1. Decide: L1 (entity with identity, list of N webhooks) or L2 group (a single config knob)?
2. Multiple webhooks per project ⇒ L1: new `Webhooks` table + migration + admin page at `/ui/{ws}/admin/projects/{key}/webhooks`.
3. Add a row to the entity catalog in this doc.

**Scenario D: new permission (`ws-billing-manager`)**

1. Extend `WorkspaceRole` enum.
2. Add policy in `Program.cs`.
3. Update permission column in catalog rows that reference the new role.

**Scenario E: new scope axis**

Not anticipated. Six current axes (System / Workspace / Project / Service / User / Membership) cover all known cases. If the need arises, this doc updates first, then the `Scope` enum.

## 6. Migration smells

| Smell | Verdict | Rationale |
|---|---|---|
| Admin credentials split between `appsettings.json` bootstrap and `Users` table | live with it | Bootstrap-only, single writer (`AdminBootstrapper`). Documented here. |
| Master key has no rotation mechanism | fix when relevant | One-shot CLI `dotnet run -- --rotate-master-key <old> <new>`. Not now. |
| Feature flags only in `appsettings.json` | fix when relevant | Easy via L2 `Settings(scope=workspace, path=features.{name})` once a per-workspace toggle is actually needed. |
| Retention in two places (appsettings + RetentionPolicies table) | **fix in implementation** | `RetentionPolicies` is removed. Retention settings move into L2 (project + system scope). `appsettings.json` Retention section is removed in the same change. |
| `REVEAL_WINDOW_MS` duplicated TS↔C# | live with it | Two sites are fine. Third occurrence is the trigger to promote to a `<meta>` tag in `_Layout`. |
| `ShareLink.CreatedBy` is a username string, not a `UserId` FK | fix when relevant | When a rename-user UI ships. |
| `RetentionPolicy.ProjectKey` is not an FK | resolved by removal | The table itself disappears. |
| Catalog in code vs DB-backed metadata | live with it | Code catalog is the right choice — compile-time types and reflection-based UI. |

## 7. Open follow-ups

- Sysadmin claim source (env var? first-created user gets the role? config flag?) — needs a decision before any L2 sysadmin-only setting ships.
- Encryption for L2 `type=secret` — reuse `ISecretEncryptor` (AES-GCM, master key).
- Reserved path prefixes (`auth.*`, `sys.*` restricted to sysadmin) — add as a validator in `SettingAttribute`.
- Optional `[SettingsSection]` group attribute if generic UI grouping needs explicit hints beyond record-type grouping.
