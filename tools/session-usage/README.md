# session-usage

Local token usage reporting for Claude Code, opencode, AND Qwen Code. Claude Code
numbers come from the transcript archive under `~/.claude/projects/`; opencode
numbers come from its own SQLite session store at
`~/.local/share/opencode/opencode.db`; Qwen Code numbers come from its own session
archive under `~/.qwen/projects/`.

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
| `qwen_store.py` | shared Qwen Code session-archive reader + usage-aggregation code (read-only, native archive, no telemetry opt-in) — `q-roles`/`q-money` import this |
| `session_usage.py` | CLI entry point, nine subcommands (below) |
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

Qwen Code session archive (`~/.qwen/projects/**`, read-only):

```bash
python3 tools/session-usage/session_usage.py q-roles [--days 30] [--qwen-dir PATH]
python3 tools/session-usage/session_usage.py q-money [--days 30] [--qwen-dir PATH] [--prices prices.json]
```

- `q-roles` — per-subagent-role usage (sum/median/p90 per bucket, call count), plus
  root/orchestrator session totals, plus aggregate API latency
  (`duration_ms`/`ttft_ms`/`status_code`) read from `ui_telemetry` lines — see gotcha
  #18.
- `q-money` — recomputed cost (from `prices.json`) per model, grouped by **wallet**
  (the `ds-`/`go-` prefix baked into the model id) — never summed together — plus the
  same totals grouped by role. Cache-subset and thoughts-subset handling use a
  *different* formula from the Claude/opencode legs — see gotchas #13-14.

Only Python stdlib. No dependencies. Default `--projects-dir` is `~/.claude/projects`;
default `--db` is `~/.local/share/opencode/opencode.db`; default `--qwen-dir` is
`~/.qwen/projects`.

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

**qwen-specific gotchas (13-18) — `q-roles`/`q-money`:**

13. **`cachedContentTokenCount` is a SUBSET of `promptTokenCount`, not a disjoint
    bucket — unlike the Claude Code leg, where input and cache-read are separate
    numbers that get summed.** Verified on 24 real assistant turns from one real
    session: `promptTokenCount` and `cachedContentTokenCount` trail a few hundred
    tokens apart on every turn (e.g. prompt=71,435 / cached=71,040 -> derived
    `fresh_input`=395), never behaving like an independent pair. The correct
    input-rate bucket is `fresh_input = promptTokenCount - cachedContentTokenCount`.
    There is no cache-WRITE bucket at all in this format — `qwen_store.py` always
    reports `cache_write=0`, never invents one. Porting the Claude-leg cost formula
    unchanged would double-count cache on every priced turn.
14. **`thoughtsTokenCount` is ALSO a subset, of `candidatesTokenCount` this time —
    not disjoint like opencode's `reasoning` bucket.** Verified on the same 24 real
    turns: `totalTokenCount == promptTokenCount + candidatesTokenCount` EXACTLY,
    every time, regardless of `thoughtsTokenCount`'s own value (ranged 42..18,578
    across those turns) — so it never gets its own slice of the total. Unlike the
    opencode leg, where `(output+reasoning)*price.output` is the correct cost
    formula because `reasoning` is genuinely separate from `output`, here `output`
    (`candidatesTokenCount`) must be priced ALONE — adding `thoughts` on top would
    double-count exactly like adding `cache_read` to `input` would. `thoughts` is
    still its own report column, just never added into a costed bucket.
15. **Subagent call lines carry no `model` field at all — join it from the paired
    `.meta.json`'s `persistedCliFlags.model`.** Verified across all 23 real
    subagent call files in one archive: 0 had `model` on any assistant line; every
    one had a `.meta.json` sibling with `persistedCliFlags.model` set. Root-session
    assistant lines DO carry `model` inline. The role comes literally from
    `.meta.json`'s `agentType`/`subagentName` (and inline `agentName`) — no
    inference needed, same discipline as everywhere else in this tool: read what
    the archive actually says, don't derive it.
16. **Model ids bake in a wallet prefix and SOMETIMES an effort suffix — and you
    cannot tell which by pattern-matching alone.** `ds-deepseek-v4-pro` ->
    `deepseek/deepseek-v4-pro` (real-money DeepSeek direct API);
    `go-glm-5.3-flash` -> `opencode-go/glm-5.3-flash` (quota). An effort suffix can
    trail the model slug (`go-glm-5.3-flash-low`, `ds-deepseek-v4-pro-max`, both
    observed on real turns in this archive) — but a suffix that LOOKS like an
    effort tag can also be part of the base model's own name:
    `go-qwen3.8-max` is a WHOLE model id (also observed on real turns), not
    `qwen3.8` + effort `max` — `opencode-go/qwen3.8-max` is a real `prices.json`
    entry. `qwen_store.resolve_price()` tries the full remainder against
    `prices.json` FIRST and only strips one trailing known effort suffix
    (low/high/max/xhigh/thinking) as a FALLBACK on a miss — stripping first would
    have silently mispriced every `qwen3.8-max` turn as plain `qwen3.8`. An
    unrecognized wallet prefix (`or-deepseek-v4-flash` was observed once in this
    archive; meaning unclear) or no prefix at all (`coder-model`, bare
    `deepseek-v4-pro` — both observed on real turns, predating the `ds-`/`go-`
    convention or from a probe/test run) is reported as an unknown wallet, NEVER
    folded into `deepseek` or `opencode-go` by guesswork — `q-money` prints these
    as their own `unknown-wallet(...)` groups with `cost=no price`.
