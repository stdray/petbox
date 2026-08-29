# Wire a project with petbox-wire

`petbox-wire` is the CLI that connects a project directory to PetBox: it persists the API key where every agent looks, writes the per-harness MCP configs and skills, installs the global session hooks, and compiles agent role files from a portable agent definition. Use it instead of hand-editing configs — the [connect guide](/doc/agent) explains what the wiring gives your agent once it is in place.

Requires **Node ≥ 23.6** (the kit is plain TypeScript run through native type-stripping — no build step, no dependencies).

## 1. Wire one project

Run the full wire once per project directory. You need an API key for that project first — mint it on the project's **Connect agent** page in the UI; `petbox-wire` never mints keys.

Pass the key as an **environment variable**, not `--key <KEY>` — npm writes the full command line
of every invocation to `~/.npm/_logs/*.log` in plain text with no rotation, so an argument-passed
key sits there readable indefinitely; an env-var assignment is never part of that logged argv.
`petbox-wire` already reads the key straight from `process.env[VAR]` when `--key` is omitted, so
no extra flag is needed:

```
# bash / zsh
PETBOX_<PROJECT>_API_KEY=<API_KEY> npx petbox-wire <dir> <projectKey>

# PowerShell
$env:PETBOX_<PROJECT>_API_KEY='<API_KEY>'; npx petbox-wire <dir> <projectKey>
```

It validates the key against the server **before** persisting anything, so a bad key never lands in your stores. Re-running is idempotent and self-heals a half-wired machine.

| Flag | Effect |
| --- | --- |
| `--env VAR` | Name of the environment variable holding the key. Overrides the derived / registered name. |
| `--key KEY` | The API key, passed directly. Still supported, but every use prints a warning: npm logs the full argv (this key included) to `~/.npm/_logs/*.log` in plain text with no rotation. Prefer the environment variable above. Omitted → taken from the environment variable, then from `~/.petbox/keys.json`. |
| `--workspace WS` | Workspace stamped into the generated skill. Omitted → the workspace the server reports for your key. There is no hardcoded fallback: if the server reports none and you pass no flag, the wire stops with a usage error (exit 2). |
| `--cleanup-legacy` | Remove wiring artefacts left by older kit versions from the project. |
| `--telemetry` | Wire Claude Code OTLP export into the project's `.claude/settings.json` (off by default; Claude Code only). |
| `--telemetry-log <name>` | Target named log for telemetry (default `cc-telemetry`); the log is created if missing. |
| `--help`, `-h` | Usage banner, exit 0. |

What the full wire writes into `<dir>`: `.mcp.json` (Claude Code), `.opencode/opencode.json` (opencode), `.factory/mcp.json` (Factory Droid — **merged**, not overwritten, so team servers survive), and a `SKILL.md` under `.claude/skills/petbox/`, `.factory/skills/petbox/` plus the `petbox-agent-factory`, `petbox-write-economy` and `petbox-node-authoring` skills in the same surfaces. All three MCP configs reference the key as `${VAR}` / `{env:VAR}` — the key itself is never written into a project file.

> **Note:** on a fresh machine the environment variable only exists in **new** terminals (Windows user-scope env; POSIX `~/.petbox/env.sh` sourced from your login profiles). The kit's own hooks work immediately, because they read `~/.petbox/keys.json` directly.

## 2. The env-var name

The key is always held in an environment variable named **`PETBOX_<PROJECT>_API_KEY`**. The project key is upper-cased, every run of non-alphanumeric characters collapses to a single `_`, leading and trailing `_` are trimmed, and `PETBOX_` is prefixed. So `kpvotes` → `PETBOX_KPVOTES_API_KEY` and `$system` → `PETBOX_SYSTEM_API_KEY`. This is the same name the UI **Connect agent** page and the [onboarding runbook](/doc/onboarding) show you, so the two paths agree.

