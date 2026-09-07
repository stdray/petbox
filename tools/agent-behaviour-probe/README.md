# agent-behaviour-probe

Pins the **behaviour** of an agent working PetBox, not the **text** of the rules it is given.

`src/clients-ts/petbox-wire/src/protocol.test.ts` already pins the text — that a rule exists and
says a particular thing. Nothing pins what an agent then *does*. This probe closes that gap for
the three scenarios of the umbrella
[`umbrella-agent-text-names-both-axes`](https://petbox.3po.su/ui/$system/$system/tasks/work/umbrella-agent-text-names-both-axes):

| Scenario | Question it answers |
| --- | --- |
| `s1-bodyref-cyrillic` | Given a long Cyrillic body to write, does the agent go through `bodyRef`, or inline it into the tool call? |
| `s2-classic-status-movement` | Given a card to finish on a `classic` board, does it move the intermediate statuses, or leave the card where it found it "because there is a gate"? |
| `s3-methodology-blocks-gate` | Asked to make closing a blocker release the dependent card, does it find `blocksGate`, or declare there is no mechanism? |

## What it records, and why not pass/fail

Per run it stores three things:

* **what the agent READ before it acted** — skill invocations and guide/workflow reads, in
  order, cut at the first decisive tool call;
* **which AXIS it named out loud**, matched post hoc against a frozen word list, *with the
  surrounding text as evidence*;
* **what it DID** — the decisive tool call and the shape of its arguments.

That triple is the point. A verdict of "wrong" cannot distinguish **the text is wrong** from
**the text is right and was not read**, and those two have opposite repairs. The umbrella exists
because of that distinction, so the probe refuses to collapse it.

## Two properties that make M0→M1 comparison meaningful

**Neutral prompts.** The prompts state a task and name no axis. Asking "did you consider the
token budget?" would put the axis in front of the agent, which is the exact variable under test.

**A frozen scoring contract.** "Which axis did it name" rests on a hand-written word list, so if
that list may drift between the two measurements, *"behaviour improved"* becomes
indistinguishable from *"the word list was adjusted"*. The axis lists, signal definitions and
decisive-tool sets are therefore fingerprinted (`spec_hash`) into every run record and every
summary, and `score` **exits 3 rather than report** across a mismatch:

```
SPEC HASH MISMATCH: runs on disk were scored under [...], current scenarios.json is ...
```

The sanctioned way through is `score --rescore`, which recomputes **every** run from its stored
raw transcript under the new contract — so the whole set moves together or not at all. Raw stdout
is always written before parsing, which is what makes re-scoring free.

Markers are substrings; a `re:` prefix makes one a regex. That exists because the sharpest
reliability marker is the escape notation itself, and a bare `\u` also matches every `C:\Users`
path an agent prints.

> Measured, not assumed: an earlier revision scanned *all* text and scored 5/5 "named the
> reliability axis" for a cell whose agents had said nothing of the kind — the hits were
> `readFileSync(file, "utf8")` inside a skill's bundled validator and a Windows path, both
> arriving as **tool results**. Text extraction is now structurally restricted to the agent's own
> prose. Keep it that way, and keep the evidence field: it is what caught this.

## Provenance — which text was actually served

The kit does not update itself. The global hooks run a stable mirror under `~/.petbox/wire/`, and
`.claude/skills/` is gitignored and materialized by a separate `petbox-wire` step nobody runs
automatically, so a developer's skill text can lag git by days. Every run therefore writes
`provenance.json`: the templates' git sha, the kit version, server version, harness versions,
a digest of every skill file **served to the agent**, and which of this machine's materialized
skills are **stale against the templates**.

**This probe serves the git templates, not the machine's materialized copy** — that is what makes
a run reproducible from a sha instead of from one laptop's unversioned state. On 2026-09-07 the
two differed for exactly one skill (`petbox-write-economy`, stale by 5 days, missing commit
`31f345b`), which is why recording both is not paranoia.

## Containment

The probe drives real agents with permissions bypassed against a real server, so containment is
mechanical, not careful behaviour:

1. **Sandbox project + sandbox-only key.** A `sandboxOnly` key is refused by the server on every
   project not flagged `sandbox`, so a write into a real project cannot succeed even if attempted.
2. **Every credential is stripped from the child environment** — by name pattern *and* by value
   shape — and only the probe key is injected.
3. **Transcripts are redacted before they touch disk.** Belt and braces for a credential an agent
   read from a *file* rather than the environment.
4. **The workspace lives outside every path in `~/.petbox/projects.json`.** The global Stop /
   SessionStart hooks resolve their project by longest-prefix match on cwd and no-op on no match,
   so probe sessions are never mirrored into a real project — and no canon is injected either.
5. **`--strict-mcp-config`** stops Claude Code inheriting the user's real petbox MCP server;
   opencode takes MCP only from the project directory.

`probe.py check` asserts 2, 3 and 4 empirically and exits non-zero on violation. **Run it first.**

> Why 2 and 3 are written that way. This started as a `PETBOX_*` prefix denylist, which was
> wrong: the same shell holds live PetBox keys under names carrying no such prefix
> (`ANIMEMOV_API_KEY`, `AGENT_RELAY_API_KEY`, `KPVOTES_API_KEY`, `YOBAPUB_API_KEY`). A probe
> agent ran `env | grep -iE 'petbox|api_key'` while orienting itself, so those values were
> captured into a stored transcript **and sent to a third-party model API**. GitHub push
> protection then refused the commit — but only for the provider key, since it has no rule for
> `yb_key_*`; the PetBox keys would have been published. The prefix rule stripped 12 of the 25
> credential-shaped variables present. Do not weaken this back into a name list.

## What the agent is shown, recorded per run

`provenance.json` carries a `workspace_inventory`: **every** file placed in front of the agent.
The probe workspace is a bare directory holding only the eight rendered `SKILL.md` files and the
two MCP configs — no `AGENTS.md`, no project canon, no `$system` boards, and (being unregistered)
no hook-injected memory banner. That makes "the agent was measured against the kit, not against
the PetBox repository" a property of the recorded run rather than a claim in a report.

Two honest gaps against a strict clean-room spec, both visible in `provenance.json`:
`workspace_is_git_repo` is `false`, and the kit is placed by **rendering the git templates**
rather than by a full `petbox-wire <dir> <project>` — that command also installs global hooks,
writes `~/.petbox/keys.json` and registers the directory, i.e. mutates the developer's machine
and would defeat containment item 4.

## Running it

```bash
export PETBOX_PROBE_API_KEY=<sandbox-only key for a sandbox project>

python tools/agent-behaviour-probe/probe.py check  --key-env PETBOX_PROBE_API_KEY
python tools/agent-behaviour-probe/probe.py run    --key-env PETBOX_PROBE_API_KEY \
    --project <sandbox-project> --workspace <workspace> \
    --repeats 5 --out tools/agent-behaviour-probe/baseline/<label>
```

`run` builds its own fixtures (boards, cards, methodology instances) over MCP with the sandbox
key, so it is one command end to end — a probe whose fixtures are set up by hand is not
reproducible, and reproducibility is the whole requirement. It **skips runs already on disk**, so
an interrupted sweep resumes by re-issuing the same command; `--force` re-runs.

Cells are independent: `--only <scenario-id>` and `--harnesses claude|opencode` narrow a sweep,
which is also how you keep a single invocation inside a foreground timeout.

### Repeats

Default **5 per scenario per harness** (30 runs). The known deviation rate for this class is
about 1 in 4 (observation `write-economy-skill-answered-from-description`), and at p=0.25 five
runs miss it entirely with probability 0.75⁵ ≈ 0.24. Five is therefore enough to usually surface
a quarter-rate behaviour and — more importantly — yields a **rate** (k/5) to compare rather than a
boolean. Five is **not** enough to distinguish 0% from ~10%; do not read `0/5` as "never".

## Comparing M1 against M0

1. Re-run with the **same** `spec_hash`. If `scenarios.json` changed, `score --rescore` the M0
   baseline first so both ends are scored by one contract.
2. Diff `summary.json` cell by cell — `signal_rates` and `axis_rates` are the comparable numbers.
3. Diff `provenance.json`. A changed `skills_served_to_agent_sha256` is the *intended* difference
   after `petbox-wire update`; a changed harness or server version is a confounder to name.

A cell whose `runs_exit0` is below `runs` had failing runs — check `runs/*.err.txt` before reading
its rates, because a rate over crashed runs is not a behaviour measurement.
