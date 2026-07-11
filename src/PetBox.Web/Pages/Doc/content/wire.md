# Wire a project with petbox-wire

`petbox-wire` is the CLI that connects a project directory to PetBox: it persists the API key where every agent looks, writes the per-harness MCP configs and skills, installs the global session hooks, and compiles agent role files from a portable agent definition. Use it instead of hand-editing configs — the [connect guide](/doc/agent) explains what the wiring gives your agent once it is in place.

Requires **Node ≥ 23.6** (the kit is plain TypeScript run through native type-stripping — no build step, no dependencies).

## 1. Wire one project

Run the full wire once per project directory. You need an API key for that project first — mint it on the project's **Connect agent** page in the UI; `petbox-wire` never mints keys.

```
npx petbox-wire <dir> <projectKey> --key <API_KEY>
```

It validates the key against the server **before** persisting anything, so a bad key never lands in your stores. Re-running is idempotent and self-heals a half-wired machine.

| Flag | Effect |
| --- | --- |
| `--env VAR` | Name of the environment variable holding the key. Overrides the derived / registered name. |
| `--key KEY` | The API key. Omitted → taken from the environment variable, then from `~/.petbox/keys.json`. |
| `--workspace WS` | Workspace stamped into the generated skill. Omitted → the workspace the server reports for your key. There is no hardcoded fallback: if the server reports none and you pass no flag, the wire stops with a usage error (exit 2). |
| `--cleanup-legacy` | Remove wiring artefacts left by older kit versions from the project. |
| `--telemetry` | Wire Claude Code OTLP export into the project's `.claude/settings.json` (off by default; Claude Code only). |
| `--telemetry-log <name>` | Target named log for telemetry (default `cc-telemetry`); the log is created if missing. |
| `--help`, `-h` | Usage banner, exit 0. |

What the full wire writes into `<dir>`: `.mcp.json` (Claude Code), `.opencode/opencode.json` (opencode), `.factory/mcp.json` (Factory Droid — **merged**, not overwritten, so team servers survive), and a `SKILL.md` under `.claude/skills/petbox/`, `.factory/skills/petbox/` plus the `petbox-agent-factory` skill in the same surfaces. All three MCP configs reference the key as `${VAR}` / `{env:VAR}` — the key itself is never written into a project file.

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
| `petbox-wire doctor` | Offline truthfulness gate: checks the default definition against every known harness and prints OK or each violation. |
| `petbox-wire roles` | Prints the active profile and its role→model bindings from `~/.petbox/roles.json`. Offline; an empty store exits 0 with a message — it never invents a model. |
| `petbox-wire roles export` | Writes a bootstrap copy of `roles.json` to stdout (no secrets). Pipe it to a file on a new machine. |
| `petbox-wire profile use <name>` | Sets `activeProfile` in `~/.petbox/roles.json` (creating an empty profile shell if new). Re-run `apply` afterwards — this does not compile anything. |

`update`, `apply`, `doctor`, `roles` and `profile` take no `<dir> <projectKey>`; they resolve the project themselves (or don't need one).

## 4. Where a roster comes from

An agent roster is assembled from three independent sources, each with its own owner:

1. **The portable agent definition — server-authoritative.** Roles, tiers, required capabilities, spawn/escalation rules. Fetched with `GET /api/{project}/agent-defs/{key}` (`agents:read`). It is *portable*: it carries **no model ids** — a definition containing `role.model` is rejected.
2. **The local role→model binding — machine-authoritative.** `~/.petbox/roles.json`: `activeProfile` + `profiles.<name>.agents.<harness>.roles.<role>.model`. Never uploaded, never invented; if a role is unbound, no `model:` line is emitted (a Factory droid gets `model: inherit`).
3. **The harness capability matrix — kit data.** Ships with the npm package and states, per harness, which capabilities exist (`mcp_subagent`, `hooks`, `spawn_subagents`, …). Known harnesses: `claude-code`, `opencode`, `droid`.

The gate between them is **truthfulness**: a role may only require capabilities the target harness actually declares. A role that fails is **skipped and reported** — never silently written with the offending line dropped. Clean roles in the same run are still written.

## 5. `apply` — compiled agent files

```
npx petbox-wire apply                        # server definition, or LKG cache
npx petbox-wire apply --offline              # never touch the network
npx petbox-wire apply --definition my-roster # a non-default definition key
```

`apply` finds the project root by the **longest matching directory prefix** in `~/.petbox/projects.json` (falling back to cwd), then resolves the definition **server → LKG cache → built-in default** and writes, under the project root:

| Harness | Path |
| --- | --- |
| Claude Code | `.claude/agents/<role>.md` |
| opencode | `.opencode/agent/<role>.md` |
| Factory Droid | `.factory/droids/<name>.md` |

> **Warning:** `apply` **overwrites** these generated files. Do not hand-edit them — put your changes in the agent definition (server) or in `roles.json` (models) and re-apply.

## 6. Offline and the LKG cache

Every successful fetch writes a last-known-good copy to `~/.petbox/cache/<project>.agent-def.json`. When the server is unreachable — or you pass `--offline` — `apply` uses that cache and says so, marking the result **stale**. Only when there is no cache at all (a fresh machine) does it fall back to the small built-in default definition. `doctor`, `roles`, `roles export` and `profile use` are offline by construction.

The SessionStart memory canon has its own cache alongside it: `~/.petbox/cache/<project>.canon.md`.

## 7. Exit codes

| Code | Meaning |
| --- | --- |
| `0` | Success. |
| `1` | Hard failure — invalid definition, unexpected throw. |
| `2` | Usage / bad arguments. |
| `3` | Truthfulness policy block — some roles or harnesses were refused. **A partial write is possible.** |

> **Note:** only `apply` and `doctor` use the full 0/1/2/3 taxonomy. The **full-wire path** exits `2` for usage errors and `1` for *any* other failure — do not script against `3` there.

Exit `3` is a *policy* outcome, not a crash: the definition asked for something a harness does not offer. Fix the definition (or accept the skip); don't retry.

## 8. Scopes and endpoints

The CLI only ever **reads** definitions, so an `agents:read` key is enough to wire and apply. `agents:write` is needed only to push a definition **back** to the server — `PUT /api/{project}/agent-defs/{key}`, or the `agent_def_upsert` MCP tool — which is an authoring action, not a wiring one.

| Endpoint | Used by |
| --- | --- |
| `GET /api/auth/validate` | Full wire — key validation before anything is persisted; also reports the workspace the key belongs to. |
| `GET /api/{project}/agent-defs/{key}` | `apply` — the portable definition (`agents:read`). |
| `GET /api/memory/{project}/canon` | SessionStart hook — the memory canon (cached to `~/.petbox/cache/`). This is the only context the wiring injects; there is no per-prompt injection. |
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

These are **not** secrets you should commit anywhere, and nothing here is regenerated by `update` except the kit itself.
