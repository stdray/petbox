---
name: petbox
description: Shared task boards, memory and session plans for this project via the PetBox MCP server (server name `petbox`). Use to record/read plans, durable notes and working-session state for PetBox development.
---

This project is connected to a PetBox instance over MCP (server `petbox`, https://petbox.3po.su).
Pass projectKey "petbox" in every call.

**Plans are a Phase > Wave > Task tree.** Identify nodes by phase/wave/task (not a flat
label); give each a short `name` and a detailed `body`. A cold `tasks.upsert` auto-creates
the board. The upsert response is the fresh state — advance your cursor and merge
added/updated/removed instead of re-reading.

**Memory entries are typed** (`user` | `feedback` | `project` | `reference`) — `type` is
required on `memory.upsert`; `tags` is free CSV. `memory.search` is FTS5-ranked.

**What goes where:**
- Session (`session.*`) — the current working plan/thinking. "Stale next week?" → session.
- Tasks (`tasks.*`) — a unit of work with a status tracked to Done.
- Memory (`memory.*`) — a durable fact that should outlive the work. Don't store what
  code/git already records, transient state, secrets, or actionable work (that's a task).

**Tools:**
- `tasks.board_list / board_create / board_delete / get / upsert / delta`
- `memory.store_list / store_create / store_delete / list / search / get / upsert / delta`
- `session.append / get / list`
