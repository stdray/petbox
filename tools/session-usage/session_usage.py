#!/usr/bin/env python3
"""
session-usage — local Claude Code token usage reporting, from transcript archives.

WHY THIS EXISTS: the Claude Code subscription has no monthly usage dashboard at all.
`/cost` is interactive-only and resets on `/clear` (current session only). `/usage`
is interactive-only and caps out at 7 days. Neither is scriptable. The local
transcript archive under `~/.claude/projects/**/*.jsonl` (plus per-call subagent
transcripts under `<session>/subagents/agent-*.jsonl`) is the ONLY source that lets
you look back further than a week or add anything up programmatically. This tool
reads that archive.

SCOPE: only the Claude Code transcript format is supported. opencode session
storage (`opencode session` / `opencode export` / `~/.local/share/opencode/`) is a
DIFFERENT format and is NOT read by this tool — that is tracked as follow-up work,
not a bug. See README.md, "Known gap: opencode sessions".

Subcommands:
  summary   Per-project and per-ACTUAL-model token totals over the last N days
            (root sessions only - excludes subagents/, see gotcha #3 in README).
  roles     Per-subagent-role usage stats (sum/median/p90 per bucket, call count)
            over the last N days, plus root/orchestrator session totals.
  money     Apply a price list (prices.json, NOT hardcoded - see README) to
            role usage from `roles`, per routing profile, plus the worst 5-hour
            spend window for quota-metered ("opencode-go/*") routes.

Read the README in this directory before trusting a dollar figure out of this tool -
sections "Gotchas" and "Known gap" cover mistakes that have already cost real
miscalculations once each.

Only Python stdlib. No dependencies.
"""
import argparse
import json
import os
import statistics
import sys
from datetime import timedelta

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import archive as ar

DEFAULT_PROJECTS_DIR = os.path.expanduser("~/.claude/projects")
HERE = os.path.dirname(os.path.abspath(__file__))

# Known subagent role names, ordered as commonly seen. Any OTHER agentType value
# found in the archive is still reported, grouped under its own name - this list is
# just the preferred display order, not a filter.
KNOWN_ROLES = [
    "petbox-worker",
    "petbox-worker-highstakes",
    "petbox-explore",
    "petbox-reserve",
    "petbox-utility",
    "petbox-orchestrator",
]


def _load_json_data(path):
    with open(path, encoding="utf-8") as f:
        raw = json.load(f)
    return {k: v for k, v in raw.items() if not k.startswith("_")}


# --------------------------------------------------------------------------- summary

