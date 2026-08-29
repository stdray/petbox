"""Shared opencode session-database reader + usage aggregation.

Single place for reading opencode's SQLite session store (mirrors what archive.py
does for the Claude Code transcript archive — one reader, everything else imports
it). Only stdlib (sqlite3 is stdlib). See README.md "opencode sessions" section for
the gotchas this module exists to avoid.

STORAGE: `~/.local/share/opencode/opencode.db` (SQLite). A `-wal` / `-shm` file sits
next to it — NEVER touch, copy, or checkpoint those; this module opens the database
read-only via a `file:...?mode=ro` URI and nothing else. A file-mirror also exists
under `~/.local/share/opencode/storage/{message,part,session,project}/` but the
database is more complete and is what this module reads.

DATA MODEL — the part that cost real debugging time:

1. Attribution (agent / model / provider / variant) lives at TWO levels: the
   `session` table has `agent` and `model` (a JSON string) columns, AND every
   assistant row in the `message` table's `data` JSON column carries its own
   `agent`, `modelID`, `providerID`, `variant`, `cost`, and `tokens`. On this
   archive, ~1/3 of sessions (65/195) have NULL `session.agent` / `session.model`
   — they predate those columns being added — but EVERY assistant message still
   carries full attribution. Reading session-level columns only would misclassify
   a third of the archive as "unattributed" when in fact only sessions with ZERO
   assistant messages (6/195 here) are genuinely unattributable. This module reads
   attribution from MESSAGE rows (`iter_assistant_messages`), not from `session`
   columns, for exactly this reason. `session` columns are used only for tree
   structure (`parent_id`), title, and directory/project — never as the primary
   source of model/agent truth.

2. The unit of account is (providerID, modelID, variant) — variant (low/default/
   high/max/xhigh/thinking) is a distinct reasoning-effort tier of the SAME model,
   priced the same per-token but consuming a different token volume. A session (and
   even a single message stream) can also change modelID mid-session (observed on
   this archive) — another reason to group at message level, not session level.

3. Wallets are NOT just two. `providerID` values observed on this archive:
   `deepseek` (direct API, real invoice), `opencode-go` (Go subscription quota),
   `opencode` (a THIRD, pre-Go-subscription paid provider — e.g. `glm-5.2` under
   plain `opencode` billed $11.76 real money on this archive; do not fold it into
   `opencode-go`, it is a different billing relationship), `mockllm` (synthetic,
   always $0), and local runners `lmstudio` / `llama.cpp` (always $0, no cloud
   billing at all). `wallet_label()` below names the ones seen; anything else is
   reported, not dropped.

4. `session.cost` and each per-message `cost` are opencode's OWN recorded cost —
   already computed by opencode itself at ingest time, in USD, at the model's BASE
   per-token price. This is a DIFFERENT number from what this tool recomputes from
   `prices.json`, and the two should usually agree closely for a correctly priced
   model — but see gotcha #5 (quota multiplier) for a case where the dashboard
   figure a human sees is neither of these numbers.

5. Reasoning tokens are priced at the OUTPUT rate, not left unpriced, not priced
   separately. Verified empirically on 164 real glm-5.3-flash messages: summing
   input*price.input + output*price.output + cache_read*price.cache_read +
   cache_write*price.cache_write (reasoning excluded) reproduces only 96.7% of
   opencode's own recorded cost sum (ratio 1.0327); folding reasoning into the
   output bucket for the cost formula ONLY (output+reasoning)*price.output
   reproduces it to 1.000 exactly. `bucket_cost()` below does this. This does NOT
   mean reasoning should be merged into the output column in any REPORT — the five
   buckets are still shown separately everywhere in this tool; only the priced-cost
   arithmetic treats reasoning as output-rate.

6. QUOTA MULTIPLIER — the ×2 that broke a first reconciliation attempt. Two models
   in the opencode-go price list are billed against the Go subscription's usage
   caps ($12/5h, $30/week, $60/month) at a MULTIPLE of their base per-token price:
   `glm-5.3-flash` at 2x, `hy3` at 8x. `opencode models opencode-go --verbose`
   states this ONLY as free text inside the human-readable `name` field
   ("GLM-5.3-Flash (2x usage)", "Hy3 (8x usage)") — there is no structured field
   for it. `session.cost` / message `cost` are recorded at the BASE (1x) price;
   the dashboard's quota consumption is base_cost * multiplier. Confirmed on
   glm-5.3-flash on 2026-08-29: recorded/recomputed base cost $0.277327 (ratio
   1.000 to each other), dashboard quota figure $0.56 ≈ $0.277327 * 2 = $0.554654.
   `prices.json` carries this as a `quota_multiplier` field (absent = 1); it must
   be transcribed BY HAND from the `name` field whenever the price list is
   refreshed — there is nothing to parse it out of automatically, and getting it
   wrong silently understates quota usage by exactly the missed factor.
"""
import json
import os
import sqlite3
from datetime import datetime, timedelta, timezone

