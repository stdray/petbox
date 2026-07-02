---
name: petbox
description: Shared task boards, memory and session plans for this project via the PetBox MCP server (server name `petbox`). Use to record/read plans, durable notes and working-session state for {{PROJECT}} development.
---

This project is connected to a PetBox instance over MCP (server `petbox`, https://petbox.3po.su).
Pass projectKey "{{PROJECT}}" in every call (the key is scoped to the {{PROJECT}} project;
boards/memory/sessions live at https://petbox.3po.su/ui/{{WORKSPACE}}/{{PROJECT}}).

**Tool naming:** in opencode the MCP tools are `petbox_<verb>` (e.g. `petbox_tasks_upsert`,
`petbox_memory_recall`); in Claude Code they are `mcp__petbox__<verb>`. The dotted verbs below
(`tasks.upsert`, `memory.recall`, …) are logical names — replace the dot with `_` and prefix
per runtime.

**Plan nodes are FLAT slugs** (`key` = [a-z][a-z0-9_-]*); hierarchy is the `partOf` edge,
grouping is `tags` (`area:*` / `concern:*`). Give each node a short `title` and a markdown
`body`. A cold `tasks.upsert` auto-creates the board. The upsert response is a pure ack for
YOUR call (added/updated/removed cover only your nodes); to catch up on everyone's changes
call `tasks.delta` with `sinceVersion` = a previous `currentVersion`. `nodes`/`entries` are
TYPED arrays — pass real JSON arrays, not stringified JSON.

**`tasks.search` is THE read verb** — two modes: without `q` it's a deterministic LISTING
(pass `board` for one board, omit for the whole project; default order priority-then-key),
with `q` it's hybrid relevance search (FTS ⊕ vectors). Filters work in both modes:
`status[]`, `keys[]` (slug|NodeId), `under` (subtree), `includeClosed`; `sort{by,desc}`
reorders; `bodyLen` snippets bodies. One node in full: `tasks.node_get`.

**Memory entries are typed** (`user` | `feedback` | `project` | `reference`) — `type` is
required on `memory.upsert`; `tags` is free CSV. `memory.recall` is THE search verb
(hybrid FTS ⊕ vectors; use `bodyLen` for snippets).

**What goes where:**
- Session (`session.*`) — the current working plan/thinking. "Stale next week?" → session.
- Tasks (`tasks.*`) — a unit of work with a status tracked to Done.
- Memory (`memory.*`) — a durable fact that should outlive the work. Don't store what
  code/git already records, transient state, secrets, or actionable work (that's a task).

**Tools:**
- `tasks.board_list / board_create / board_delete / search / node_get / upsert / delta / workflow`
- `memory.store_list / store_create / store_delete / list / recall / remember / get / upsert / delta`
- `session.upsert / get / list` (upsert = optimistic-concurrency replace; pass the current version)
- Logs: `log.query` (KQL), `log.create / list / delete`
- Admin (per-type, flat params): `project.create / list`, `apikey.create / list / delete`,
  `db.create / list / delete / describe`
