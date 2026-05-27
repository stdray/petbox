# User stories

Concrete daily scenarios for YobaBox. Each story is what a user (today: the author; tomorrow: invited friends) actually does — what they click, where they go, what they see.

This is the source of truth for what flows must work end-to-end. E2E tests in `tests/YobaBox.E2ETests/` should each map to one or more stories here. If a flow exists in tests but not here, either it's a bug in this doc or the flow is over-engineered.

Each story has:
- **Goal** — what the user wants to accomplish.
- **Trigger** — what made them open YobaBox right now.
- **Steps** — concrete clicks + URLs. Numbered.
- **Outcome** — what they see/get.
- **Edge cases** — failure modes worth thinking about.
- **Status** — `works` / `partial` / `stub` / `not yet` (current state of this flow).

URL placeholders use `{ws}` for workspace key, `{key}` for project key, `{svc}` for service key.

---

## S-1: Glance at all pets — "is everything alive?"

**Goal:** Quick health check across all my pet-projects.

**Trigger:** Morning routine; or notification that something might be down.

**Steps:**
1. Open YobaBox → lands on `/ui/{ws}` (Status overview). [Implicit: workspace resolved from cookie/claim/membership.]
2. Scan project cards. Each shows: project name (link to project page), services list with health badges, version, last build SHA.

**Outcome:** Within ~3 seconds, user can tell which pets are green / which need attention.

**Edge cases:**
- No pets yet → see onboarding card with "+ create your first project" CTA.
- Many pets (>10) → grid wraps; user scrolls.
- Pet has no services registered → card shows "No services registered."
- Service health unknown → grey badge (Dashboard module not yet collecting data per pet → mostly grey today; see Status: partial).

**Status:** **partial**. Page renders project cards + service tables. Health badges show but real health collection (pull/push) is not wired up — most show Unknown.

---

## S-2: Diagnose a misbehaving pet via logs

**Goal:** Figure out what's wrong with a specific pet.

**Trigger:** "Twitter bot didn't post overnight." User opens YobaBox.

**Steps:**
1. Land on `/ui/{ws}` (Status). Spot the project tile.
2. Click project name → `/ui/{ws}/{key}` which IS the Logs view.
3. Default KQL query shows recent events. User narrows down: types `where Level == "Error"` in KQL textarea.
4. Optional: clicks a service chip to filter to one service.
5. Click row → expands to show full message + properties.
6. If error references a trace → click trace link → `/ui/{ws}/{key}/traces/{traceId}` waterfall view.

**Outcome:** User finds the error message, stack trace if any, and the context (service, time, related events).

**Edge cases:**
- No logs ingested yet → "No events match query" empty state.
- KQL syntax error → inline error message under textarea; query not run.
- User wants to bookmark a useful query → clicks "Save as…" with a name → it appears in saved-queries chips.
- User wants to share the query result → clicks "Share" → gets `/s/{token}` URL.

**Status:** **works**. KQL editor with completions, saved queries, share endpoint — all functional. The path from sidebar/Status to project Logs is direct after the IA rework.

---

## S-3: Set up a new pet from scratch

**Goal:** Onboard a pet-project that doesn't exist in YobaBox yet.

**Trigger:** User wrote a new PoC and wants to give it config + logs.

