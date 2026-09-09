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
- `agent-definition.ts` — the portable agent-definition type + validator + `DEFAULT_AGENT_DEFINITION`,
  the kit's shipped **base layer**. Roles carry `slug`/`tier`/`requiredCapabilities`/`spawn`/`escalation`
  and **never** a model id.
- `layer-cascade.ts` — the resolver: lay ordered layer DIRECTORIES over each other (add / patch a
  field / tombstone a role / replace the roster), with per-field provenance, a trace and a
  diagnostic report. No cache, by decision: a broken layer throws with the file and the parser's
  own position rather than degrading into a stale-but-working resolve.
- `definition-source.ts` — WHERE the layers are, and the two modes every caller uses:
  `resolveLocalDefinition` (BUILD — `apply`/`doctor`; a broken layer refuses the run) and
  `resolveDefinitionForSession` (RENDER — the SessionStart hooks; never throws, but a broken layer
  puts a marker line naming the file at the head of the banner and a trace in `~/.petbox/wire.log`).
  Order: `base` (in-package `default-agents.json`) < `user` (`~/.petbox/agents`) < `project`
  (`<root>/.petbox/agents`). Nothing in the kit resolves a definition any other way, and nothing
  asks a server for one; `definition-source.test.ts` carries a structural ratchet against the
  retired `/agent-defs/` path reappearing in non-test source.
- `harness-capabilities.ts` — kit data: which capabilities each harness declares
  (`HARNESS_IDS = claude-code, opencode, droid, codex, qwen`). Every cell is a factual claim from that harness's docs.
- `truthfulness.ts` — the gate: list every `(role, capability)` a role requires that the target
  harness does not declare. Non-empty ⇒ the caller must fail loud.
- `apply-artifacts.ts` — pure `planApply(definition, harness, roleModels)` → the per-harness role
  files. Clean roles are emitted; a dirty role is skipped WHOLE and reported (never written with the
  offending line silently dropped).
- `roles.ts` — the local role→model binding store `~/.petbox/roles.json` (`formatVersion` +
  `activeProfile` + `profiles.<name>.agents.<harness>.roles.<role>`). Machine-authoritative,
  offline, never uploaded, never invents a model. A binding is `{ model, origin, provider }`
  (format **v2**, see §2g).
- `binding-provider.ts` — which provider serves a binding, parsed out of the harness's own model
  grammar (one sourced rule per harness; an unparseable value derives `null`, never a guess).
  Pure and offline: it reads no config file and makes no network call — validating a binding
  against LIVE machine config is a separate, later stage.
- `wire-exit.ts` — the exit taxonomy (`WIRE_EXIT`, `classifyApplyExit`) **and** the two sanctioned
  ways a run may end (`exitWith`, `abortRun`); see §2b and §2b-2.
- `templates/petbox/SKILL.md` — per-project petbox skill template (`{{PROJECT}}` / `{{WORKSPACE}}`).
- `templates/petbox-agent-factory/SKILL.md` — the on-demand `petbox-agent-factory` skill (no
  placeholders): the `roles` / `profile` / `doctor` / `apply` procedure.
- `templates/petbox-methodology/SKILL.md` — the on-demand `petbox-methodology` skill (only
  `{{PROJECT}}`): a THIN, project-agnostic pointer that fetches the wired project's live
  task-methodology rules via `tasks_methodology_guide` at runtime — it never bakes in this repo's
  own gates (preset `quartet`, `spec_plan`, …), because a wired project may run a different preset,
  a custom instance, or no methodology at all.
- `templates/petbox-write-economy/SKILL.md` — the on-demand `petbox-write-economy` skill (only
  `{{PROJECT}}`): `bodyRef`/`fragment` write-cost mechanisms, raw UTF-8 vs. `\uXXXX`, the `bodyLen`
  read contract, and when the mechanisms don't pay off.
- `templates/petbox-node-authoring/SKILL.md` — the on-demand `petbox-node-authoring` skill (only
  `{{PROJECT}}`): how to structure a node/comment BODY — GFM formatting the renderer already gives
  for free, the GFM-alert callout convention, the sanitized inline-SVG diagram convention (a
  `<figure>`/`<figcaption>` pair carrying the same claim as the drawing's own `role="img"`/`<title>`
  text alternative), and — the part that matters most — when a diagram is not worth drawing.
- `templates/petbox-analysis-workspace/SKILL.md` — the deliberate `petbox-analysis-workspace`
  skill (no placeholders): run a voluminous, multi-part investigation as staged files in an
  external working folder instead of hundreds of tool calls or one sprawling transcript.
- `templates/petbox-factory-run/SKILL.md` — the deliberate `petbox-factory-run` skill (no
  placeholders): drive a batch of already-written task statements to completion in one unattended
  pass — one implementer per task in its own worktree, sequential merges, gates, deploy, cards.
- `templates/petbox-second-reading/SKILL.md` — the deliberate `petbox-second-reading` skill (no
  placeholders): a blind second reading of an ask, compared against the caller's own sealed
  reading before either becomes work, plus a short acceptance tail scored against the ask itself.

**Registration and what actually pins it.** `skill-files.ts`'s `PROJECT_SKILLS` is the one place a
new skill is registered. Three separate ratchets keep the pieces from drifting, and they cover
different things — no one of them covers the prose in this file:

- `templates/` ⇄ `PROJECT_SKILLS`, both directions (`skill-files.test.ts`): a template directory
  that nothing registers, or a registry entry with no directory, fails the kit's own test suite.
- `PROJECT_SKILLS` → the kit's `README.md` "What it installs" section (`skill-files.test.ts`):
  every registered `dir` must be named there. It stops at the npm package boundary — `README.md`
  ships inside the package, this file and the Doc-site page do not.
