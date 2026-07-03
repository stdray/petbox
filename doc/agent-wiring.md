# Agent-wiring kit

A single global TypeScript kit that wires any project to PetBox for **Claude Code**, **opencode**
and **Factory Droid** — instead of copying PowerShell hooks and a per-project opencode plugin into
every repo. The logic is installed once at user scope; each project keeps only thin config files
(no logic). The active project is resolved per call by **cwd** against a global registry, so the
global hooks are a clean no-op in unregistered folders.

The kit ships as the **`petbox-wire`** npm package, so a project can be wired without cloning the
repo: `npx petbox-wire <dir> <project> --key <KEY>`.

Kit locations:
- **Repo (source of truth):** `src/clients-ts/petbox-wire/src/` (the package's `src/`; `wire.ts`
  is the bootstrap CLI, `bin/petbox-wire.js` is the npm launcher).
- **Runtime (stable copy):** `~/.petbox/wire/` — `wire.ts` copies the kit here on every run, and
  every global hook / opencode plugin link points at this stable path (so wiring survives npx
  cache eviction and does not depend on any checkout).

- `registry.ts` — reads `~/.petbox/projects.json`, longest-prefix match of cwd → project + key
  (key from `process.env[VAR]`, else `~/.petbox/keys.json`).
- `protocol.ts` — the **single source** for the injected memory-protocol text. `buildProtocol(project, tool, opts)`
  renders one canonical text parametrized only by the MCP tool namer (`mcp__petbox__<verb>` for
  Claude Code / Droid, `petbox_<verb>` for opencode) plus the opt-in resume/compact suffix. All
  three SessionStart injectors render from it so the texts can't drift as hand-synced copies.
