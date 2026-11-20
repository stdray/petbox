# Environment setup — the agent-executable scenario

## Start here — the prompt to give the agent

Paste this into a `pi` session started **inside a clone of this repository**. It is deliberately
short: everything else is in this file.

> Set up this machine to work in this repository, following `doc/environment-setup.md` end to end.
> Read that file first — it is the whole procedure, and every step carries its own verification.
> Run the verification; if one fails, stop and tell me the failing command and its output instead
> of working around it.
>
> Ask me only what the machine cannot tell you: the PetBox project key to wire, and an API key for
> it. If we are only proving that things work, use the `smoke` project with a `sandboxOnly` key.
>
> Rules: never hand-edit anything marked `petbox: managed`, and never edit files under `~/.pi/agent`
> — `tools/pi/deploy.sh` and `npx petbox-wire apply` own them. Changes confined to `tools/pi/**` are
> gated by `bun test tools/pi/tests/` plus a second `deploy.sh` run that writes nothing, not by
> `./build.ps1`.
>
> Finish with a report: what you installed, the verification output for each step, and anything this
> document did not cover.

This is a **runbook an agent follows**, not an article a human reads. A new person installs
pi, clones this repository, and says "set me up". The agent walks the steps below in order,
runs the verification after each one, and stops at the first red instead of pressing on.

Two things make it agent-shaped rather than human-shaped:

- every step has a **command whose output decides** whether the step succeeded;
- every place where the right answer depends on the machine (Windows vs Linux, which models
  exist, whether opencode is installed) is a **decision point**, and the runbook says what to
  read to make the call instead of assuming.

Anything marked **ASK** cannot be discovered from the machine: ask the owner.

## 0. Prerequisites

```sh
node --version      # >= 22; the reference machine runs 24.11.0
bun --version       # >= 1.2; reference 1.3.14
git --version
```

**Decision point — is `pi` installed?** `pi --version` answering with a version (reference:
1.5.2) means yes. If not:

```sh
npm install -g @agegr/pi-web      # the host AND the web UI, one package
pi --version
```

**Decision point — will this machine also drive `opencode`?** Optional. Without it everything
except the `opencode-bridge` extension works. If needed: `opencode --version` (reference 2.3.2).

## 1. Clone and stand in the repository

```sh
git clone https://github.com/stdray/petbox.git
cd petbox
```

## 2. Install the pi packages

`pi` manages its own packages — install them with the CLI, not by copying files:

```sh
pi install npm:pi-web-access
pi install npm:pi-compact-tools
pi install npm:pi-chrome
pi install npm:@tintinweb/pi-subagents

pi list                    # what is configured (this is the verification)
pi update --extensions     # reconcile after a version bump
```

`pi install` writes the entry into `~/.pi/agent/settings.json` and installs into
`~/.pi/agent/npm/`; add `--local` to write the project's settings instead of the global ones.
`pi remove <source>` takes one out, `pi config` chooses which resources of a package load, and
`pi update` without a target updates pi itself.

**Why `pi list` and not “it started”:** a package that fails to install at startup — npm
unreachable, a bad version — is **warned about and skipped**, and the session runs on without it.
Manual editing of the `packages` array also works (pi installs what it finds on the next start), but
then nothing reports the failure. So the reference set must show up in `pi list`, and the four
packages must be the ones above.

The same file carries the default provider, model and thinking level (`defaultProvider`,
`defaultModel`, `defaultThinkingLevel`) — **ASK** if the owner has not told you which provider the
account is on.

**What each package gives:** `pi-web-access` web search/fetch; `pi-chrome` browser control;
`pi-compact-tools` the compact tool surface; `@tintinweb/pi-subagents` the `Agent`,
`get_subagent_result`, `steer_subagent` and `SubagentWorkflow` tools.

## 2a. Pi Web — the same config, in a browser (try it before you need it)

`@agegr/pi-web` is not a second agent: it is a **local browser UI over the same
`~/.pi/agent` config and the same session files**. Everything set up in the steps below is
immediately visible there — no import, no second config.

```sh
npx @agegr/pi-web@latest          # or: npm install -g @agegr/pi-web@latest && pi-web
```

It binds `127.0.0.1:30141` and opens a browser. What makes it worth trying:

- **Sessions are a workspace, not a scrollback**: grouped by project, with running state,
  context usage, cost and compaction details; rename, export, delete, resume.
- **Two kinds of branching**: *New session* forks an independent session file from an earlier
  message, *Edit from here* creates a branch inside the current one. This is the fastest way
  to try “what if I had said it differently” without losing the original.
- **The project files are right there**: browse/upload files, inspect Git diffs, preview
  Markdown, images, audio, PDF and DOCX, with automatic refresh.
