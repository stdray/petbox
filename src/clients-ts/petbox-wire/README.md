# petbox-wire

Wire any project to [PetBox](https://petbox.3po.su) for **Claude Code**, **opencode** and
**Factory Droid** — with one command, no repo clone:

```bash
npx petbox-wire <dir> <project> --key <KEY>
npx petbox-wire update
```

- `<dir>` — the project directory to wire (its cwd prefix is registered).
- `<project>` — the PetBox project key the directory maps to.
- `--key <KEY>` — the project's API key. Mint one from a Claude session on the `$system`
  project (`mcp__petbox__apikey_create`); key minting is out of scope for this tool.
- `--env <VAR>` — override the env-var name (default `<PROJECT>_API_KEY`, uppercased).
- `--workspace <WS>` — workspace for the SKILL.md permalink (default `stdray`).
- `--cleanup-legacy` — remove a project's old per-project hook/plugin copies.
- `update` — refresh only the stable kit under `~/.petbox/wire/` (see below).

## Requirements

**Node >= 23.6** — the kit is plain TypeScript run through Node's native type-stripping
(no build, zero runtime dependencies). Older Node exits with a clear version error.

## What it installs

- **Global hooks** for Claude Code (`~/.claude/settings.json`) and Factory Droid
  (`~/.factory/settings.json`): a `Stop` hook that mirrors each session into PetBox and a
  `SessionStart` hook that injects the memory protocol + canon. Merged, never clobbered.
- **A global opencode plugin** (`~/.config/opencode/plugins/petbox.ts`) with the same two
  behaviors.
- **Per-project config** in `<dir>`: `.mcp.json` (Claude Code MCP), `.opencode/opencode.json`
  (opencode MCP), `.factory/mcp.json` (Droid MCP, merged), the rendered `SKILL.md` under
  `.claude/skills/petbox/` and `.factory/skills/petbox/`, and the on-demand
  **agent-factory** skill under `.claude/skills/petbox-agent-factory/` and
  `.factory/skills/petbox-agent-factory/` (`roles` / `profile` / `doctor` / `apply` — not every session).

## Where things live

- **Stable kit copy:** `~/.petbox/wire/` — every run copies the kit here, and all global hooks
  point at this stable location (so they survive `npx` cache eviction).
- **Registry:** `~/.petbox/projects.json` — maps a filesystem prefix → project + env var. The
  hooks and opencode plugin resolve the active project by cwd against it.
- **Key store:** `~/.petbox/keys.json` — `{ "<ENV_VAR>": "<key>" }` (POSIX: `chmod 0600`). The
  kit hooks read `process.env[<ENV_VAR>]` first, then this file. Header values in the committed
  configs stay as `${VAR}` references — no secret is written into any project file.
- **Environment variable:** the per-project MCP configs resolve `${<ENV_VAR>}` from the real
  environment, so wire also persists it — Windows: user-scope env; POSIX: `~/.petbox/env.sh`
  (generated from the key store) sourced from your login profiles. Start a **new terminal**
  before launching agents after the first wiring.

## Updating the kit

**Safe kit-text refresh** (no project/key; preferred when only hook/protocol/script text changed):

```bash
npx petbox-wire@latest update
```

This mirrors the package's `src/` into `~/.petbox/wire/` with the same orphan cleanup as a full
wire (exact mirror, never a union), prints a short content hash before → after, and **does not**:

- rotate or require API keys / touch `~/.petbox/keys.json`
- wipe or rewrite registry entries in `~/.petbox/projects.json`
- reset sticky per-project prompt-RAG or telemetry flags
- reinstall global hooks or rewrite per-project MCP configs / rendered `SKILL.md`

v1 is **stable kit only**. To regenerate per-project MCP/skills after a template change, re-run
the full wire (idempotent):

```bash
npx petbox-wire@latest <dir> <project> --key <KEY>
```