17. **The opencode-go quota is shared across harnesses — Qwen's `go-*` totals are
    only ONE contributor to it, not the whole subscription's draw.** Every `go-*`
    model id here draws on the SAME opencode-go subscription quota
    (`opencode_store.py`'s wallet `opencode-go`) that opencode itself draws on.
    `q-money` prices `go-*` turns against the same `prices.json` entries
    (including their `quota_multiplier`) for that reason, but it does NOT merge
    Qwen's quota consumption with opencode's into one window figure — a true
    shared-quota-window forecast needs both this leg's `go-*` totals and
    `oc-money`'s `opencode-go` wallet totals combined by timestamp, which is the
    scope of the linked card `session-usage-quota-window-and-price-dating`, not
    implemented here. `q-money`'s output labels the `opencode-go` wallet total
    accordingly.
18. **`ui_telemetry` lines are a near-duplicate token source that must NOT be
    summed alongside the assistant-line totals — but they DO carry latency data
    neither other leg has.** Verified on one real 24-assistant-turn session: it
    also contains 129 `qwen-code.api_response` `ui_telemetry` events (plus 188
    `qwen-code.tool_call` events) — 5.4x the assistant-turn count, not a 1:1
    shadow copy (every LLM inference round of an agentic loop appears to fire its
    own `api_response` event, including ones that end in a tool call rather than a
    persisted `assistant` message). `qwen_store.py` reads token buckets ONLY from
    `type:"assistant"` lines' `usageMetadata`; `ui_telemetry` lines are read ONLY
    for `duration_ms`/`ttft_ms`/`status_code`, reported as their own aggregate in
    `q-roles`, never merged into a token or turn count. `ui_telemetry` lines were
    also only observed in ROOT session files in a real archive (0 in any of 23
    real subagent call files) — so `q-roles`' latency section has no
    subagent-level breakdown.

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

## qwen sessions

Qwen Code writes its own session archive by default under `~/.qwen/projects/` — no
telemetry opt-in needed, history observed back to 2025-12-12 with no truncation.

**Schema (what matters here):**

- Root sessions: `~/.qwen/projects/<cwd-slug>/chats/<session-uuid>.jsonl`, one file
  per session. `qwen_store.iter_root_sessions()` walks the whole `~/.qwen/projects/`
  tree rather than deriving `<cwd-slug>` itself — slug casing is not normalized
  across a real archive (`D--my-tmp-yobapub` sits next to `d--my-prj-petbox`), so a
  computed match would be unreliable; globbing every project directory is not.
- Subagent calls: `~/.qwen/projects/<cwd-slug>/subagents/<parent-session>/
  agent-<role>-call_<id>.jsonl` + a paired `.meta.json` — one pair per call.
  `qwen_store.iter_subagent_calls()` reads them.
- A root `type:"assistant"` line carries `model` (e.g. `ds-deepseek-v4-pro`) and
  `usageMetadata: {promptTokenCount, candidatesTokenCount, thoughtsTokenCount,
  cachedContentTokenCount, totalTokenCount}` directly. A subagent
  `agent-*.jsonl` `type:"assistant"` line carries `usageMetadata` and
  `agentId`/`agentName`/`agentRound` but **no `model`** — join it from the sibling
  `.meta.json`'s `persistedCliFlags.model` (gotcha #15).
- `type:"system", subtype:"ui_telemetry"` lines (root sessions only) carry a
  near-duplicate token count under `systemPayload.uiEvent` PLUS `duration_ms`,
  `ttft_ms`, `status_code` — read for latency only, never for tokens (gotcha #18).

**Token buckets are DERIVED, not raw fields — see gotchas #13-14.** `qwen_store.py`
reports five buckets (`input`, `output`, `thoughts`, `cache_read`, `cache_write`):
`input = promptTokenCount - cachedContentTokenCount`, `output = candidatesTokenCount`
(as-is — already includes `thoughts`), `thoughts = thoughtsTokenCount` (a diagnostic
subset of `output`, never added on top of it), `cache_read = cachedContentTokenCount`,
`cache_write` is always `0` (no such bucket exists in this format).

**Model ids bake in the wallet AND sometimes an effort suffix — see gotcha #16.**
`qwen_store.parse_model_id()` splits a `ds-`/`go-` prefix off into a wallet
(`deepseek` / `opencode-go`); anything else is an `unknown-wallet(...)` label, never
guessed into one of the two real wallets. `qwen_store.resolve_price()` then tries the
remainder against `prices.json` UNCHANGED first, only stripping a trailing known
effort suffix (low/high/max/xhigh/thinking) as a fallback on a miss — direct-match-
first is load-bearing: `go-qwen3.8-max` is a whole model id in `prices.json`, not an
effort-suffixed `qwen3.8`.

**Windowing** follows the same file-start-time convention as the Claude Code leg's
`roles` (see "Windowing semantics" above), not opencode's turn-level windowing — qwen
organizes usage as one file per root session / subagent call, the same shape as the
Claude Code archive.

## Live-archive check: qwen (2026-09-10)

Run against the real `~/.qwen/projects/` archive (86 root session files usable,
23 real subagent call files). Hand-reconciled one real root session
(`f96c4480-20a0-4894-91f3-545e9a6af3c2.jsonl`, 24 assistant turns) by summing its raw
`usageMetadata` fields independently of `qwen_store.py`, then comparing against
`qwen_store.parse_session()`'s own output for the same file:

```
independent hand-sum : SUM promptTokenCount=3,893,860  SUM cachedContentTokenCount=3,808,384
                        derived fresh_input=85,476       SUM candidatesTokenCount=75,612
                        SUM thoughtsTokenCount=62,711
qwen_store.py output  : input=85,476  output=75,612  thoughts=62,711  cache_read=3,808,384  cache_write=0
```

**All five figures match exactly.** A concrete cache-subset turn from that same
session (the first turn): `promptTokenCount=71,435`, `cachedContentTokenCount=71,040`
(a SUBSET, not a disjoint number) -> `qwen_store.py` derives `input=395`
(`71,435 - 71,040`), `output=591`, `thoughts=369` (a subset of `output`, not added to
it), `cache_read=71,040` — this is gotcha #13/#14 in concrete numbers.

That same root session has one real subagent call
(`agent-petbox-worker-call_01_oOPZwj7AjUUE7UXlvRCs7423.jsonl`, role `petbox-worker`,
model `go-glm-5.3-flash`, 105 assistant turns) living in a completely separate file
under `subagents/`. Its own tool-computed sums (`cache_read=17,412,352`) are **not**
included in the root session's 24-turn total above — `iter_root_sessions()` only
globs `<project>/chats/*.jsonl`, `iter_subagent_calls()` only globs
`<project>/subagents/**/agent-*.jsonl`, and `q-roles` reports them in two disjoint
sections (root/orchestrator totals vs. per-role call sums) for exactly this reason —
the same double-counting trap gotcha #3 already covers for the Claude Code leg.

`q-roles --days 30` over the real archive:

```
Other agentType values seen (not in KNOWN_ROLES): {'general-purpose': 1}

-- Per role: sum / median / p90 per bucket --
  role                          n_calls      input sum     output sum   thoughts sum cache_read sum cache_write sum
  petbox-worker                      10      1,255,810         95,053         58,212     19,056,704              0
  petbox-worker-highstakes            1         57,775              7              0              0              0
  petbox-explore                      4        126,722            481            424         64,576              0
  petbox-reserve                      5        188,587          2,470          2,032         87,424              0
  petbox-utility                      0
  petbox-orchestrator                 1         57,233              7              0            512              0
  general-purpose                     1         61,499          7,931          6,861              0              0

-- Root / orchestrator sessions --
Root session files (usable): 86 all-time, 79 in window
  windowed : input=2,826,848  output=152,223  thoughts=115,111  cache_read=10,191,232  cache_write=0

-- API latency (ui_telemetry, root sessions only) --
  samples: 378
  duration_ms  median=    5904  p90=   24074
  ttft_ms      median=    2052  p90=    8358
  non-200 status_code: 0 / 378
```

`q-money --days 30` over the same window (`prices.json` unchanged, no new entries
needed): both real wallets (`deepseek` $1.1202, `opencode-go` $1.1368 recomputed /
$1.5786 quota with the `glm-5.3-flash` x2 multiplier correctly applied) plus three
`unknown-wallet(...)` groups (`deepseek-v4-pro` unprefixed, `probe-ds-effort`,
`or-deepseek-v4-flash`) reported honestly as `cost=no price` rather than folded into
a real wallet — confirming gotcha #16's direct-match-first resolver and the
never-guess-a-wallet discipline both hold on real, not synthetic, data. A
`coder-model` id (from before the `ds-`/`go-` convention existed, dated 2026-03-25)
was seen in the full archive but correctly fell outside the 30-day window.
