# Agent onboarding

This is the short path for bringing a coding agent onto a PetBox project — from a login to the agent planning real work. There are five steps; do them in order and check each one worked before moving on, because a silent failure early (wrong key, stale connection, half-read model) is expensive to debug later.

**Step 0 (if it applies to you) and step 1 happen in the UI** — either as a maintainer minting a key for an agent onto a project that already exists, or as a new self-serve user setting up your own workspace and project first. From step 2 on, the **agent** does the rest, from a terminal in the project directory. The deeper material lives on other pages, linked inline — this page is just the sequence.

> **Keep this in mind throughout.** After any server deploy, an MCP client's tool list goes stale: a tool you expect can return *"unknown tool"*, or a write can quietly do nothing. Whenever that happens, re-establish the MCP connection and retry before assuming the key or the request is wrong. This is the most common confusing failure here.

## 0. New here? Create your workspace and project first

Skip straight to step 1 if a maintainer already handed you a project. Otherwise — you have a login and nothing else yet — there's a short prelude:

1. **Sign in.** With no workspace membership, you land on **"No workspaces yet"**. If your account's quota allows it, that page shows a **Create workspace** button — granting that quota is the one part of this whole flow that needs an administrator; everything from here on is yours to drive. If the button isn't there, ask an administrator to create a workspace and add you to it, then pick up at step 2 below inside it.
2. **Create the workspace** — the button lands on `/ui/me/workspaces/new` (not `/ui/me/new-workspace`; that route doesn't exist). You become its administrator.
3. **Create a project** inside the workspace, from its admin **Projects** page → **New project**. Pick the project key now — it drives the `PETBOX_<PROJECT>_API_KEY` env var name used in step 1 below.

From here the rest of this page applies to you exactly as written, starting at step 1.

## 1. Mint a key and wire the project

On the project's **Connect** page (admin gear → project → Connect) mint an access key — this is the **only** legal place a project key is minted; nowhere else in the UI or docs issues one. The key is shown once — copy it, and copy the `npx petbox-wire@latest …` command shown right below it.

From a terminal **inside the project directory**, run that one command. The key rides as an
**environment variable**, not a command-line argument — npm writes the full command line of every
invocation to `~/.npm/_logs/*.log` in plain text with no rotation, so a key passed as `--key <KEY>`
sits there readable indefinitely; an env-var assignment is never part of that logged argv. The
Connect page shows both shell forms and copies the right one:

```
# bash / zsh (macOS, Linux, WSL)
PETBOX_<PROJECT>_API_KEY=<KEY> npx petbox-wire@latest . '<project>' --env PETBOX_<PROJECT>_API_KEY

# PowerShell (Windows)
$env:PETBOX_<PROJECT>_API_KEY='<KEY>'; npx petbox-wire@latest . '<project>' --env PETBOX_<PROJECT>_API_KEY
```

`--key <KEY>` still works as a flag (existing scripted wiring keeps running), but every use prints
a warning pointing back here. Already ran with `--key`? The key is now sitting in a debug log —
find and clean it up: `grep -l -F '<the key>' ~/.npm/_logs/*.log`, then delete or scrub the
matching file(s).

This single step replaces what older material described as several manual ones: it validates the key, persists it where every agent looks, writes the MCP config (`.mcp.json` for Claude Code, `.factory/mcp.json` for Factory Droid, `.opencode/opencode.json` for opencode), installs the `petbox` skill, and installs the session hooks that inject the memory protocol at the start of every session. See the [wire guide](/doc/wire) for exactly what it does and its full flag/command reference.

Requires **Node ≥ 23.6**. `petbox-wire` never mints a key itself — if you don't have one yet, get it from the Connect page above; a brand-new agent has no project of its own to mint one from.

**Check:** the run reaches `[10/10] self-smoke: OK`. If it doesn't, fix the reported step before continuing — everything after this depends on it.

The run doesn't stop there — it continues into a `[11/10]` phase (the numbering isn't a typo: step 10 is the last of the original 10-step sequence, and 11 was added later without renumbering the rest). `[11/10]` seeds a default role→model binding on a fresh machine and compiles the per-harness startup artifacts (`.claude/agents/petbox-*.md`, `.opencode/agent/petbox-*.md`, `.factory/droids/petbox-*.md`) for all three wired harnesses (Claude Code, opencode, Factory Droid), so the freshly-wired roster is usable immediately. It does **not abort** the run — everything already written by steps 1-10 stays written, and step 11 keeps going even after its own first failure — but a hiccup here (a truthfulness block, a refused clobbering write, an unreachable workspace probe) **does** flip the run's own exit code: see the [wire guide's exit-code table](/doc/wire) for exactly which code. Only when step 11 (and self-smoke before it) both come back clean does the run print `done.`; a non-zero step 11 suppresses that sign-off and the last line names the failure instead — `re-run petbox-wire apply` retries just that step. Re-running `apply` afterwards is safe regardless: it's the same idempotent compile step 11 already ran.

> **Agent not covered by `petbox-wire` yet?** `opencode` and Factory Droid are wired the same way as Claude Code; `omp` and `pi` aren't, and need the manual registration steps on the [connect reference](/doc/agent) instead. Claude Code is the priority path — treat the other harnesses as best-effort until that page says otherwise.

## 2. Open a new session and verify the connection

Wiring persists the key to a real environment variable, which only new shells pick up. **Open a new terminal**, `cd` back into the project directory, and start the agent there.