def cmd_summary(args):
    cutoff = ar.cutoff_dt(args.days)
    project_dirs = sorted(
        d for d in __import__("glob").glob(os.path.join(args.projects_dir, "*")) if os.path.isdir(d)
    )

    buckets = {b: 0 for b in ar.BUCKETS}
    by_project = {}
    by_model = {}
    turns_total = 0
    sessions_in_window = 0
    files_scanned = 0
    parse_errs = 0
    earliest = None
    latest = None

    for pname, path in ar.iter_root_sessions(args.projects_dir):
        files_scanned += 1
        start_dt, msgs, n_err = ar.parse_transcript(path)
        parse_errs += n_err
        if not msgs or start_dt is None or start_dt < cutoff:
            continue
        sums = ar.sum_usage(msgs, include_sidechain=not args.no_sidechains)
        if sum(sums.values()) == 0:
            continue
        sessions_in_window += 1
        turns_total += len(msgs)
        for b in ar.BUCKETS:
            buckets[b] += sums[b]
        pd = by_project.setdefault(pname, {b: 0 for b in ar.BUCKETS} | {"turns": 0, "sessions": 0})
        for b in ar.BUCKETS:
            pd[b] += sums[b]
        pd["turns"] += len(msgs)
        pd["sessions"] += 1

        model_sums, model_turns = ar.per_model_usage(msgs, include_sidechain=not args.no_sidechains)
        for model, ms in model_sums.items():
            md = by_model.setdefault(model, {b: 0 for b in ar.BUCKETS} | {"turns": 0})
            for b in ar.BUCKETS:
                md[b] += ms[b]
            md["turns"] += model_turns[model]

        if earliest is None or start_dt < earliest:
            earliest = start_dt
        if latest is None or start_dt > latest:
            latest = start_dt

    print("=" * 78)
    print(f"session-usage summary - root sessions, last {args.days} days"
          f" ({'excluding' if args.no_sidechains else 'including'} inline sidechain turns)")
    print(f"NOTE: a session counts toward the window if it STARTED within it (by the")
    print(f"session's earliest timestamp) - this is call/session-level windowing, not")
    print(f"per-message. See README 'Windowing semantics'.")
    print("=" * 78)
    print(f"Projects dir           : {args.projects_dir}")
    print(f"Project folders found  : {len(project_dirs)}")
    print(f"Root session files     : {files_scanned} scanned, {sessions_in_window} in window")
    print(f"Malformed JSON lines    : {parse_errs}")
    print(f"Assistant turns (deduped by message.id): {turns_total}")
    if earliest:
        print(f"Window session-start span: {earliest.isoformat()} .. {latest.isoformat()}")
    print()

    print("-- Four buckets (report SEPARATELY, never summed - see README) --")
    for b in ar.BUCKETS:
        print(f"  {ar.BUCKET_LABEL[b]:15s}: {ar.fmt_int(buckets[b])}")
    print()

    print("-- By project --")
    hdr = f"  {'project':45s} {'sessions':>8s} {'turns':>7s} {'input':>10s} {'output':>10s} {'cache_read':>14s} {'cache_creation':>15s}"
    print(hdr)
    for pname in sorted(by_project, key=lambda p: -sum(by_project[p][b] for b in ar.BUCKETS)):
        d = by_project[pname]
        print(f"  {pname:45s} {d['sessions']:8d} {d['turns']:7d} "
              f"{ar.fmt_int(d['input_tokens']):>10s} {ar.fmt_int(d['output_tokens']):>10s} "
              f"{ar.fmt_int(d['cache_read_input_tokens']):>14s} {ar.fmt_int(d['cache_creation_input_tokens']):>15s}")
    print()

    print("-- By ACTUAL model (message.model, not roster binding - see README gotcha #5) --")
    hdr2 = f"  {'model':40s} {'turns':>7s} {'input':>10s} {'output':>10s} {'cache_read':>14s} {'cache_creation':>15s}"
    print(hdr2)
    for model in sorted(by_model, key=lambda m: -sum(by_model[m][b] for b in ar.BUCKETS)):
        d = by_model[model]
        print(f"  {model:40s} {d['turns']:7d} "
              f"{ar.fmt_int(d['input_tokens']):>10s} {ar.fmt_int(d['output_tokens']):>10s} "
              f"{ar.fmt_int(d['cache_read_input_tokens']):>14s} {ar.fmt_int(d['cache_creation_input_tokens']):>15s}")
    print()
    _print_footer()


def _print_footer():
    print("=" * 78)
    print("READ BEFORE QUOTING A DOLLAR FIGURE: these are token COUNTS from local")
    print("transcripts, not a billing record. A Claude Code subscription is not metered")
    print("per token. Any USD figure elsewhere in this tool is 'what this token volume")
    print("would cost at a given list price', not an invoice.")
    print("=" * 78)


# ----------------------------------------------------------------------------- roles

def _collect_role_calls(projects_dir, cutoff):
    """Returns (role_calls: dict[role] -> list of {"sums","turns","ts","file"},
    other_roles: dict[str,int], meta_missing, parse_err, outside_window, no_timestamp)"""
    role_calls = {}
    other_roles = {}
    meta_missing = 0
    parse_err = 0
    outside_window = 0
    no_timestamp = 0

    for jf, meta_path in ar.iter_subagent_calls(projects_dir):
        if meta_path is None:
            meta_missing += 1
            continue
        try:
            with open(meta_path, encoding="utf-8") as mf:
                meta = json.load(mf)
        except Exception:
            parse_err += 1
            continue
        role = meta.get("agentType") or "unknown"
        start_dt, msgs, n_err = ar.parse_transcript(jf)
        parse_err += n_err
        if not msgs:
            continue
        if start_dt is None:
            no_timestamp += 1
            continue
        if start_dt < cutoff:
            outside_window += 1
            continue
        sums = ar.sum_usage(msgs)
        role_calls.setdefault(role, []).append(
            {"sums": sums, "turns": len(msgs), "ts": start_dt, "file": jf, "description": meta.get("description")}
        )
        if role not in KNOWN_ROLES:
            other_roles[role] = other_roles.get(role, 0) + 1

    return role_calls, other_roles, meta_missing, parse_err, outside_window, no_timestamp


