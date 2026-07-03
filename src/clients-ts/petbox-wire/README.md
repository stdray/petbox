# petbox-wire

Wire any project to [PetBox](https://petbox.3po.su) for **Claude Code**, **opencode** and
**Factory Droid** — with one command, no repo clone:

```bash
npx petbox-wire <dir> <project> --key <KEY>
```

- `<dir>` — the project directory to wire (its cwd prefix is registered).
- `<project>` — the PetBox project key the directory maps to.
- `--key <KEY>` — the project's API key. Mint one from a Claude session on the `$system`
  project (`mcp__petbox__apikey_create`); key minting is out of scope for this tool.
- `--env <VAR>` — override the env-var name (default `<PROJECT>_API_KEY`, uppercased).
- `--workspace <WS>` — workspace for the SKILL.md permalink (default `stdray`).
- `--cleanup-legacy` — remove a project's old per-project hook/plugin copies.

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
  (opencode MCP), `.factory/mcp.json` (Droid MCP, merged), and the rendered `SKILL.md` under
  `.claude/skills/petbox/` and `.factory/skills/petbox/`.

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

Re-run `npx petbox-wire@latest <dir> <project> --key <KEY>` to refresh the stable copy in
`~/.petbox/wire/` and the generated config. Runs are idempotent.