DEFAULT_DB_PATH = os.path.expanduser("~/.local/share/opencode/opencode.db")

# The five token buckets opencode tracks per assistant message. Like the Claude Code
# leg of this tool, NEVER sum these into one "total tokens" figure - cache_read is
# priced one to two orders of magnitude below fresh input, and reasoning is a
# distinct generation mode. Show all five separately in every report.
TOKEN_BUCKETS = ("input", "output", "reasoning", "cache_read", "cache_write")
BUCKET_LABEL = {
    "input": "input",
    "output": "output",
    "reasoning": "reasoning",
    "cache_read": "cache_read",
    "cache_write": "cache_write",
}

# providerID -> human label. Anything not listed here is still reported (never
# silently dropped) under an "unknown provider (<id>)" label - see module docstring
# point 3.
KNOWN_WALLETS = {
    "deepseek": "direct API (real invoice)",
    "opencode-go": "Go subscription (usage quota, not a per-token bill)",
    "opencode": "legacy/other paid provider (predates Go split - real charges, do not fold into opencode-go)",
    "mockllm": "synthetic (always $0)",
    "lmstudio": "local runner (always $0)",
    "llama.cpp": "local runner (always $0)",
}


def wallet_label(provider_id):
    return KNOWN_WALLETS.get(provider_id, f"unknown provider ({provider_id})")


def connect_ro(db_path=DEFAULT_DB_PATH):
    """Open the opencode SQLite database READ-ONLY. Never opens for write, never
    touches the -wal/-shm files sitting next to it."""
    if not os.path.exists(db_path):
        raise FileNotFoundError(f"opencode database not found: {db_path}")
    con = sqlite3.connect(f"file:{db_path}?mode=ro", uri=True)
    return con


def _extract_msg_row(session_id, msg_id, data_json, time_created_ms):
    try:
        d = json.loads(data_json)
    except Exception:
        return None
    if d.get("role") != "assistant":
        return None
    tokens_raw = d.get("tokens") or {}
    cache = tokens_raw.get("cache") or {}
    return {
        "session_id": session_id,
        "msg_id": msg_id,
        "time_ms": time_created_ms,
        "agent": d.get("agent"),
        "model_id": d.get("modelID"),
        "provider_id": d.get("providerID"),
        "variant": d.get("variant") or "default",
        "cost": d.get("cost") or 0.0,
        "tokens": {
            "input": tokens_raw.get("input", 0) or 0,
            "output": tokens_raw.get("output", 0) or 0,
            "reasoning": tokens_raw.get("reasoning", 0) or 0,
            "cache_read": cache.get("read", 0) or 0,
            "cache_write": cache.get("write", 0) or 0,
        },
    }


def iter_assistant_messages(con, since_ms=None, until_ms=None):
    """Yield one dict per assistant message across the whole database (optionally
    windowed by message.time_created, half-open [since_ms, until_ms)). This is the
    PRIMARY attribution source for this tool - see module docstring point 1. A
    message whose data JSON fails to parse, or whose role isn't "assistant", is
    silently skipped (it contributes nothing to any bucket)."""
    sql = "SELECT session_id, id, time_created, data FROM message"
    where = []
    params = []
    if since_ms is not None:
        where.append("time_created >= ?")
        params.append(since_ms)
    if until_ms is not None:
        where.append("time_created < ?")
        params.append(until_ms)
    if where:
        sql += " WHERE " + " AND ".join(where)
    cur = con.cursor()
    cur.execute(sql, params)
    for session_id, mid, tc, data in cur:
        row = _extract_msg_row(session_id, mid, data, tc)
        if row is not None:
            yield row


def load_sessions(con):
    """dict[session_id] -> session metadata row. Used for tree structure
    (parent_id), title, and directory - NOT as the primary attribution source
    (see module docstring point 1); `agent`/`model` here can be NULL even for
    sessions with fully-attributed messages."""
    cur = con.cursor()
    cur.execute(
        "SELECT id, project_id, parent_id, title, agent, model, cost, "
        "tokens_input, tokens_output, tokens_reasoning, tokens_cache_read, tokens_cache_write, "
        "time_created, time_updated, directory FROM session"
    )
    out = {}
    for (sid, pid, parent, title, agent, model_json, cost, ti, to, tr, tcr, tcw, tc, tu, directory) in cur:
        model = None
        if model_json:
            try:
                model = json.loads(model_json)
            except Exception:
                model = None
        out[sid] = {
            "id": sid,
            "project_id": pid,
            "parent_id": parent,
            "title": title,
            "agent": agent,
            "model": model,
            "cost": cost or 0.0,
            "tokens": {
                "input": ti or 0, "output": to or 0, "reasoning": tr or 0,
                "cache_read": tcr or 0, "cache_write": tcw or 0,
            },
            "time_created": tc,
            "time_updated": tu,
            "directory": directory,
        }
    return out