**Expect a one-time approval prompt first.** The project-scoped MCP server in `.mcp.json` needs a one-time trust grant before either call below works — this is the normal first outcome on a fresh machine, not a symptom. Interactively, `claude mcp list` shows it pending:

```
$ claude mcp list
petbox: https://petbox.3po.su/mcp (HTTP) - Pending approval (run `claude` to approve)
```

and a headless run instead returns `Claude requested permissions to use mcp__petbox__whoami, but you haven't granted it yet.` for every petbox tool call. Clear it with a one-time interactive approval (run `claude`, approve when prompted) or by skipping the prompt entirely via `--mcp-config .mcp.json --strict-mcp-config`.

**Check:** the agent's first reply opens with the injected memory banner (`🧠 PetBox memory active` or similar); calling `tasks_board_list` returns a list — even an empty one. An auth error means a bad or wrong-project key; a missing tool means the MCP tool list is stale (reconnect and retry); a permissions/approval error means the server is still pending the one-time trust grant above.

## 3. Read the platform, then confirm understanding

Before writing anything, the agent reads the [overview](/doc/overview) (what PetBox is and its modules) and the [methodology](/doc/methodology) (the spec / work / idea rails and how nodes are addressed). This is the step that prevents misuse of the rails, so it is confirmed by answering, not by claiming to have read.

**Check — the agent answers, the maintainer grades against the right answer:**

- "For the requirement *'users can reset their password by email'* — which board and kind, roughly what slug `key`/`partOf` placement and title, and what must the implementing task link to?"
- "You just finished coding a feature and it works locally — what status do you set, and who sets the next one?" (Answer: `Review`; the **maintainer** sets `Done`.)
- "A user reports a bug — which board does it land on first?"

## 4. Confirm the skill loaded

Step 1 already installed `SKILL.md` at the right path for your agent type — nothing to copy by hand.

**Check:** in a fresh session the agent lists the petbox skill and can answer one question from it (e.g. "what memory store and key does the canon index live at?" → store `canon`, key `index`). If it's missing, re-run the wire command from step 1 — a skill in the wrong place silently won't load, and `petbox-wire` always writes the right path per harness.

The skill deliberately doesn't say what the status ceiling is — that depends on the board's kind (e.g. `Review` for `classic`/`work`, no ceiling at all for `simple`), so the agent reads it off the SessionStart memory banner and `tasks_methodology_guide`, not the skill.

## 5. Do one real piece of work end-to-end

First, **right-size the rails to the work** (see the [methodology](/doc/methodology) for the tiers): a throwaway spike → one `simple` board; a small, single-board project → **`classic`**; a long-lived, multi-session project that wants a durable spec → **`quartet`** (idea → spec → work). For most small projects `classic` is the natural default — it's one board, no `links.idea_spec`/`links.task_spec` to wire, and the path to shipping one task is shorter.

Provision either with **one click**: on the project's `.../projects/{proj}/tasks` page, the **Methodology** panel at the top has a preset dropdown (`quartet` selected by default) — pick the preset you want and press **Enable methodology**. `quartet` creates all four boards (`ideas`, `spec`, `work`, `intake`) with `work → spec` auto-wire; `classic` creates one board, named `classic` (type `task`/`feature`/`bug`), with free movement among open statuses but `Done` reachable only from `Review`, and no spec/idea linkage. It's idempotent (only adds what's missing), so it's also safe to press again later.

Creating the boards explicitly, one at a time, with the right kind is the fallback — reach for it only if you need something the preset doesn't give you (e.g. a subset of the boards, or a non-default methodology). Whichever way you provision them, get the kind right **at creation**: not by a bare write (a cold write makes a plain `simple` board and the kind can't change).

The fifth step itself differs by preset — follow whichever one you enabled:

### On `quartet`

The flow **starts in `ideas`** — the spec falls out of an accepted idea, you don't invent it from nothing. Capture the work as a short idea on `ideas` and accept it; record the requirement(s) it settles into on `spec` and note each `nodeId`; then create a `work` feature that links one by passing it as `links:{task_spec: <nodeId>}`. Move the feature `Pending → InProgress → Review` as you go, and stop at `Review`. The [methodology](/doc/methodology) spells out the contract if anything is unfamiliar.

**Check:** the work node shows a live link to the spec node, and the spec leaf's computed delivery reads `in_progress`. If delivery still says `not_started`, the link didn't take (recheck the `links.task_spec` id) or the feature never left `Pending`. The agent should **not** have set `Done` — the maintainer reviews and sets it from the UI, which closes the loop.

### On `classic`

No idea or spec step first — just quick-add a task on the `classic` board (it starts in `Backlog`). Move it forward through `InProgress` to `Review` as you work it, and **stop at `Review`** — same ceiling as `quartet`: `Done` is reachable only from `Review`, and the agent doesn't set it.

**Check:** the task sits in `Review`, not `Done`. The project owner (the maintainer, or you yourself if this is your own self-serve project) reviews and sets `Done` from the UI, which closes the loop.

## From here

The agent now plans on the rails: requirements onto `spec`, technical tasks onto `work` (linked), thoughts onto `ideas`, inbound reports onto `intake` — always stopping at `Review` and leaving `Done` to the maintainer. Build the app against PetBox (config / logs / data) while tracking the work in PetBox. See the [methodology](/doc/methodology) for the day-to-day contract and the [overview](/doc/overview) for what each module offers.
