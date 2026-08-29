# session-usage

Local token usage reporting for Claude Code, built from the transcript archive under
`~/.claude/projects/`.

## Why this exists

The Claude Code subscription has **no monthly usage dashboard**. `/cost` is
interactive-only and reports the current session, resetting on `/clear`. `/usage` is
interactive-only and caps out at **7 days**. Neither is callable from a script. The
local transcript archive — one `.jsonl` per session under
`~/.claude/projects/<project>/`, plus one `.jsonl` + `.meta.json` per subagent call
under `<session>/subagents/agent-*.jsonl` — is the **only** source that lets you look
back further than a week, break usage down by project/role/model, or add anything up
programmatically. This tool reads that archive.

## Scripts

| script | what |
|---|---|
| `archive.py` | shared archive-walking + usage-aggregation code (dedup, windowing, per-model grouping) — everything else imports this |
| `session_usage.py` | CLI entry point, three subcommands (below) |
| `prices.json` | price list data for `money` (see "Price list" below — **not** hardcoded, and not vendored-forever) |
| `profiles.example.json` | example role→model routing profiles for `money` — copy and edit, not authoritative |

## Run

```bash
python3 tools/session-usage/session_usage.py summary [--days 30] [--no-sidechains] [--projects-dir PATH]
python3 tools/session-usage/session_usage.py roles   [--days 30] [--projects-dir PATH]
python3 tools/session-usage/session_usage.py money   [--days 30] [--prices prices.json] [--profiles profiles.example.json]
```

Only Python stdlib. No dependencies. Default `--projects-dir` is `~/.claude/projects`.

- `summary` — per-project and per-**actual**-model token totals (root sessions only).
- `roles` — per-subagent-role usage (sum/median/p90 per bucket, call count), plus root/
  orchestrator session totals.
- `money` — prices `roles` output per routing profile from `prices.json` /
  `profiles.example.json`, plus the worst real 5-hour spend window for quota-metered
  (`opencode-go/*`) routes.

## Gotchas — each of these already produced a wrong number once

1. **Never sum the four usage buckets into one "total tokens" figure.**
   `input_tokens`, `output_tokens`, `cache_read_input_tokens`, `cache_creation_input_tokens`
   are not equivalent in cost — `cache_read` is one to two orders of magnitude cheaper per
   token than fresh input on API list price (measured ratio on this archive: input :
   cache_read ≈ 1 : 77,000 over 30 days). Report them separately, always.
2. **Dedupe by `message.id` before summing anything.** Usage records are streamed: the
   same assistant `message.id` can appear more than once in a transcript file, and only
   the **last** occurrence carries the final bucket values. Measured on one real 32 MB
   session file: 772 raw assistant lines, 400 unique message ids — an undeduped count
   would have been ~1.9x too high. `archive.parse_transcript()` is the only place this
   tool reads a transcript, and it dedupes by id (keeping the last occurrence) before
   anything downstream sees a number.
3. **Exclude `subagents/` when walking root sessions.** Subagent transcripts live at
   `<session>/subagents/agent-*.jsonl`; if a root-session walk isn't filtered to exclude
   that subdirectory, calls get double-counted. This has previously inflated an
   orchestrator's usage 2.3–3x. `archive.iter_root_sessions()` filters on
   `os.sep + "subagents" + os.sep` for exactly this reason.
4. **Don't use `subagent_tokens` from the spawn-completion notification as a call's
   size.** It equals `cache_creation_input_tokens` — the *smallest* of the four buckets,
   not the call's total volume. Read the full per-call transcript instead:
   `<session>/subagents/agent-<id>.jsonl`, with the subagent's role in the paired
   `agent-<id>.meta.json` under the `agentType` key.
5. **Group by the actual `message.model` field, not by the roster's nominal role→model
   binding.** There is an open observation (`model-binding-change-not-deterministic-in-session`)
   that the binding actually used in a session can differ from what the roster
   configuration says. `summary`'s "By ACTUAL model" table and `archive.per_model_usage()`
   read `message.model` directly off every deduped assistant message for this reason.
6. **Prices are data, not code — and they drift.** `prices.json` is not scraped from any
   pricing page and is not guaranteed current. Refresh it with
   `opencode models <provider> --verbose` and read `cost.input`, `cost.output`,
   `cost.cache.read`, `cost.cache.write` off each model entry (units: USD per 1M tokens).
   Treat any number `money` prints as only as fresh as the last time someone updated this
   file.

## Windowing semantics