- **Worktrees in the sidebar** — the same repo, several checkouts, sessions grouped together.
- **Web configuration**: provider login, API keys, models, model tests, plugin packages and
  skills without leaving the browser. On a fresh machine this is the honest shortest path to
  step 2: the *Models* panel is where a provider is signed in.
- It installs as a PWA, so it behaves like a desktop app on a phone or a laptop dock.

Commands worth knowing:

```sh
pi-web status          # which servers are running
pi-web stop [--port N]
pi-web open [--port N]
pi-web update [--check]
```

**Decision point — local only, or reachable from the network?** On loopback, no password is
needed. The moment you bind a non-loopback address you are exposing an agent that executes
high-privilege actions:

```sh
PI_WEB_PASSWORD='a-long-random-password' pi-web --hostname 0.0.0.0
```

**The password does not encrypt the connection.** Do not put it on plain HTTP over the
internet: terminate HTTPS in a trusted reverse proxy or use a VPN, and if the proxy forwards
its own hostname add that exact name to `PI_WEB_ALLOWED_HOSTS`. API clients use Basic Auth
with the username `pi`.

Useful defaults to know about, because they surprise people:

| | |
|---|---|
| `PI_WEB_IDLE_TIMEOUT_MS` | `600000` (10 min) — an idle session is shut down; `0` disables it |
| `PI_CODEMODE_MAX_WORKERS` | pi-web sets **8** when unset (pi's own default is 2); each script may use up to 256 MB |
| `PI_WEB_NO_OPEN=1` | do not open a browser (CI, remote box) |
| `PI_WEB_APP_NAME` | the PWA name (`Pi Web` by default) |

**Verification:** `pi-web open` shows the sessions already living in `~/.pi/agent/sessions`,
and the *Models* panel lists the providers step 2 configured. If the list is empty, the pi
config is not where you think it is — fix that before anything else.

## 3. Install the vendored pi config (agents, rules, extensions)

```sh
tools/pi/deploy.sh --dry-run     # what would change
tools/pi/deploy.sh               # install into ~/.pi/agent
tools/pi/deploy.sh               # SECOND run must print only "=" lines
```

What it writes: `tools/pi/agents/*.md` → `~/.pi/agent/agents/`, `tools/pi/rules/amd-*.md` and
`pbx-*.md` → `~/.pi/agent/skills/`, `tools/pi/extensions/*.ts` and `extensions/<name>/` →
`~/.pi/agent/extensions/`, plus the three rendered PetBox skills.

**Decision point — Kit location.** `petbox-pi.ts` imports the wire kit from
`${PETBOX_WIRE_ROOT:-$HOME/.petbox/wire}`. When the kit lives elsewhere, export
`PETBOX_WIRE_ROOT` **before** deploying; the script renders it into the installed file and
refuses to fall back silently on Windows when `cygpath` is missing.

**Verification:** the second run writes nothing, and `grep -c "@PETBOX_WIRE_ROOT@" ~/.pi/agent/extensions/petbox-pi.ts` is `0` (the placeholder must be rendered).

**Restart pi afterwards.** Extensions load at startup; `/reload` re-reads config but does not
pick up new files.

**Roles are merged, not overwritten.** `deploy.sh` copies `agents/*.md` but keeps `model:` and
`thinking:` from the installed file when they are already there — that is how the Roles panel's
choices survive a deploy. On a fresh machine the repository's baseline pins apply; on a machine
that already had the roles, the existing pins win. Same prose either way (verified: all five role
files are byte-identical to the repo once those two keys are excluded).

**What deploy does NOT carry, measured on the reference machine.** The install produced **41 files**
(`agents/` 5, `skills/` 30, `extensions/` 6) and **every one of them already existed on the
reference machine** — so the repository reproduces exactly what the repository owns. The reference
machine has **144 further files** the repo does not own, and a new machine will not get them:

| | |
|---|---|
| `skills/answer-me-with-html/**` (138 files) | a third-party skill cloned from its own repo, with `.git` |
| `skills/caveman`, `grilling`, `grill-me`, `code-edit-hygiene` (4) | single-file third-party skills |
| `extensions/child-session-dir.ts` | a **local** extension that was never vendored into `tools/pi` — the one real gap |
| `agents/bonsai.md.disabled` | a disabled role, kept for reference |

So: after `deploy.sh` the machine has everything this repository defines, and nothing else. If the
colleague needs one of those skills, it is installed from its own source (`pi` packages / a cloned
skill directory), not from here — and `child-session-dir.ts` should be vendored before someone
assumes it works on a fresh machine.

## 4. Wire the project to PetBox

**ASK** for the project key and an API key (the owner mints one on the agent-keys page; a
`$system` key covers everything, a project-scoped key is enough for one project).

```sh
npx petbox-wire@latest <dir> <projectKey>   # global hooks, MCP configs, skills
npx petbox-wire@latest apply                # write the per-harness artifacts
npx petbox-wire@latest status               # what is installed where
npx petbox-wire@latest doctor               # environment problems
```

**Decision point — Linux vs Windows roles.** `apply` writes role files per harness
(`~/.pi/agent/agents`, `~/.config/opencode/agents`, `~/.claude/agents`, `~/.qwen/agents`), each
marked `petbox: managed`. **Never hand-edit a managed file** — `apply` overwrites it. Change a
role's model through the CLI:

```sh
npx petbox-wire@latest roles                       # the active profile and every harness binding
npx petbox-wire@latest roles --check-models        # must end with 0 invalid
npx petbox-wire@latest model set <role> <provider/model> --agent opencode
npx petbox-wire@latest model reset <role> --agent opencode
```

## 5. Credentials

```sh
export PETBOX_API_KEY=...        # read by .mcp.json (pi) and by the opencode MCP block
```

Nothing else is required for the pi side. For **smoke tests**, use a `sandboxOnly` key and the
project `smoke` — never a real project, never the session's `$system` key.

## 6. opencode (optional, needed by the bridge)

`~/.config/opencode/opencode.json` holds the model default, `websearch.provider`, the
`instructions` file and the `agent.petbox-*` blocks (mode/steps/options) — all written by
`apply`. Check what the machine can actually reach:

```sh
opencode models | head -20      # the real catalog for THIS machine
opencode debug agents           # the resolved agents, with their models and permissions
```

**Decision point — does the CLI have the same account as the desktop app?** Not necessarily.
Measured on the reference machine: `opencode-go/*` models answer `403 "An active OpenCode Go
subscription is required"` from the CLI while the desktop app runs them fine, because the CLI
holds a different credential. Pick models bound to a provider the CLI can authenticate
(`opencode/*` Zen models), or expect the failure.

## 7. Verify the whole thing

```sh
bun test tools/pi/tests/                       # Windows and Linux
OPENCODE_BRIDGE_SKIP_LIVE=1 bun test tools/pi/tests/   # unit only, no opencode needed
pi -p --no-session "List the names of your tools that contain 'opencode'. Names only."
```

The last command must name `opencode_delegate` (and `delegate`, `steer_delegate`,
`abort_delegate`, `delegate_result`). Then one real delegation:

```
pi -p --no-session "Call delegate with role='scout' and prompt='Reply with exactly: PONG'.
                     Report the route line and the text."
```

A route line looks like `route pi · scout · <provider>/<model> · <level> · <reason>`; the
`opencode` leg instead prints `route opencode · … · opencode:<provider>/<model> · …`.

## 8. Platform differences (all measured)

| | |
|---|---|
| **Windows** | `cwd` in Git-Bash form (`/c/…`) is converted to `C:/…`; a POSIX path there is a real path and is left alone. Process trees are killed with `taskkill /T /F`. `opencode serve` flashes a console window per child spawn until upstream #60471 lands. Command-line length is capped at 32 000 characters (CreateProcess). |
| **Linux/macOS** | `/c/anything` is an ordinary path — no drive logic runs at all. Children are spawned as a process group and killed as a group. The argv budget is 128 KiB (ARG_MAX is ~2 MB; we stay an order of magnitude under because argv shares the budget with `env`). |

## 9. Traps that already cost time

- **`pi` loads extensions at startup.** `/reload` does not see new files; restart.
- **`opencode` takes its working directory from `PWD`, not from the spawn cwd.** The bridge
  pins `PWD` for that reason; a hand-written wrapper must too.
- **`opencode run` reads stdin to EOF** and hangs if the parent keeps the pipe open. Close it.
- **A model pin that cannot be resolved is silently replaced by the parent model** in
  `@tintinweb/pi-subagents` for a role's frontmatter `model:`. The bridge detects and reports
  the substitution; a plain `Agent` call will not.
- **`opencode api`** cannot parse its own arguments on 2.3.2 — drive the server with HTTP.
- **Free models are quota-limited per model, not per plan**, and the quota burns fast; the same
  model that answered `cost: 0` an hour ago can answer `provider.free-limit`.
- **`.mcp.json` is not an opencode config source.** Only `opencode.json` / `.opencode/`.
- **`tools/pi/**` has its own gate**, not the Cake one: `bun test tools/pi/tests/` plus a
  `deploy.sh` whose second run writes nothing.

## 10. What a human still has to do

- mint the API key(s) and tell the agent which project to wire;
- choose the provider/models if the account differs from the reference machine;
- log in to `opencode` (its auth is separate from pi's) if the bridge is wanted.