- `templates/` → **this file** and `src/PetBox.Web/Pages/Doc/content/wire.md`
  (`tests/PetBox.Tests/Core/WireSkillDocsSyncTests.cs`): a .NET test reads the template directory
  names off disk and requires each one to be mentioned in both documents. It exists because the
  skill list in these two documents had drifted from the delivered set twice, and a hand-fixed
  list is exactly what drifted the second time. It is a **name-presence** ratchet only: it proves
  every delivered name is mentioned, never that what is written about it is true.

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
     qwen gets no copy at all and is not a `SKILL_SURFACES` entry: it is wired by POINTER instead,
     through the `skills.directories` entry in `.qwen/settings.json` (§2d's qwen section) — one
     absolute path at the `.claude/skills` above, rather than a third set of files to keep in sync.
   - `.claude/skills/petbox-agent-factory/SKILL.md` + `.factory/skills/petbox-agent-factory/SKILL.md`
     — the on-demand **agent-factory** skill (`templates/petbox-agent-factory/SKILL.md`, no
     placeholders): the `roles` / `profile` / `doctor` / `apply` procedure. Written to both
     surfaces, same as the petbox skill; it is a skill an agent loads when it needs it, not every
     session.
   - `.claude/skills/petbox-methodology/SKILL.md` + `.factory/skills/petbox-methodology/SKILL.md`
     — the **methodology** skill (`templates/petbox-methodology/SKILL.md`, `{{PROJECT}}` only): a
     thin, project-agnostic pointer that fetches the wired project's live task-methodology rules
     (`tasks_methodology_guide`) at runtime instead of assuming this repo's own gates. Written to
     both surfaces, same as the others.
   - `.claude/skills/petbox-write-economy/SKILL.md` + `.factory/skills/petbox-write-economy/SKILL.md`
     — the **write-economy** skill (`templates/petbox-write-economy/SKILL.md`, `{{PROJECT}}`
     only):
     `bodyRef` (upload a body once, write by reference), `fragment` (point-patch a body), the raw
     UTF-8 vs. `\uXXXX` cost, the `bodyLen` read contract, and when none of it pays off. Written to
     both surfaces, same as the others.
   - `.claude/skills/petbox-node-authoring/SKILL.md` + `.factory/skills/petbox-node-authoring/SKILL.md`
     — the **node-authoring** skill (`templates/petbox-node-authoring/SKILL.md`, `{{PROJECT}}`
     only): what GFM formatting already gives an author for free, the GFM-alert callout convention,
     the sanitized inline-SVG diagram convention and its caption-states-the-claim discipline, and
     when a diagram is not worth drawing. Written to both surfaces, same as the others.
   - `.claude/skills/petbox-analysis-workspace/SKILL.md` +
     `.factory/skills/petbox-analysis-workspace/SKILL.md` — the **analysis-workspace** skill
     (`templates/petbox-analysis-workspace/SKILL.md`, no placeholders): staging a large,
     multi-part investigation as files in an external folder. `petbox-digest: manual` +
     `disable-model-invocation: true` — a human-invoked procedure, never picked up on its own.
     Sweeps a pre-rename copy at `analysis-workspace/` (`legacyDirs`, see §2f).
   - `.claude/skills/petbox-factory-run/SKILL.md` + `.factory/skills/petbox-factory-run/SKILL.md`
     — the **factory-run** skill (`templates/petbox-factory-run/SKILL.md`, no placeholders): a
     batch of prepared task statements driven to completion in one unattended pass. Same
     `petbox-digest: manual` + `disable-model-invocation: true` pair; sweeps a pre-rename copy at
     `factory-run/`.
   - `.claude/skills/petbox-second-reading/SKILL.md` +
     `.factory/skills/petbox-second-reading/SKILL.md` — the **second-reading** skill
     (`templates/petbox-second-reading/SKILL.md`, no placeholders): a blind second reading of an
     ask, compared against the caller's own sealed reading, before any plan or diff exists — for
     work that is expensive or hard to reverse. `petbox-digest: manual` but **no**
     `disable-model-invocation` — out of the digest, still callable by an agent that decides it
     applies (§2f explains why those are two different questions). Sweeps a pre-rename copy at
     `petbox-card-check/` (`legacyDirs`, see §2f) — the skill it replaces scored a result against
     the executor's own plan instead of the ask itself.
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
11. Seed / refresh `~/.petbox/roles.json` (§2g): fill in any harness that has no entry at all,
    migrate a v1 file to v2, and bring bindings whose `origin` is `kit` up to this kit's current
    defaults. **A binding whose `origin` is `owner` is never touched**, in any of those passes.
    Then run `apply` in-process (§2d) so the roster this run just wired is actually usable
    (`fresh-wire-roster-unusable`). Logged as `[11/10]` — it
    is deliberately outside the "of 10" count because it compiles artifacts rather than wiring the
    machine. It never aborts the run; its exit code nonetheless counts toward the run's own (§2b).

## 2a. Subcommands (no `<dir> <project>`)

Every subcommand below resolves the project itself (longest registry prefix against cwd) or needs
none at all. They are dispatched **before** arg parsing, so they never require a key.

| Command | What it does |
| --- | --- |
| `petbox-wire update` | Mirror this package's `src/` into `~/.petbox/wire/` (same orphan cleanup as step 5, content hash before → after). **Only** that: no keys, no registry, no hooks reinstall, no MCP/skills regeneration, no sticky-flag reset. It does **not** compile agent artifacts — that is `apply`. |
| `petbox-wire apply [--offline]` | Compile the per-harness agent role files from the portable definition (the file cascade base < user < project) + the local role→model binding. See §2d. |
| `petbox-wire layers [dir...]` | Diagnose the cascade: which layers exist, what each did to the roster, and which layer supplied every field. With no arguments it checks exactly what `apply` would; explicit directories compare an arbitrary set instead (no base added). Read-only. Exit **0** clean / **1** a cascade ERROR / **2** usage / **3** could not check (nothing to compare, or a present layer's source is broken) — the three are never confused. |
| `petbox-wire status [--offline]` | Print FACT, not a verdict, per role × harness: materialized artifact path, bound model, and where that model came from (roster / seed / none). Plus a four-pillar summary: definition layers (which are present, and which supplied each field), roster completeness, memory canon size, and skill-file drift. Reads the same resolvers `apply`/`doctor` use; never gates, never writes. `--offline` skips the canon/skill-template network calls (the definition resolve has none). A broken layer is REPORTED here — named, by absolute path — rather than thrown: `status` always exits **0** unless it itself crashes. |
| `petbox-wire doctor [--offline]` | Gate (exit code is significant): resolves the agent definition from the file cascade (base < user < project), then runs `checkTruthfulness(resolvedDefinition, harness, resolveAgentRoles(roles, harness))` for every id in `HARNESS_IDS` and prints OK or each violation. It gates the **resolved** definition — the one `apply` would compile — not the bare base layer, and it prints the layers plus their per-field provenance so you can see which is which. A **broken layer is a hard failure here** (exit 1, the file named by absolute path), for the same reason it is in `apply`: doctor exists to gate what apply would build. Also reports skill-file drift (materialized vs. kit templates: in sync / behind / foreign-BLOCKED), the session-banner budget margin, and a tail of `~/.petbox/wire.log`. Network checks are skipped with an explicit reason when the server is unreachable. `--offline` skips them up front instead: the skill-file-drift and banner-budget checks — both of which need a live workspace probe — are not attempted. The definition resolve and the truthfulness gate are unaffected, because neither touches a network. The local binding is not *required* — but where one exists it is fed into the gate, so a binding this harness cannot resolve is caught here. (The built-in-vs-server definition drift check that used to live here is gone: there is no second document to drift from.) |
| `petbox-wire roles` | Print `activeProfile`, the file's format version, and the resolved role→model tree from `~/.petbox/roles.json` — each row as `<role>: <model>  [<provider>, set by <kit\|owner>]`. Offline. An empty store exits **0** with a message — it never invents a model. |
| `petbox-wire roles --check-models` | Run the live model-identifier gate (§2h) over **every** binding in `roles.json` and print the three-way tally, listing each non-`valid` row. Strictly read-only: writes nothing, gates nothing, always exits **0** — reporting is its whole job; the gate that can still prevent a mistake lives on the write path. Costs one provider round trip (codex) and one `opencode models` spawn for the WHOLE sweep, not per binding, which is why it is opt-in rather than folded into plain `roles`. |
| `petbox-wire roles export` | Write a bootstrap copy of `roles.json` to **stdout** (no secrets); pipe it to a file on a new machine. Offline. |
| `petbox-wire profile use <name>` | Set `activeProfile` in `~/.petbox/roles.json`, creating an empty profile shell if the name is new. Offline; compiles nothing — re-run `apply` afterwards. |
| `petbox-wire model set <role> <model> [--agent <id>] [--profile <name>] [--allow-unknown-model]` | The only sanctioned way to write a role→model binding into `~/.petbox/roles.json`. Validated against `harness-models.ts`'s three-tier policy (known/unknown write, unknown warns; a recognizably foreign harness id is refused unless `--allow-unknown-model`). Stamps `origin: "owner"` and derives `provider` — from then on the kit never rewrites that cell (§2g). A **second, live** gate then asks the harness's own machine-local or network source whether the identifier is known at all, with three outcomes (§2h). **Not offline** for `--agent codex` (a provider round trip) or `--agent opencode` (an `opencode models` spawn); offline for the other three. Compiles nothing — prints `next: petbox-wire apply`. |
| `petbox-wire model unset <role> [--agent <id>] [--profile <name>]` | Remove a role→model binding for the given agent/profile from `~/.petbox/roles.json`. Offline; compiles nothing — re-run `apply` afterwards. |

## 2b. Exit codes

`src/wire-exit.ts` is the single definition (`WIRE_EXIT`, the pure `classifyApplyExit`, and
`strongestExitCode`, so the classification is unit-testable without spawning a process). It also
owns the only two sanctioned ways a run may end — see §2b-2.

| Code | Meaning |
| --- | --- |
| `0` | Success — every requested step ran. |
| `1` | Hard failure — invalid definition, unexpected throw, a refused clobbering write, a rejected/unreachable API key. |
| `2` | Usage / bad arguments. |
| `3` | Truthfulness policy block — some roles/harnesses were refused. **A partial write is possible.** |
| `4` | **Incomplete** — a requested step did not run for a reason the user did not ask for. |

`3` is a *policy* outcome, not a crash: the definition asked a harness for a capability it does not
declare. Fix the definition (or accept the skip) — retrying changes nothing.

`doctor` never reports `4` — it skips no step of its own.

### The **full `wire`** contract: which steps raise the run's exit code

The table above says what each code *means*; this says what actually produces one in a full wiring
run, which the table alone left to guesswork (`full-wire-exit-ignores-step-11`). Read off `wire.ts`'s
`main()`, not inferred — every entry is a real call site.

| Step | Raises | Aborts the run? |
| --- | --- | --- |
| argv parsing → `usage()` | `2` | yes |
| pre-flight: `<dir>` does not exist | `1` | yes |
| 2 — no API key found anywhere | `1` | yes |
| 3 — `validateKey`: unreachable, `401`, or key scoped to another project | `1` | yes (`abortRun`) |
| 3b — no workspace resolvable (no `--workspace`, server reports none) | `2` | yes |
| 4 — Windows user-scope env persistence fails | `1` | yes |
| 7b — `--telemetry` only: the log-ensure call fails | `1` | yes (`abortRun`) |
| 10 — self-smoke fails | `1` | **no** — the run continues to the end |
| 11 — `apply` (seed bindings + compile artifacts) | `1`, `3` or `4` | **no** — the run continues to the end |
| anywhere — an unexpected throw reaches `main().catch` | `1` | yes |

Steps **1, 5, 6, 7, 8, 9** (envVar derivation, kit copy, registry, project files, global hooks,
`--cleanup-legacy`) have no exit code of their own: they either succeed or throw, and a throw lands
in `main().catch` ⇒ `1`. Nothing on that list swallows a failure (the single `catch` inside them is a
best-effort `chmod 0600` on the key store).

Consequences worth scripting against:

- **A full `wire` really can exit `3` or `4`** — through step 11, which runs `apply` in-process. The
  older advice ("do not script against `3`/`4` outside `apply`") is obsolete; `2` on the other hand
  still only comes from argv parsing and step 3b.
- **Non-aborting is not code-free.** Steps 10 and 11 both keep going *and* raise the code. Step 11 in
  particular must not abort: the key is validated and every other file is written by then, and
  throwing that away over a compile hiccup is the bug `fresh-wire-roster-unusable` fixed. Reporting
  `0` for it was a *different* decision that got fused into the same comment, and it is what
  `full-wire-exit-ignores-step-11` undid.
- **When both fail, the strongest code wins**, by the same `1` > `3` > `4` > `0` priority —
  `strongestExitCode` in `wire-exit.ts`, not "whichever step assigned last" (step 11 assigns last, so
  a bare assignment would report its `4` over step 10's `1`).
- **The final line never contradicts the code.** A non-zero step 10 *or* step 11 suppresses `done.`
  and makes the failure the last line, on stderr (`selfsmoke-failure-prints-done`, `finishWireRun`).

Pinned end-to-end by `wire-full-exit-step11.test.ts` (every step-11 code out of a real process, the
both-failed priority case, and the proof that the run is still not interrupted) and
`wire-full-exit-races.test.ts` (the aborting sites above).

### `4` (incomplete) — added 2026-07-28, **breaking**

A path that used to exit `0` now exits `4`. That is deliberate. `apply` could complete "successfully"
while silently skipping its skills refresh, and the only trace was a line of stdout — invisible to
the one reader that matters here, a CI step branching on the exit code
(`wire-exit-incomplete-is-invisible-to-automation`).

- **Unintentional** skip ⇒ `4`: the workspace probe that gates the skills refresh failed (HTTP error,
  timeout, a key without the scope). Retrying later can succeed, so `4` means *retry*, not *broken*.
- **Intentional** skip ⇒ still `0`: `--offline`, or a directory that is not a registered project.
  The user asked for these; alarming on them trains people to ignore the alarm.
- **Not `3`.** `3` means policy blocked something on purpose. A step that failed for a reason outside
  the user's control is not policy, and folding them together would make `3` mean two things — the
  exact ambiguity this taxonomy exists to prevent.
- **Priority:** `1` > `3` > `4` > `0`. A refusal to write and a policy block are statements about what
  the run *refused* to do; "a step did not get to run" is the weaker claim and yields to both. It is
  never lost — it stays in the `summary` JSON under `skillsSkipped`. Pinned by `wire-exit.test.ts`
  (pure classifier) and `apply-skills-skip.test.ts` (end-to-end, both orderings).
  The same ladder decides between codes raised by *different* steps of one run —
  `classifyApplyExit` ranks conditions inside one `apply` pass, `strongestExitCode` ranks the
  finished codes of the steps that each kept going (full `wire`: steps 10 and 11).

## 2b-2. How a run ends (never `process.exit`)

A hard exit tears the process down while libuv may still be closing a socket left by a just-completed
`fetch`. On Windows that races the teardown —
`Assertion failed: !(handle->flags & UV_HANDLE_CLOSING), src\win\async.c` — and the caller observes
**127** instead of the code the call site named. The same defect was fixed one place at a time in
`doctor`, then `status`, then `apply`, then in six further sites in the full `wire` command plus
`import-sessions.ts`. Three of those recurrences happened because each fix left no guard behind.

So the mechanism now lives in `wire-exit.ts` and there are exactly two spellings:

| Use | When |
| --- | --- |
| `exitWith(code)` | The run is over and control is already where it needs to be. Sets `process.exitCode`, unrefs handles still mid-close, lets Node exit naturally. Does **not** abort control flow — `return` after it. |
| `abortRun(code, message)` | The run must end from deep inside a helper with no clean `return`. Returns `never`, so it cuts control flow exactly as `process.exit()` did; the entrypoint's `.catch` turns it back into `exitWith`. |

`wire-process-exit-whitelist.test.ts` enforces this across **every** non-test source in the package:
a new raw `process.exit(` fails the build unless it is added to that file's whitelist with a
justification for why no network call can precede it. There is deliberately no "tracked risk"
verdict — a site that would need one has `exitWith`/`abortRun` available instead.

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

Not part of the full wire; run it explicitly. It resolves the artifact root from git's own toplevel
for cwd (falling back to cwd), builds the definition from the **file cascade base < user < project**
(no network on that leg at all — `--offline` does not affect it), then per harness writes:

| Harness | Path |
| --- | --- |
| `claude-code` | `.claude/agents/<role>.md` |
| `opencode` | `.opencode/agent/<role>.md` |
| `droid` | `.factory/droids/<name>.md` |
| `codex` | `.codex/agents/<role>.toml` (whole TOML document, not frontmatter — auto-discovered per codex-spec.md §4) |
| `qwen` | `.qwen/agents/<role>.md` (Claude Code subagent frontmatter schema, byte-identical renderer — qwen-spec.md §5) |

These files are **overwritten** — they are generated. A role is written only when the target harness
declares every capability the role requires; a dirty role is skipped WHOLE and reported, and clean
roles in the same run are still written (⇒ exit 3, partial write). `model:`/`model =` appears only
when `roles.json` binds that role (droid unbound → `model: inherit`; codex/qwen have no such
universal default, so an unbound role is a hard refusal there too, same as claude-code — see
apply-artifacts.ts / harness-models.ts); a concrete model id is never invented. Three sources,
three owners: the definition is **file**-authoritative (kit base + the layers on this disk),
`roles.json` is **machine**-authoritative, the capability matrix is **kit** data.

codex additionally needs USER-scope config (`model_providers`/`model_provider`/`model`/
`model_catalog_json` are denylisted at PROJECT scope — codex-spec.md §1) and pre-trusted hooks:
`wire`'s global-install step (`installGlobalHooks` in wire.ts) writes `$CODEX_HOME/hooks.json`,
merges `model_providers.deepseek` + `model_providers.opencode-go` (regenerated each run,
preserving the `opencode-go` session UUID across re-installs) plus `[hooks.state."<key>"]` trust
entries into `$CODEX_HOME/config.toml` (codex-hook-trust.ts reproduces codex's own SHA-256 trust
hash — see that file's header), and sets the root `model_provider = "deepseek"` / `model =
"deepseek-v4-pro"` scalars that govern a bare `codex`/`codex exec` invocation with no role file in
play (`model` matches the orchestrator role's own binding, the strongest model the direct
subscription serves). Codex pins **one** `model_provider` per process — a role `.toml`'s own
`model_provider` field is accepted and then silently DROPPED (measured: the child session hit the
parent's endpoint while the role's `model` was applied, a redirected local listener received
nothing), so a per-role split across two subscriptions is not possible on codex today; both roles
therefore stay on the same `deepseek` provider, and `[model_providers.opencode-go]` stays
registered but unused (owner decision 2026-09-08, until a routing proxy exists — idea
`model-prefix-routing-proxy`). `wire` also writes `$CODEX_HOME/petbox-model-catalog.json`
(codex-model-catalog.ts — the UNION of every codex role→model binding across every profile in
`roles.json`, sorted/de-duplicated, `inherit`/empty skipped; falls back to a 3-slug default,
logged, only when that union is empty — never the 3 slugs alone, so a `model set ... --agent
codex` rebinding lands in the catalog on the next full `wire` run). With the current bindings the
catalog holds exactly `deepseek-v4-flash` and `deepseek-v4-pro`. The catalog exists because a
model bound to a role but ABSENT from it silently loses the `apply_patch` tool and gets a 272000
context window instead of the kit's chosen 128000 — exit 0, no warning either way. Honest
limitation: **`apply` does not refresh this file** — only a full `wire` run does, so `model set
... --agent codex` needs a `wire` re-run, not just `apply`, to take full effect. The project layer
only ever carries `[mcp_servers.petbox]` (not denylisted).

`apply` also writes ONE non-role project file: `<root>/.qwen/settings.json`
(qwen-project-settings.ts), carrying `mcpServers.petbox` and `skills.directories` — see the qwen
section below for what each is for. It is the one MCP config `apply` touches, and it is there
because it was the one whose ABSENCE was silent: the four others are written by `wire` only, but
a project that never gets them still has a working `.mcp.json`/`config.toml` from whenever it was
wired, whereas a project wired before this file existed had no `.qwen` directory at all and qwen
silently fell back to `.mcp.json`, which it cannot env-var-resolve — `needs authentication`, zero
tools, on a perfectly healthy key (caught live in `petsonde`, card
`wire-qwen-project-settings-mcp-and-skills`). Two preconditions, both refusals rather than
guesses: the root must be a real git working tree (under `--roles=user` the root can legitimately
BE the home directory, and a `skills.directories` written there would load at qwen's user level —
see the trap below), and the directory must be in the registry (the `${ENV_VAR}` name is the
registry's to state; `apply` never invents a project identity). `--offline` does not skip it: the
write is pure filesystem.

qwen additionally needs USER-scope `$QWEN_HOME/settings.json` (qwen-paths.ts; default `~/.qwen`,
overridable by `QWEN_HOME`), all written by the same `installGlobalHooks` step: `hooks.<Event>`
(Stop **and** StopFailure both point at `qwen-push-session.ts` — Qwen's `SessionStart`/`Stop`
have the SAME `{matcher?, hooks:[...]}` shape Claude Code uses, no separate hooks file and no
trust-hash mechanism the way codex needs one, because qwen's folder-trust default is disabled and
a user-scope hook is always honored regardless — qwen-spec.md §1/§3), a `mcpServers.petbox` entry
for the owner's interactive use OUTSIDE any wired directory, `security.auth.selectedType =
"openai"`, and `agents.modelGrades`.

Model routing goes through a provider-keyed mechanism built for TWO distinguishable providers
(task wire-support-codex-qwen, live smoke on 0.23.0 with two local listeners), but as of the
owner's 2026-09-08 decision only one of them is actually bound to a role: **both** codex and qwen
run entirely on the DIRECT DeepSeek subscription until a routing proxy exists (idea
`model-prefix-routing-proxy`) — codex pins one `model_provider` per process (a role's own
`model_provider` field is silently dropped, measured), so a per-role split across two
subscriptions is not possible there today, and the owner preferred both harnesses consistently
direct over codex silently billing everything to the gateway. A provider name can never appear in
the `model:` selector itself — qwen matches the pre-colon segment against a closed auth-type enum
and silently treats any unknown prefix as a bare model id (measured: `opencode-go:glm-5.3-flash`
silently hit the wrong provider, exit 0, no warning) — so routing goes through a
wholesale-replaced `providerProtocol` (`deepseek` → `openai`, `opencode-go` → `openai`) paired
with a wholesale-replaced, provider-keyed `modelProviders` (`modelProviders.deepseek` — two
entries, `ds-deepseek-v4-pro` and `ds-deepseek-v4-flash`, direct to `https://api.deepseek.com/v1`,
no `x-opencode-session` header; `modelProviders.opencode-go` — two entries, `go-glm-5.3-flash` and
`go-qwen3.8-max`, still registered and still carrying `x-opencode-session` — reusing codex's OWN
just-minted UUID rather than minting a second — but unused by every role, kept only so a future
rebinding is a single `model set` away — qwen-spec.md §11/§12). Every id is globally unique across
both provider keys; the wire model name lives in `generationConfig.extra_body.model`, never in the
id. `agents.modelGrades` lists exactly the two `ds-*` ids `QWEN_ROLE_MODEL_SEED` actually binds
today — without an id listed there, a spawn-time `model` parameter on the Agent tool naming it is
rejected outright, qwen-spec.md §6; the `go-*` ids stay out of it for the same reason. `model.name`
is `ds-deepseek-v4-pro`. Note: `-m`/`--model` does NOT accept the `authType:model` grammar (it
silently falls back to the first registered model) — never pass `-m openai:...`; the bare id form
(`model.name`, or a role file's own `model:` frontmatter) is the only supported surface for these
ids.

`reserve` deliberately collapses onto `deepseek-v4-pro` (`openai:ds-deepseek-v4-pro` on qwen,
same as `orchestrator`/`worker-highstakes`) rather than getting a distinct model family: the direct
subscription serves only `deepseek-v4-pro`, `deepseek-v4-flash` and `deepseek-v4-flash-vision-exp`,
so there is no third family available for it. This is a known, accepted cost until the routing
proxy lands.

**Inside a wired project**, that user-scope `mcpServers.petbox` entry is NOT what governs — a
second, WORKSPACE-scope entry is (defect qwen-mcp-json-shadows-workspace-entry, live smoke
wire-support-codex-qwen, root-caused against the qwen-code source under
`packages/cli/src/config/`): the project's own `.mcp.json` (written for claude-code, above) has
no `${ENV_VAR}` resolution at all (`mcpJson.ts`'s loader never calls `resolveEnvVarsInObject`) and
would send the API-key header out **literally**, so PetBox 401s — and `.mcp.json` OUTRANKS the
user-scope entry by name (`assembleMcpServers`'s precedence: user/default < project `.mcp.json` <
workspace/system < `--mcp-config`), so on a wired project the correct user-scope entry never even
gets a chance to run. Both `wire` (`writeProjectFiles`) and `apply` therefore merge a THIRD
`mcpServers.petbox` entry into the project's own `<root>/.qwen/settings.json`
(`SettingScope.Workspace` — the file `Storage.getWorkspaceSettingsPath()` resolves to), which both
outranks `.mcp.json` and IS env-var-resolved. One writer for both commands
(`qwen-project-settings.ts`), because for a year only `wire` wrote it and a project kept current
with `apply` alone never grew the file at all. Workspace scope is held behind qwen's pending-approval gate for an interactive
run exactly like project scope (`isGatedMcpScope`) — deliberately left unapproved by the kit (the
approval hash bakes in the *resolved*, i.e. literal-key, config, so a later key rotation would
silently invalidate a baked-in approval): headless callers pass `qwen --approval-mode yolo`
(bypasses the gate outright, already the default for this kit's own headless wrappers) and an
interactive user gets a one-time approval prompt on first launch (the closing NOTE `wire` prints
names this explicitly). Because the USER-scope entry is machine-global, it can only reference ONE
project's `${ENV_VAR}` at a time — the last project a `wire` ran for — but that only matters for a
`qwen` session run OUTSIDE any wired project directory; inside one, the project's own
workspace-scope entry always resolves its own correct `${ENV_VAR}` regardless of what the
user-scope entry currently points at.

Both the user- and workspace-scope `mcpServers.petbox` entries set `alwaysLoadTools: true`: qwen
defers MCP tools to `tool_search` by default, declaring them to the model eagerly only when the
active model's id matches `/deepseek-(v3|v4|chat)/i` (`cli/src/config/config.ts`). Every role binds
a `ds-deepseek-v4-*` id today, which in fact matches that regex — but the flag is set
unconditionally regardless: it is cheap, harness-portable, and keeps working unchanged the moment
a role is rebound off DeepSeek (e.g. onto the `opencode-go` gateway once the routing proxy lands),
which the regex match alone would not survive.

The same project file carries `skills.directories` — an array of foreign skill roots, union-merged,
holding ONE entry: the ABSOLUTE path of this project's own `.claude/skills`. qwen is deliberately
NOT a `SKILL_SURFACES` entry (skill-files.ts): it reads foreign roots natively, the SKILL.md format
is compatible (`name`/`description` required, `disable-model-invocation` is the same key with the
same semantics — verified by running qwen 0.23.2's own `SkillManager`), so a pointer beats a third
on-disk copy of every skill body. Two measured properties make the exact placement load-bearing,
and getting either wrong fails silently:
- **Absolute only.** A relative entry is resolved against the RUNTIME's `process.cwd()`, not
  against the project the settings file belongs to — so it names a different directory every time
  qwen is launched from somewhere else. `mergeQwenProjectSettings` throws on a relative path
  rather than write one.
- **Project scope only.** Every directory named in `skills.directories` is loaded at qwen's `user`
  LEVEL regardless of which settings file named it — so the same key in `~/.qwen/settings.json`
  would publish ONE project's skills into EVERY project on the machine. The user-scope file
  therefore never gets this key; a test asserts its absence there in the same run that asserts its
  presence in the project file.

Two qwen behaviors worth knowing when debugging either of the above, verified against the
qwen-code source rather than its own docs (which are wrong on the first one):
- `QWEN_CODE_DEBUG=1` — the variable qwen's own runtime warning tells you to set — **is read
  nowhere in the source**. The real switches are `--debug`, `DEBUG=1`, or `DEBUG_MODE=1`
  (`cli/src/config/config.ts`), and the resulting log goes only to a file, never stderr:
  `$QWEN_HOME/debug/<session-id>.txt` — pair with `--session-id` to know the filename in advance.
- MCP tools are deferred by default (`core/src/tools/mcp-tool.ts`) and reach the model through
  `tool_search` plus a names-only startup reminder, **except** for a model whose id matches
  `/deepseek-(v3|v4|chat)/i`, where `tool_search` is disabled and every MCP tool is declared
  eagerly up front (`cli/src/config/config.ts`) — see `alwaysLoadTools` above for how this kit
  works around that for every other model this kit routes to.

A layer directory that is absent is a layer with no opinion. A layer that is PRESENT and cannot be
read, parsed or validated **refuses the whole run**: exit 1, the absolute path and the parser's own
message on stderr, and not one artifact written or changed. No previously-successful result is ever
substituted — that substitution is precisely what turns a broken source into a silent one. Every
run prints its layers and, per role and field, which layer supplied it; `petbox-wire layers` prints
the same cascade in full, plus what each layer did to the roster.

The orphan sweep (removing the artifact of a role that left the definition, marker-gated) runs
unconditionally. It used to be gated on a server-sourced definition, because a degraded network
resolve could legitimately hold fewer roles than the project really has; a file cascade either reads
or refuses, so that state no longer exists.

## 2e. What lives under `~/.petbox/`

| Path | Owner / contents |
| --- | --- |
| `wire/` | The stable kit copy. Every global hook / plugin shim points here. Refreshed by a full wire or `update`; an exact mirror of the shipped kit. |
| `projects.json` | Registry: `{prefix, project, envVar, baseUrl?}` per entry. Resolved by longest prefix against cwd. |
| `keys.json` | Flat `{ "<ENV_VAR>": "<key>" }` the kit hooks read directly (no env var needed). POSIX `0600`. Ground truth for "what is my env-var actually called". |
| `env.sh` | POSIX only — regenerated from the whole key store, sourced (marker-guarded) from the login profiles. |
| `roles.json` | Local role→model bindings (`model` + `origin` + `provider`, format v2 — §2g) + `activeProfile`. Machine-authoritative; never uploaded. |
| `agents/` | OPTIONAL machine-wide definition layer (`layer.json` + `petbox-<slug>.{json,md,append.md}`). Absent = no opinion. Applied over the kit base, under a project's own `<root>/.petbox/agents`. |
| `cache/<project>.canon.md` | LKG copy of the memory canon (§6). The definition has no cache of its own — its layers ARE local files. |

Nothing here is regenerated by `update` except `wire/` itself.

## 2f. The skill delivery contract: provenance, digest mode, invocation lever, rename sweep

Four mechanisms decide what the kit may write over, what an agent is told about unprompted, what
an agent is allowed to call, and what gets cleaned up when a skill is renamed. They are four
**independent** axes and conflating any two of them has already cost a bug each. Everything below
is a statement about code in `src/clients-ts/petbox-wire/src/`; the file and function that decide
it are named inline, because a doc sentence about a mechanism is exactly the kind of claim that
rots without anyone noticing.

### Provenance — `petbox: managed` | `petbox: manual` | no marker

Every file the kit renders carries a `petbox:` line in its YAML frontmatter, written *before* any
write decision is taken, so a file already sitting at that path can be classified instead of
guessed at. `readPetboxProvenance` (`origin-marker.ts`) maps the value onto three states:

| Frontmatter | Meaning | What the kit does |
| --- | --- | --- |
| `petbox: managed` | The kit renders this file and is its only source of truth. | Safe to rewrite: overwritten silently whenever the render differs (an identical render reports `unchanged`). The **only** state `cleanupLegacyArtifact` will ever `unlink`. |
| `petbox: manual` | The project claims this path as its own. | Never written, never deleted, and **not counted as a conflict** — `writeSkillArtifact` returns `declared-manual` and the run stays clean. |
| no `petbox:` line | Somebody else's file. | Refused loudly and left byte-for-byte alone (`writeArtifact` → `blocked`). |

`hasPetboxMarker` is `true` for **`managed` only** (`origin-marker.ts`), and that narrowness is
the safety property, not an accident: it is the single gate on both overwriting and deleting, so
if it accepted any `petbox: <token>` a path the project had explicitly claimed would be silently
rewritten — and, once `legacyDirs` below existed, deleted. `petbox: manual` is therefore the
escape hatch: it is how you take one delivered skill out of the kit's hands **without** taking
the wire apart, and it is a different thing from `petbox-digest: manual`, which is about
attention, not ownership. `writeSkillArtifact` checks `isDeclaredManual` *first*, ahead of both
the pre-marker migration carve-out and `writeArtifact`, because either of those would otherwise
write.

One carve-out: an unmarked file that is byte-for-byte what the pre-marker template used to render
is a leftover of the kit's own, not a stranger's file, and is promoted in place (reason
`migrated`).

### `petbox-digest: auto | manual` — and the boundary that matters

`petbox-digest` answers one narrow question: *should this skill be named to the agent without
being asked?* Declaring `auto` puts a single trigger line — derived from the skill's own
`description:` frontmatter, never a copy of its body — into a salience index.

**That index is built for opencode and nothing else.** The whole read path is
`readAutoDigestSkillTriggers` → `buildAutoSkillsIndex` (`skill-files.ts`), and its sole caller is
`opencode-plugin.ts`, which pushes the block into opencode's system prompt. Nothing in Claude
Code's or Droid's path reads the key. Concretely: **`petbox-digest: manual` saves no context on
Claude Code or Droid.** Both harnesses list every discovered skill's name and description in the
session regardless — that is the progressive-disclosure shape the index was written *not* to
duplicate (`skill-files.ts`, the salience-index header comment: only the body is lazy; name and
description are always listed). What `manual` buys on those two harnesses is exactly nothing; the
lever there is the next mechanism.

Selection is by **declaration, not by directory name**: a skill enters the digest iff the
materialized file on disk says `petbox-digest: auto`. So a project can drop a delivered skill out
of its own opencode digest by editing one frontmatter line, and a repo-native skill that merely
happens to be named `petbox-something` never sneaks in.

### `disable-model-invocation: true` — the Claude Code / Droid lever

This is the frontmatter key those two harnesses honour to refuse a *model-initiated* call, read
back by `isModelInvocationDisabled` (`origin-marker.ts`). It is the mechanism that actually keeps
a procedure skill from firing on its own; `petbox-digest` cannot do that job on those harnesses,
and an earlier version of this contract assumed it could.

`PROJECT_SKILLS` declares the *intent* per skill as `invocation: "user" | "agent"`; the template's
frontmatter carries the *lever*. A parity test in `skill-files.test.ts` requires the two to agree
in **both** directions — every `"user"` template carries the key, and no `"agent"` template does
(an `"agent"` skill that carried it would be uncallable by the agent it was written for).

The two keys are genuinely independent, and `petbox-second-reading` is the case that proves it:
`petbox-digest: manual` (never surfaced unprompted) with `invocation: "agent"` and no
`disable-model-invocation` — an agent must be able to run that check on its own initiative before
spawning a worker. Commit `0daca301` set the lever on all four `digestMode: "manual"` templates,
which silently made its predecessor (`petbox-card-check`) unreachable; splitting the axes is what
fixed it.

The delivered set as it stands:

| Skill | `petbox-digest` | `disable-model-invocation` |
| --- | --- | --- |
| `petbox` | `auto` | — |
| `petbox-methodology` | `auto` | — |
| `petbox-write-economy` | `auto` | — |
| `petbox-node-authoring` | `auto` | — |
| `petbox-agent-factory` | `manual` | `true` |
| `petbox-analysis-workspace` | `manual` | `true` |
| `petbox-factory-run` | `manual` | `true` |
| `petbox-second-reading` | `manual` | — (deliberately) |

That table is prose and drifts like prose. The frontmatter is the source of truth; the parity
tests in `skill-files.test.ts` are what hold it to `PROJECT_SKILLS`.

### `legacyDirs` — sweeping the name a skill used to have

A `PROJECT_SKILLS` entry may declare `legacyDirs: readonly string[]` — directory names this skill
was delivered under *before* it was renamed. Each old `<surface>/<legacyDir>/SKILL.md` is swept by
`cleanupLegacySkillDir` (`skill-files.ts`), because the kit was the only source of truth for what
it put there: leaving the file behind leaves a standing instruction to read a skill that no longer
exists.

The sweep is gated on the replacement having **actually landed** — `writeSkillFiles` skips it for
any outcome that is not `written`, so a `blocked` or `declared-manual` new path never causes the
old one to be deleted and orphan the skill entirely. `--adopt` does not reach the sweep either: it
is a write-side lever only, and deletion stays marker-gated with no override anywhere in the
package. A `--dry-run` previews the sweep without performing it.

Deletion goes through `cleanupLegacyArtifact` unchanged, so the provenance gate above still
holds — **only a `petbox: managed` file is ever unlinked**. A foreign file, a file the project
declared `petbox: manual`, or one that could not be read comes back `kept-foreign` and stays
exactly where it is. The now-empty directory is removed too, via a plain `rmdirSync` that is
allowed to fail: if anything else lives there (a `references/` folder, the project's own notes)
it throws `ENOTEMPTY` and the folder survives whole.

Currently declared: `petbox-analysis-workspace` sweeps `analysis-workspace/`, `petbox-factory-run`
sweeps `factory-run/`, `petbox-second-reading` sweeps `petbox-card-check/` (the skill it
replaces). Every other entry has none.

## 2g. `roles.json` format v2 — binding origin and provider

A binding is `{ model, origin, provider }`, and the file carries `formatVersion: 2`.

| field | what it is |
|---|---|
| `model` | The harness's OWN dialect, byte for byte. There is no name valid in all five harnesses, so nothing is translated at render time — this file IS the correspondence table. |
| `origin` | `kit` (this kit seeded it) or `owner` (a human chose it, via `model set` or a hand edit). |
| `provider` | Which subscription/registry serves `model`, derived from the harness's grammar; `null` when the value genuinely cannot name one. |

**Why `origin` exists.** Without it the kit could not tell its own past seed from the operator's
choice, so the only safe seeding strategy was "never overwrite anything" — and a changed default
therefore never reached a machine that already had a `roles.json`. Two profiles on the owner's
machine sat on retired ids for a month that way, and one of them was a qwen id that harness
resolves by *silently* falling back to the first registered model. With `origin`, the kit updates
its own cells and leaves the owner's alone.

**How a legacy (v1) file is attributed** — one pass, on the next `wire`/`apply`, reported line by
line: a binding whose value matches **byte for byte** a value this kit has ever seeded *for that
exact harness and role* (`HISTORICAL_ROLE_MODEL_SEEDS`, append-only) is the kit's, and is brought
up to the current default; everything else is the owner's and is kept byte for byte. Matching is
per harness AND role because the same string can be a kit seed for one role and a deliberate
choice for another. The pass is idempotent, and it runs on read, so no writer can persist a file
whose bindings have not been attributed yet.

`origin` is a cached label, not a claim taken on trust: a cell still marked `kit` whose value this
kit has never shipped was edited by hand, so it is re-attributed to the owner and its value kept —
an edit is never silently reverted on the next run.

**Provider per harness** (each rule is a measured fact about that harness's grammar, in
`binding-provider.ts`):

| harness | provider | how |
|---|---|---|
| opencode | `deepseek`, `opencode-go`, … | the segment before the FIRST `/`; further slashes belong to the id |
| qwen | `deepseek`, `opencode-go` | the `modelProviders` key, recovered from the id's own `ds-`/`go-` decoration — the `authType:` prefix is an auth TYPE and can never name a provider |
| codex | `deepseek` | the process-level `model_provider` this kit pins; the value is a bare slug that cannot express one |
| droid | `custom`, `factory` | which REGISTRY resolves the id — `custom:` is the BYOK registry (`customModels`), a bare slug is Factory's built-in catalog. The vendor behind a `custom:` entry is not expressible here. |
| claude-code | `anthropic` | tier aliases and `claude-*` ids are one namespace; the grammar has no provider segment at all |

The label is checked against the value it describes on every `wire`/`apply`
(`findBindingProviderInconsistencies`) — internal consistency only, value vs. label. Checking the
identifier itself against LIVE machine config is the separate gate in §2h.

## 2h. The live model-identifier gate — three outcomes, never two

`model-validity.ts`. Four of the five harnesses have an `open` model policy in `harness-models.ts`,
so before this gate `model set --agent qwen worker ds-deepsek-v4-pro` (one letter short) was
accepted in silence — and qwen does not fail on an unresolvable id either, it quietly falls back to
the FIRST registered model of the protocol. A typo therefore moved a role to a different model with
no error anywhere, which is the acceptance this closes.

**The verdicts, and the line between them:**

| verdict | meaning |
|---|---|
| `valid` | the source was consulted and it knows this identifier |
| `invalid` | the source was consulted, it is complete for its own id space, and this identifier is not in it |
| `unverified` | the source could **not** be consulted at all — no key, no binary on `PATH`, no config file yet, timeout, unparseable answer. It says **nothing** about the model. |

`unverified` is a first-class third outcome and must never be folded into either neighbour.
Collapsing it into `invalid` is the defect recorded as
`apply-reports-missing-key-as-unregistered-project-and-exits-0` (a missing credential reported as a
fact about the subject), and for codex it is unavoidable at the transport level: a **missing** key
and a **wrong** key both answer `401` in ~0.4s, so the status code cannot tell them apart.
Collapsing it into a warning nobody reads is the original defect this gate exists to close.

**Sources, cost and who blocks** (each measured on the owner's machine, 2026-09-09):

| harness | source | cost | on `invalid` |
|---|---|---|---|
| claude-code | the kit's own alias list (`harness-models.ts`) | ~0, offline | **REFUSES the write** |
| qwen | `$QWEN_HOME/settings.json` → `modelProviders[].id` | ~0, local file | warns and writes |
| droid | droid's compiled-in catalog (a snapshot of CLI 0.170.0) + `~/.factory/settings.json` → `customModels[].id` | ~0, local file | warns and writes |
| codex | `GET <base_url>/models` of the **active** `model_provider` from `$CODEX_HOME/config.toml` | ~0.6-0.7s, network + provider key | warns and writes |
| opencode | `opencode models` | ~1.1s, subprocess | warns and writes |

claude-code is the only blocking harness because it is the only one where the kit holds positive,
free, offline knowledge. Its concrete-`claude-*`-id tier stays **non-blocking** — that is the
deliberate 2026-07-13 decision (a closed catalog of concrete ids false-blocked genuinely new
models), and in this module's vocabulary that tier is exactly `unverified`. For the other four the
source describes THIS machine, and an absent or empty one is a legitimate "not configured yet": the
owner's normal order is bind roles first, configure providers second.

> [!IMPORTANT]
> No source here proves the model **works** — only that some catalog knows the identifier. The
> live counter-example is a codex role bound to `grok-4.6`: real and served by `opencode-go`, while
> the active `model_provider` is `deepseek`, which has never heard of it (observation
> `codex-reserve-bound-to-unreachable-grok`). Existence and reach are different questions, and
> every message the gate emits says which one it answered.

Cost is paid once per process, not once per binding: a `ModelSourceCache` memoizes codex's round
trip and opencode's spawn, so `roles --check-models` over 75 bindings costs one of each (~2s
measured). It is deliberately **not** persisted to disk — a stale cached catalog reported as a live
fact is the same confusion in a new place.

Reading `~/.factory/settings.json` is done for `customModels[].id` and nothing else: every entry in
that array also carries the owner's provider credential in plaintext, and no other field is ever
copied into a return value, a message or a log line (asserted in `model-validity.test.ts`).

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
     `PETBOX_<PROJECT>_API_KEY=<KEY> npx petbox-wire@latest <dir> <project>` (or the dev checkout
     command), which refreshes `~/.petbox/wire/` *and* the generated config. `--key <KEY>` still
     works but puts the key in argv, which npm logs in plain text to `~/.npm/_logs/*.log` with no
     rotation — prefer the env var above (already wired? the key is already in `~/.petbox/keys.json`,
     so re-running needs no key at all);
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
  A part is `null` only when that leg was never queryable at all (no workspace, or withheld by
  sandbox containment) — a leg that WAS queried and has nothing curated yet comes back non-null
  at `version: 0` with an empty `body` (`MemoryApi.CanonAsync`), not null.
  `canon.ts` classifies each leg by `version` (0 = queried-but-empty, never by comparing `body`
  text) and renders it under its OWN heading either way: real content as
  `### Project ({project})` / `### Workspace`, an empty leg as `### Project ({project}) — empty`
  / `### Workspace — empty` followed by the kit's own curation-nudge line. Every rendered leg
  gets a heading — empty is always attributed to the SPECIFIC part it describes, and a populated
  section is never left directly adjacent to an unheaded empty-notice (card
  canon-banner-empty-notice-unlabelled). Nothing is injected only when BOTH legs are absent
  (`null`).
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
