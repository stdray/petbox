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
| `petbox-wire apply [--definition <key>] [--offline]` | Compile the per-harness agent role files from the portable agent definition + your local role→model binding. |
| `petbox-wire doctor` | Offline gate: check the default agent definition against every known harness; print OK or each violation. |
| `petbox-wire roles` | Print the active profile and its role→model bindings (`~/.petbox/roles.json`). Offline; an empty store exits 0 — no model is ever invented. |
| `petbox-wire roles export` | Write a bootstrap copy of `roles.json` to stdout (no secrets). Pipe it to a file on a new machine. |
| `petbox-wire profile use <name>` | Set `activeProfile` in `~/.petbox/roles.json`. Compiles nothing — re-run `apply`. |

`update`, `apply`, `doctor`, `roles` and `profile` take no `<dir> <project>`.

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
  `.factory/skills/petbox-agent-factory/` (`roles` / `profile` / `doctor` / `apply`).
- **Optional**, per flag: the Claude Code OTLP export env (`--telemetry`).

All MCP configs reference the key as `${VAR}` / `{env:VAR}` — the key itself is never written into a
project file.

## `apply` — generated agent files

`apply` resolves the project root by the longest registry prefix covering cwd, fetches the portable
agent definition (`GET /api/{project}/agent-defs/{key}`, scope **`agents:read`** — a read-only key is
enough), and compiles one file per role:

| Harness | Path |
| --- | --- |
| Claude Code | `.claude/agents/<role>.md` |
| opencode | `.opencode/agent/<role>.md` |
| Factory Droid | `.factory/droids/<name>.md` |

These are **overwritten** — don't hand-edit them. Models come from your local `~/.petbox/roles.json`
binding only; an unbound role gets no `model:` line (droid: `model: inherit`). A role that requires a
capability the target harness does not declare is **skipped and reported**, never silently written
without it.

Every successful definition fetch is cached to `~/.petbox/cache/<project>.agent-def.json` (LKG). When
the server is unreachable — or you pass `--offline` — `apply` uses that cache and marks the result
stale; only with no cache at all does it fall back to the built-in default definition.

## Exit codes

| Code | Meaning |
| --- | --- |
| `0` | Success. |
| `1` | Hard failure — invalid definition, unexpected throw. |
| `2` | Usage / bad arguments. |
| `3` | Truthfulness policy block — some roles/harnesses were refused. **A partial write is possible.** |

Only `apply` and `doctor` use the full taxonomy. The **full wire** exits `2` for usage errors and `1`
for *any* other failure — do not script against `3` there.

## Where things live

| Path | Contents |
| --- | --- |
| `~/.petbox/wire/` | The stable kit copy. Every hook and plugin points here, so wiring survives `npx` cache eviction. Refresh with `update`. |
| `~/.petbox/projects.json` | Registry: directory prefix → project, env-var name, base URL. |
| `~/.petbox/keys.json` | `{ "<ENV_VAR>": "<key>" }` (POSIX: `chmod 0600`). The kit hooks read `process.env[<ENV_VAR>]` first, then this file. |
| `~/.petbox/env.sh` | POSIX only — generated from the key store, sourced from your login profiles. |
| `~/.petbox/roles.json` | Local role→model bindings + `activeProfile`. Never uploaded. |
| `~/.petbox/cache/` | LKG agent definition + memory canon per project. |

The per-project MCP configs resolve `${<ENV_VAR>}` from the **real** environment, so wire also
persists it (Windows: user-scope env; POSIX: `env.sh` + profile source). Start a **new terminal**
before launching agents after the first wiring — the kit's own hooks work immediately, because they
read `~/.petbox/keys.json` directly.
