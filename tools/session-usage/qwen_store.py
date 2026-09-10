"""Shared Qwen Code session-archive reader + usage aggregation.

Third leg for session-usage, alongside archive.py (Claude Code) and
opencode_store.py (opencode). Reads Qwen Code's native session archive, written
by default with no telemetry opt-in needed. Only stdlib. See README.md "qwen
sessions" section for the gotchas this module exists to avoid.

STORAGE: `~/.qwen/projects/<cwd-slug>/chats/<session-uuid>.jsonl` — root
sessions, one file per session, history back to 2025-12-12 with no truncation
observed. `~/.qwen/projects/<cwd-slug>/subagents/<parent-session>/
agent-<role>-call_<id>.jsonl` + a paired `.meta.json` — one pair per subagent
call. Like archive.py, this module walks the WHOLE `~/.qwen/projects/` tree
rather than deriving the cwd-slug itself: slug casing is not normalized across
projects in a real archive (`D--my-tmp-yobapub` sits next to
`d--my-prj-petbox`), so matching a computed slug would be unreliable; globbing
every project directory is not.

DATA MODEL — the part that cost real debugging time:

1. CACHE IS A SUBSET OF PROMPT, NOT A DISJOINT BUCKET. `cachedContentTokenCount`
   is counted INSIDE `promptTokenCount` — unlike the Claude Code leg, where input
   and cache-read are separate numbers that get summed. Verified on 24 real
   assistant turns from one real session (a `f96c4480-...` root session with
   cache in play): `cachedContentTokenCount` trails a few hundred tokens behind
   `promptTokenCount` on every turn, e.g. prompt=71,435 / cached=71,040 — never
   the kind of independent pair you'd sum. `fresh_input = prompt - cached` is the
   correct input-rate bucket. There is no cache-WRITE bucket at all in this
   format; `cache_write` is always reported as 0, never invented.

2. THOUGHTS TOKENS ARE ALSO A SUBSET, NOT DISJOINT — of `candidatesTokenCount`,
   not `promptTokenCount`. Verified on the same 24 real turns:
   `totalTokenCount == promptTokenCount + candidatesTokenCount` EXACTLY, every
   single time, regardless of `thoughtsTokenCount`'s own value (which ranged
   42..18,578 across those turns) — so `thoughtsTokenCount` never gets its own
   slice of the total; it is already inside `candidatesTokenCount`
   (Gemini-API-style `usageMetadata`, same convention). This matters for
   pricing: unlike the opencode leg, where `reasoning` is a genuinely separate
   bucket that gets ADDED to `output` for the cost formula
   (`(output+reasoning)*price.output`), here `output` (`candidatesTokenCount`)
   must be priced ALONE — adding `thoughts` on top would double-count exactly
   like adding `cache_read` on top of `input` would. `thoughts` is still shown
   as its own report column (never silently folded away), just never added into
   anything costed.

3. SUBAGENT LINES CARRY NO `model` FIELD AT ALL — join it from the paired
   `.meta.json`'s `persistedCliFlags.model`. Root-session assistant lines DO
   carry `model` inline. Verified across all 23 real subagent call files in one
   archive: 0 had `model` on any assistant line; every one had a `.meta.json`
   sibling with `persistedCliFlags.model` set. `agentType` (and
   `subagentName`/inline `agentName`) is the ROLE, read literally — no
   inference needed.

4. MODEL IDS BAKE IN A WALLET PREFIX AND SOMETIMES AN EFFORT SUFFIX —
   `ds-deepseek-v4-pro` (real-money DeepSeek direct API -> price key
   `deepseek/deepseek-v4-pro`), `go-glm-5.3-flash` (opencode-go quota ->
   `opencode-go/glm-5.3-flash`). An effort suffix can follow the model slug
   (`go-glm-5.3-flash-low`, `ds-deepseek-v4-pro-max`, both observed on real
   turns) — but a suffix that LOOKS like an effort tag can also be part of the
   base model's own name: `go-qwen3.8-max` is a WHOLE model id (also observed
   on real turns), not `qwen3.8` + effort `max` — `opencode-go/qwen3.8-max` is
   a real `prices.json` entry. `resolve_price()` below resolves this the only
   safe way: try the full remainder against `prices.json` FIRST, and only strip
   one trailing known effort suffix (low/high/max/xhigh/thinking) as a FALLBACK
   if the direct lookup misses. Stripping first would have silently mispriced
   every `qwen3.8-max` turn as `qwen3.8`. An unrecognized wallet prefix
   (`or-deepseek-v4-flash` was observed once in a real archive; meaning
   unclear) or no prefix at all (`coder-model`, bare `deepseek-v4-pro` — both
   seen on real turns, predating the `ds-`/`go-` convention or from a
   probe/test run) is reported as an unknown wallet, NEVER folded into
   `deepseek` or `opencode-go` by guesswork.

5. THE GO-SUBSCRIPTION QUOTA IS SHARED ACROSS HARNESSES. Every `go-*` model id
   here draws on the SAME opencode-go subscription quota
   (`opencode_store.py`'s wallet `opencode-go`) that opencode itself draws on.
   This module prices `go-*` turns against the same `prices.json` entries
   (`opencode-go/...`, including their `quota_multiplier`) for that reason — but
   it does NOT merge Qwen's quota consumption with opencode's into one window
   figure. A true shared-quota-window forecast needs both this module's `go-*`
   totals AND opencode_store.py's `opencode-go` wallet totals combined by
   TIMESTAMP into one sliding window — that is the scope of the linked card
   `session-usage-quota-window-and-price-dating`, not implemented here.
   `session_usage.py`'s `q-money` labels the `opencode-go` wallet total
   accordingly so a reader does not mistake it for the whole subscription's
   draw.

6. `ui_telemetry` LINES ARE A NEAR-DUPLICATE TOKEN SOURCE — DO NOT SUM THEM
   ALONGSIDE THE ASSISTANT-LINE TOTALS. Verified on one real 24-assistant-turn
   session: it also contains 129 `qwen-code.api_response` `ui_telemetry` events
   (plus 188 `qwen-code.tool_call` events) — 5.4x the assistant-turn count, not
   a 1:1 shadow copy (apparently every LLM inference round of an agentic loop
   fires its own `api_response` event, including ones that end in a tool call
   rather than a persisted `assistant` message). This module reads token
   buckets ONLY from `type:"assistant"` lines' `usageMetadata`; `ui_telemetry`
   lines are read ONLY for `duration_ms`/`ttft_ms`/`status_code` (latency),
   reported as their own aggregate, never merged into a token or turn count.
   `ui_telemetry` lines were also only observed in ROOT session files across a
   real archive (0 in any of the 23 real subagent call files) — so latency data
   has no subagent-level breakdown in this module.
"""
import glob
import json
import os
from datetime import datetime, timedelta, timezone

