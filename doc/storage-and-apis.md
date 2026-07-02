# PetBox: storage, APIs & navigation

A map of *what goes where and through which door*. Verified against prod
(`petbox.3po.su`) 2026-06-03.

## 1. Hierarchy & reserved keys

```
Workspace (Key)               e.g. $system, infra, stdray
  └─ Project (Key, WorkspaceKey)   e.g. $system, petbox, kpvotes, yoba-summarizer, $workspace
```

- **Project is the unit everything is scoped by.** Memory/sessions/tasks/logs/config all key on `projectKey`.
- An **API key** carries a `project` claim — either ONE project key, or `*` (cross-project). Most tools assert the call's `projectKey` against that claim.
- **Reserved project:**
  - `$system` — the built-in internal project AND the single **shared cross-project container**: self-logs, the dogfooding ideas/spec/roadmap boards, the `ops` memory store, and the destination of `scope=workspace` memory. (There used to be a separate `$workspace` project for cross-project sharing — consolidated into `$system` 2026-06-03; one container, not two.)

## 2. Storage map (`/opt/petbox/data/`)

```
petbox.db            ← CENTRAL relational DB (one file). Holds the METADATA/registries:
                       Workspaces, Projects, ApiKeys, TaskBoards (board meta: kind/specBoard/closed),
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

## 4. Memory — **MCP only** (no REST)

Storage: `memory/{projectKey}/{store}.db`. A project has named **stores**; a store holds temporal (SCD-2) entries with a taxonomy `type ∈ User|Feedback|Project|Reference`, tags (surface = string ARRAY; stored as a CSV column), FTS5 search, free-form `Metadata`.

**Scope dimension** (over the per-project store files):
- `project` (default) → the key's own project.
- `workspace` → the shared cross-project container (`$system`). When the key's project IS `$system`, project and workspace collapse and a cascade recall searches it once.

**MCP tools** (server `petbox`):
- Read: `memory.search{q?,scope?,store?,type?,sort?,limit?,bodyLen?}` — THE read verb (uniform-entity-verbs v2; replaced `memory.list`+`memory.recall`). Without `q` a deterministic listing (updated desc); with `q` hybrid FTS ⊕ vectors. No scope ⇒ **cascade** project ⊕ workspace, sweeps every store **except `ops`**, rows labelled by scope, project first. One entry: `memory.get`.
- Capture: `memory.remember{text,scope?,store?,type?,tags?,description?}` (verbatim, auto-key).
- Structural/curated: `memory.store_create|store_list|store_delete`, `memory.upsert|delta`.

**Capture flow:** the SessionStart hook (`pull-memory.ts`) injects an instruction; the agent itself calls `memory_search` at start and `memory_remember` as it learns (instruct-the-agent — there is no memory READ REST). Background distillation (SessionFactsJob/BehaviorPatternJob) also writes into the `autocaptured` store; raw capture goes to Sessions (below).

## 5. Sessions — REST + MCP

Storage: `sessions/{projectKey}.db` — a **flat latest-snapshot** per session (one row, no temporal history), content stored as a Brotli-compressed JSONL message blob. Keyed by `sessionId`; `version` == the last message's ordinal.

- **REST:** `POST /api/sessions/{projectKey}/{sessionId}?agent=…` — body is `application/x-ndjson` (one `{role, content}` message per line). This is what the agent **Stop hook** (`agents/wiring/push-session.ts`, opencode `opencode-plugin.ts`) calls every turn: it re-sends the full ordered transcript (last-write-wins; the server numbers the messages).
- **MCP:** `session.search|get|upsert|append|delete` — `session.search` is THE read verb (uniform-entity-verbs v2; replaced `session.list`): without `q` a listing of compact rows, with `q` the two-stage archive search (digest discovery → episodic hydration; hits carry message ordinals for `session.get`).
- **UI:** `/ui/{ws}/{project}/sessions/{sessionId}` (read-only detail).

## 6. Tasks — MCP + Razor UI

Storage: `tasks/{projectKey}.db` (`plan_nodes` partitioned by `Board`; `node_tag`/`tag_vocab`) + `TaskBoards` meta and `Relation` edges in `petbox.db`.

- **Model (spec-flat-tags):** nodes are FLAT slugs; hierarchy is the `part_of` edge; grouping is enforced tags (`area:*`/`concern:*`); the "tree" is a projection (`tasks.search` returns `parentSlug`/`depth`, or pass `groupBy=area|concern`).
- **Methodology quartet:** the kinds `spec|ideas|intake|work` are **per-project singletons** (≤1 each; `free` unlimited). `tasks.methodology_enable(project)` idempotently provisions the missing ones and auto-wires `work→spec`; `tasks.methodology_get(project)` returns the quartet as one **compact index** (per-board status `counts` + header rows, no node bodies by default; pass `bodyLen` for a body snippet, `includeBoards` to pick boards; full bodies via `tasks.search` / `tasks.node_get`). The admin board page (`/ui/.../tasks`) offers EITHER **Enable methodology** (provisions the quartet as one unit) OR a **Free board** form — never per-kind creation by hand.
- **MCP tools:** `tasks.board_create|list|delete|close|reopen|set_spec`, `tasks.search|node_get|upsert|delta|workflow`, `tasks.methodology_enable|get`, `relations.create|list|delete` (kinds `task_spec|issue_task|idea_spec|blocks|part_of|supersedes`). `tasks.search|node_get|methodology_get|upsert|delta` accept `include_url=true` to add an absolute `url` permalink (the `/ui/{ws}/{project}/tasks/node/{nodeId}` detail page) to each returned node — off by default.
- **UI:** `/ui/{ws}/{project}/tasks` (board list, admin) and `/ui/{ws}/{project}/tasks/{board}` (board detail, part_of tree).

## 7. One shared container (`$system`)

There is **one** reserved cross-cutting project: `$system`. It is both the internal/system project (self-logs, ops, dogfooding boards) and the shared container that `scope=workspace` memory targets. A separate `$workspace` project briefly existed (for cross-project sharing) but was consolidated into `$system` on 2026-06-03 — for a single-user install two cross-cutting projects were redundant. The methodology quartet is **per-project** (e.g. enable it on `$system` or a real project); there is no separate workspace-level quartet.
