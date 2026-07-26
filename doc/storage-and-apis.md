# PetBox: storage, APIs & navigation

A map of *what goes where and through which door*. Unlike most of `doc/`, this file is
**maintained reference, not a historical record** — it is meant to be true now, so a claim
here that has gone stale is a defect rather than a dated note.

Last reconciled against the code at `2ea9c11c` (2026-07-26). The body of the map was
originally verified against prod (`petbox.3po.su`) on 2026-06-03; sections that changed
later say so and carry their own dates.

## 1. Hierarchy & reserved keys

```
Workspace (Key)               e.g. $system, infra, stdray
  └─ Project (Key, WorkspaceKey)   e.g. $system, petbox, kpvotes, yoba-summarizer, $workspace
```

- **Project is the unit everything is scoped by.** Memory/sessions/tasks/logs/config all key on `projectKey`.
- An **API key** carries a `project` claim — either ONE project key, or `*` (cross-project). Most tools assert the call's `projectKey` against that claim.
- **Reserved projects:**
  - `$system` — the built-in internal project: self-logs, the dogfooding ideas/spec/roadmap boards, and the `ops` memory store.
  - `$workspace` / `$ws-{workspaceKey}` — the **shared cross-project memory container**, ONE PER WORKSPACE: the destination of `scope=workspace` memory, kept SEPARATE from `$system` so a project's memory cascade doesn't inherit all of `$system`'s memory as "workspace". `$workspace` is the legacy key for the `$system` workspace specifically; every other workspace gets `$ws-{workspaceKey}`. The caller never picks it — it is derived from the project's workspace (§7). These rows are Projects rows but NOT user projects (`WorkspaceMemory.IsWorkspaceContainer`). (Briefly consolidated into `$system` 2026-06-03; revived as its own container 2026-07-03 — M031 re-ensures the row.)

## 2. Storage map (`/opt/petbox/data/`)

```
petbox.db            ← CENTRAL relational DB (one file). Holds the METADATA/registries:
                       Workspaces, Projects, ApiKeys, TaskBoards (board meta: kind/wiredBoard/closed),
                       Relation (task graph edges), MemoryStores (store registry), ConfigBindings meta,
                       Users, ShareLinks, Settings, Health, SavedQueries, …
memory/{project}/{store}.db   ← per-project, per-store memory (FTS5 + SCD-2 temporal). e.g.
                                $system/{dogfooding,notes,ops,stdray}, $workspace/notes, petbox/dogfooding
sessions/{project}.db ← per-project raw agent-session archive (append-only). e.g. $system.db, $workspace.db
tasks/{project}.db    ← per-project plan_nodes (all boards, partitioned by Board) + node_tag/tag_vocab.
                        ({project}/ subdirs are the LEGACY one-file-per-board layout, now *.migrated)
config/               ← per-workspace config DBs (bindings + tag vocab)
logs/, db/, keys/, backups/   ← logs, infra, secrets, pre-migration snapshots
```

**Rule of thumb:** the *registry/metadata* lives in `petbox.db`; the *content* (memory entries, session lines, plan nodes) lives in per-project scoped files. Relations & tags are the exception worth knowing: **relations** (the task graph) are in `petbox.db` (project-scoped, bind to stable NodeId, cross-board); **node tags** are in the per-project `tasks.db` (they need a same-file FK to `tag_vocab`).

## 3. Three doors (URL prefixes)

| Prefix | Audience | Auth | Examples |
|---|---|---|---|
| `/ui/{ws}/{project}/…` | humans (Razor pages) | cookie login | `/ui/$system/$system/tasks/ideas`, `/ui/{ws}/{project}/sessions/{id}`, `/ui/{ws}/{project}/memory/{store}` |
| `/api/…` | programmatic REST | `X-Api-Key` / `Authorization: Token\|Bearer` | `/api/sessions/{project}/{sessionId}`, `/api/health`, `/v1/logs/{project}/{logName}` |
| `/mcp` | agents (MCP, streamable HTTP) | `X-Api-Key` | the whole `tasks.*`/`memory.*`/`session.*`/`relations.*`/`config.*`/`data.*`/`log.*` tool surface |