DEFAULT_QWEN_DIR = os.path.expanduser("~/.qwen/projects")

# The five token buckets this module reports per assistant turn. `input` and
# `output` are DERIVED (see module docstring points 1-2), not raw usageMetadata
# fields — never sum these into one "total tokens" figure, same discipline as
# the other two legs.
TOKEN_BUCKETS = ("input", "output", "thoughts", "cache_read", "cache_write")
BUCKET_LABEL = {
    "input": "input",              # promptTokenCount - cachedContentTokenCount
    "output": "output",            # candidatesTokenCount (ALREADY includes thoughts)
    "thoughts": "thoughts",        # SUBSET of output — diagnostic column, never added on top
    "cache_read": "cache_read",    # cachedContentTokenCount — SUBSET of promptTokenCount
    "cache_write": "cache_write",  # always 0 — no such bucket in this format
}

# Model-id wallet prefix -> price-list wallet name (module docstring point 4).
WALLET_PREFIXES = {"ds": "deepseek", "go": "opencode-go"}

# Known reasoning-effort suffixes that can trail a model slug. Tried ONLY as a
# fallback after a direct price-key lookup misses — see resolve_price().
EFFORT_SUFFIXES = ("thinking", "xhigh", "high", "low", "max")


def zero_tokens():
    return {b: 0 for b in TOKEN_BUCKETS}