- `push-session.ts` — Claude Code **Stop** hook (mirrors the transcript into the Session module).
- `pull-memory.ts` — Claude Code **SessionStart** hook (injects the memory protocol).
- `opencode-plugin.ts` — global opencode plugin (system-prompt memory protocol + `session.idle` push).
- `droid-pull-memory.ts` — Factory Droid **SessionStart** hook (injects the memory protocol + canon).
- `droid-push-session.ts` — Factory Droid **Stop** hook (mirrors the transcript into the Session module).
- `droid-transcript.ts` — droid JSONL adapter (thin wrapper over `transcript.ts`'s shared extract/exclude rules).
- `transcript.ts` — Claude Code transcript parsing + the shared `extractText`/`isExcluded` rules.
- `wire.ts` — bootstrap CLI (everything below).
- `templates/SKILL.md` — per-project Claude skill template (`{{PROJECT}}` / `{{WORKSPACE}}`).

Runtime: plain TypeScript executed by **node ≥ 23.6** native type-stripping. Zero dependencies.
(No `enum`/`namespace`/parameter-properties; type-only imports; relative imports with explicit
`.ts`.) The npm package carries `bin/petbox-wire.js` (a plain-JS launcher that checks the Node
version, then imports `wire.ts`) plus the `src/` kit.

## 1. Wiring a new project

1. **Mint an API key.** From a Claude Code session on the `$system` project, call
   `mcp__petbox__apikey_create` scoped to the new project. (Key minting is intentionally out of
   scope for `wire.ts`.)
2. **Run wire** (no clone needed):
   ```
   npx petbox-wire <dir> <project> --key <KEY> --env <VAR>
   ```
   Dev, from a checkout (identical behavior — the kit is still copied to `~/.petbox/wire/`):
   ```
   node src/clients-ts/petbox-wire/src/wire.ts <dir> <project> --key <KEY> --env <VAR>
   ```
   - `--env` is optional; default is `<PROJECT>_API_KEY` (uppercased, non-alphanumerics → `_`).
   - `--workspace` is optional; default `stdray` (used only for the SKILL.md permalink).
   - `--key` persists the key (after validation) to `~/.petbox/keys.json` AND to a real
     environment variable (Windows user-scope env / POSIX `~/.petbox/env.sh` sourced from the
     login profiles) — the MCP configs reference `${VAR}`. Omit it once the key is already
     stored. Kit hooks see the key immediately; **agents need a new terminal** after the first
     wiring so their MCP configs resolve the env var.

## 2. What `wire.ts` does (idempotent, 10 steps)

1. Derive the env-var name (`--env`, else the existing registry entry's `envVar` for this
   prefix, else `<PROJECT>_API_KEY`).
2. Obtain the key (`--key`, else `process.env[VAR]`, else `~/.petbox/keys.json`). If absent, it
   prints how to mint one and exits 1.
3. Validate the key: `GET /api/auth/validate` with `X-Api-Key`. On 200 it compares the returned
   `project` to your `<project>` and aborts on mismatch; 401 aborts; a missing/non-standard
   endpoint only warns and continues. (Contract: `src/PetBox.Core/Auth/AuthApi.cs`.) Validation
   runs BEFORE persistence, so a bad key never lands in the stores.
4. Persist the key everywhere agents look: the cross-platform key store `~/.petbox/keys.json`
   (merge, never clobber; POSIX `chmod 0600`) for the kit hooks, plus a real environment
   variable for the MCP configs — Windows: user-scope env via PowerShell; POSIX:
   `~/.petbox/env.sh` regenerated from the key store and sourced (marker-guarded) from
   `~/.profile`/`~/.bashrc`/`~/.zshenv`. Header values in the committed configs stay as `${VAR}`
   references, so no secret lands in any project file.
5. Copy the running kit (npx cache or checkout `src/`) into the stable location `~/.petbox/wire/`
   (whole `src/` dir: all `.ts` + `templates/SKILL.md`, overwrite). Skipped when already running
   the installed copy. Every global link in step 8 is computed from this stable path.
6. Upsert the registry entry `~/.petbox/projects.json`: `{prefix: <dir>, project, envVar}`
   (replace by prefix; other entries untouched). `baseUrl` is written only when non-default.
7. (Re)generate per-project files in `<dir>`:
   - `.mcp.json` — Claude Code MCP, `X-Api-Key: ${VAR}` (petbox-only file, regenerated whole).
   - `.opencode/opencode.json` — opencode remote MCP, `X-Api-Key: {env:VAR}` (regenerated whole).
   - `.factory/mcp.json` — Factory Droid MCP, `X-Api-Key: ${VAR}` — **merged** (never clobbers
     other team servers or top-level keys; only the `petbox` entry is regenerated). Droid expands
     `${VAR}` in header values, so the key stays in the env var, never the file.
   - `.claude/skills/petbox/SKILL.md` — from the template with `{{PROJECT}}`/`{{WORKSPACE}}`.
     Serves Claude Code natively **and** opencode, which discovers it via its Claude-compatible
     skills path (`.claude/skills/…`); wire.ts deliberately writes no second `.opencode/skills/`
     copy (a same-name duplicate whose resolution opencode does not document).
   - `.factory/skills/petbox/SKILL.md` — the same rendered skill for Factory Droid (its native
     skills root is `.factory/skills/`; its Claude-compat root is `.agent/skills/`, not
     `.claude/skills/`, so it needs a dedicated copy).
8. Global install (idempotent; all commands point at the stable copy `~/.petbox/wire/`):
   - `~/.claude/settings.json` — **merge** a `Stop` → `node "~/.petbox/wire/push-session.ts"` and
     `SessionStart` → `node "~/.petbox/wire/pull-memory.ts"` hook. The rest of the live settings
     (env/permissions/statusLine/model/…) is preserved; duplicate commands are not re-added.
     **Stale-hook prune:** any existing kit hook whose command does not point at the current
     stable path (e.g. an old checkout path like `…/agents/wiring/push-session.ts`) is removed.
   - `~/.factory/settings.json` — **merge** a `Stop` → `node "~/.petbox/wire/droid-push-session.ts"`
     and `SessionStart` → `node "~/.petbox/wire/droid-pull-memory.ts"` hook under the `hooks` key
     (same merge + stale-prune semantics). Factory Droid uses the Claude-Code-compatible hook
     shape and snake_case payloads; the reference documents no `enableHooks` gate, so none is
     written.
   - `~/.config/opencode/plugins/petbox.ts` — a thin shim that re-exports the kit plugin from the
     stable copy's `file:///` URL (single source of truth; overwritten each run).
9. `--cleanup-legacy` (see §3).
10. Self-smoke: `POST /api/sessions/<project>/wire-smoke?agent=wire` (application/x-ndjson) and assert a numeric `version` in the response.

## 3. Migrating a legacy (per-project copy) repo

Run `wire.ts` with `--cleanup-legacy`. After (re)generating config it removes the old in-repo
logic from `<dir>` only:

- `.claude/hooks/` — the whole folder.
- `.claude/settings.local.json` — **only** the `hooks` key (permissions etc. are kept; absent
  file is skipped).
- `.opencode/plugin/` — the per-project plugin folder.
- `.opencode/package.json` + `bun.lock` + `node_modules` — only if `package.json` depends
  solely on `@opencode-ai/plugin` (otherwise kept, with a note).

## 4. Fixing / evolving the kit

The canonical source is `src/clients-ts/petbox-wire/src/` in the repo. The runtime source of
truth on each machine is the stable copy `~/.petbox/wire/`, which every global hook and the
opencode shim point at.

Workflow to ship a kit change:
1. Edit the kit in the repo (`src/clients-ts/petbox-wire/src/…`).
2. Publish by pushing the **`npm`** tag — CI (`./build.sh --target=NpmPublish`) stamps the
   GitVersion version and publishes **both** npm packages (`@stdray-npm/petbox-client` and
   `petbox-wire`).
3. On each machine, re-run `npx petbox-wire@latest <dir> <project> --key <KEY>` (or the dev
   checkout command). This refreshes `~/.petbox/wire/` and the generated config.

Editing `~/.petbox/wire/` in place is no longer canonical — it is overwritten on the next run.
Re-running `wire.ts` for a project is also how you change that project's config/registry entry.

## 5. Gotchas

- **Two key surfaces.** The kit hooks read `process.env[VAR]` first, then `~/.petbox/keys.json`
  (via `registry.ts`) — they work immediately after wiring. The agents' MCP configs resolve
  `${VAR}` from the real environment only, which wire persists per platform (Windows user-scope
  env / POSIX `env.sh` + profile source) — **they need a new terminal / login shell** after the
  first wiring. An already-exported env var always wins over the file.
- **Stale MCP schema in a live session after a PetBox deploy.** Newly-added MCP tool params are
  cached per session; smoke them from a fresh session, not the one open during deploy.
- **A folder outside the registry → hooks no-op.** This is normal and intended: the global hooks
  run in every project, resolve `null` for unregistered cwds, and silently do nothing.

## 6. Memory canon injected at session start

Both SessionStart injectors (`pull-memory.ts` for Claude Code, `opencode-plugin.ts` for
opencode) append the project's **memory canon** — the curated memory index, pointers to the
durable facts — beneath the memory protocol. The shared builder is `canon.ts`, so the injected
block is byte-identical across agents.

- **Endpoint:** `GET {baseUrl}/api/memory/{project}/canon` with header `X-Api-Key` → 200
  `{ "project": {body,updatedAt,version}|null, "workspace": {...}|null }`. Best-effort, ~8 s
  timeout. The block carries a `### Project ({project})` section and/or a `### Workspace`
  section — a section whose part is `null` is omitted; when both are empty nothing is injected.
- **Offline cache:** every successful fetch writes the block to
  `~/.petbox/cache/{project}.canon.md`. If a later fetch fails and a cache file exists, the
  cached block is injected instead, prefixed with a stale marker line
  (`⚠ Canon below is from the local cache (PetBox unreachable) — may be stale.`).
- **Graceful degradation:** the endpoint is new — a server without it (404), any other error,
  a timeout, or bad JSON simply yields no canon block (or the stale cache, if present). The
  memory protocol is always injected regardless; the canon is purely additive. `canon.ts`
  never throws.

## 7. Importing local session history

`import-sessions.ts` backfills the PetBox session archive from the agents'' LOCAL history —
run it once after wiring a project (or any time) to make the whole past searchable:

Run it from the stable runtime copy (installed by `wire.ts`), or from a checkout:

```bash
node ~/.petbox/wire/import-sessions.ts                                          # cwd project, all agents
node ~/.petbox/wire/import-sessions.ts --agent claude --project mykey
# dev, from a checkout:
node src/clients-ts/petbox-wire/src/import-sessions.ts
# flags: --dry-run  --since YYYY-MM-DD  --limit N  --force
```

- Sources: Claude Code (`~/.claude/projects/*/*.jsonl`, attributed by the cwd recorded
  inside each transcript) and opencode (`~/.local/share/opencode/storage`, attributed by
  the session''s `directory`). Both resolve through the same registry matching the hooks use.
- Sessions are pushed under their agents'' NATIVE ids (the same ids the live hook/plugin
  uses), so re-imports replace, never duplicate; and the importer is **upgrade-only** — it
  skips any session whose server version is already >= the local message count (`--force`
  to override), so a stale file read can''t roll back a fresher snapshot.
- Only dialogue turns are sent (the shared `transcript.ts` parsing the Stop hook uses);
  raw tool outputs never leave the machine.
- After a big import the server pipelines (digest -> facts -> patterns) backfill in the
  background; with the budgeted drain expect tens of minutes for a multi-MB archive.
  Search fills in as it warms — nothing blocks.

## 8. Factory Droid specifics

Factory Droid (the `droid` CLI) is a **first-class** wiring target: after `wire.ts` it has MCP,
hooks and skills with zero manual steps, at parity with Claude Code.

- **MCP registration:** `<dir>/.factory/mcp.json` — Droid's documented project-level MCP config
  (`docs.factory.ai/cli/configuration/mcp`). Shape is the standard `mcpServers` map:
  `{"mcpServers":{"petbox":{"type":"http","url":"https://petbox.3po.su/mcp","headers":{"X-Api-Key":"${VAR}"},"disabled":false}}}`.
  Droid expands `${VAR}` (and `${VAR:-default}`) in `url` and header **values**, so the API key
  lives in the env var, never the committed file (matches Droid's "never put secrets in project
  config" guidance). `wire.ts` **merges** the `petbox` entry in — any other team servers and
  top-level keys are preserved; re-runs are byte-identical. (An equivalent interactive path is
  `droid mcp add petbox https://petbox.3po.su/mcp --type http --header "X-Api-Key: ${VAR}"`;
  writing the file directly keeps the merge idempotent and offline.)
- **Skills:** `<dir>/.factory/skills/petbox/SKILL.md` — Droid's documented native skills root
  (`docs.factory.ai/cli/configuration/skills`: workspace skills live at
  `<repo>/.factory/skills/<name>/SKILL.md`, YAML frontmatter with `name`/`description`). It is the
  same rendered template used for Claude Code. Droid's *only* Claude-compat skills root is
  `.agent/skills/` (not `.claude/skills/`), so it needs its own copy rather than piggybacking on
  the Claude one.

It also wires the same two hook behaviors as the other agents from the shared modules:

- **Settings location:** `~/.factory/settings.json`, `hooks` key (a documented fallback for
  `~/.factory/hooks.json`). Same JSON shape as Claude Code:
  `{"hooks":{"SessionStart":[{"hooks":[{"type":"command","command":"…"}]}]}}`. `wire.ts` merges
  the two droid hooks in and never clobbers existing content. The hooks reference documents no
  `enableHooks` flag gating execution, so none is written.
- **Hook events:** `SessionStart` → `droid-pull-memory.ts` (injects protocol + canon),
  `Stop` → `droid-push-session.ts` (mirrors the transcript).
- **Payloads:** droid delivers snake_case stdin (`session_id`, `transcript_path`, `cwd`,
  `source`, `stop_hook_active`) — the same fields Claude Code uses, so the hooks read them
  identically.
- **SessionStart output contract:** `droid-pull-memory.ts` returns context via the documented
  structured form `{ "hookSpecificOutput": { "hookEventName": "SessionStart", "additionalContext": "…" } }`
  on stdout (stdout-as-context is also accepted; the structured form is the documented preference).
- **MCP tool naming:** droid exposes MCP tools as `mcp__<server>__<tool>` (identical to Claude
  Code), so the petbox verbs are `mcp__petbox__*` and the injected protocol renders byte-identical
  to the Claude Code hook via the shared `protocol.ts` builder.
- **Transcript adapter:** `droid-transcript.ts` parses droid JSONL (line 1
  `{type:"session_start", …}`; turns `{type:"message", message:{role, content}}`, content a
  string or `text`/`thinking`/`tool_use`/`tool_result` parts). It keeps user/assistant TEXT
  turns only and reuses `transcript.ts`'s shared `extractText`/`isExcluded` rules, so
  `<system-reminder>` injections (and `visibility:"llm_only"` records) and tool dumps are
  dropped. Pushes under `agent:"droid"`.

## Headless / exec run modes (gotchas from live experiments, 2026-07-03)

Non-interactive runs behave differently per agent — the wiring works in all three, but the
launch flags and sandbox rules differ:

- **Factory Droid** (`droid exec "…"`): read-only by default — a task that edits files or
  writes over MCP needs `--auto medium` or `--auto high`, else it halts with "insufficient
  permission". At runtime droid names MCP tools `petbox___<tool>` (triple underscore) — the
  docs' `mcp__<server>__<tool>` form did not match the shipped CLI (the protocol renders
  droid tool names accordingly, see `protocol.ts` / `droidPetboxTool`).
- **opencode** (`opencode run "…"`): permission prompts auto-reject in run mode, and paths
  OUTSIDE the project folder are `external_directory` — a sibling `git worktree` is
  unreachable. Create worktrees INSIDE the project folder (e.g. `./.wt-<task>`), remove them
  when done.
- **Claude Code** (`claude -p "…"`): headless prompts auto-deny too — pass
  `--permission-mode acceptEdits` (or, deliberately and only on a trusted task,
  `--dangerously-skip-permissions`). Claude Code also carries a built-in
  "commit/push only when asked" default that can override the repo's process contract in
  one-shot runs — phrase headless tasks with an explicit finish line ("доведи до Review:
  ветка + коммит + пуш"), which satisfies that rule.
