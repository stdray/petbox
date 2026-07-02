# Dogfooding backlog

Captured locally because the live instance's per-file memory/tasks writes are
currently failing (see Blocker). Once fixed, these migrate into the petbox
project's Memory store + Tasks board (the "incoming" intake idea below is exactly
the mechanism for that).

## BLOCKER (2026-05-31): per-file temporal writes fail on the live instance

- Meta layer works: `tasks_board_list`/`memory_store_list` succeed; a cold
  `tasks_upsert`/`memory_upsert` **creates the board/store meta** (in the main
  `petbox.db`) — so feature flags are on and the key has read+write scopes.
- Per-file layer fails: writing the actual node/entry into
  `data/{tasks,memory}/petbox/<name>.db` returns the opaque `"An error occurred
  invoking 'X'"`.
- Can't see the cause: MCP errors are opaque (finding D1) and the exception is
  logged to the `$system` self-log, which a petbox-scoped key can't query.
- Hypotheses: (a) `app` user can't create the nested subdir / file on the mounted
  `/app/data` volume; (b) WAL behaviour on the server's filesystem (tier files use
  WAL since A1; the main DB + log tier don't, and they work).
- **Fastest diagnosis:** read the deployed container logs (`docker logs <petbox>`)
  around a failed upsert — the real exception is there. Alternative: ship
  non-opaque MCP errors (D1 fix) then retry.

## User-reported UX/feature requests (2026-05-31)

1. **Stay in admin after creating a project.** Creating a project from the admin
   area bounces the user out to the `/ui` log-view; should remain in the admin zone.
2. **Preserve session across deploy.** A deploy logs the user out; session should
   survive (likely DataProtection keys / auth cookie persistence on the volume).
3. **Footer feature flags.** In the footer (where the version is shown) list which
   feature modules are enabled vs disabled.
4. **Edit key permissions.** Add UI to edit an existing API key's scopes/permissions
   (today scopes are fixed at mint; relates to finding D5).
5. **User-filed tasks + an "incoming" intake phase.** Let a user file tasks from the
   UI into a dedicated `incoming` phase; the agent periodically re-reads `incoming`
   and triages/distributes those tasks into the real phases. (This is also the
   natural home for this backlog file once writes work.)

## Dogfooding findings (agent, 2026-05-31)

- **D1 — MCP errors are opaque.** Failing calls return only `"An error occurred
  invoking 'X'"`, no cause. Blocked every diagnosis here. Surface a category
  (feature-off | missing-scope:<scope> | project-mismatch | server-error:<msg>).
  *Highest-priority fix.*
- **D2 — projectKey not self-discoverable.** No whoami; a project-scoped key can't
  enumerate projects. Add a ping/verify tool returning the key's project + scopes.
- **D3 — guide misleads on key storage.** `/doc/agent` says put the key in
  `.env.local`, but Claude Code expands `${VAR}` from the process env, not from
  `.env.local`. Tell the agent to export the env var (setx) before launch.
- **D4 — features off by default.** The deployed instance shipped with all modules
  off, so tasks/memory silently failed AssertFeature. Now all enabled; add a
  startup log line listing enabled features.
- **D5 — Connect-agent key defaulted to read-only.** First minted key had
  read scopes but not write; writes failed opaquely. Default agent keys to
  read+write, or flag the requirement (ties to request #4).