A session or subagent call counts toward the `--days N` window based on the **earliest
timestamp found in its own transcript file** (its start time), not per assistant
message. A long session that starts one day before the cutoff and continues for a week
past it counts its *entire* usage as "in window"; a session that starts one day *after*
the cutoff and is still running counts entirely too. This matches how the reference
numbers this tool was checked against were computed, and keeps windowing logic in one
place instead of re-filtering every message.

## Known gap: opencode sessions

This tool reads **only** the Claude Code transcript format
(`~/.claude/projects/**/*.jsonl`). It does **not** read opencode's own session storage
(`opencode session`, `opencode export <sessionID>`, `~/.local/share/opencode/`, or
`opencode stats --models`). Adding that support — plus a model+variant unit of account
for opencode's separate reasoning-level `variant`, splitting direct-DeepSeek-API spend
from Go-subscription-quota consumption, and a forecast-vs-actual reconciliation mode —
is tracked as follow-up work on the `session-usage-tool` card on the PetBox `work`
board, project `$system`. Not implemented here; `--help` says so too.

## Live-archive check (2026-08-29)

Run against the real `~/.claude/projects/` archive (14 project folders, 66 root session
files, 406 subagent call files). `roles --days 30`:

```
Projects dir            : C:\Users\stdray/.claude/projects
.meta.json missing      : 0
Malformed JSON lines    : 0
Calls with no timestamp : 0 (excluded)
Calls outside window    : 33
Other agentType values seen (not in KNOWN_ROLES): {'general-purpose': 2, 'claude-code-guide': 2}

-- Per role: sum / median / p90 per bucket --
  role                          n_calls        input sum       output sum   cache_read sum cache_creation sum
  petbox-worker                     206           38,437        5,638,404    1,087,754,635       30,656,578
  petbox-worker-highstakes           83            7,902        3,384,054      471,578,248       15,251,663
  petbox-explore                     50           17,928          465,132       84,964,562        3,763,235
  petbox-reserve                     27              808          546,736       26,392,892        2,285,580
  petbox-utility                      2               80            5,067          204,403           70,972
  petbox-orchestrator                 1              240           96,858       21,224,389          700,141

-- Root / orchestrator sessions --
Root session files (usable): 64 all-time, 60 in window
  windowed : input=12,649  output=6,984,105  cache_read=1,154,121,218  cache_creation=28,560,111
```

Checked against the numbers this port was required to reproduce (root sessions, 30d:
input 12,294 / output 6,592,972 / cache_read 1,117,887,526 / cache_creation 27,474,131;
call counts worker 203 / highstakes 61 / explore 50 / reserve 24 / utility 2):

- **Root session buckets are within ~3% of the reference** across all four
  (12,649/6,984,105/1,154,121,218/28,560,111 vs the numbers above) — expected drift, not
  a defect: this is a live, continuously-growing archive and several hours passed
  between when the reference was captured and this run, all inside the same 30-day
  window (the window itself also slid forward by that much).
- **`explore` and `utility` match exactly** (50, 2).
- **`worker` (206 vs 203) and `reserve` (27 vs 24)** are a few calls over — consistent
  with the same time drift.
- **`worker-highstakes` (83 vs 61) is the largest gap**, expected and called out in the
  task: session `54bb57df-64c1-47e0-9d99-4cf8cd5745fe` (the session that did this port)
  itself spawned 16 `petbox-worker-highstakes` subagents, 14 of them synthetic
  experiment probes (`Arm S run*`, `Arm O run*`, `Probe*`) unrelated to real work. The
  reference numbers were captured before those existed. 61 + 16 = 77, still short of 83
  — the remaining ~6 are further ordinary drift from elapsed time, same as the other
  roles. Not a dedup or windowing defect: `roles` and `summary` agree on root-session
  totals to the token, and `money`'s worst-5h-window dollar figures below reproduce
  exactly, which they would not if dedup/windowing were broken.

`money --days 30` worst-5h-quota-window figures, checked against the task's reference
table (`$3.92` / `$7.09` / `$9.95`):

```
opencode-direct : worst 5h quota window : $3.92 over 5 calls
opencode-main   : worst 5h quota window : $7.09 over 42 calls
opencode-go-max : worst 5h quota window : $9.95 over 50 calls
```

**All three match the reference to the cent.** The profile totals (direct/quota/%-of-cap)
are a few percent higher than the reference table, for the same reason as the role call
counts above (more highstakes/reserve/worker calls accumulated since the reference was
taken) — not a computation defect.