def _collect_root_totals(projects_dir, cutoff):
    all_time = {b: 0 for b in ar.BUCKETS}
    windowed = {b: 0 for b in ar.BUCKETS}
    files_total = 0
    files_windowed = 0
    for _pname, path in ar.iter_root_sessions(projects_dir):
        start_dt, msgs, _n_err = ar.parse_transcript(path)
        if not msgs:
            continue
        sums = ar.sum_usage(msgs)
        if sum(sums.values()) == 0:
            continue
        files_total += 1
        for b in ar.BUCKETS:
            all_time[b] += sums[b]
        if start_dt is not None and start_dt >= cutoff:
            files_windowed += 1
            for b in ar.BUCKETS:
                windowed[b] += sums[b]
    return all_time, windowed, files_total, files_windowed


def cmd_roles(args):
    cutoff = ar.cutoff_dt(args.days)
    role_calls, other_roles, meta_missing, parse_err, outside_window, no_timestamp = _collect_role_calls(
        args.projects_dir, cutoff
    )
    root_all_time, root_windowed, root_files_total, root_files_windowed = _collect_root_totals(
        args.projects_dir, cutoff
    )

    print("=" * 78)
    print(f"session-usage roles - subagent calls, last {args.days} days (by call start time)")
    print("=" * 78)
    print(f"Projects dir            : {args.projects_dir}")
    print(f".meta.json missing      : {meta_missing}")
    print(f"Malformed JSON lines    : {parse_err}")
    print(f"Calls with no timestamp : {no_timestamp} (excluded)")
    print(f"Calls outside window    : {outside_window}")
    if other_roles:
        print(f"Other agentType values seen (not in KNOWN_ROLES): {other_roles}")
    print()

    roles_in_order = list(KNOWN_ROLES) + [r for r in role_calls if r not in KNOWN_ROLES]
    hdr = f"  {'role':28s} {'n_calls':>8s} " + " ".join(f"{ar.BUCKET_LABEL[b]+' sum':>16s}" for b in ar.BUCKETS)
    print("-- Per role: sum / median / p90 per bucket --")
    print(hdr)
    for role in roles_in_order:
        calls = role_calls.get(role, [])
        n = len(calls)
        if n == 0:
            print(f"  {role:28s} {0:8d}")
            continue
        print(f"  {role:28s} {n:8d} " + " ".join(
            f"{ar.fmt_int(sum(c['sums'][b] for c in calls)):>16s}" for b in ar.BUCKETS))
        for b in ar.BUCKETS:
            vals = [c["sums"][b] for c in calls]
            med = statistics.median(vals)
            p90 = statistics.quantiles(vals, n=10)[8] if n >= 2 else vals[0]
            print(f"    {ar.BUCKET_LABEL[b]:14s} median={ar.fmt_int(int(med)):>12s}  p90={ar.fmt_int(int(p90)):>12s}")
    print()

    print("-- Root / orchestrator sessions --")
    print(f"Root session files (usable): {root_files_total} all-time, {root_files_windowed} in window")
    print(f"  all-time : " + "  ".join(f"{ar.BUCKET_LABEL[b]}={ar.fmt_int(root_all_time[b])}" for b in ar.BUCKETS))
    print(f"  windowed : " + "  ".join(f"{ar.BUCKET_LABEL[b]}={ar.fmt_int(root_windowed[b])}" for b in ar.BUCKETS))
    print()
    _print_footer()


# ----------------------------------------------------------------------------- money

def _bucket_cost(sums, price):
    return (
        sums["input_tokens"] / 1e6 * price["input"]
        + sums["output_tokens"] / 1e6 * price["output"]
        + sums["cache_read_input_tokens"] / 1e6 * price["cache_read"]
        + sums["cache_creation_input_tokens"] / 1e6 * price["cache_write"]
    )


def _worst_5h_window(calls_with_cost):
    """calls_with_cost: list of (datetime, cost). Two-pointer sliding max over any 5h span."""
    if not calls_with_cost:
        return 0.0, None, None, 0
    calls_with_cost = sorted(calls_with_cost, key=lambda x: x[0])
    ts = [c[0] for c in calls_with_cost]
    costs = [c[1] for c in calls_with_cost]
    n = len(ts)
    best = 0.0
    best_i = best_j = 0
    j = 0
    running = 0.0
    for i in range(n):
        if i > 0:
            running -= costs[i - 1]
        if j < i:
            j = i
        while j < n and (ts[j] - ts[i]) <= timedelta(hours=5):
            running += costs[j]
            j += 1
        if running > best:
            best = running
            best_i, best_j = i, j - 1
    count = best_j - best_i + 1 if best > 0 else 0
    return best, (ts[best_i] if n else None), (ts[best_j] if n else None), count


