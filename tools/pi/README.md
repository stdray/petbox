# pi harness adapter (unofficial)

The [pi](https://pi.dev) coding agent is wired to PetBox by a **hand-deployed extension**, not by
`petbox-wire`. This directory is the source of truth for that extension; the copy pi actually
loads lives in pi's extension directory, outside the repo, and is refreshed by copying the file:

```
tools/pi/petbox-pi.ts  ──copy──▶  ~/.pi/agent/extensions/petbox-pi.ts     (user scope, all projects)
                                   <project>/.pi/extensions/petbox-pi.ts  (project scope)
```

It is deliberately **not part of `petbox-wire`**: the kit owns the shared ladder (registry,
transcript filters, protocol + canon rendering, budget), and this file only adapts pi's
in-process session tree and prompt sections to it. Nothing in `petbox-wire apply` / `update`
touches it, so an update never clobbers it and a deploy never overwrites a kit file.

What it does, once deployed:

- **Session mirror** — appends the active branch's dialogue (user/assistant text only) to the
  PetBox Sessions module: one incremental push per turn, a final best-effort push on shutdown.
  A cwd that is not in `~/.petbox/projects.json` is a silent no-op.
- **Memory-protocol banner** — injects the same protocol + canon + owner-only-skills banner the
  Claude Code / codex / opencode hooks inject, as a pi system-prompt section named `petbox`.
  Built once per session (see "Banner stability" below).
- **Recall nudge** — on a resumed session, one message telling the model to recall recent
  session/decision memories, so the frozen banner does not have to carry it.

## Install

Prerequisites: `petbox-wire` has run on this machine at least once (the extension imports the
stable kit mirror `~/.petbox/wire`), the project is registered in `~/.petbox/projects.json`, and
its key is reachable as an env var (or via `~/.petbox/keys.json`).

```sh
# 1. the extension (user scope; use <project>/.pi/extensions/ to scope it to one project)
mkdir -p ~/.pi/agent/extensions
cp tools/pi/petbox-pi.ts ~/.pi/agent/extensions/petbox-pi.ts

# 2. project MCP config — copy this repo's .pi/mcp.json into the project you are wiring
mkdir -p <project>/.pi
cp .pi/mcp.json <project>/.pi/mcp.json     # adjust the key env var if the project is not $system

# 3. the key itself (never committed; .pi/mcp.json only references ${...})
export PETBOX_API_KEY=...                  # or PETBOX_<PROJECT>_API_KEY for another project

# 4. restart pi (extensions are loaded once at startup; `/reload` re-reads config, not new files)
```

`.pi/mcp.json` is the pi analogue of Claude Code's `.mcp.json`: one `petbox` server, exposed via
`codemode` with eight core tools declared `direct` (`tasks_search`, `tasks_node_get`,
`tasks_upsert`, `tasks_delta`, `comments_upsert`, `tasks_methodology_guide`, `memory_search`,
`memory_remember`) so the methodology core is callable without a `tool_search` hop; the remaining
~90 tools stay reachable from codemode scripts. Project config is read only for **trusted**
projects, so the first pi run in a project asks.

## Verify

1. `/mcp` in pi lists `petbox` as connected, and `tool_search "memory_upsert"` finds the
   non-direct tools.
2. The model opens its first reply with the PetBox banner line (the section was injected).
3. The banner is in the session file, once, as the leading system message's `petbox` section:
   ```sh
   grep -c '"petbox"' ~/.pi/agent/sessions/<cwd-slug>/<session>.jsonl
   ```
4. A `session_*` search over the same period shows the pi session being appended.

## Banner stability (why the section is frozen, and where it is withheld)

pi replays the system prompt as an **append-only transcript of section patches**: it diffs the
desired sections against the ones the current system message carries, and when any section
differs it appends a new system message containing that section **whole**
(`dist/core/agent-session.js` → `_preparePromptAndToolLoadout` → `diffSystemPromptSections`).
Unchanged sections cost nothing; a changed one is re-injected in full.

The banner must therefore be byte-stable for the life of a transcript. On `session_start` the
extension first looks for a `petbox` section already present on the restored branch and reuses it
verbatim — **unwrapping one tag level first**: pi stores a section the way
`buildSystemPromptSections` built it (`<petbox>…</petbox>`), so feeding the stored value straight
back in makes pi wrap it a second time, and the banner grows one tag level per restart until the
grown text re-injects itself. Only a transcript without a section gets a freshly assembled banner. The SessionStart reason
(`startup` / `new` / `resume` / `fork` / `reload`) never reaches the section — a resumed session
gets the kit's recall line as a one-shot message instead. Consequence, by design: a resumed
session keeps the canon it started with, and a canon/definition edit reaches the model at its next
session rather than by re-rendering the prompt mid-conversation.

The section is also **withheld where the protocol cannot be acted on**: if the session declares no
petbox MCP tool (`pi.getActiveTools()` has nothing under the kit's own tool prefix), neither the
banner nor the nudge is injected — a pi subagent runs with a strict native tool allowlist and has
no MCP tools at all, so the banner there was an instruction it could not follow. A subagent that
does declare them (a custom agent listing them in `tools`) gets the banner normally. The decision
is taken on the session's first run and traced to `~/.petbox/wire.log` when it abstains.

Background: observation `pi-banner-reinjected-mid-conversation` (`$system` board `observations`),
which recorded a 7.6 KB banner re-injected 157 messages into a live yobagent session.

## Portability

The kit imports at the top of `petbox-pi.ts` are **machine-absolute**
(`C:/Users/stdray/.petbox/wire/...`) because the deploy target is not fixed: the user-scoped and
project-scoped extension directories sit at different depths, so no single relative specifier
reaches `~/.petbox/wire` from both. On a machine whose home directory differs, repoint the prefix:

```sh
sed -i 's#C:/Users/stdray/.petbox/wire#/home/<user>/.petbox/wire#g' ~/.pi/agent/extensions/petbox-pi.ts
```

Tracked as a follow-up observation — the fix (a home-resolved dynamic import) is a change to this
file's loading mechanism and does not belong in the banner-stability change.

## Layout

| Path | Role |
| --- | --- |
| `tools/pi/petbox-pi.ts` | the extension (source of truth; copy to pi's extension dir) |
| `.pi/mcp.json` | pi MCP config for **this** project — the template for others |
| `~/.petbox/wire/*` | the petbox-wire kit mirror it imports (installed by `petbox-wire`, not by us) |
