# Connect a coding agent to PetBox

PetBox exposes a project's **task boards**, **memory stores** and **session plans** over **MCP** (Model Context Protocol) — a shared, durable plan/notes store that survives across sessions and across different agents on a team. Once connected, an agent reads and writes them with the `tasks_*`, `memory_*` and `session_*` tools.

> **Primary path: use `petbox-wire`, not this page's manual steps.** For **Claude Code**, **opencode** and **Factory Droid**, the [wire guide](/doc/wire) — driven by one command copied off the project's **Connect** page — does everything sections 1–3 below describe by hand, plus the session hooks and skill install that hand-registration skips. Follow the [onboarding runbook](/doc/onboarding) for the end-to-end sequence. Sections 1–3 here exist for **`omp`/`pi`** (not yet wired automatically) and for debugging a config `petbox-wire` already generated — they are not a second way to set the same thing up.

## 1. Get connection details

A workspace admin mints a project-scoped API key on the project's **Connect** page (admin gear → project → Connect) — the **only** legal place a project key is minted. That page shows, once, the three things you need:

- MCP endpoint (this instance): `{{mcp}}`
- Auth header: `X-Api-Key: <your key>`
- Project key: passed as `projectKey` in every tool call

## 2. Register a project-level MCP server (manual — `omp`/`pi`, or debugging)

Add a server named `petbox` in the config file *your* agent reads:

- **Claude Code** — `.mcp.json` (`mcpServers.petbox`, type `http`, url + headers) — normally written by `petbox-wire`, not by hand.
- **Factory Droid** — `.factory/mcp.json` (same shape) — also written by `petbox-wire`.
- **opencode** — `opencode.json` (`mcp.petbox`, type `remote`, url + headers, `enabled: true`) — also written by `petbox-wire`.
- **omp** (oh-my-pi) — its native project MCP config (HTTP url + header). Not yet covered by `petbox-wire`.
- **pi** — no native MCP; bridge with `pi-mcp-adapter` against the same url + header. Not yet covered by `petbox-wire`.

Example (Claude Code `.mcp.json`):

```json
{
  "mcpServers": {
    "petbox": {
      "type": "http",
      "url": "{{mcp}}",
      "headers": { "X-Api-Key": "${PETBOX_<PROJECT>_API_KEY}" }
    }
  }
}
```

## 3. Keep the key out of version control

Never commit the key. The config references it as an environment variable, which your agent expands from its **process environment**. Name the variable **per project** — `PETBOX_<PROJECT>_API_KEY` (e.g. `PETBOX_KPVOTES_API_KEY`) — so several projects can coexist on one machine; a single shared `PETBOX_API_KEY` can't. The project's **Connect** page shows the exact name and ready-to-paste commands. `petbox-wire` sets this for you (a new terminal is needed for it to take effect); doing it by hand looks like:

```
# macOS / Linux (current shell)
export PETBOX_<PROJECT>_API_KEY=<your key>

# Windows (persisted for new shells)
setx PETBOX_<PROJECT>_API_KEY "<your key>"
```

If your agent can't expand env vars, inline the key directly in the MCP config instead — but keep that config file gitignored.

## 4. Plan nodes are flat slugs; hierarchy is `partOf`

Every node has a **flat slug** `key` (lowercase `^[a-z][a-z0-9_-]{0,99}$`, board-unique). Nesting is the `partOf` field — a parent slug or nodeId (`""` detaches to a root); cross-cutting grouping is `tags` (`area:*` / `concern:*`). Give every node a short `title` AND a markdown `body`.

```
tasks_upsert board="spec" projectKey="<proj>" nodes=[
  { "key":"auth", "status":"defined", "title":"Auth" },
  { "key":"login", "partOf":"auth", "status":"defined", "title":"Login flow" }
]
```

`priority` is a sparse ordering int (lower first). `version` is the baseline you last saw (0 = new). Rename with `prevKey`. The upsert response is a pure ack for *your* call (`added/updated/removed` + `currentVersion`); catch up on everyone's changes via `tasks_delta` with a previous `currentVersion`.

> A board has a **kind** (spec / work / ideas / intake / simple) that drives its statuses, links and invariants. Before writing real plans, read the [methodology cheatsheet](/doc/methodology) — it's the operational contract (and call `tasks_workflow` for a board's live statuses).

## 5. Tools

- `tasks_board_create(kind?) / board_list / board_delete` — named boards; `kind` ∈ spec|work|ideas|intake|simple. A cold `tasks_upsert` auto-creates a simple board.
- `tasks_search / node_get / upsert / delta` — nodes (key, nodeId, parentSlug, depth, status, type, title, body, priority, version; on a spec board also computed `delivery`). `search` is the one read verb: without `q` a deterministic listing (board or whole project), with `q` hybrid relevance search; filters (`status`, `keys`, `under`) and `sort` work in both modes. `links:{kind:ref}` / `blockedBy` create links.
- `tasks_workflow` — the live statuses/transitions for a board's kind.
- `relations_create / list / delete` — typed temporal edges (task_spec|issue_task|idea_spec|blocks|nfr|dup). See the [cheatsheet](/doc/methodology).
- `memory_store_list / store_create / store_delete` — named memory stores (a cold `memory_upsert` auto-creates the store).
- `memory_search / get / upsert / delta / remember` — durable notes. Each entry needs a `type` (User|Feedback|Project|Reference) plus description, body, optional `tags` (an array of strings). `upsert` is a PATCH on edits: an omitted field stays unchanged, an explicit `""` (or `[]` for tags) clears it. `search` is the one read verb: without `q` a deterministic listing (updated desc), with `q` hybrid relevance search (FTS ⊕ vectors); no `scope` cascades project ⊕ workspace over every store; optional `type` filter and `sort` work in both modes.
- `session_search / get / upsert / append / delete` — the session archive. `search` is the one read verb: without `q` a listing of compact rows, with `q` a two-stage search (digest discovery → episodic hits with message ordinals for `session_get`).

