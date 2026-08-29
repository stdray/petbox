# session-usage

Local token usage reporting for Claude Code AND opencode. Claude Code numbers come
from the transcript archive under `~/.claude/projects/`; opencode numbers come from
its own SQLite session store at `~/.local/share/opencode/opencode.db`.

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
| `archive.py` | shared Claude Code archive-walking + usage-aggregation code (dedup, windowing, per-model grouping) — `summary`/`roles`/`money` import this |
| `opencode_store.py` | shared opencode SQLite reader + usage-aggregation code (read-only, message-level attribution) — `oc-roles`/`oc-money`/`oc-tree`/`reconcile` import this |
| `session_usage.py` | CLI entry point, seven subcommands (below) |
| `prices.json` | price list data for `money`/`oc-money`/`reconcile` (see "Price list" below — **not** hardcoded, and not vendored-forever) |
| `profiles.example.json` | example role→model routing profiles for `money` — copy and edit, not authoritative |
| `actuals.example.json` | example ACTUAL dashboard numbers for `reconcile` — copy and edit, not authoritative |

## Run

Claude Code archive (`~/.claude/projects/**/*.jsonl`):

```bash
python3 tools/session-usage/session_usage.py summary [--days 30] [--no-sidechains] [--projects-dir PATH]
python3 tools/session-usage/session_usage.py roles   [--days 30] [--projects-dir PATH]
python3 tools/session-usage/session_usage.py money   [--days 30] [--prices prices.json] [--profiles profiles.example.json]
```

- `summary` — per-project and per-**actual**-model token totals (root sessions only).
- `roles` — per-subagent-role usage (sum/median/p90 per bucket, call count), plus root/
  orchestrator session totals.
- `money` — prices `roles` output per routing profile from `prices.json` /
  `profiles.example.json`, plus the worst real 5-hour spend window for quota-metered
  (`opencode-go/*`) routes.

opencode session database (`~/.local/share/opencode/opencode.db`, read-only):

```bash
python3 tools/session-usage/session_usage.py oc-roles   [--days 30] [--db PATH]
python3 tools/session-usage/session_usage.py oc-money   [--days 30] [--db PATH] [--prices prices.json]
python3 tools/session-usage/session_usage.py oc-tree    "<title substring>" [--db PATH]
python3 tools/session-usage/session_usage.py reconcile  --actual actuals.example.json [--db PATH] [--prices prices.json] [--tz local|utc]
```