**Navigation into a project (UI):** log in → land on a workspace → pick a project → its module pages (`tasks`, `sessions`, `memory`, `config`, `logs`). The workspace is switched via `POST /api/ui/workspace`; routes are built in `Routes.cs` (`Project(ws,key)`, `ProjectSession(...)`, …).

### 3b. The uniform-entity-verbs matrix (MCP) — and the two delete shapes

Every content family exposes the same verb cells over `/mcp`: `*_upsert` (write), `*_search`
(list = search without `q`), `*_get` (addressed single read), `*_delta` (catch-up since a
cursor), plus a delete. The **delete cell is satisfied by TWO intentional shapes**, chosen by
the family's write mechanics — both are correct:

- families with a **batch temporal upsert** (tasks, memory) express delete as a `{deleted:true}`
  marker item *inside* `*_upsert` — a soft temporal-close, batched alongside the writes;
- families **without** batch-temporal semantics (sessions, comments, config, and relations —
  the latter immutable) use a **dedicated `*_delete` verb**.

The shape follows the family's write mechanics, not one mandated signature. (Consistently,
`*_delta` reads each family's REAL monotonic field — a version watermark for tasks/memory/
comments, config's auto-increment binding id, the session `Updated` timestamp — never a faked
one; where a field can't express a clean watermark the limitation is documented on the tool.)

## 4. Memory — **MCP only** (no REST)

Storage: `memory/{projectKey}/{store}.db`. A project has named **stores**; a store holds temporal (SCD-2) entries with a taxonomy `type ∈ User|Feedback|Project|Reference`, tags (surface = string ARRAY; stored as a CSV column), FTS5 search, free-form `Metadata`.

**Scope dimension** (over the per-project store files):
- `project` (default) → the key's own project.
- `workspace` → the calling project's **own workspace** container — one per workspace, never a single global one. The key is DERIVED, not named by the caller: `$workspace` for the `$system` workspace (the legacy key, seeded by M028/M031), `$ws-{workspaceKey}` for every other. So a project in workspace `smoke` resolves `$ws-smoke`, and reaches nothing of `$system`'s.

**An empty workspace leg is not proof of absence.** A cascade skips a leg the caller is not authorized for *silently* (the read succeeds and simply contributes nothing, rather than failing), so "no rows from workspace" can mean either "nothing there" or "not yours". This is deliberate — a foreign container must not become an existence oracle — but it means you cannot read absence out of a cascade result.

**MCP tools** (server `petbox`):
- Read: `memory_search{q?,scope?,store?,type?,sort?,limit?,bodyLen?}` — THE read verb (uniform-entity-verbs v2; replaced `memory.list`+`memory.recall`). Without `q` a deterministic listing (updated desc); with `q` hybrid FTS ⊕ vectors. No scope ⇒ **cascade** project ⊕ workspace, sweeps every store **except `ops`**, rows labelled by scope, project first. One entry: `memory_get`.
- Capture: `memory_remember{text,scope?,store?,type?,tags?,description?}` (verbatim, auto-key).
- Structural/curated: `memory_store_create|store_list|store_delete`, `memory_upsert|delta`.

**Capture flow:** the SessionStart hook (`pull-memory.ts`) injects an instruction; the agent itself calls `memory_search` at start and `memory_remember` as it learns (instruct-the-agent — there is no memory READ REST). Background distillation (SessionFactsJob/BehaviorPatternJob) also writes into the `autocaptured` store; raw capture goes to Sessions (below).

## 5. Sessions — REST + MCP

Storage: `sessions/{projectKey}.db` — a **flat latest-snapshot** per session (one row, no temporal history), content stored as a Brotli-compressed JSONL message blob. Keyed by `sessionId`; `version` == the last message's ordinal.