def cmd_money(args):
    prices = _load_json_data(args.prices)
    profiles_data = _load_json_data(args.profiles)
    orchestrator_model = profiles_data["orchestrator_model"]
    profiles = profiles_data["profiles"]

    cutoff = ar.cutoff_dt(args.days)
    role_calls, *_ = _collect_role_calls(args.projects_dir, cutoff)
    _root_all_time, root_windowed, *_ = _collect_root_totals(args.projects_dir, cutoff)

    if orchestrator_model not in prices:
        print(f"ERROR: orchestrator_model {orchestrator_model!r} not in {args.prices}", file=sys.stderr)
        sys.exit(1)
    orch_cost = _bucket_cost(root_windowed, prices[orchestrator_model])

    print("=" * 78)
    print(f"session-usage money - last {args.days} days, priced from {args.prices}")
    print(f"profiles from {args.profiles}")
    print("=" * 78)
    print(f"Orchestrator/root sessions ({orchestrator_model}, always direct): ${orch_cost:.2f}")
    print()

    for profile, mapping in profiles.items():
        direct_cost = orch_cost
        go_cost = 0.0
        per_role = {}
        go_calls = []
        for role, model in mapping.items():
            if model not in prices:
                print(f"  WARNING: {profile}/{role} -> {model!r} not in price list, skipping", file=sys.stderr)
                continue
            calls = role_calls.get(role, [])
            role_sum = {b: sum(c["sums"][b] for c in calls) for b in ar.BUCKETS}
            c = _bucket_cost(role_sum, prices[model])
            per_role[role] = (model, c, len(calls))
            if model.startswith("opencode-go/"):
                go_cost += c
                for call in calls:
                    go_calls.append((call["ts"], _bucket_cost(call["sums"], prices[model])))
            else:
                direct_cost += c

        worst_cost, w_start, w_end, w_count = _worst_5h_window(go_calls)

        print(f"-- Profile: {profile} --")
        for role, (model, c, n) in per_role.items():
            print(f"  {role:28s} -> {model:32s} n={n:4d}  ${c:8.2f}")
        print(f"  direct (DeepSeek API) total : ${direct_cost:.2f}")
        print(f"  quota (opencode-go) total   : ${go_cost:.2f}  ({go_cost/60.0*100:.1f}% of $60 monthly cap)")
        if w_start:
            print(f"  worst 5h quota window       : ${worst_cost:.2f} over {w_count} calls, "
                  f"{w_start.isoformat()} .. {w_end.isoformat()}"
                  f"{'  [OVER $12/5h CAP]' if worst_cost > 12.0 else ''}")
        else:
            print("  worst 5h quota window       : no quota-routed calls in window")
        print()

    print("NOTE: 'quota' cost is NOT a dollar charge - the Go subscription is a fixed")
    print("$10, this is what fraction of the $12/5h, $30/week, $60/month usage caps the")
    print("token volume would consume if priced at the given rates. See README.")


# ------------------------------------------------------------------------------- main

def build_parser():
    ap = argparse.ArgumentParser(
        prog="session_usage.py",
        description=__doc__,
        formatter_class=argparse.RawDescriptionHelpFormatter,
    )
    sub = ap.add_subparsers(dest="command", required=True)

    common = argparse.ArgumentParser(add_help=False)
    common.add_argument("--days", type=int, default=30, help="Lookback window in days (default 30)")
    common.add_argument("--projects-dir", default=DEFAULT_PROJECTS_DIR,
                         help="Root of the Claude Code per-project transcript folders "
                              "(default ~/.claude/projects). opencode session storage is a "
                              "DIFFERENT format and is not supported - see README.")

    p_summary = sub.add_parser("summary", parents=[common], help="Per-project and per-model totals")
    p_summary.add_argument("--no-sidechains", action="store_true",
                            help="Exclude inline isSidechain=true turns (legacy format; modern "
                                 "subagent calls live under subagents/ and are always excluded here)")
    p_summary.set_defaults(func=cmd_summary)

    p_roles = sub.add_parser("roles", parents=[common], help="Per-subagent-role usage stats")
    p_roles.set_defaults(func=cmd_roles)

    p_money = sub.add_parser("money", parents=[common], help="Price role usage per routing profile")
    p_money.add_argument("--prices", default=os.path.join(HERE, "prices.json"),
                          help="Price list JSON (default prices.json next to this script). "
                               "NOT vendored pricing - see README for how to refresh it.")
    p_money.add_argument("--profiles", default=os.path.join(HERE, "profiles.example.json"),
                          help="Role->model routing profiles JSON (default profiles.example.json "
                               "next to this script - copy and edit for your own routing).")
    p_money.set_defaults(func=cmd_money)

    return ap


def main():
    args = build_parser().parse_args()
    args.func(args)


if __name__ == "__main__":
    main()