**Steps:**
1. Land on `/ui/{ws}` (Status).
2. Sidebar → click `+` next to "PROJECTS" → `/ui/{ws}/projects/new`.
3. Fill form: Key=`twitter-poster`, Name="Twitter Poster", Description, Workspace (pre-filled).
4. Submit → redirected to `/ui/{ws}/twitter-poster` (the new project's Logs view, empty).
5. Click `Settings` tab → `/ui/{ws}/twitter-poster/settings`.
6. **Add service:** scroll to Services section → "Add Service" → fill Key=`bot-runner`, Kind=`Cron`, Url=empty (no HTTP) → Submit. Row appears.
7. **Mint API key:** scroll to API Keys section → "Issue Key" → check scopes (`config:read`, `logs:ingest`) → Submit. **Key is shown ONCE** in plaintext — user copies it into keepass.
8. Back to project page → no logs yet.
9. **In pet's code:** point pet at `https://yobabox/api/...` with the new API key + service key.
10. First log arrives → user sees it in `/ui/{ws}/twitter-poster`.

**Outcome:** Pet is configured. Logs flowing. Visible in Status.

**Edge cases:**
- Project key collides with reserved name (`logs`, `traces`, `config`, `admin`, `projects`, `sys`) → form error.
- Project key already exists in another workspace → form error (project keys are globally unique).
- Service key collides with existing service in same project → form error.
- User loses API key after closing the page → must mint a new one (cannot reveal again).

**Status:** **partial**. Project create exists. Service create + Key issue exist on the new Settings page (renamed from old ProjectDetail). Reserved-name validation NOT yet implemented (see follow-ups).

---

## S-4: Add config bindings for a pet

**Goal:** Move config out of `.env` files into YobaConf so the pet fetches it at startup.

**Trigger:** Setting up a new pet (continuation of S-3) or migrating an existing pet's config.

**Steps:**
1. From project page (`/ui/{ws}/{key}`) → Config tab → `/ui/{ws}/{key}/config` (project-scoped Bindings).
2. Auto-applied filter chip: `project:{key}`. Cannot be cleared from this view.
3. Click "+ New binding" → `/ui/{ws}/{key}/config/editor`.
4. Fill Path=`twitter/api-key`, Value=`***secret***`, Tags=`project:{key}` (auto-pre-filled), Kind=Secret (encrypted at rest).
5. Submit → back to project Config list; new row appears.
6. Repeat for `twitter/proxy-host`, `twitter/poll-interval`, etc.
7. Pet's code on startup: `GET /api/config/{ws}/resolve?path=twitter/api-key&tags=project:{key},env:prod` → gets the value.

**Outcome:** Bindings stored. Pet fetches them at runtime, no `.env` file needed.

**Edge cases:**
- User edits binding → history tab shows the change with timestamp + actor.
- User accidentally creates two bindings for the same path with overlapping tags → on resolve, ambiguity → API returns 409 with candidate IDs. User goes to Preview to debug.
- Secret value: rendered as `***` in list view; revealed only in Editor with explicit "Reveal" click.

**Status:** **partial**. The shared `/ui/{ws}/config` exists. The **project-scoped** `/ui/{ws}/{key}/config` with auto-filter and auto-tag is **step 8** in the IA migration — not yet split. Today, both URLs would route to the same Shared view.

---

## S-5: Rotate a shared secret across pets

**Goal:** Change a value used by multiple pets (e.g., Twitter API key shared between twitter-poster and meme-bot).

**Trigger:** Old key compromised / quota changed.

**Steps:**
1. Sidebar → Shared config → `/ui/{ws}/config` (workspace-level shared bindings — no project tag).
2. Find binding by Path filter or scrolling.
3. Click Edit → `/ui/{ws}/config/editor/{id}`.
4. Change Value. Tags stay the same (no `project:*`).
5. Submit. History tab now shows the change.
6. Pets pick up the new value on next poll (or on next process start, depending on their config-fetch strategy).

**Outcome:** Single edit propagates to all pets that resolve this path with matching tags.

**Edge cases:**
- User accidentally adds a `project:` tag to a shared binding → on save, UI shows warning "this binding is no longer shared; consider moving it to a project Config tab."
- User wants to verify it still resolves before considering rotation done → uses Preview tab.

**Status:** **partial**. Shared bindings page works (it's the workspace-level Config). The "warning on adding project tag" UX is part of step 8.

---

## S-6: Override config for one env

**Goal:** Use `env:prod` value in prod, `env:dev` value in dev — without touching pet code.

**Trigger:** Pet works in dev but needs a different URL / timeout / endpoint in prod.

**Steps:**
1. Project page → Config tab → `/ui/{ws}/{key}/config`.
2. "+ New binding" → fill Path=`api/base-url`, Value=`https://prod.example.com`, Tags=`project:{key}, env:prod`. Submit.
3. Repeat with `env:dev` value for same Path.
4. Click Preview sub-tab → `/ui/{ws}/{key}/config/preview`.
5. Input tags `project:{key} env:prod` (or `env:dev`) → see which binding resolves + its specificity.

**Outcome:** Resolver picks the env-specific binding deterministically. Subset semantics — if request has `env:prod`, only bindings whose tags are a subset of request match; `env:dev` binding does NOT match for a prod request.

**Edge cases:**
- User creates two bindings with identical tag sets at the same path → resolve throws `AmbiguousConfigException` → API returns 409; UI Preview marks the row red with candidate IDs.
- User asks for `env:staging` but no staging binding exists → falls back to the binding with fewest tags that's still a subset (e.g., `project:{key}` alone).

**Status:** **works** (resolver fix landed). Preview-tab integration with new semantics: **works**. UI to inspect ambiguity in Preview: **works**.

---

## S-7: Cross-project log search

**Goal:** Find a specific request ID across all pets, not just one.

**Trigger:** Customer support: "tracking id ABCD123 failed somewhere — which pet?"

**Steps:**
1. Sidebar → Logs (all) → `/ui/{ws}/logs`.
2. KQL: `where Properties has 'ABCD123' | take 100`.
3. Results show events from any pet in this workspace, grouped or annotated by ServiceKey/ProjectKey.

**Outcome:** User finds the right pet without guessing which one to open.

**Edge cases:**
- Many results → use `take` to limit; sort by timestamp.
- User wants to drill in → click the ProjectKey/ServiceKey badge on a row to filter further (jumps to that project's Logs).

**Status:** **not yet**. Sidebar link exists but the page is not wired. Step 9 in the IA migration.

---

## S-8: Switch workspace

**Goal:** Move from "personal" workspace to a shared one (when friends arrive) or to `$system`.

**Trigger:** Multi-tenant usage.

**Steps:**
1. Sidebar → top of sidebar, workspace switcher (a daisyUI select).
2. Pick another workspace key → form auto-submits to `/api/ui/workspace`.
3. Server validates membership, sets `yb_ws` cookie, redirects to `/ui/{newWs}` (Status).

**Outcome:** All workspace-scoped pages now show new ws's data.

**Edge cases:**
- User picks workspace they're not a member of → 403 (form choices are filtered, so this is only via URL hacking).
- One workspace only → switcher still shows (per "explicit > implicit"); single option.

**Status:** **works**. Switcher moved into sidebar header in Layout V2.

---

## S-9: Create a new workspace

**Goal:** Set up a workspace for a separate context (e.g., a friend signs up; or user wants isolated playground).

**Trigger:** Onboarding a friend; or wanting clean separation.

**Steps:**
1. Sysadmin: top-bar → System (cog icon) → `/ui/sys`.
2. Click Workspaces → `/ui/sys/workspaces`.
3. "New Workspace" form → fill Key, Name, Description → Submit.
4. New workspace appears. Add members via WorkspaceDetail page.

**Outcome:** New workspace exists; members can switch to it.

**Edge cases:**
- Workspace key collides → form error.
- Workspace key is `sys` → reserved, form error.
- User without sysadmin claim → page hidden / 403.

**Status:** **partial**. Workspaces list page works. Sysadmin claim gating is not yet implemented (everyone sees the link).

---

## S-10: Delete a pet

**Goal:** Remove an obsolete pet-project.

**Trigger:** Project is dead; cleaning up.

**Steps:**
1. Project page → Settings → `/ui/{ws}/{key}/settings`.
2. Scroll to "Danger zone" → "Delete project" button.
3. Confirmation: type project key to confirm (typed-name confirmation, not just OK/Cancel).
4. Submit → project + all its services + API keys deleted. Logs and config bindings: TBD (retention or cascade — currently NOT cascaded; orphan data stays until log retention purges it).
5. Redirect to `/ui/{ws}` (Status).

**Outcome:** Project gone from UI.

**Edge cases:**
- Project is `$system` → undeletable per invariant; button disabled.
- User mistypes confirmation → no-op.

**Status:** **stub**. Delete handler exists in old ProjectDetail page but the typed-confirmation UI isn't implemented yet; current is `onclick="return confirm(...)"`.

---

## S-11: Manage workspace members

**Goal:** Add / remove users from a workspace; assign roles.

**Trigger:** Friend signs up; old member leaves.

**Steps:**
1. Sidebar → Workspace → `/ui/{ws}/admin` → Members sub-tab.
2. List of members with roles (Admin/Member/Viewer).
3. "Invite user" → fill email or pick existing user → assign role → Submit.
4. To remove: click x on member row → confirm.

**Outcome:** Membership matrix updated.

**Edge cases:**
- Removing yourself → warning ("you'll lose access to this workspace").
- Promoting/demoting yourself → allowed but warns.
- Workspace must have ≥1 Admin at all times.

**Status:** **partial**. Page exists at `/ui/{ws}/admin/members` (renamed from old `/ui/admin/workspaces/{ws}/users`). The combined `/ui/{ws}/admin` tabbed landing is step 10. Invite flow is TBD (no user registration UI yet).

---

## S-12: Bootstrap an AI agent for a pet

**Goal:** Give a coding agent (claude code, factory droid, etc.) credentials so it can develop the pet with logs+config visibility.

**Trigger:** Starting a new feature; want to delegate to an agent.

**Steps:**
1. Project page → Settings → API Keys section.
2. "Issue Agent Key" — special variant: temporary (1h TTL), reduced scope (`logs:ingest`, `config:read`).
3. Copy key + paste link to instruction page (`/agent`) into agent's prompt.
4. Agent fetches `/agent` page, reads how to ingest logs and read config.
5. Agent develops the pet, can read its own logs back to debug.
6. After feature merged: rotate / revoke the agent key.

**Outcome:** Agent works against YobaBox with bounded credentials and self-documented instructions.

**Edge cases:**
- Agent key TTL expires mid-session → agent's next call returns 401 → agent reports back to user → user re-mints key.
- Agent attempts a scope it doesn't have → 403 with clear error mentioning required scope.

**Status:** **not yet**. `/agent/` prefix is reserved documentary-only (see `doc/url-conventions` in `project-url-conventions` memory). No endpoints yet. Major next module after IA rework — see `~/.claude/plans/proud-waddling-naur.md` Module roadmap.

---

## S-13: Sysadmin — global retention defaults

**Goal:** Change the default log retention days for all workspaces (with per-project overrides).

**Trigger:** Disk usage growing; want longer/shorter retention.

**Steps:**
1. Top-bar → System cog → `/ui/sys`.
2. Click Retention → `/ui/sys/retention`.
3. Edit "Default retention days" and "System retention days" inputs → Save.

**Outcome:** Defaults propagate to all workspaces that don't have an override.

**Edge cases:**
- Per-project override: project's Settings tab has its own Retention days input that takes precedence over workspace default.

**Status:** **partial**. Page exists, gating by sysadmin claim not yet enforced.

---

## Test coverage mapping

E2E tests in `tests/YobaBox.E2ETests/` map to stories like this:

| Test class | Stories covered |
|---|---|
| `KpVotesOnboardingTests` | S-3 (project create) + S-4 (config bindings) + S-2 (log ingest + view) |
| `ConfigResolvePriorityTests` | S-6 (env override) |
| `ApiKeyScopeTests` | S-3 step 7 (scope-bound key) |
| `ConfigCrudTests` | S-4 + S-5 |
| `ConfigPageTests` | S-4 navigation surface |
| `LogsPageTests` | S-2 |
| `DashboardTests` | S-1 |
| `ProjectDetailTests` | S-3 (services + keys via Settings tab) |
| `DataTableTests` | (data module, not yet covered in stories; matches S-3 step variant for data tables) |
| `LoginTests` | (auth — implicit prerequisite for all stories) |

After the IA migration, several existing tests reference old URLs (`/ui/admin/projects/...`, `/ui/logs/...`). They need to be ported to the new URL structure. The stories above are the contract.

When `/agent/` and Tasks modules land, add S-12 implementation and a new test class. When Data module is rewritten, replace `DataTableTests` story.

---

## Authoring notes

- Keep stories short. If a story spans more than ~10 steps, split it.
- Edge cases are LIMITED to "user-visible failure modes" — not internal error paths.
- "Status" field is the truth about what's wired up. Update it as code lands.
- New stories: add the `S-N` ID continuing the sequence; do not re-number existing.
