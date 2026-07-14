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

Kit modules (all under `src/clients-ts/petbox-wire/src/`):

- `wire.ts` — bootstrap CLI (everything below; the only module with a top-level `main()`, so it is
  never importable by a test — that is why `posix-env.ts` / `telemetry-settings.ts` / `wire-exit.ts`
  exist as separate side-effect-free modules).
- `registry.ts` — reads `~/.petbox/projects.json`, longest-prefix match of cwd → project + key
  (key from `process.env[VAR]`, else `~/.petbox/keys.json`).
- `protocol.ts` — the **single source** for the injected memory-protocol text. `buildProtocol(project, tool, opts)`
  renders one canonical text parametrized only by the MCP tool namer (`mcp__petbox__<verb>` for
  Claude Code, `petbox_<verb>` for opencode, `petbox___<verb>` for droid) plus the opt-in
  resume/compact suffix. All three SessionStart injectors render from it so the texts can't drift
  as hand-synced copies.
- `canon.ts` — memory-canon fetch + LKG cache (`~/.petbox/cache/<project>.canon.md`); see §6.
- `push-session.ts` — Claude Code **Stop** hook (mirrors the transcript into the Session module).
- `pull-memory.ts` — Claude Code **SessionStart** hook (injects the memory protocol + canon).
- `opencode-plugin.ts` — global opencode plugin (system-prompt memory protocol + `session.idle` push).
- `droid-pull-memory.ts` — Factory Droid **SessionStart** hook (injects the memory protocol + canon).
- `droid-push-session.ts` — Factory Droid **Stop** hook (mirrors the transcript into the Session module).
- `droid-transcript.ts` — droid JSONL adapter (thin wrapper over `transcript.ts`'s shared extract/exclude rules).
- `transcript.ts` — Claude Code transcript parsing + the shared `extractText`/`isExcluded` rules.
- `append.ts` — the shared session-push HTTP call the Stop hooks / plugin use.
- `import-sessions.ts` — one-shot backfill of the local agent history (§7).
- `posix-env.ts` — POSIX half of `persistKeyForAgents` (regenerates `~/.petbox/env.sh` from the key
  store and marker-guards the login-profile source lines).
- `telemetry-settings.ts` — builds the OTLP export env (`--telemetry`), split into a non-secret half
  (→ `.claude/settings.json`) and the API-key-bearing header (→ `.claude/settings.local.json`).
- `agent-definition.ts` — the portable agent-definition type + validator + the built-in
  `DEFAULT_AGENT_DEFINITION`. Roles carry `slug`/`tier`/`requiredCapabilities`/`spawn`/`escalation`
  and **never** a model id.
- `agent-def-fetch.ts` — `GET /api/{project}/agent-defs/{key}` (`agents:read`) + the LKG cache
  `~/.petbox/cache/<project>.agent-def.json`. Resolution: server → LKG (with a staleness mark) →
  built-in DEFAULT (only when no cache). Best-effort: never throws.
- `harness-capabilities.ts` — kit data: which capabilities each harness declares
  (`HARNESS_IDS = claude-code, opencode, droid`). Every cell is a factual claim from that harness's docs.
- `truthfulness.ts` — the gate: list every `(role, capability)` a role requires that the target
  harness does not declare. Non-empty ⇒ the caller must fail loud.
- `apply-artifacts.ts` — pure `planApply(definition, harness, roleModels)` → the per-harness role
  files. Clean roles are emitted; a dirty role is skipped WHOLE and reported (never written with the
  offending line silently dropped).
- `roles.ts` — the local role→model binding store `~/.petbox/roles.json` (`activeProfile` +
  `profiles.<name>.agents.<harness>.roles.<role>.model`). Machine-authoritative, offline, never
  uploaded, never invents a model.
- `wire-exit.ts` — the exit taxonomy (`WIRE_EXIT`, `classifyApplyExit`); see §2b.
- `templates/SKILL.md` — per-project petbox skill template (`{{PROJECT}}` / `{{WORKSPACE}}`).
- `templates/agent-factory/SKILL.md` — the on-demand `petbox-agent-factory` skill (no placeholders):
  the `roles` / `profile` / `doctor` / `apply` procedure.

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
   - `--env` is optional; the canonical derived name is **`PETBOX_<PROJECT>_API_KEY`** (project key
     upper-cased, runs of non-alphanumerics → a single `_`, leading/trailing `_` trimmed, `PETBOX_`
     prefixed — the same derivation the UI Connect page shows). `$system` → `PETBOX_SYSTEM_API_KEY`,
     `kpvotes` → `PETBOX_KPVOTES_API_KEY`. A machine wired **before** this scheme keeps its recorded
     name: the var is read back from `~/.petbox/projects.json` rather than re-derived, so the legacy
     CLI form (`<PROJECT>_API_KEY`, e.g. `_SYSTEM_API_KEY`) survives a re-run. `~/.petbox/keys.json`
     is the ground truth for what a given machine actually has.
   - `--workspace` is optional and has **no hardcoded default**: the workspace comes from the server
     (`GET /api/auth/validate` reports it for the key). Resolution order: `--workspace` (explicit
     override) → the workspace the server reports → a usage error (exit 2) if neither. It is used for
     exactly one thing: stamping `{{WORKSPACE}}` into the generated SKILL.md.
   - `--key` persists the key (after validation) to `~/.petbox/keys.json` AND to a real
     environment variable (Windows user-scope env / POSIX `~/.petbox/env.sh` sourced from the
     login profiles) — the MCP configs reference `${VAR}`. Omit it once the key is already
     stored. Kit hooks see the key immediately; **agents need a new terminal** after the first
     wiring so their MCP configs resolve the env var.
   - `--telemetry` / `--telemetry-log <name>` — opt-in Claude Code OTLP export (§2c).
   - `--cleanup-legacy` — remove a project's old per-project hook/plugin copies (§3).
   - `--help` / `-h` — the usage banner on stdout, exit 0.

## 2. What the full wire does (idempotent, 10 steps)

1. Derive the env-var name (`--env`, else the existing registry entry's `envVar` for this
   prefix, else `PETBOX_<PROJECT>_API_KEY`).
2. Obtain the key (`--key`, else `process.env[VAR]`, else `~/.petbox/keys.json`). If absent, it
   prints how to mint one and exits 1.
3. Validate the key: `GET /api/auth/validate` with `X-Api-Key`. On 200 it compares the returned
   `project` to your `<project>` and aborts on mismatch; 401 aborts; a missing/non-standard
   endpoint only warns and continues. (Contract: `src/PetBox.Core/Auth/AuthApi.cs`.) Validation
   runs BEFORE persistence, so a bad key never lands in the stores. The same response also carries
   the **workspace** the key belongs to — that is where `{{WORKSPACE}}` comes from when `--workspace`
   is not passed. No workspace from either source ⇒ usage error, exit 2 (there is no fallback).
4. Persist the key everywhere agents look: the cross-platform key store `~/.petbox/keys.json`
   (merge, never clobber; POSIX `chmod 0600`) for the kit hooks, plus a real environment
   variable for the MCP configs — Windows: user-scope env via PowerShell; POSIX:
   `~/.petbox/env.sh` regenerated from the key store and sourced (marker-guarded) from
   `~/.profile`/`~/.bashrc`/`~/.zshenv`. Header values in the committed configs stay as `${VAR}`
   references, so no secret lands in any project file.
5. Copy the running kit (npx cache or checkout `src/`) into the stable location `~/.petbox/wire/`
   (whole `src/` dir: all `.ts` + `templates/`, overwrite). It is an **exact mirror, never a union**:
   top-level entries present in `~/.petbox/wire/` but not shipped by this kit are removed first
   (orphan cleanup), so a downgrade cannot leave a newer file standing next to older peers. A short
   content fingerprint is printed before → after. Skipped when already running the installed copy.
   Every global link in step 8 is computed from this stable path.
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
   - `.claude/skills/petbox-agent-factory/SKILL.md` + `.factory/skills/petbox-agent-factory/SKILL.md`
     — the on-demand **agent-factory** skill (`templates/agent-factory/SKILL.md`, no placeholders):
     the `roles` / `profile` / `doctor` / `apply` procedure. Written to both surfaces, same as the
     petbox skill; it is a skill an agent loads when it needs it, not every session.
   - *7b (opt-in, `--telemetry`)*: ensure the named log exists
     (`POST /api/logs/<project>/logs`; 201 or 409 = ready, anything else aborts), then merge the OTLP
     export env into `.claude/settings.json` (non-secret) and the API-key-bearing
     `OTEL_EXPORTER_OTLP_HEADERS` into `.claude/settings.local.json` (gitignored). See §2c.
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

## 2a. Subcommands (no `<dir> <project>`)

Every subcommand below resolves the project itself (longest registry prefix against cwd) or needs
none at all. They are dispatched **before** arg parsing, so they never require a key.

| Command | What it does |
| --- | --- |
| `petbox-wire update` | Mirror this package's `src/` into `~/.petbox/wire/` (same orphan cleanup as step 5, content hash before → after). **Only** that: no keys, no registry, no hooks reinstall, no MCP/skills regeneration, no sticky-flag reset. It does **not** compile agent artifacts — that is `apply`. |
| `petbox-wire apply [--definition <key>] [--offline]` | Compile the per-harness agent role files from the portable definition + the local role→model binding. See §2d. |
| `petbox-wire doctor` | Offline truthfulness gate: run `checkTruthfulness(DEFAULT_AGENT_DEFINITION, harness)` for every id in `HARNESS_IDS` and print OK or each violation. The local binding is *noted*, not required. |
| `petbox-wire roles` | Print `activeProfile` + the resolved role→model tree from `~/.petbox/roles.json`. Offline. An empty store exits **0** with a message — it never invents a model. |
| `petbox-wire roles export` | Write a bootstrap copy of `roles.json` to **stdout** (no secrets); pipe it to a file on a new machine. Offline. |
| `petbox-wire profile use <name>` | Set `activeProfile` in `~/.petbox/roles.json`, creating an empty profile shell if the name is new. Offline; compiles nothing — re-run `apply` afterwards. |

## 2b. Exit codes

`src/wire-exit.ts` is the single definition (`WIRE_EXIT` + the pure `classifyApplyExit`, so the
classification is unit-testable without spawning a process):

| Code | Meaning |
| --- | --- |
| `0` | Success. |
| `1` | Hard failure — invalid definition, unexpected throw. |
| `2` | Usage / bad arguments. |
| `3` | Truthfulness policy block — some roles/harnesses were refused. **A partial write is possible.** |

**Be honest about the scope:** only `apply` and `doctor` use the full 0/1/2/3 taxonomy. The
**full-wire path** exits `2` only through `usage()` (bad/absent args) and `1` on *any* other failure
— missing key, validation abort, telemetry-log failure, self-smoke failure. Do not script against `3`
outside `apply`/`doctor`. `3` is a *policy* outcome, not a crash: the definition asked a harness for a
capability it does not declare. Fix the definition (or accept the skip) — retrying changes nothing.

## 2c. Telemetry (`--telemetry`, off by default)

Claude Code only: opencode's and droid's OTLP exporters append `/v1/{signal}` to a base endpoint and
cannot carry the project/log path PetBox's path-scoped ingest (`/v1/{metrics,logs}/{project}/{log}`)
requires. `--telemetry-log <name>` picks the target named log (default `cc-telemetry`); it is created
if missing (409 = already there = success), because the ingest 404s when the log is absent.

The env is written **split by secrecy**: endpoints/protocol/interval → `.claude/settings.json`;
`OTEL_EXPORTER_OTLP_HEADERS` (which carries the raw API key) → `.claude/settings.local.json`, the
conventionally-gitignored local override. The key is written **resolved, not as `${VAR}`** — Claude
Code does not expand `${VAR}` inside `settings.json` `env` values (verified 2026-07-06), so a
reference form ships the literal string and the ingest 401s. That also **pins** the value: rotate the
project key and the header goes stale — re-run the wire with `--telemetry` to re-provision.

## 2d. `apply` — compiled agent artifacts

Not part of the full wire; run it explicitly. It resolves the project root by the longest matching
prefix in `~/.petbox/projects.json` (falling back to cwd), resolves the definition
**server → LKG cache → built-in DEFAULT** (`--offline` skips the network), then per harness writes:

| Harness | Path |
| --- | --- |
| `claude-code` | `.claude/agents/<role>.md` |
| `opencode` | `.opencode/agent/<role>.md` |
| `droid` | `.factory/droids/<name>.md` |

These files are **overwritten** — they are generated. A role is written only when the target harness
declares every capability the role requires; a dirty role is skipped WHOLE and reported, and clean
roles in the same run are still written (⇒ exit 3, partial write). `model:` frontmatter appears only
when `roles.json` binds that role (droid unbound → `model: inherit`); a concrete model id is never
invented. Three sources, three owners: the definition is **server**-authoritative, `roles.json` is
**machine**-authoritative, the capability matrix is **kit** data.

## 2e. What lives under `~/.petbox/`

| Path | Owner / contents |
| --- | --- |
| `wire/` | The stable kit copy. Every global hook / plugin shim points here. Refreshed by a full wire or `update`; an exact mirror of the shipped kit. |
| `projects.json` | Registry: `{prefix, project, envVar, baseUrl?}` per entry. Resolved by longest prefix against cwd. |
| `keys.json` | Flat `{ "<ENV_VAR>": "<key>" }` the kit hooks read directly (no env var needed). POSIX `0600`. Ground truth for "what is my env-var actually called". |
| `env.sh` | POSIX only — regenerated from the whole key store, sourced (marker-guarded) from the login profiles. |
| `roles.json` | Local role→model bindings + `activeProfile`. Machine-authoritative; never uploaded. |
| `cache/<project>.agent-def.json` | LKG copy of the last successfully fetched agent definition (written on every successful fetch; used with a staleness mark when the server is unreachable or `--offline`). |
| `cache/<project>.canon.md` | LKG copy of the memory canon (§6). |

Nothing here is regenerated by `update` except `wire/` itself.

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
2. Publish by pushing the **`npm-wire`** tag — CI (`./build.sh --target=NpmWirePublish`)
   stamps the GitVersion version and publishes `petbox-wire` (one tag per package channel;
   `npm` still publishes only `@stdray-npm/petbox-client`).
3. On each machine, refresh:
   - kit text only (hooks / protocol / scripts / templates changed) → `npx petbox-wire@latest update`
     — no key, no registry write, no sticky-flag reset;
   - anything per-project (MCP config, rendered SKILL.md, registry entry, hook install) →
     `npx petbox-wire@latest <dir> <project> --key <KEY>` (or the dev checkout command), which
     refreshes `~/.petbox/wire/` *and* the generated config;
   - agent role files → `npx petbox-wire apply` (neither of the above compiles them).

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

Session start is the **only** point at which the wiring injects context — there is no per-prompt
injection; within a session an agent pulls what it needs through the MCP tools.

- **Endpoint:** `GET {baseUrl}/api/memory/{project}/canon` with header `X-Api-Key` → 200
  `{ "project": {body,updatedAt,version}|null, "workspace": {...}|null }`. Best-effort, a 2 s
  wall-clock budget (`SESSION_FETCH_BUDGET_MS` in `pull-memory.ts` / `droid-pull-memory.ts`).
  The block carries a `### Project ({project})` section and/or a `### Workspace`
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
