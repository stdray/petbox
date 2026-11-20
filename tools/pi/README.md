# pi harness wiring (unofficial)

The [pi](https://pi.dev) coding agent is wired to PetBox by **hand-deployed files**, not by
`petbox-wire`. This directory is the source of truth for all of them; the copies pi actually
loads live in `~/.pi/agent/`, outside the repo, and are refreshed by `deploy.sh`:

```
tools/pi/extensions/petbox-pi.ts   ──▶  ~/.pi/agent/extensions/petbox-pi.ts
tools/pi/extensions/agent-rules.ts ──▶  ~/.pi/agent/extensions/agent-rules.ts
tools/pi/extensions/<name>/        ──▶  ~/.pi/agent/extensions/<name>/   (whole directory)
tools/pi/agents/*.md               ──▶  ~/.pi/agent/agents/*.md
tools/pi/rules/amd-*.md            ──▶  ~/.pi/agent/skills/amd-*.md
```

Flat `extensions/*.ts` files are copied with a `cmp` check; `extensions/<name>/`
directories are mirrored with `diff -r` (pi loads `index.ts` from a subdirectory and its
sibling modules have to arrive with it). Both are idempotent — a second run writes nothing.
The installed directory is mirrored, not merged — do not hand-edit `~/.pi/agent/extensions/<name>/`, it is replaced wholesale whenever the repo copy changes.

```sh
./tools/pi/deploy.sh --dry-run   # what would change
./tools/pi/deploy.sh             # install / refresh
```

Then restart pi: extensions load at startup, and `/reload` re-reads config but not new files.

**Tests live in `tests/`, not beside the extensions.** `deploy.sh` copies `extensions/*.ts` into
pi's load path, so a `*.test.ts` sitting there would be loaded as an extension. Run them with:

```sh
bun test tools/pi/tests/opencode-bridge/          # or: node --test tools/pi/tests/opencode-bridge/*.test.ts
OPENCODE_BRIDGE_SKIP_LIVE=1 bun test …            # unit only; the live tests spawn real opencode
```

**This folder has its own gate, not the Cake one.** A change confined to `tools/pi/**` is proven by
`bun test tools/pi/tests/` (unit, then live) plus a real `tools/pi/deploy.sh` run whose *second*
invocation writes nothing (`+` absent, everything `=`) — not by the repo's Cake `Test` target, because
this folder is shipped to `~/.pi/agent`, not built into the product, and `dotnet format` / `WebTs*`
never see it. The moment a diff escapes into `src/`, `tests/` or `build.cs`, the full Cake gate
applies to that change again.

**Role-derived child config (`opencode-bridge/role-config.ts`).** With a `role` parameter the tool
stops using its fixed read-only policy and derives the child's opencode config from that role's
file in `~/.pi/agent/agents/<role>.md`: the markdown body becomes `agents.<role>.system` (measured:
that REPLACES opencode's base prompt, and `instructions` is emitted empty for the same reason), the
frontmatter `tools` list becomes `agents.<role>.permissions` — `read`/`find`/`ls`/`grep` → `read`,
`glob`, `grep`; `edit`/`write` → `edit` only with `allowWrite`; `bash`/`powershell` → `shell` only
with `allowShell`; `subagent` denied unless explicitly lifted — behind a leading catch-all
`"*": "deny"` rule (the per-agent permission shape is an ARRAY of rules; the object form is ignored
there, which was measured the hard way). `model:` pins the model (a caller's `model` still wins) and
`thinking:` becomes the single opencode `providers.<p>.models.<id>.variants` entry plus
`-m provider/model#variant`. The production PetBox MCP server is included only with `mcp: true`,
and `websearch.provider` only for a role that declares a web tool. Without `role` the behaviour is
the old one: pinned model plus the fixed read-only policy. One caveat, measured: a role's own
`model:` is used unless the caller passes `model`, and some role pins name a subscription provider
the standalone child cannot authenticate to (`opencode-go/hy4-flash-free` failed with
`Header 'Authorization' has invalid value` while `opencode/hy4-flash-free` ran the same role fine) —
pass `model` explicitly when delegating in an environment with only free-tier credentials.

**Tool parity on the opencode leg (`bridge-parity`).** A role's `tools:` list is pi's vocabulary;
the child only has what opencode can express. `EXPRESSIBLE_TOOLS` in `role-config.ts` is the single
source of truth, and `analyzeToolParity` reports the difference **in the tool result**
(`parity: codemode — …`), because silently losing a capability is how two earlier defects shipped.

| Carries over | Notes |
|---|---|
| `read` | opencode `read` |
| `grep` | opencode `grep` |
| `find`, `ls` | both collapse to opencode's `glob` — there is no separate list tool |
| `edit`, `write`, `notebook_edit` | one `edit` action; opencode normalises write to edit |
| `bash`, `powershell`, `shell` | one `shell` action (gated by `allowShell`) |
| `websearch`, `fetch_content`, `web_fetch`, … | `websearch` + `webfetch`, plus `websearch.provider: exa` — only when the role declares one of them |
| `subagent`, `task` | `subagent` action (needs `allowSubagent`) |
| MCP (PetBox) | only via `mcp: true` — the whole server, never per-tool |

| Does NOT carry over | What to do |
|---|---|
| `codemode` — pi-side surface that can call MCP tools | the child loses that MCP reach: pass `mcp: true`, or restate the role without it |
| `pwsh` — the pi extension re-registering PowerShell under another name | declare `powershell` instead; it maps to `shell` |
| pi extension tools, `mcp__*` tool names, skill tools | not expressible per-tool; do not delegate that role if it depends on them |
| `Agent`, `get_subagent_result`, any pi-only tool | same — restate the role for the opencode leg |

A role whose **entire** tool list is unexpressible is reported as `BLOCK` rather than `parity`: the
child would start with no tools and its "I cannot do that" would read as a task failure.

**Role files are merged, not copied.** `model:` and `thinking:` in a role's frontmatter are owned
by the Roles panel, which writes them into the *installed* file (that is where the
`*.md.bak-before-models-panel-*` files come from). A plain copy reverted the owner's model choice
on every deploy, so `deploy.sh` keeps the installed pins for those two keys and the repo's own
values are the baseline a fresh machine gets. Everything else in the file still comes from the
repo.

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
| `extensions/agent-rules.ts` | assembles `rules/amd-*.md` into the `agent_rules` prompt section for the main session; reports an unreadable or missing fragment once to the log and the UI instead of dropping the whole section silently, and checks that every role's `skills:` resolves to an installed fragment — in the same roots `pi-subagents` searches (the session's `.pi/skills` and `.agents/skills`, then the user scope), `<name>.md` or `<name>/SKILL.md` |
| `extensions/pwsh-tool.ts` | the built-in PowerShell tool registered a second time as `pwsh` — see "Why a second PowerShell tool" below |
| `extensions/session-id-status.ts` | one extra footer line: `sid:xxxxxxxx · up 12m · compact×2` |
| `extensions/opencode-bridge/` | `opencode_delegate` tool: runs a prompt through the external `opencode` CLI and returns its answer, tool calls, usage and cost (directory extension: `index.ts` + `types.ts`/`parse.ts`/`delegate.ts`/`role-config.ts`) |
| `agents/*.md` | our five subagent roles: `worker`, `scout`, `oracle`, `reviewer`, `researcher` |
| `rules/amd-*.md` | the operating rules, one topic per file (always injected) |
| `rules/pbx-*.md` | PetBox-specific rules — injected only when the session declares a petbox MCP tool |

## Why a second PowerShell tool (`pwsh`)

`pwsh-tool.ts` re-registers pi's **own** PowerShell tool definition under the name `pwsh`.
Nothing is reimplemented: `createPowerShellToolDefinition` is imported from the SDK and only
`name`/`label` are overridden, so the parameter schema (`command`, `timeout`), output
truncation, temp-file spill and session environment are the built-in's. The export exists in
the package root of both `pi-coding-agent` 1.3.0 (CLI) and 1.2.0 (nested inside pi-web), and
extensions resolve that specifier through the loader's virtual-module table, so one file
serves both hosts.

It exists because **pi-web collapses the two shells into one**. Its
`lib/powershell-settings.ts` renames every name in `{bash, powershell}` to whichever shell is
configured, so with both listed in `defaultTools` the model gets `bash` only — `powershell`
is rewritten away. A tool whose name is outside pi-web's fixed `CODING_TOOL_NAMES` set is
carried through untouched, which is why `pwsh` survives.

Consequence, deliberately accepted: in the terminal both `powershell` and `pwsh` are active.
They are the *same* code and the *same* binary — the tool named `powershell` resolves
`pwsh.exe` first and only falls back to `powershell.exe` (`dist/utils/shell.js:105-114`), so
the second name buys ~6 characters of prompt rather than a second engine. Banishing the
canonical name instead would cost a rename across `settings.json`, this prose and four agent
profiles, and would leave PowerShell unavailable whenever this extension fails to load.

## Rules: why skills and not `AGENTS.md`

A pi context file cannot include another file, and a subagent in `prompt_mode: replace` never
loads context files at all — so one big `AGENTS.md` reaches either the main session or the
children, never both. The rules are therefore one file per topic, consumed by two paths:

- **main session** — `agent-rules.ts` reads `rules/amd-*.md` **once, on `session_start`**, and
  serves that exact text for the rest of the session. Editing a rule takes effect at the next
  **`session_start`** — a new session, `/new`, a fork, **and a resume** all re-read the files; the
  same contract a context file has. The freeze is not laziness: a section that
  changes mid-transcript makes pi replay the prompt and the provider re-read the whole
  conversation without its cache (`Cache miss: N tokens re-billed`) — and a resume after editing
  a rule costs exactly that one replay, by design. Files
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

The PetBox gate is decided once per session too, from the MCP **config** (`getMcpServers()`, then
the effective `mcp.json` files) rather than the live tool registry: MCP connects in the
background, so a runtime check is false at the first prompt of every short run and would add the
section later — another re-bill, and none at all in headless runs.

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

The kit imports at the top of `petbox-pi.ts` are **machine-absolute** — the user-scoped and
project-scoped extension directories sit at different depths, so no single relative specifier
reaches `~/.petbox/wire` from both. The tracked file therefore carries a placeholder:

```ts
import { pushTranscript } from "@PETBOX_WIRE_ROOT@/append.ts";
```

`deploy.sh` substitutes it at install time from `${PETBOX_WIRE_ROOT:-$HOME/.petbox/wire}`, in
forward-slash form on every platform (under Git Bash `cygpath -m` turns `$HOME` into
`C:/Users/…`, which is what the imports held before the placeholder existed). The render is
deterministic, so a second deploy writes nothing, and **the tracked file is deliberately not
loadable as-is** — the installed copy is the only loadable one. Never hand-edit the installed
file; change the source, or set `PETBOX_WIRE_ROOT` for a kit mirror somewhere else:

```sh
PETBOX_WIRE_ROOT=/opt/petbox/wire tools/pi/deploy.sh --dry-run   # what would change
```

## Layout

| Path | What it is |
|---|---|
| `extensions/petbox-pi.ts` | session mirror + memory banner + recall nudge (see below) |
| `extensions/agent-rules.ts` | assembles `rules/amd-*.md` into the `agent_rules` prompt section for the main session; reports an unreadable or missing fragment once to the log and the UI instead of dropping the whole section silently, and checks that every role's `skills:` resolves to an installed fragment — in the same roots `pi-subagents` searches (the session's `.pi/skills` and `.agents/skills`, then the user scope), `<name>.md` or `<name>/SKILL.md` |
| `extensions/pwsh-tool.ts` | the built-in PowerShell tool registered a second time as `pwsh` — see "Why a second PowerShell tool" below |
| `extensions/session-id-status.ts` | one extra footer line: `sid:xxxxxxxx · up 12m · compact×2` |
| `extensions/opencode-bridge/` | `opencode_delegate` tool: runs a prompt through the external `opencode` CLI and returns its answer, tool calls, usage and cost (directory extension: `index.ts` + `types.ts`/`parse.ts`/`delegate.ts`/`role-config.ts`) |
| `agents/*.md` | our five subagent roles: `worker`, `scout`, `oracle`, `reviewer`, `researcher` |
| `rules/amd-*.md` | the operating rules, one topic per file (always injected) |
| `rules/pbx-*.md` | PetBox-specific rules — injected only when the session declares a petbox MCP tool |

## Why a second PowerShell tool (`pwsh`)

`pwsh-tool.ts` re-registers pi's **own** PowerShell tool definition under the name `pwsh`.
Nothing is reimplemented: `createPowerShellToolDefinition` is imported from the SDK and only
`name`/`label` are overridden, so the parameter schema (`command`, `timeout`), output
truncation, temp-file spill and session environment are the built-in's. The export exists in
the package root of both `pi-coding-agent` 1.3.0 (CLI) and 1.2.0 (nested inside pi-web), and
extensions resolve that specifier through the loader's virtual-module table, so one file
serves both hosts.

It exists because **pi-web collapses the two shells into one**. Its
`lib/powershell-settings.ts` renames every name in `{bash, powershell}` to whichever shell is
configured, so with both listed in `defaultTools` the model gets `bash` only — `powershell`
is rewritten away. A tool whose name is outside pi-web's fixed `CODING_TOOL_NAMES` set is
carried through untouched, which is why `pwsh` survives.

Consequence, deliberately accepted: in the terminal both `powershell` and `pwsh` are active.
They are the *same* code and the *same* binary — the tool named `powershell` resolves
`pwsh.exe` first and only falls back to `powershell.exe` (`dist/utils/shell.js:105-114`), so
the second name buys ~6 characters of prompt rather than a second engine. Banishing the
canonical name instead would cost a rename across `settings.json`, this prose and four agent
profiles, and would leave PowerShell unavailable whenever this extension fails to load.

## Rules: why skills and not `AGENTS.md`

A pi context file cannot include another file, and a subagent in `prompt_mode: replace` never
loads context files at all — so one big `AGENTS.md` reaches either the main session or the
children, never both. The rules are therefore one file per topic, consumed by two paths:

- **main session** — `agent-rules.ts` reads `rules/amd-*.md` **once, on `session_start`**, and
  serves that exact text for the rest of the session. Editing a rule takes effect at the next
  **`session_start`** — a new session, `/new`, a fork, **and a resume** all re-read the files; the
  same contract a context file has. The freeze is not laziness: a section that
  changes mid-transcript makes pi replay the prompt and the provider re-read the whole
  conversation without its cache (`Cache miss: N tokens re-billed`) — and a resume after editing
  a rule costs exactly that one replay, by design. Files
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

The PetBox gate is decided once per session too, from the MCP **config** (`getMcpServers()`, then
the effective `mcp.json` files) rather than the live tool registry: MCP connects in the
background, so a runtime check is false at the first prompt of every short run and would add the
section later — another re-bill, and none at all in headless runs.

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

## Layout

| Path | Role |
| --- | --- |
| `tools/pi/extensions/petbox-pi.ts` | session mirror, memory banner, recall nudge |
| `tools/pi/extensions/agent-rules.ts` | assembles `rules/amd-*.md` into the `agent_rules` prompt section |
| `tools/pi/agents/*.md` | subagent roles (`worker`, `scout`, `oracle`, `reviewer`, `researcher`) |
| `tools/pi/rules/amd-*.md` | operating rules, one topic per file |
| `tools/pi/skills.mjs` | renders `petbox-methodology`, `petbox-write-economy` and `petbox-node-authoring` from the kit's own templates (`src/clients-ts/petbox-wire/src/templates`) through the kit's own `renderSkillTemplate`, applies the pi-specific deltas (each pinned to an anchor, so an upstream template edit fails the install loudly instead of silently dropping a delta), and writes `<target>/skills/<name>/SKILL.md` plus `validate-body.mjs` — `<target>` is `$PI_AGENT_DIR` or `~/.pi/agent`, same as `deploy.sh` |
| `tools/pi/deploy.sh` | installs all of the above into `~/.pi/agent` |
| `.pi/mcp.json` | pi MCP config for **this** project — the template for others |
| `~/.petbox/wire/*` | the petbox-wire kit mirror `petbox-pi.ts` imports (installed by `petbox-wire`, not by us) |