`--env VAR` overrides the derived name, and a re-run reuses the name already recorded in the registry for that directory — an existing project never gets renamed under you. If your machine was wired before this scheme landed it may still carry an older name (e.g. `_SYSTEM_API_KEY`); when a config's `${VAR}` doesn't resolve, check `~/.petbox/keys.json` for the name you actually have.

## 3. Commands

| Command | What it does |
| --- | --- |
| `petbox-wire <dir> <projectKey>` | Full wire (above): key → validate → persist → kit copy → registry → project files → hooks → smoke. |
| `petbox-wire update` | Mirrors this package's `src/` into the stable kit at `~/.petbox/wire/` (orphan cleanup + content fingerprint). Nothing else: no keys, no registry, no hooks reinstall, no MCP/skills, no sticky flags. It does **not** compile agent files — that's `apply`. |
| `petbox-wire apply [--definition <key>] [--offline]` | Compiles per-harness agent role files from the agent definition + your local role→model binding. |
| `petbox-wire status [--offline]` | Prints FACT (never a verdict) about the current roster: per role × harness, the materialized artifact path, its bound model, and **where that model came from** — `roster` (`~/.petbox/roles.json`), `seed` (built-in preview, nothing written yet) or `none` (a problem — nothing to resolve from). Plus a four-pillar summary: definition source, roster completeness, memory canon, skill files. Always exits 0 unless `status` itself crashes. |
| `petbox-wire doctor [--offline]` | Resolves the agent definition the same way `apply` does (server → LKG cache → built-in default) and runs the truthfulness gate for every known harness against it, printing OK or each violation. Also reports definition drift, skill-file drift, and the session-banner budget margin. `--offline` skips all the network-backed checks up front (falls straight to LKG cache/built-in default; no drift or banner checks) — the truthfulness gate itself still runs. |
| `petbox-wire roles` | Prints the active profile and its role→model bindings from `~/.petbox/roles.json`. Offline; an empty store exits 0 with a message — it never invents a model. |
| `petbox-wire roles export` | Writes a bootstrap copy of `roles.json` to stdout (no secrets). Pipe it to a file on a new machine. |
| `petbox-wire profile use <name>` | Sets `activeProfile` in `~/.petbox/roles.json` (creating an empty profile shell if new). Re-run `apply` afterwards — this does not compile anything. Offline. |
| `petbox-wire model set <role> <model> [--agent <id>] [--profile <name>] [--allow-unknown-model]` | **The way to edit `roles.json` — not by hand.** Binds one role to a model for the given harness (`--agent`, default `claude-code`; aliases `cc`/`claude`, `factory`/`factory-droid`/`droid`, `opencode`). For `claude-code` the model must be a tier alias (`sonnet`\|`opus`\|`haiku`\|`fable`\|`inherit`) — the Task tool's `model` parameter is a closed enum of exactly those. A foreign-harness id (e.g. a droid `custom:*` id in a claude-code binding) is refused unless `--allow-unknown-model` forces it. Offline. Prints `next: petbox-wire apply` — it never compiles artifacts itself. |
| `petbox-wire model unset <role> [--agent <id>] [--profile <name>]` | Clears one role's binding for the given harness. A fair-empty binding is sometimes intentional (e.g. the machine lacks access to the tier a role would otherwise be bound to) — the role then inherits the session model, and `apply` warns about that honestly. Offline. Prints `next: petbox-wire apply`. |