def add_tokens(dst, src):
    for b in TOKEN_BUCKETS:
        dst[b] += src[b]


def parse_ts(s):
    """Parse an archive ISO timestamp (e.g. '2026-09-09T16:21:45.654Z') into an
    aware UTC datetime. Returns None if s is falsy or unparseable."""
    if not s:
        return None
    try:
        if s.endswith("Z"):
            s = s[:-1] + "+00:00"
        dt = datetime.fromisoformat(s)
        if dt.tzinfo is None:
            dt = dt.replace(tzinfo=timezone.utc)
        return dt
    except Exception:
        return None


def _extract_usage(um):
    """Raw usageMetadata dict -> the 5-bucket dict this module reports. See
    module docstring points 1-2 for why `input`/`output` are derived rather
    than being the raw `promptTokenCount`/`candidatesTokenCount` fields."""
    prompt = um.get("promptTokenCount", 0) or 0
    cached = um.get("cachedContentTokenCount", 0) or 0
    candidates = um.get("candidatesTokenCount", 0) or 0
    thoughts = um.get("thoughtsTokenCount", 0) or 0
    return {
        "input": max(prompt - cached, 0),
        "output": candidates,
        "thoughts": thoughts,
        "cache_read": cached,
        "cache_write": 0,
    }


def parse_session(path):
    """Read one root-session or subagent-call .jsonl file. Returns
    (start_dt, turns, telemetry, n_parse_err):

      start_dt   - aware UTC datetime of the EARLIEST timestamp seen in the
                   file, or None if no timestamp was found. Used for window
                   filtering, same convention as archive.parse_transcript.
      turns      - list of {"model": str|None, "tokens": {5 buckets},
                   "ts": raw ISO string, "agent_round": int|None}, one per
                   type:"assistant" line that carries usageMetadata (a line
                   without one contributes nothing — never observed in a real
                   assistant line, but not assumed).
      telemetry  - list of {"model": str|None, "duration_ms", "ttft_ms",
                   "status_code", "ts"}, one per `qwen-code.api_response`
                   ui_telemetry event (module docstring point 6). NOT the same
                   count as `turns` — never used for token totals.
      n_parse_err - number of lines that failed json.loads.
    """
    start_dt = None
    turns = []
    telemetry = []
    n_parse_err = 0
    with open(path, encoding="utf-8", errors="replace") as f:
        for line in f:
            line = line.strip()
            if not line:
                continue
            try:
                obj = json.loads(line)
            except Exception:
                n_parse_err += 1
                continue
            ts_raw = obj.get("timestamp")
            if ts_raw:
                dt = parse_ts(ts_raw)
                if dt and (start_dt is None or dt < start_dt):
                    start_dt = dt
            t = obj.get("type")
            if t == "assistant":
                um = obj.get("usageMetadata")
                if not um:
                    continue
                turns.append({
                    "model": obj.get("model"),
                    "tokens": _extract_usage(um),
                    "ts": ts_raw,
                    "agent_round": obj.get("agentRound"),
                })
            elif t == "system" and obj.get("subtype") == "ui_telemetry":
                ev = (obj.get("systemPayload") or {}).get("uiEvent") or {}
                if ev.get("event.name") != "qwen-code.api_response":
                    continue
                telemetry.append({
                    "model": ev.get("model"),
                    "duration_ms": ev.get("duration_ms"),
                    "ttft_ms": ev.get("ttft_ms"),
                    "status_code": ev.get("status_code"),
                    "ts": ts_raw,
                })
    return start_dt, turns, telemetry, n_parse_err


def iter_root_sessions(qwen_dir=DEFAULT_QWEN_DIR):
    """Yield (project_slug, session_id, path) for every root session file, i.e.
    every *.jsonl directly under <project>/chats/. Mirrors
    archive.iter_root_sessions — walks the WHOLE tree rather than deriving a
    cwd-slug (see module docstring)."""
    for pdir in sorted(d for d in glob.glob(os.path.join(qwen_dir, "*")) if os.path.isdir(d)):
        pname = os.path.basename(pdir)
        chats_dir = os.path.join(pdir, "chats")
        if not os.path.isdir(chats_dir):
            continue
        for path in sorted(glob.glob(os.path.join(chats_dir, "*.jsonl"))):
            sid = os.path.splitext(os.path.basename(path))[0]
            yield pname, sid, path


