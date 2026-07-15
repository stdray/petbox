---
name: petbox
description: Shared task boards, memory and session plans for this project via the PetBox MCP server (server name `petbox`). Use to record/read plans, durable notes and working-session state for {{PROJECT}} development.
---

This project is connected to a PetBox instance over MCP (server `petbox`, https://petbox.3po.su).
Pass projectKey "{{PROJECT}}" in every call (the key is scoped to the {{PROJECT}} project;
boards/memory/sessions live at https://petbox.3po.su/ui/{{WORKSPACE}}/{{PROJECT}}).

**Tool naming:** the base verbs are underscore-delimited (`tasks_upsert`, `memory_search`, …).
In opencode the MCP tools are `petbox_<verb>` (e.g. `petbox_tasks_upsert`, `petbox_memory_search`);
in Claude Code they are `mcp__petbox__<verb>`. Just prefix the base verb per runtime.

**Plan nodes are FLAT slugs** (`key` = [a-z][a-z0-9_-]*); hierarchy is the `partOf` edge,
grouping is `tags` (`area:*` / `concern:*`). Give each node a short `title` and a markdown
`body`. A cold `tasks_upsert` auto-creates the board. The upsert response is a pure ack for
YOUR call (added/updated/removed cover only your nodes); to catch up on everyone's changes
call `tasks_delta` with `sinceVersion` = a previous `currentVersion`. `nodes`/`entries` are
TYPED arrays — pass real JSON arrays, not stringified JSON.

**`tasks_search` is THE read verb** — two modes: without `q` it's a deterministic LISTING
(pass `board` for one board, omit for the whole project; default order priority-then-key),
with `q` it's hybrid relevance search (FTS ⊕ vectors). Filters work in both modes:
`status[]`, `keys[]` (slug|NodeId), `under` (subtree), `includeClosed`; `sort{by,desc}`
reorders; `bodyLen` snippets bodies. One node in full: `tasks_node_get`.

**Memory entries are typed** (`user` | `feedback` | `project` | `reference`) — `type` is
required on `memory_upsert`; `tags` is an ARRAY of strings ([] clears, omit keeps).
`memory_search` is THE read verb: with `q`
a hybrid relevance search (FTS ⊕ vectors), without `q` a deterministic listing (updated
desc); no `scope` cascades project ⊕ workspace over every store (use `bodyLen` for snippets).

**Canon** — SessionStart injects an index from memory store `canon`, key `index` (per
scope: `{{PROJECT}}` project / `{{WORKSPACE}}`). To edit it: `memory_upsert` with
`store:"canon"`, `key:"index"` at the matching scope; keep it a compact index of pointers,
not a growing doc.

**What goes where:**
- Session (`session_*`) — the current working plan/thinking. "Stale next week?" → session.
- Tasks (`tasks_*`) — a unit of work with a status tracked to Done.
- Memory (`memory_*`) — a durable fact that should outlive the work. Don't store what
  code/git already records, transient state, secrets, or actionable work (that's a task).

**Tools:**
- `tasks_board_list / board_create / board_delete / search / node_get / upsert / delta / workflow`
- `memory_store_list / store_create / store_delete / search / remember / get / upsert / delta`
- `session_search / get / upsert / append / delete` (`search` without `q` = the session listing; with `q` = two-stage archive search whose hits carry message ordinals for `session_get`)
- Logs: `log_query` (KQL), `log_create / list / delete`
- Admin (per-type, flat params): `project_create / list`, `apikey_create / list / delete`,
  `db_create / list / delete / describe`