- `oc-roles` — per-agent usage (message-level `agent`, counted per assistant **turn**,
  not per session — see gotcha #9), 5 buckets including `reasoning`, plus a root-only-
  vs-full-tree recorded-cost comparison.
- `oc-money` — opencode's own recorded cost vs our recomputed cost (from `prices.json`),
  per model, grouped by **wallet** (`providerID`) — never summed across wallets. Shows
  the quota multiplier (gotcha #11) where one applies.
- `oc-tree` — root vs full-subtree recorded cost for one session, by a case-insensitive
  title substring or `--id ses_...` — the opencode TUI's "spent" figure is root-only
  (gotcha #10).
- `reconcile` — our recomputed figures for one calendar day against ACTUAL numbers you
  supply in a JSON file (gotcha #12) — per model: ours, actual, delta, ratio; per-wallet
  totals. Day boundary is local-timezone by default (gotcha #8).

Only Python stdlib. No dependencies. Default `--projects-dir` is `~/.claude/projects`;
default `--db` is `~/.local/share/opencode/opencode.db`.

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

**opencode-specific gotchas (7-12) — `oc-roles`/`oc-money`/`oc-tree`/`reconcile`:**

7. **Read attribution from MESSAGE rows, not `session` columns.** `session.agent` and
   `session.model` are NULL on 65/195 sessions in one real archive (they predate those
   columns) — but every assistant row in `message.data` still carries its own `agent`,
   `modelID`, `providerID`, `variant`, `cost`, and `tokens`. Reading session columns only
   would misclassify a third of the archive as "unattributed"; only sessions with ZERO
   assistant messages (6/195 there) are genuinely unattributable.
   `opencode_store.iter_assistant_messages()` is the only place this tool reads
   attribution, and it reads it from `message.data`.
8. **The calendar-day boundary for `reconcile` is LOCAL by default, not UTC — and the
   choice materially changes the answer.** Checked against a real Go-subscription
   dashboard snapshot for one day: the local (UTC+3) boundary reproduced the dashboard's
   `glm-5.3` figure to $0.0013 ($0.2913 vs $0.29); the UTC boundary was off by $0.07
   ($0.2175) and dropped `grok-4.6`/`mimo-v2.5` out of the bucket entirely (their calls
   landed on the other side of midnight UTC). `--tz local|utc` makes this explicit
   instead of hardcoding one.
9. **`oc-roles` counts per assistant TURN, not per session — deliberately different
   from the Claude-leg `roles`, which counts per subagent-call file.** A single opencode
   session's `agent` and `modelID` are not guaranteed constant: verified 12/195 sessions
   mix more than one `agent` value and 4/195 mix more than one `modelID` within the same
   session (mode switches, model fallbacks). Grouping at session level would silently
   misattribute those turns to whichever value happened to be read.
10. **The opencode TUI's "spent" figure is ROOT-ONLY.** Every subagent call is a
    *separate* `session` row linked by `parent_id` to its root — the TUI never sums
    them in. Verified on two real sessions: "Просмотрщик крупных диаграмм" showed
    root=$0.1692 in the TUI, but with its 4 subagent sessions the full tree is $0.3597
    (2.1x); "Чиним ui-back-nav-no-bfcache" showed root=$0.1261, full tree with 3
    subagents is $0.1789 (1.4x). `oc-tree` prints both numbers and the difference,
    always.
11. **Some opencode-go models are billed against the Go subscription's usage caps at a
    MULTIPLE of their base per-token price, and it's stated only as free text.**
    `opencode models opencode-go --verbose` names two such models in the human-readable
    `name` field — `"GLM-5.3-Flash (2x usage)"`, `"Hy3 (8x usage)"` — with no structured
    field for the multiplier. `session.cost` / message `cost` are recorded at the BASE
    (1x) price regardless. Confirmed on `glm-5.3-flash` for one real day: recorded/
    recomputed base cost agreed with each other at $0.2773 (ratio 1.000), but the Go
    dashboard's actual quota draw for that day was $0.56 ≈ $0.2773 × 2. `prices.json`
    carries this as an optional `quota_multiplier` field per entry (absent = 1) that
    must be **transcribed by hand** from the `name` field whenever the price list is
    refreshed — there is nothing to parse it out of automatically. Getting it wrong
    silently understates quota usage by exactly the missed factor. Never applied to the
    `deepseek` wallet — that's a real per-token invoice, not a quota.
12. **Reasoning tokens are priced at the OUTPUT rate for the cost recompute, but always
    shown as their own report column.** Verified empirically on 164 real
    `glm-5.3-flash` messages: `input*price.input + output*price.output +
    cache_read*price.cache_read + cache_write*price.cache_write` (reasoning excluded)
    reproduces only 96.7% of opencode's own recorded cost sum; folding reasoning into
    the output bucket **for the cost formula only**
    (`(output+reasoning)*price.output`) reproduces it to 1.000 exactly.
    `opencode_store.bucket_cost()` does this — it does NOT mean reasoning gets merged
    into the output column anywhere in a report; the five buckets (`input`, `output`,
    `reasoning`, `cache_read`, `cache_write`) are always shown separately, same
    discipline as gotcha #1.

## Windowing semantics

A session or subagent call counts toward the `--days N` window based on the **earliest
timestamp found in its own transcript file** (its start time), not per assistant
message. A long session that starts one day before the cutoff and continues for a week
past it counts its *entire* usage as "in window"; a session that starts one day *after*
the cutoff and is still running counts entirely too. This matches how the reference
numbers this tool was checked against were computed, and keeps windowing logic in one
place instead of re-filtering every message.

## opencode sessions

opencode stores its own sessions in a SQLite database at
`~/.local/share/opencode/opencode.db` (a `-wal`/`-shm` file sits next to it — this tool
never touches those, never opens for write, and never copies or checkpoints the
database; `opencode_store.connect_ro()` opens it via a `file:...?mode=ro` URI and
nothing else). A file-mirror also exists under
`~/.local/share/opencode/storage/{message,part,session,project}/`, but the database is
more complete and is what `oc-roles`/`oc-money`/`oc-tree`/`reconcile` read.

**Schema (what matters here):**

- `session`: `id`, `project_id`, `parent_id` (NULL for a root session, the parent's id
  for a subagent session), `title`, `agent`, `model` (a JSON string
  `{"id":...,"providerID":...,"variant":...}`), `cost` (REAL, USD — opencode's own
  recorded cost), `tokens_input`/`tokens_output`/`tokens_reasoning`/
  `tokens_cache_read`/`tokens_cache_write`, `time_created`/`time_updated` (ms epoch),
  `directory`.
- `message`: `id`, `session_id`, `data` (JSON — see below), `time_created`. `part`:
  message content blocks, not read by this tool.
- `project`: `id`, `worktree`, `name`.
- An assistant `message.data` JSON carries, per turn: `role`, `agent`, `modelID`,
  `providerID`, `variant`, `cost`, and `tokens: {input, output, reasoning, cache:
  {read, write}}`. This — not the `session` columns — is what this tool reads for
  attribution (gotcha #7).

**The unit of account is (`providerID`, `modelID`, `variant`)**, not just a model name.
`variant` (`low`/`default`/`high`/`max`/`xhigh`/`thinking`) is a distinct
reasoning-effort tier of the same model, priced the same per-token but consuming a
different token volume — every grouping in `oc-roles`/`oc-money` carries it.

**Wallets are not just two.** `providerID` values seen on a real archive: `deepseek`
(direct API, real invoice), `opencode-go` (Go subscription quota), `opencode` (a
*third*, pre-Go-subscription paid provider — e.g. `glm-5.2` under plain `opencode`
billed $11.76 real money on that archive; never folded into `opencode-go`, it's a
different billing relationship), `mockllm` (synthetic, always $0), and local runners
`lmstudio`/`llama.cpp` (always $0). `opencode_store.wallet_label()` names the known
ones; anything else is still reported, never dropped.

**Recorded vs recomputed cost are two different numbers, on purpose.** `session.cost`
and every message's `cost` are opencode's own cost, computed by opencode itself at the
model's base (1x) price. `oc-money`/`reconcile` separately recompute cost from raw
token buckets against `prices.json`. The two normally agree closely for a correctly
priced model (see gotcha #12) — a gap is a diagnostic signal, not something to hide by
picking one number. One such gap is open and unexplained: `deepseek/deepseek-v4-pro`
(the direct-API wallet) shows opencode's own recorded cost running 5-30x above the
price-based recompute, non-constantly, across thousands of real messages — filed as
observation `opencode-deepseek-direct-cost-vs-recompute-diverges` on the PetBox
`observations` board, not solved here.

See gotchas #7-12 above for the specific mistakes already paid for.

## Live-archive check: Claude Code (2026-08-29)

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

## Live-archive check: opencode (2026-08-29)

Run against the real `~/.local/share/opencode/opencode.db` (195 sessions, opencode
1.18.25). `oc-tree`, the two sessions named in the task:

```
$ python3 session_usage.py oc-tree "Просмотрщик крупных диаграмм"
root: 'Просмотрщик крупных диаграмм' (ses_fb2f626d4ffeAnsVjhIYuCPr04)
  root cost   : $0.1692
  tree cost   : $0.3597  (5 sessions incl. root)
  difference  : $0.1905

$ python3 session_usage.py oc-tree "ui-back-nav-no-bfcache"
root: 'Чиним ui-back-nav-no-bfcache' (ses_fb3053cd7ffeZO8UujFmG8Em1J)
  root cost   : $0.1261
  tree cost   : $0.1789  (4 sessions incl. root)
  difference  : $0.0529
```

**Both match the task's reference exactly**: root $0.1692 / tree $0.3597, and root
$0.1261 / tree $0.1789.

`reconcile` against a 2026-08-29 Go-subscription dashboard snapshot (`actuals.example.json`),
local (UTC+3) day boundary:

```
$ python3 session_usage.py reconcile --actual actuals.example.json
-- Wallet: opencode-go (Go subscription (usage quota, not a per-token bill)) --
  glm-5.3              ours=$0.2913  actual=$0.2900  delta=$+0.0013  ratio=1.005  [OK]
  grok-4.6             ours=$0.0379  actual=$0.0400  delta=$-0.0021  ratio=0.946  [OK]
  mimo-v2.5            ours=$0.0016  actual=$0.0000  delta=$+0.0016  ratio=inf  [OK]
  qwen3.8-max          ours=$0.3100  actual=$0.3100  delta=$+0.0000  ratio=1.000  [OK]
  glm-5.3-flash        ours=$0.5547 (base $0.2773 x2)  actual=$0.5600  delta=$-0.0053  ratio=0.990  [OK]
  wallet total: ours=$1.1955  actual=$1.2000  delta=$-0.0045
```

**All five models reconcile** (the `glm-5.3-flash` row includes the ×2 quota multiplier
— gotcha #11 — its base recomputed cost is $0.2773, matching opencode's own recorded
cost for that day to the same figure at ratio 1.000; the dashboard sees the ×2 quota
draw, $0.5547 ≈ $0.56).

`oc-money --days 365` and `oc-roles --days 365` were also run over the full archive to
confirm every wallet is reported (including `opencode` — a real-money third wallet
distinct from `opencode-go` — and unpriced models correctly shown as `no price`, never
`$0`), and that `oc-roles` correctly separates root-only ($255.58) from full-tree
($262.02) recorded cost in aggregate, not just for the two spot-checked sessions above.
See the `opencode_store.py` module docstring and gotchas #7-12 for what those runs
turned up, including the one open, unexplained discrepancy (filed as an observation,
not fixed here).
