# petbox-wire

Wire any project to [PetBox](https://petbox.3po.su) for **Claude Code**, **opencode** and
**Factory Droid** — one command, no repo clone:

```bash
npx petbox-wire <dir> <project> --key <KEY>
```

Full documentation: <https://petbox.3po.su/doc/wire>.

## Requirements

**Node >= 23.6** — the kit is plain TypeScript run through Node's native type-stripping
(no build, zero runtime dependencies). Older Node exits with a clear version error.

## Commands

| Command | What it does |
| --- | --- |
| `petbox-wire <dir> <project>` | Full wire: validate the key → persist it → copy the kit → register the directory → write per-project MCP configs and skills → install the global hooks → self-smoke. Idempotent. |
| `petbox-wire update` | Refresh only the stable kit under `~/.petbox/wire/` (exact mirror + orphan cleanup, content hash before → after). No keys, no registry, no hook reinstall, no MCP/skills, no sticky-flag reset. Does **not** compile agent files. |
| `petbox-wire apply [--offline]` | Compile the per-harness agent role files from the portable agent definition (built from files: base < user < project) + your local role→model binding. |
| `petbox-wire layers [dir...]` | Show the definition cascade: which layers exist, what each did to the roster, and which layer supplied every field. Read-only. |
| `petbox-wire status [--offline]` | Print fact, not a verdict: per role × harness, the materialized artifact path, its bound model and where that model came from, plus a layers/roster/canon/skills summary. Read-only, never gates; `--offline` skips network lookups (the definition never needed one). Always exits 0 unless `status` itself crashes. |
| `petbox-wire doctor [--offline]` | Gate (exit code is significant): resolves the agent definition from the file cascade (base < user < project) and checks it against every known harness, printing OK or each violation, plus the layers and their per-field provenance. A broken layer is a hard failure here, same as in `apply`. Also reports skill-file drift against the kit templates, the session-banner budget margin, and a tail of `~/.petbox/wire.log`. Network checks are skipped with an explicit reason when the server is unreachable; `--offline` skips them itself up front (skill-file drift and banner-budget checks) — the definition resolve and the truthfulness gate still run, because neither touches a network. |
| `petbox-wire roles` | Print the active profile and its role→model bindings (`~/.petbox/roles.json`). Offline; an empty store exits 0 — no model is ever invented. |
| `petbox-wire roles export` | Write a bootstrap copy of `roles.json` to stdout (no secrets). Pipe it to a file on a new machine. |
| `petbox-wire profile use <name>` | Set `activeProfile` in `~/.petbox/roles.json`. Compiles nothing — re-run `apply`. |
| `petbox-wire model set <role> <model> [--agent <id>] [--profile <name>] [--allow-unknown-model]` | The only sanctioned way to write a role→model binding to `~/.petbox/roles.json`. Compiles nothing — prints `next: petbox-wire apply`. |
| `petbox-wire model unset <role> [--agent <id>] [--profile <name>]` | Remove a role→model binding for the given agent/profile from `~/.petbox/roles.json`. Compiles nothing — re-run `apply` afterwards. |

`update`, `apply`, `status`, `doctor`, `roles`, `profile` and `model` take no `<dir> <project>`.

## Flags (full wire)

| Flag | Effect |
| --- | --- |
| `--key <KEY>` | The project's API key. Mint it on the project's **Connect agent** page in the UI — key minting is out of scope for this tool. Omitted → taken from the env var, then from `~/.petbox/keys.json`. |
| `--env <VAR>` | Override the env-var name holding the key. |
| `--workspace <WS>` | Workspace stamped into the generated `SKILL.md`. Omitted → the workspace the server reports for your key; if neither is available the wire stops with a usage error (exit 2). |
| `--cleanup-legacy` | Remove a project's old per-project hook/plugin copies. |
| `--telemetry` | Wire Claude Code OTLP export into the project (off by default; Claude Code only). |
| `--telemetry-log <name>` | Target named log for telemetry (default `cc-telemetry`; created if missing). |
| `--help`, `-h` | Usage banner, exit 0. |

The API key is validated against the server **before** anything is persisted, so a bad key never
lands in your stores.

## The env-var name

The key is held in `PETBOX_<PROJECT>_API_KEY` — the project key upper-cased, runs of
non-alphanumeric characters collapsed to `_`, leading/trailing `_` trimmed, `PETBOX_` prefixed
(`kpvotes` → `PETBOX_KPVOTES_API_KEY`). Same name the UI's Connect page shows you. `--env` overrides
it, and a re-run reuses whatever name is already recorded for that directory — so a machine wired
before this scheme keeps its older variable. `~/.petbox/keys.json` is the ground truth for the name
your machine actually has.

## What it installs

- **Global hooks** for Claude Code (`~/.claude/settings.json`) and Factory Droid
  (`~/.factory/settings.json`): a `Stop` hook that mirrors each session into PetBox and a
  `SessionStart` hook that injects the memory protocol + canon — the only context the wiring
  injects; there is no per-prompt injection. Merged, never clobbered.
- **A global opencode plugin** (`~/.config/opencode/plugins/petbox.ts`) with the same two behaviors.
- **Per-project config** in `<dir>`: `.mcp.json` (Claude Code MCP), `.opencode/opencode.json`
  (opencode MCP), `.factory/mcp.json` (Droid MCP — **merged**, so team servers survive), the rendered
  `SKILL.md` under `.claude/skills/petbox/` and `.factory/skills/petbox/`, and the on-demand
  **agent-factory** skill under `.claude/skills/petbox-agent-factory/` and
  `.factory/skills/petbox-agent-factory/` (`roles` / `profile` / `doctor` / `apply`), plus the
  **write-economy** skill under `.claude/skills/petbox-write-economy/` and
  `.factory/skills/petbox-write-economy/` (`bodyRef`/`fragment`, when they pay off and when they
  don't), plus the **node-authoring** skill under `.claude/skills/petbox-node-authoring/` and
  `.factory/skills/petbox-node-authoring/` (GFM callouts, the sanitized inline-SVG diagram
  convention, and when a diagram is not worth drawing), plus the **methodology** skill under
  `.claude/skills/petbox-methodology/` and `.factory/skills/petbox-methodology/` (a thin,
  project-agnostic pointer that fetches this project's live task-methodology rules at runtime
  instead of assuming another project's), plus three **procedure** skills —
  **petbox-analysis-workspace** (run a voluminous multi-part investigation as staged files in an
  external folder instead of hundreds of tool calls), **petbox-factory-run** (drive a batch of
  prepared task statements to completion in one unattended pass) and **petbox-card-check** (is
  the ask checkable before a card is sent, and does the result cover it — bullet by bullet
  against the real diff) — under the same two roots.
  Only the first four are in the agent's automatic skill digest (`petbox-digest: auto`);
  **agent-factory**, **petbox-analysis-workspace**, **petbox-factory-run** and
  **petbox-card-check** are `petbox-digest: manual`, reachable by an explicit `skill(name)` call
  and costing no system-prompt room otherwise.
- **Optional**, per flag: the Claude Code OTLP export env (`--telemetry`).

All MCP configs reference the key as `${VAR}` / `{env:VAR}` — the key itself is never written into a
project file.

## `apply` — generated agent files

`apply` resolves the root it writes into with **`git rev-parse --show-toplevel` from cwd** — the
worktree it is actually running in — falling back to cwd only when cwd is not inside a git working
tree. It deliberately does NOT use the registry for this: the registry answers project *identity*,
not *where artifacts land* (§5 of `doc/agent-wiring.md` and the Doc site say the same). That root is
doubly load-bearing now, because the **project** definition layer is looked up under it.

From there `apply` builds the portable agent definition **from files** — no network, no key, no
scope — and compiles one file per role.

The definition is a CASCADE of layers, laid over each other lowest first:

| Layer | Where | Notes |
| --- | --- | --- |
| `base` | `default-agents.json`, inside this package | Always present. Missing = a broken install, and it throws at import. |
| `user` | `~/.petbox/agents/` | Machine-wide. Optional. |
| `project` | `<project root>/.petbox/agents/` | Per worktree. Optional. |

A layer directory that does not exist is a layer with **no opinion** — the normal case, never a
warning. So is one that exists but declares nothing: an empty directory you made for a future
override, or one holding only `.DS_Store` / `Thumbs.db` / a `README.md`. A layer that IS there and
states an intent (a `layer.json`, or any `petbox-*` document) and then cannot be read, parsed or
validated **hard-refuses the run**, naming the file and the parser's own position, having written
nothing. There is deliberately no last-known-good cache anywhere on this path: substituting a
previously-successful result is exactly what turns a broken source into a silent one.

Each layer holds `layer.json` (its name and `"mode": "overlay" | "replace"`) plus per-role
documents: `petbox-<slug>.json` to add a role or patch fields, `petbox-<slug>.md` to replace its
prose, `petbox-<slug>.append.md` to add an attributed section, and `{"slug": "...", "removed": true}`
to tombstone one. `petbox-wire layers` prints the whole cascade — which layers exist, what each did
to the roster, and which layer supplied every field. `apply`, `doctor` and `status` print that
per-field provenance too, on every run.

### Upgrading from a kit that fetched the definition from the server

**Read this if you ever edited roles through the PetBox admin UI, `agent_def_upsert`, or
`PUT /api/{project}/agent-defs/{key}`.** Those edits are no longer read by anything. The kit does
not fetch a definition, and there is no cache of one left on disk either.

The server side is GONE too: the endpoints, the MCP `agent_def_*` tools, the admin screens and the
table behind them were removed once the kit stopped reading them, so those calls now 404 rather than
accepting a write nothing would honour. Whatever you had stored there is not retrievable — the list
below is what changed for you when the kit stopped fetching:

- a role you ADDED server-side is gone from the roster, and its generated agent file is deleted by
  the orphan sweep on the next `apply` (it carries our `petbox: managed` marker, so it is ours to
  remove);
- prose you REWROTE server-side reverts to whatever the kit's `base` layer says;
- a tier or capability you changed server-side reverts likewise.

Move each edit into a layer instead — `~/.petbox/agents/` if it should apply to every project on
this machine, `<project root>/.petbox/agents/` if it belongs to one checkout. A layer is a directory
holding a `layer.json` plus one document per role you are changing; you only write the fields you
are actually changing, and everything else keeps coming from the layer below:

```
~/.petbox/agents/
  layer.json                  {"name": "user", "mode": "overlay"}
  petbox-worker.json          {"slug": "worker", "tier": "worker-highstakes"}
  petbox-worker.md            replaces that role's prose entirely
  petbox-worker.append.md     adds a section, attributed to this layer by name
  petbox-explore.json         {"slug": "explore", "removed": true, "reason": "..."}
```

Run `petbox-wire layers` afterwards: it prints what each layer did to the roster and which layer
supplied every field, so you can confirm the move landed before you `apply`. There is no import
command and there is nothing left to import from, deliberately: this is a one-time move of a small
document, and a migration tool would have outlived its use by years.

Files compiled, one per role:

| Harness | Path |
| --- | --- |
| Claude Code | `.claude/agents/<role>.md` |
| opencode | `.opencode/agent/<role>.md` |
| Factory Droid | `.factory/droids/<name>.md` |

These are **overwritten** — don't hand-edit them. Models come from your local `~/.petbox/roles.json`
binding only; an unbound role gets no `model:` line (droid: `model: inherit`). A role that requires a
capability the target harness does not declare is **skipped and reported**, never silently written
without it.

`--offline` has nothing to do with the definition (that resolve is file-only and always runs). It
skips the one network call `apply` still makes: the `/api/auth/validate` workspace probe behind the
skill refresh.

## Exit codes

| Code | Meaning |
| --- | --- |
| `0` | Success — every requested step ran. |
| `1` | Hard failure — invalid definition, unexpected throw, a refused clobbering write, a rejected or unreachable API key. |
| `2` | Usage / bad arguments. |
| `3` | Truthfulness policy block — some roles/harnesses were refused. **A partial write is possible.** |
| `4` | **Incomplete** — a requested step did not run for a reason you did not ask for. |

Only `apply` and `doctor` use the full taxonomy. The **full wire** exits `2` for usage errors and `1`
for *any* other failure — do not script against `3` or `4` there.

### `4` is new, and it is a breaking change

A run that skipped a step used to exit `0`, so `if petbox-wire apply; then …` could not tell a
partial run from a complete one — the honesty lived only in stdout, where no script reads it. Today
`apply` exits `4` when it had to skip a step for a reason outside your control (the workspace probe
that gates the skills refresh failed). **If you script against these codes, treat `4` as "retry
later", not as success.**

An **intentional** skip still exits `0`: `--offline` and running in a directory that is not a
registered project are things you asked for, and alarming on them would only teach you to ignore the
alarm.

When several conditions hold at once the stronger one owns the code — `1` and `3` both outrank `4`.
The skipped step is never lost: it stays in the `summary` JSON those failures print, under
`skillsSkipped`.

## Where things live

| Path | Contents |
| --- | --- |
| `~/.petbox/wire/` | The stable kit copy. Every hook and plugin points here, so wiring survives `npx` cache eviction. Refresh with `update`. |
| `~/.petbox/projects.json` | Registry: directory prefix → project, env-var name, base URL. |
| `~/.petbox/keys.json` | `{ "<ENV_VAR>": "<key>" }` (POSIX: `chmod 0600`). The kit hooks read `process.env[<ENV_VAR>]` first, then this file. |
| `~/.petbox/env.sh` | POSIX only — generated from the key store, sourced from your login profiles. |
| `~/.petbox/roles.json` | Local role→model bindings + `activeProfile`. Never uploaded. |
| `~/.petbox/agents/` | Optional machine-wide definition layer. Absent = no opinion. |
| `~/.petbox/cache/` | LKG memory canon per project. The definition has no cache — its layers are local files. |

The per-project MCP configs resolve `${<ENV_VAR>}` from the **real** environment, so wire also
persists it (Windows: user-scope env; POSIX: `env.sh` + profile source). Start a **new terminal**
before launching agents after the first wiring — the kit's own hooks work immediately, because they
read `~/.petbox/keys.json` directly.
