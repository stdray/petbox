"""Shared Claude Code transcript archive walking + usage aggregation.

Single place for the parsing logic that used to be copy-pasted across three draft
scripts (monthly_usage.py, token_audit.py, intro_where.py) in the source research repo.
Only stdlib. See README.md in this directory for the gotchas this code exists to avoid —
most importantly: usage records are STREAMED (one `message.id` can appear more than once
per transcript line, only the LAST occurrence per id carries the final bucket values), so
every aggregation in this file goes through `parse_transcript`, which dedupes by id before
anything downstream sums or windows it.
"""
import glob
import json
import os
from datetime import datetime, timedelta, timezone

# Raw usage bucket keys as they appear in message.usage. NEVER sum these into one
# "total tokens" number: cache_read is one to two orders of magnitude cheaper per token
# than fresh input on API list price, and a subscription plan has no per-token rate at
# all for any of them.
BUCKETS = (
    "input_tokens",
    "output_tokens",
    "cache_read_input_tokens",
    "cache_creation_input_tokens",
)

# Short labels used in table headers / JSON keys throughout this tool.
BUCKET_LABEL = {
    "input_tokens": "input",
    "output_tokens": "output",
    "cache_read_input_tokens": "cache_read",
    "cache_creation_input_tokens": "cache_creation",
}

SUBAGENTS_MARKER = os.sep + "subagents" + os.sep


def parse_ts(s):
    """Parse a transcript ISO timestamp (e.g. '2026-08-01T12:05:15.625Z') into an
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


def parse_transcript(path):
    """Read one transcript .jsonl file (a root session, or a subagents/agent-*.jsonl
    call). Returns (start_dt, msgs, n_parse_err):

      start_dt  - aware UTC datetime of the EARLIEST timestamp seen in the file (any
                  record type), or None if no timestamp was found. This is the file's
                  "when did this call/session start" marker used for window filtering.
      msgs      - dict {message_id: {"usage": <raw usage dict>, "model": str,
                  "ts": <raw ISO string>, "sidechain": bool}}, one entry per UNIQUE
                  assistant message id, keeping the LAST occurrence seen in the file
                  (usage buckets are cumulative/final only on the last stream chunk for
                  a given id — see BUCKETS docstring above).
      n_parse_err - number of lines that failed json.loads.
    """
    start_dt = None
    msgs = {}
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
            if obj.get("type") != "assistant":
                continue
            msg = obj.get("message", {})
            mid = msg.get("id")
            usage = msg.get("usage")
            if not mid or not usage:
                continue
            msgs[mid] = {
                "usage": usage,
                "model": msg.get("model") or "unknown",
                "ts": ts_raw,
                "sidechain": bool(obj.get("isSidechain")),
            }
    return start_dt, msgs, n_parse_err


def sum_usage(msgs, include_sidechain=True):
    """Sum the four buckets across a deduped msgs dict (see parse_transcript)."""
    sums = {b: 0 for b in BUCKETS}
    for rec in msgs.values():
        if not include_sidechain and rec["sidechain"]:
            continue
        u = rec["usage"]
        for b in BUCKETS:
            sums[b] += u.get(b, 0) or 0
    return sums


def per_model_usage(msgs, include_sidechain=True):
    """Group the four buckets by ACTUAL message.model (never the roster's nominal
    binding for a role — see README gotcha #5). Returns dict[model] -> bucket sums,
    plus a parallel dict[model] -> turn count."""
    out = {}
    turns = {}
    for rec in msgs.values():
        if not include_sidechain and rec["sidechain"]:
            continue
        model = rec["model"]
        d = out.setdefault(model, {b: 0 for b in BUCKETS})
        u = rec["usage"]
        for b in BUCKETS:
            d[b] += u.get(b, 0) or 0
        turns[model] = turns.get(model, 0) + 1
    return out, turns


def iter_root_sessions(projects_dir):
    """Yield (project_name, path) for every root session transcript, i.e. every
    *.jsonl directly under a project folder. Explicitly EXCLUDES anything under a
    subagents/ subdirectory — walking those into the root total is the #1 way this
    kind of report ends up double-counted (observed 2.3-3x orchestrator inflation)."""
    for pdir in sorted(d for d in glob.glob(os.path.join(projects_dir, "*")) if os.path.isdir(d)):
        pname = os.path.basename(pdir)
        for path in glob.glob(os.path.join(pdir, "**", "*.jsonl"), recursive=True):
            if SUBAGENTS_MARKER in path:
                continue
            yield pname, path


def iter_subagent_calls(projects_dir):
    """Yield (jsonl_path, meta_path_or_None) for every subagents/agent-*.jsonl call
    across the whole archive, paired with its sibling .meta.json (holds `agentType`,
    the subagent's role)."""
    for jf in glob.glob(os.path.join(projects_dir, "**", "subagents", "agent-*.jsonl"), recursive=True):
        meta_path = jf[: -len(".jsonl")] + ".meta.json"
        yield jf, (meta_path if os.path.exists(meta_path) else None)


def cutoff_dt(days):
    return datetime.now(timezone.utc) - timedelta(days=days)


def fmt_int(n):
    return f"{n:,}"
