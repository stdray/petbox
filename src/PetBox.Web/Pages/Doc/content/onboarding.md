# Agent onboarding

This is the short path for bringing a coding agent onto a PetBox project — from minting a key to the agent planning real work. There are five steps; do them in order and check each one worked before moving on, because a silent failure early (wrong key, stale connection, half-read model) is expensive to debug later.

The **maintainer** (you) does step 1 in the UI. The **agent** does the rest, from a terminal in the project directory. The deeper material lives on other pages, linked inline — this page is just the sequence.

> **Keep this in mind throughout.** After any server deploy, an MCP client's tool list goes stale: a tool you expect can return *"unknown tool"*, or a write can quietly do nothing. Whenever that happens, re-establish the MCP connection and retry before assuming the key or the request is wrong. This is the most common confusing failure here.

## 1. Mint a key and wire the project

On the project's **Connect** page (admin gear → project → Connect) mint an access key — this is the **only** legal place a project key is minted; nowhere else in the UI or docs issues one. The key is shown once — copy it, and copy the `npx petbox-wire@latest …` command shown right below it.

From a terminal **inside the project directory**, run that one command:

```
npx petbox-wire@latest . '<project>' --key <KEY> --env PETBOX_<PROJECT>_API_KEY
```

This single step replaces what older material described as several manual ones: it validates the key, persists it where every agent looks, writes the MCP config (`.mcp.json` for Claude Code, `.factory/mcp.json` for Factory Droid, `.opencode/opencode.json` for opencode), installs the `petbox` skill, and installs the session hooks that inject the memory protocol at the start of every session. See the [wire guide](/doc/wire) for exactly what it does and its full flag/command reference.

Requires **Node ≥ 23.6**. `petbox-wire` never mints a key itself — if you don't have one yet, get it from the Connect page above; a brand-new agent has no project of its own to mint one from.

**Check:** the run ends with `[10/10] self-smoke: OK`. If it doesn't, fix the reported step before continuing — everything after this depends on it.

> **Agent not covered by `petbox-wire` yet?** `opencode` and Factory Droid are wired the same way as Claude Code; `omp` and `pi` aren't, and need the manual registration steps on the [connect reference](/doc/agent) instead. Claude Code is the priority path — treat the other harnesses as best-effort until that page says otherwise.

## 2. Open a new session and verify the connection

Wiring persists the key to a real environment variable, which only new shells pick up. **Open a new terminal**, `cd` back into the project directory, and start the agent there.

**Check:** the agent's first reply opens with the injected memory banner (`🧠 PetBox memory active` or similar); calling `tasks_board_list` returns a list — even an empty one. An auth error means a bad or wrong-project key; a missing tool means the MCP tool list is stale (reconnect and retry).

## 3. Read the platform, then confirm understanding

Before writing anything, the agent reads the [overview](/doc/overview) (what PetBox is and its modules) and the [methodology](/doc/methodology) (the spec / work / idea rails and how nodes are addressed). This is the step that prevents misuse of the rails, so it is confirmed by answering, not by claiming to have read.

**Check — the agent answers, the maintainer grades against the right answer:**

- "For the requirement *'users can reset their password by email'* — which board and kind, roughly what slug `key`/`partOf` placement and title, and what must the implementing task link to?"
- "You just finished coding a feature and it works locally — what status do you set, and who sets the next one?" (Answer: `Review`; the **maintainer** sets `Done`.)
- "A user reports a bug — which board does it land on first?"

## 4. Confirm the skill loaded

Step 1 already installed `SKILL.md` at the right path for your agent type — nothing to copy by hand.

**Check:** in a fresh session the agent lists the petbox skill and can answer one question from it (e.g. "what is your status ceiling?" → `Review`). If it's missing, re-run the wire command from step 1 — a skill in the wrong place silently won't load, and `petbox-wire` always writes the right path per harness.

## 5. Do one real piece of work end-to-end

First, **right-size the rails to the work** (see the [methodology](/doc/methodology) for the tiers): a throwaway spike → one `simple` board; a small build → a short idea → a thin `spec` → `work`; a long-lived project → the full rails. The flow **starts in `ideas`** — the spec falls out of an accepted idea, you don't invent it from nothing. The walk-through below is the small-build tier.

Use the standard boards (named for their kind: `ideas`, `spec`, `work`, `intake`) — create them explicitly **with the right kind**, not by a bare write (a cold write makes a plain `simple` board and the kind can't change).

Capture the work as a short idea on `ideas` and accept it; record the requirement(s) it settles into on `spec` and note each `nodeId`; then create a `work` feature that links one by passing that `nodeId` as `specRef`. Move the feature `Pending → InProgress → Review` as you go, and stop at `Review`. The [methodology](/doc/methodology) spells out the contract if anything is unfamiliar.

**Check:** the work node shows a live link to the spec node, and the spec leaf's computed delivery reads `in_progress`. If delivery still says `not_started`, the link didn't take (recheck the `specRef` id) or the feature never left `Pending`. The agent should **not** have set `Done` — the maintainer reviews and sets it from the UI, which closes the loop.

## From here

The agent now plans on the rails: requirements onto `spec`, technical tasks onto `work` (linked), thoughts onto `ideas`, inbound reports onto `intake` — always stopping at `Review` and leaving `Done` to the maintainer. Build the app against PetBox (config / logs / data) while tracking the work in PetBox. See the [methodology](/doc/methodology) for the day-to-day contract and the [overview](/doc/overview) for what each module offers.