`update`, `apply`, `status`, `doctor`, `roles`, `profile` and `model` take no `<dir> <projectKey>`; they resolve the project themselves (or don't need one).

## 4. Where a roster comes from

An agent roster is assembled from three independent sources, each with its own owner:

1. **The portable agent definition — server-authoritative.** Roles, tiers, required capabilities, spawn/escalation rules. Fetched with `GET /api/{project}/agent-defs/{key}` (`agents:read`). It is *portable*: it carries **no model ids** — a definition containing `role.model` is rejected.
2. **The local role→model binding — machine-authoritative.** `~/.petbox/roles.json`: `activeProfile` + `profiles.<name>.agents.<harness>.roles.<role>.model`. Never uploaded, never invented; if a role is unbound, no `model:` line is emitted (a Factory droid gets `model: inherit`). Edit it with `petbox-wire model set` / `model unset` (see the commands table above) — not by hand; both print `next: petbox-wire apply` because neither compiles artifacts itself. `petbox-wire status` (also above) shows exactly where a role's current model came from (`roster`/`seed`/`none`).
3. **The harness capability matrix — kit data.** Ships with the npm package and states, per harness, which capabilities exist (`mcp_subagent`, `hooks`, `spawn_subagents`, …). Known harnesses: `claude-code`, `opencode`, `droid`.

The gate between them is **truthfulness**: a role may only require capabilities the target harness actually declares. A role that fails is **skipped and reported** — never silently written with the offending line dropped. Clean roles in the same run are still written.

## 5. `apply` — compiled agent files

```
npx petbox-wire apply                        # server definition, or LKG cache
npx petbox-wire apply --offline              # never touch the network
npx petbox-wire apply --definition my-roster # a non-default definition key
```

`apply` finds the artifact target directory by **`git rev-parse --show-toplevel` from cwd** — the git worktree apply is actually running in — falling back to cwd itself only when cwd is not inside a git working tree at all. It deliberately does **not** consult the registry (`~/.petbox/projects.json`) for this: the registry answers project *identity* (which project/key/base-URL), not *where artifacts land*. Running `apply` from inside a worktree therefore writes into that worktree, never into the primary tree it was branched from — an earlier version resolved the target the same way it resolved project identity (registry longest-prefix) and could silently rewrite the primary tree's agent files from a worktree checked out on a different branch; that bug is fixed. `apply` always prints which root it resolved and how (`git`/`cwd`).

It then resolves the definition **server → LKG cache → built-in default** and writes, under that root:

| Harness | Path |
| --- | --- |
| Claude Code | `.claude/agents/petbox-<role>.md` |
| opencode | `.opencode/agent/petbox-<role>.md` |
| Factory Droid | `.factory/droids/petbox-<role>.md` |

Emitted file (and frontmatter `name:`) are namespaced `petbox-<role>` — `role.slug` and `~/.petbox/roles.json` themselves stay unprefixed; only the render is. `model:` frontmatter is written only when the role is bound (an unbound droid gets `model: inherit`) — it never invents a concrete model id.

> **Warning:** every generated file carries a `petbox: managed` origin marker, and `apply` **overwrites** files that carry it. It does the opposite for a file that doesn't — a real, non-PetBox file sitting at that exact path — where it **refuses** (loud, non-zero exit) to touch it at all, rather than clobbering it. Do not hand-edit a `petbox: managed` file; changes belong in the agent definition (server) or in `roles.json` (models), then re-apply. A pre-namespacing leftover (e.g. `.claude/agents/worker.md`) that PetBox itself owns is removed once its `petbox-<role>.md` replacement is written; a same-named file without the marker is left alone either way.

## 6. Offline and the LKG cache

Every successful fetch writes a last-known-good copy to `~/.petbox/cache/<project>.agent-def.json`. When the server is unreachable — or you pass `--offline` — `apply` uses that cache and says so, marking the result **stale**. Only when there is no cache at all (a fresh machine) does it fall back to the small built-in default definition.

`doctor` is **not** offline by construction — it resolves the definition the same server → LKG cache → built-in way `apply` does, plus a workspace probe for its skill-file and banner-budget checks, so a plain `petbox-wire doctor` does hit the network. Pass `--offline` to skip all of that (straight to LKG cache/built-in default, no drift or banner checks; the truthfulness gate still runs against whatever definition that leaves). `roles`, `roles export`, `profile use`, `model set` and `model unset` are the ones that are offline by construction — no network path exists for them at all.

The SessionStart memory canon has its own cache alongside it: `~/.petbox/cache/<project>.canon.md`.

## 7. Exit codes

| Code | Meaning |
| --- | --- |
| `0` | Success — every requested step ran and every known harness wrote every role. |
| `1` | Hard failure — invalid definition, unexpected throw, a refused clobbering write, or a rejected/unreachable API key. |
| `2` | Usage / bad arguments. |
| `3` | Truthfulness policy block — some roles or harnesses were refused. **A partial write is possible.** |
| `4` | **INCOMPLETE** — a requested step did not run for a reason you did not ask for (e.g. the workspace probe that gates the skills refresh failed), even though nothing was refused and no policy fired. An *intentional* skip (`--offline`, an unregistered project directory) stays `0` — this code exists so a script can tell "partial" from "clean" without reading stdout. `doctor` never reports 4 (it skips no step of its own).|

> **Note:** the **full-wire path** is not limited to `2`/`1`. Its own visible steps (self-smoke, then the `apply` pass that seeds bindings and compiles artifacts) can each fail without aborting the run, and the reported exit code is the **strongest** of them by priority `1 > 3 > 4 > 0` — so a full wire whose final `apply` step hits a truthfulness block or an incomplete skill refresh surfaces `3` or `4`, not just `1`. Usage errors (`2`) still end the run immediately during argument parsing, before any step can compete.

Exit `3` is a *policy* outcome, not a crash: the definition asked for something a harness does not offer. Fix the definition (or accept the skip); don't retry. Exit `4` similarly is not a crash — re-run `petbox-wire apply` to retry just the step that was skipped.

## 8. Scopes and endpoints

The CLI only ever **reads** definitions, so an `agents:read` key is enough to wire and apply. `agents:write` is needed only to push a definition **back** to the server — `PUT /api/{project}/agent-defs/{key}`, or the `agent_def_upsert` MCP tool — which is an authoring action, not a wiring one.

| Endpoint | Used by |
| --- | --- |
| `GET /api/auth/validate` | Full wire — key validation before anything is persisted; also reports the workspace the key belongs to. Also the workspace probe `apply`, `doctor` and `status` each run (unless `--offline`) to gate their skill-file refresh/checks — a failed probe here is what makes `apply`/full-wire exit `4` (INCOMPLETE). |
| `GET /api/{project}/agent-defs/{key}` | `apply` and `doctor` — the portable definition (`agents:read`); `doctor` resolves it the same server → LKG cache → built-in way `apply` does. |
| `GET /api/memory/{project}/canon` | SessionStart hook — the memory canon (cached to `~/.petbox/cache/`). This is the only context the wiring injects; there is no per-prompt injection. Also read by `doctor`'s banner-budget check and `status`'s four-pillar summary. |
| `POST /api/logs/{project}/logs` | Full wire — ensures the telemetry log exists. |
| `POST /api/sessions/{project}/wire-smoke` | Full wire — the final self-smoke that proves the key round-trips. |

## 9. What lives under `~/.petbox/`

| Path | Contents |
| --- | --- |
| `wire/` | The stable kit copy (hooks and scripts point here, so wiring survives npx cache eviction). Refresh with `update`. |
| `projects.json` | Registry: directory prefix → project, env-var name, base URL. Resolved by longest prefix against cwd. |
| `keys.json` | Flat `{ "<ENV_VAR>": "<key>" }` map the kit hooks read directly. Tightened to `0600` on POSIX. |
| `env.sh` | POSIX only — regenerated from the key store, sourced from your login profiles. |
| `roles.json` | Local role→model bindings + `activeProfile`. Machine-owned; never uploaded. |
| `cache/<project>.agent-def.json` | LKG agent definition. |
| `cache/<project>.canon.md` | LKG memory canon. |
| `wire.log` | Trace of silent-failure-shaped events; `doctor` prints its most recent lines (empty/absent is normal, not a failure). |

These are **not** secrets you should commit anywhere, and nothing here is regenerated by `update` except the kit itself.