- **REST:** `POST /api/sessions/{projectKey}/{sessionId}?agent=…` — body is `application/x-ndjson` (one `{role, content}` message per line). This is what the agent **Stop hook** (`src/clients-ts/petbox-wire/src/push-session.ts`, opencode `opencode-plugin.ts`) calls every turn: it re-sends the full ordered transcript (last-write-wins; the server numbers the messages).
- **MCP:** `session_search|get|upsert|append|delete` — `session_search` is THE read verb (uniform-entity-verbs v2; replaced `session.list`): without `q` a listing of compact rows, with `q` the two-stage archive search (digest discovery → episodic hydration; hits carry message ordinals for `session_get`).
- **UI:** `/ui/{ws}/{project}/sessions/{sessionId}` (read-only detail).

## 6. Tasks — MCP + Razor UI

Storage: `tasks/{projectKey}.db` (`plan_nodes` partitioned by `Board`; `node_tag`/`tag_vocab`) + `TaskBoards` meta and `Relation` edges in `petbox.db`.

- **Model (spec-flat-tags):** nodes are FLAT slugs; hierarchy is the `part_of` edge; grouping is enforced tags (`area:*`/`concern:*`); the "tree" is a projection (`tasks_search` returns `parentSlug`/`depth`, or pass `groupBy=area|concern`).
- **Methodology quartet:** the kinds `spec|ideas|intake|work` are **per-project singletons** (≤1 each; `free` unlimited). `tasks_methodology_enable(project)` idempotently provisions the missing ones and auto-wires `work→spec`; `tasks_methodology_get(project)` returns the quartet as one **compact index** (per-board status `counts` + header rows, no node bodies by default; pass `bodyLen` for a body snippet, `includeBoards` to pick boards; full bodies via `tasks_search` / `tasks_node_get`). The admin board page (`/ui/.../tasks`) offers EITHER **Enable methodology** (provisions the quartet as one unit) OR a **Free board** form — never per-kind creation by hand.
- **MCP tools:** `tasks_board_create|list|delete|close|reopen|set_wire`, `tasks_search|node_get|upsert|delta|workflow`, `tasks_methodology_enable|get`, `relations_create|list|delete` (kinds `task_spec|issue_task|idea_spec|blocks|part_of|supersedes`). `tasks_search|node_get|methodology_get|upsert|delta` accept `include_url=true` to add an absolute `url` permalink (the `/ui/{ws}/{project}/tasks/node/{nodeId}` detail page) to each returned node — off by default.
- **UI:** `/ui/{ws}/{project}/tasks` (board list, admin) and `/ui/{ws}/{project}/tasks/{board}` (board detail, part_of tree).

## 7. Shared containers: one per workspace

`scope=workspace` memory targets a **shared container per workspace**, not one global one. The container is a real `Projects` row, but it is not a user project — it has no logs/tasks/dbs, and `ProjectDirectory`/`NavigationContext` filter it out of project listings (`WorkspaceMemory.IsWorkspaceContainer` is the single definition). Its key is derived from the workspace: `$workspace` for `$system`, `$ws-{workspaceKey}` otherwise.

`$system` remains the reserved internal project (self-logs, `ops`, dogfooding boards) — but it is *not* the workspace container; those are two roles that were briefly the same object and no longer are.

**Containment note.** Because the container is DERIVED from a project's workspace rather than named by the caller, every surface that makes that hop must re-ask the authorization question — the named target being authorized does not authorize the derived container. `SandboxContainment` is where that question lives, and the call sites are enumerated mechanically by `SandboxContainmentCallSiteGuardTests`, never by a hand-maintained list.

The methodology quartet is **per-project** (enable it on `$system` or a real project); there is no workspace-level quartet.

### History of this section

The design above is the *second* answer, and the first one is worth knowing because it caused a leak:

- **2026-06-03** — the separate `$workspace` project was **consolidated into `$system`**, on the reasoning that for a single-user install two cross-cutting projects were redundant. This is what the previous edition of this section described as current.
- **2026-07-03** — that consolidation was **undone**: `$workspace` was revived as its own container (M031 re-ensures the row), because collapsing them made every project's memory cascade inherit all of `$system`'s memory as "workspace". The consolidation was also implicated in one of the three historical cross-tenant leaks.
- **2026-07-26** — containers became genuinely per-workspace in practice when M048 moved the sandbox project `smoke` out of the `$system` workspace into its own, so the container derivable from the sandbox (`$ws-smoke`) is empty. See AGENTS.md rule 7.