def iter_subagent_calls(qwen_dir=DEFAULT_QWEN_DIR):
    """Yield (jsonl_path, meta_path_or_None, parent_session_id) for every
    subagents/<parent>/agent-*.jsonl call across the whole archive. Paired
    `.meta.json` holds `agentType` (the role) and
    `persistedCliFlags.model` (the model — module docstring point 3: subagent
    lines carry no inline `model`)."""
    for jf in sorted(glob.glob(os.path.join(qwen_dir, "*", "subagents", "*", "agent-*.jsonl"))):
        meta_path = jf[: -len(".jsonl")] + ".meta.json"
        parent = os.path.basename(os.path.dirname(jf))
        yield jf, (meta_path if os.path.exists(meta_path) else None), parent


def cutoff_dt(days):
    return datetime.now(timezone.utc) - timedelta(days=days)


def fmt_int(n):
    return f"{n:,}"


def parse_model_id(model_id):
    """Split a qwen model id into (wallet, slug) by its 'ds-'/'go-' prefix (see
    module docstring point 4). `wallet` is 'deepseek' or 'opencode-go' for a
    recognized prefix; otherwise `wallet` is an 'unknown-wallet(<raw id>)'
    label and `slug` is None — callers must not guess a price key in that
    case, only report it."""
    if not model_id:
        return "unknown-wallet(<none>)", None
    prefix, sep, rest = model_id.partition("-")
    wallet = WALLET_PREFIXES.get(prefix)
    if wallet is None or not sep or not rest:
        return f"unknown-wallet({model_id})", None
    return wallet, rest


def resolve_price(prices, wallet, slug):
    """(price_key, price_entry_or_None, effort_or_None) for a (wallet, slug)
    pair. Tries the slug UNCHANGED first; only on a miss does it strip one
    trailing known effort suffix (low/high/max/xhigh/thinking) and retry — see
    module docstring point 4 for why the order matters (`qwen3.8-max` is a
    whole model id, not an effort-suffixed `qwen3.8`). Returns
    (best-attempted-key, None, None) when nothing in `prices` matches —
    callers must treat that as "no price data", never as free."""
    if slug is None:
        return wallet, None, None
    direct_key = f"{wallet}/{slug}"
    if direct_key in prices:
        return direct_key, prices[direct_key], None
    for suffix in EFFORT_SUFFIXES:
        tail = f"-{suffix}"
        if slug.endswith(tail) and len(slug) > len(tail):
            base = slug[: -len(tail)]
            key = f"{wallet}/{base}"
            if key in prices:
                return key, prices[key], suffix
    return direct_key, None, None


def quota_multiplier(price_entry):
    """1 unless the price entry carries an explicit quota_multiplier. Only
    meaningful for opencode-go priced entries; never apply to a `deepseek`
    (direct-bill) entry."""
    if not price_entry:
        return 1
    return price_entry.get("quota_multiplier", 1) or 1


def bucket_cost(tokens, price_entry):
    """Recompute cost from the 5 token buckets at a price entry's BASE (1x)
    per-token rate. Unlike opencode_store.bucket_cost, `thoughts` is NEVER
    added to `output` here — it is already included inside it (module
    docstring point 2); adding it would double-count exactly like adding
    `cache_read` on top of `input` would. Returns None (never 0.0) when there
    is no price entry, so "no price data" is never silently reported as
    "free"."""
    if not price_entry:
        return None
    return (
        tokens["input"] / 1e6 * price_entry["input"]
        + tokens["output"] / 1e6 * price_entry["output"]
        + tokens["cache_read"] / 1e6 * price_entry["cache_read"]
        + tokens["cache_write"] / 1e6 * price_entry["cache_write"]
    )


def fmt_usd(x):
    if x is None:
        return "no price"
    return f"${x:,.4f}"