## 6. Memory: tasks vs memory vs session

Three stores, pick by lifetime:

- **Session** (`session_*`) — your *current* working plan/thinking. Ephemeral, last-write-wins. Test: "stale next week?" → session.
- **Tasks** (`tasks_*`) — a *unit of work with a status* you track to Done. Test: "has a status that changes (Pending→Done)?" → task.
- **Memory** (`memory_*`) — a *durable fact* that should outlive the work. Test: "will a future agent need this to avoid re-learning it?" → memory.

What belongs in **memory**: durable facts not derivable from code/git/config — user preferences, project constraints, decisions-with-rationale, references. What does *not*: anything the repo/git already records, transient state, secrets, or actionable work (that's a task). Pick a `type`: `user` (who the user is), `feedback` (a correction/preference on how to work — include why + how to apply), `project` (a durable project fact/constraint — why + how to apply), `reference` (a pointer to an external resource).

Maintenance: search before you write; update an existing entry rather than duplicating; delete when wrong (history is kept, so deletes are safe). Put prose in `session` content or a task's `body`; when a finished task yields a generalizable lesson, move that lesson to memory as `feedback` — the task's `commits` stay on the node.

## 7. The project skill

For Claude Code, opencode and Factory Droid, `petbox-wire` already wrote this `SKILL.md` at the right path (Claude Code `.claude/skills/petbox/`; Droid `.factory/skills/petbox/`; opencode reads the Claude Code copy) — nothing to do. For `omp`/`pi`, or any agent not wired automatically, drop it yourself at the right path for that agent (`omp`: `.pi/skills/` or `.agents/skills/`):

```
---
name: petbox
description: Shared task boards, memory and session plans for this project via the PetBox MCP server (server name `petbox`). Record/read plans, durable notes and working-session state on the spec/work/idea rails.
---

This project is connected to a PetBox instance over MCP (server `petbox`).
Pass projectKey "<proj>" in every call.

BOARDS have a kind, set at board_create and IMMUTABLE: simple (lightweight preset:
task|bug|feature|chore|issue, Todo->InProgress->Done +Blocked|Cancelled, free
transitions, free-form tags) | spec (requirements, computed delivery) | work
(feature|bug|chore, FSM) | ideas (fuzzy thinking) | intake (inbound issues). A cold upsert to
a missing board auto-creates a SIMPLE one — create boards explicitly with the right
kind. Standard board names = their kind: ideas, spec, work, intake (+ simple for
scratch). Use those names.

FLOW starts in `ideas`: a raw idea, deliberated (raw->exploring->accepted), is what
PRODUCES the spec — the accepted idea's conclusions become spec nodes; work features
implement them. Don't invent spec from nothing; let it fall out of an idea.

NODES are FLAT slugs: `key` (lowercase [a-z][a-z0-9_-]); hierarchy is `partOf` (parent
slug|nodeId), grouping is `tags`. Short `title` + markdown `body`. Links bind to the
returned `nodeId`, not the slug.

RIGHT-SIZE the rails — scale how deep, not whether to start from an idea:
  throwaway spike  -> one simple board (no idea)
  small build      -> short idea -> a few thin spec leaves -> work
  ongoing project  -> ideas -> spec -> work + intake (deeper)

WORK rules: a new feature/bug MUST link a spec node — pass links:{task_spec: <spec node's
slug or nodeId>}; pin the spec board via board_create(specBoard) or board_set_spec.
Type `chore` (internal engineering hygiene: tests, flakes, refactoring, infra) is the
one exception — same FSM, no task_spec link required. Statuses Pending->InProgress->Review->Done;
your ceiling is Review — never set Done, the maintainer confirms it. Blocked needs blockedBy.

READING: tasks_search is THE read verb — without q a deterministic listing (board= one
board, omit for the project), with q a hybrid relevance search; both modes take status[],
keys[] (slug|nodeId), under=<slug> (subtree) and sort{by,desc}. A board listing carries
kind, specBoard and per-node links (spec/blockedBy/linkedTasks) plus spec `delivery`; it
HIDES terminal nodes by default — includeClosed=true to include. One full node:
tasks_node_get. Partial update: send only what changes — a status change needs just
key + version + status.

STORE by lifetime: durable fact -> memory (type User|Feedback|Project|Reference); current
thinking -> session; a unit of work with a status -> task.

MEMORY READING: memory_search is THE read verb — without q a deterministic listing
(updated desc), with q hybrid relevance search; no scope cascades project + workspace
over every store. One full entry: memory_get.

Depth: /doc/methodology (contract) + /doc/overview (modules).
Tools: tasks_board_create/board_list/board_delete/board_set_spec/board_close/board_reopen/
search/node_get/upsert/delta/workflow, relations_create/list/delete,
memory_store_*/search/remember/get/upsert/delta, session_search/get/upsert/append/delete.
```

## 8. Verify

Restart your MCP connection and call `tasks_board_list` with your `projectKey`. An empty list (not an auth error) means it works.
