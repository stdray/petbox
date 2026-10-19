# pi harness wiring (unofficial)

The [pi](https://pi.dev) coding agent is wired to PetBox by **hand-deployed files**, not by
`petbox-wire`. This directory is the source of truth for all of them; the copies pi actually
loads live in `~/.pi/agent/`, outside the repo, and are refreshed by `deploy.sh`:

```
tools/pi/extensions/petbox-pi.ts   ──▶  ~/.pi/agent/extensions/petbox-pi.ts
tools/pi/extensions/agent-rules.ts ──▶  ~/.pi/agent/extensions/agent-rules.ts
tools/pi/agents/*.md               ──▶  ~/.pi/agent/agents/*.md
tools/pi/rules/amd-*.md            ──▶  ~/.pi/agent/skills/amd-*.md
```

```sh
./tools/pi/deploy.sh --dry-run   # what would change
./tools/pi/deploy.sh             # install / refresh
```

Then restart pi: extensions load at startup, and `/reload` re-reads config but not new files.

It is deliberately **not part of `petbox-wire`**: the kit owns the shared ladder (registry,
transcript filters, protocol + canon rendering, budget), and these files only adapt pi's
in-process session tree, prompt sections and subagent roles to it. Nothing in `petbox-wire
apply` / `update` touches them, so an update never clobbers them and a deploy never overwrites
a kit file. pi is not a wire harness (owner decision 2026-10-20): roles here are free to differ
from `src/common/default-agents.json`.

## Layout

| Path | What it is |
|---|---|
| `extensions/petbox-pi.ts` | session mirror + memory banner + recall nudge (see below) |
| `extensions/agent-rules.ts` | assembles `rules/amd-*.md` into the `agent_rules` prompt section for the main session |
| `agents/*.md` | our five subagent roles: `worker`, `scout`, `oracle`, `reviewer`, `researcher` |
| `rules/amd-*.md` | the operating rules, one topic per file (always injected) |
| `rules/pbx-*.md` | PetBox-specific rules — injected only when the session declares a petbox MCP tool |

## Rules: why skills and not `AGENTS.md`

A pi context file cannot include another file, and a subagent in `prompt_mode: replace` never
loads context files at all — so one big `AGENTS.md` reaches either the main session or the
children, never both. The rules are therefore one file per topic, consumed by two paths:

- **main session** — `agent-rules.ts` reads `rules/amd-*.md` on every turn and sets them as the
  `agent_rules` prompt section (cached on mtime, so an edit lands without a restart). Files
  named `pbx-*.md` go into a separate `petbox_rules` section that is added **only when the
  session declares a petbox MCP tool** — PetBox prose must not reach a session that cannot
  call the board. Same capability test and asymmetry as `petbox-pi.ts`'s banner: a positive
  sticks, a negative is re-checked every run because MCP tools register asynchronously;
- **children** — each role lists the files it needs in its `skills:` frontmatter, and
  `pi-subagents` preloads exactly those.

The files sit in a skills directory but are **flat `.md`**, which pi's skill discovery ignores
(it only accepts directories containing `SKILL.md`), so they never pollute the main session's
skills catalog and need no `disable-model-invocation` flag. The trade-off is that they are not
`/skill:name`-invokable.

Per-role assignment is deliberate: `scout` and `reviewer` do not carry the destructive-action or
delegation rules, because they have no tools to act on them.

## `petbox-pi.ts`

- **Session mirror** — appends the active branch's dialogue (user/assistant text only) to the
  PetBox Sessions module: one incremental push per turn, a final best-effort push on shutdown.
  A cwd that is not in `~/.petbox/projects.json` is a silent no-op.
- **Memory-protocol banner** — injects the same protocol + canon + owner-only-skills banner the
  Claude Code / codex / opencode hooks inject, as a pi system-prompt section named `petbox`.
  Built once per session (see "Banner stability" below).
- **Recall nudge** — on a resumed session, one message telling the model to recall recent
  session/decision memories, so the frozen banner does not have to carry it.

## Install

Prerequisites for `petbox-pi.ts`: `petbox-wire` has run on this machine at least once (the
`extension imports the stable kit mirror `~/.petbox/wire`), the project is registered in
`~/.petbox/projects.json`, and its key is reachable as an env var (or via `~/.petbox/keys.json`).

What `petbox-pi.ts` does, once deployed:
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
does declare them (a custom agent listing them in `tools`) gets the banner normally.

The check is asymmetric on purpose: **a positive sticks, a negative is re-checked every run.** MCP
tools are declared asynchronously, so the first run of a restart can see an empty catalog on a
session that does have them (observed live on a healthy main session). Freezing that first "no"
would silently withhold the protocol for the whole session; re-checking costs at most one late
injection on the run where the tools appear. The first negative is traced to
`~/.petbox/wire.log`.

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
| `tools/pi/extensions/petbox-pi.ts` | session mirror, memory banner, recall nudge |
| `tools/pi/extensions/agent-rules.ts` | assembles `rules/amd-*.md` into the `agent_rules` prompt section |
| `tools/pi/agents/*.md` | subagent roles (`worker`, `scout`, `oracle`, `reviewer`, `researcher`) |
| `tools/pi/rules/amd-*.md` | operating rules, one topic per file |
| `tools/pi/deploy.sh` | installs all of the above into `~/.pi/agent` |
| `.pi/mcp.json` | pi MCP config for **this** project — the template for others |
| `~/.petbox/wire/*` | the petbox-wire kit mirror `petbox-pi.ts` imports (installed by `petbox-wire`, not by us) |