def session_tree_ids(sessions, root_id):
    """All session ids in the subtree rooted at root_id (root included), following
    parent_id links to arbitrary depth (grandchildren etc, even though none were
    observed on this archive - don't assume a fixed depth of 1)."""
    by_parent = {}
    for sid, s in sessions.items():
        by_parent.setdefault(s["parent_id"], []).append(sid)
    out = []
    stack = [root_id]
    while stack:
        cur = stack.pop()
        out.append(cur)
        stack.extend(by_parent.get(cur, []))
    return out


def find_root_sessions_by_title(sessions, needle):
    """Case-insensitive substring match against ROOT session titles only (parent_id
    IS NULL) - what a human looking at the opencode TUI's session list would search
    for. Returns a list of session dicts, newest first."""
    needle_low = needle.lower()
    hits = [
        s for s in sessions.values()
        if s["parent_id"] is None and s["title"] and needle_low in s["title"].lower()
    ]
    hits.sort(key=lambda s: s["time_created"] or 0, reverse=True)
    return hits


def local_day_range_ms(date_str, tz="local"):
    """Half-open [start_ms, end_ms) epoch-millisecond range for one calendar day
    (YYYY-MM-DD), in either this machine's local timezone (tz="local", the default)
    or UTC (tz="utc"). THE CHOICE MATTERS: on a reconciliation run against a
    2026-08-29 dashboard snapshot, the local (UTC+3) boundary reproduced the
    dashboard's glm-5.3 figure to $0.0013 ($0.2913 vs $0.29) while the UTC boundary
    was off by $0.07 ($0.2175) and dropped grok-4.6/mimo-v2.5 out of the bucket
    entirely. Always make this a caller-visible parameter, never hardcode one
    boundary."""
    y, m, d = (int(x) for x in date_str.split("-"))
    if tz == "utc":
        start = datetime(y, m, d, tzinfo=timezone.utc)
    elif tz == "local":
        local_tzinfo = datetime.now().astimezone().tzinfo
        start = datetime(y, m, d, tzinfo=local_tzinfo)
    else:
        raise ValueError(f"tz must be 'local' or 'utc', got {tz!r}")
    end = start + timedelta(days=1)
    return int(start.timestamp() * 1000), int(end.timestamp() * 1000)


def cutoff_ms(days):
    return int((datetime.now(timezone.utc) - timedelta(days=days)).timestamp() * 1000)


def price_key(provider_id, model_id):
    return f"{provider_id}/{model_id}"


def price_for(prices, provider_id, model_id):
    return prices.get(price_key(provider_id, model_id))


def quota_multiplier(price_entry):
    """1 unless the price entry carries an explicit quota_multiplier - see module
    docstring point 6. Only meaningful for opencode-go priced entries; callers
    should not apply this to a `deepseek` (direct-bill) entry."""
    if not price_entry:
        return 1
    return price_entry.get("quota_multiplier", 1) or 1


def bucket_cost(tokens, price_entry):
    """Recompute cost from token buckets at a price entry's BASE (1x) per-token
    rate - i.e. NOT multiplied by quota_multiplier; callers apply that separately
    when reporting quota consumption (see module docstring points 4-6). Returns
    None (never 0.0) when there is no price entry, so "no price data" is never
    silently reported as "free" - see README gotcha on unpriced models.
    Reasoning tokens are billed at the OUTPUT rate (module docstring point 5)."""
    if not price_entry:
        return None
    return (
        tokens["input"] / 1e6 * price_entry["input"]
        + (tokens["output"] + tokens["reasoning"]) / 1e6 * price_entry["output"]
        + tokens["cache_read"] / 1e6 * price_entry["cache_read"]
        + tokens["cache_write"] / 1e6 * price_entry["cache_write"]
    )


def fmt_usd(x):
    if x is None:
        return "no price"
    return f"${x:,.4f}"


def zero_tokens():
    return {b: 0 for b in TOKEN_BUCKETS}


def add_tokens(dst, src):
    for b in TOKEN_BUCKETS:
        dst[b] += src[b]
