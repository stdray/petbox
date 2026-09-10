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

THREE SOURCES, THREE SETS OF SUBCOMMANDS: `summary`/`roles`/`money` read only the
Claude Code transcript format under `~/.claude/projects/**/*.jsonl`. `oc-roles`/
`oc-money`/`oc-tree`/`reconcile` read opencode's own SQLite session store (default
`~/.local/share/opencode/opencode.db`, override with --db) via opencode_store.py.
`q-roles`/`q-money` read Qwen Code's own session archive (default
`~/.qwen/projects`, override with --qwen-dir) via qwen_store.py. The three formats
have different shapes (subagent-call-transcript-tree, a flat session-with-parent_id
table, and a subagent-call-transcript-tree with a different token schema) and are
NOT merged into one set of flags - each set of subcommands is the one way to read
its own source. See README.md, "opencode sessions" / "qwen sessions" sections.

Subcommands (Claude Code archive):
  summary   Per-project and per-ACTUAL-model token totals over the last N days
            (root sessions only - excludes subagents/, see gotcha #3 in README).
  roles     Per-subagent-role usage stats (sum/median/p90 per bucket, call count)
            over the last N days, plus root/orchestrator session totals.
  money     Apply a price list (prices.json, NOT hardcoded - see README) to
            role usage from `roles`, per routing profile, plus the worst 5-hour
            spend window for quota-metered ("opencode-go/*") routes.

Subcommands (opencode session database):
  oc-roles  Per-agent (opencode's `session.agent`/message-level `agent`) usage
            stats over the last N days, counted per ASSISTANT TURN (not per
            session - agent/model can change mid-session, see README), plus a
            root-only-vs-full-tree recorded-cost comparison.
  oc-money  Opencode's own recorded cost vs our recomputed cost (from
            prices.json) per model, grouped by WALLET (providerID) - direct
            DeepSeek API, Go subscription quota, and any other provider seen -
            never summed together. Shows the quota multiplier where one applies.
  oc-tree   Root vs full-subtree recorded cost for one session (by id or a
            title substring) - the opencode TUI's "spent" figure is root-only.
  reconcile Our recomputed figures for one calendar day against ACTUAL numbers
            you supply (a JSON file, e.g. read off the Go subscription
            dashboard) - per model: our cost, actual, delta, ratio; per wallet
            totals. The day boundary is local-timezone by default (--tz).

Subcommands (Qwen Code session archive):
  q-roles   Per-subagent-role usage stats (sum/median/p90 per bucket, call
            count) over the last N days, plus root/orchestrator session
            totals, plus aggregate API latency (duration_ms/ttft_ms/
            status_code) read from ui_telemetry lines - see README "qwen
            sessions" gotchas.
  q-money   Recomputed cost (from prices.json) per model, grouped by WALLET
            (the ds-/go- prefix baked into the model id) - never summed
            together - plus the same totals grouped by role. Cache-subset and
            thoughts-subset handling is NOT the same formula as the Claude or
            opencode legs; see qwen_store.py module docstring.

Read the README in this directory before trusting a dollar figure out of this tool -
sections "Gotchas" cover mistakes that have already cost real miscalculations once
each.

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
import opencode_store as ocs
import qwen_store as qs

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


# ------------------------------------------------------------------- opencode (oc-*)

def cmd_oc_roles(args):
    con = ocs.connect_ro(args.db)
    since = ocs.cutoff_ms(args.days) if args.days else None
    sessions = ocs.load_sessions(con)

    role_data = {}
    unattributed_turns = 0
    unattributed_tokens = ocs.zero_tokens()
    total_turns = 0

    for m in ocs.iter_assistant_messages(con, since_ms=since):
        total_turns += 1
        agent = m["agent"]
        if not agent:
            unattributed_turns += 1
            ocs.add_tokens(unattributed_tokens, m["tokens"])
            continue
        d = role_data.setdefault(
            agent, {"n_turns": 0, "tokens": ocs.zero_tokens(), "recorded_cost": 0.0, "models": {}}
        )
        d["n_turns"] += 1
        ocs.add_tokens(d["tokens"], m["tokens"])
        d["recorded_cost"] += m["cost"]
        mk = f"{m['provider_id']}/{m['model_id']}#{m['variant']}"
        d["models"][mk] = d["models"].get(mk, 0) + 1

    # Sessions with literally zero assistant messages anywhere (not window-filtered -
    # this is a data-completeness stat, not a usage stat): these are the ONLY
    # sessions this tool cannot attribute at all (see opencode_store module
    # docstring point 1 - message-level attribution recovers the rest even when
    # session.agent/session.model are NULL).
    sids_with_asst_msg = set()
    for m in ocs.iter_assistant_messages(con):
        sids_with_asst_msg.add(m["session_id"])
    fully_unattributable = [sid for sid in sessions if sid not in sids_with_asst_msg]

    print("=" * 78)
    print(f"session-usage oc-roles - opencode sessions, last {args.days} days (by message.time_created)")
    print("Counted per ASSISTANT TURN (message), not per session: agent and/or model")
    print("can change mid-session in this archive (verified: 12/195 sessions mix >1")
    print("agent value, 4/195 mix >1 modelID) - turn-level is the only grain that")
    print("doesn't misattribute those sessions. See opencode_store.py docstring.")
    print("=" * 78)
    print(f"Database                : {args.db}")
    print(f"Assistant turns in window: {total_turns}")
    print(f"Unattributed turns (agent field empty): {unattributed_turns}")
    print(f"Sessions with ZERO assistant messages (fully unattributable, all-time): "
          f"{len(fully_unattributable)} / {len(sessions)}")
    print()

    print("-- Per role (message-level `agent`): n_turns / 5 buckets / recorded cost --")
    hdr = f"  {'role':24s} {'n_turns':>8s} " + " ".join(f"{ocs.BUCKET_LABEL[b]+' sum':>14s}" for b in ocs.TOKEN_BUCKETS) + f" {'recorded $':>12s}"
    print(hdr)
    for role in sorted(role_data, key=lambda r: -role_data[r]["recorded_cost"]):
        d = role_data[role]
        print(f"  {role:24s} {d['n_turns']:8d} " +
              " ".join(f"{d['tokens'][b]:,}".rjust(14) for b in ocs.TOKEN_BUCKETS) +
              f" {d['recorded_cost']:12.4f}")
        top_models = sorted(d["models"].items(), key=lambda x: -x[1])[:6]
        print("    models: " + ", ".join(f"{mk} (n={n})" for mk, n in top_models))
    if unattributed_turns:
        print(f"  {'(unattributed)':24s} {unattributed_turns:8d} " +
              " ".join(f"{unattributed_tokens[b]:,}".rjust(14) for b in ocs.TOKEN_BUCKETS))
    print()

    root_ids = {sid for sid, s in sessions.items() if s["parent_id"] is None}
    def in_window(s):
        return since is None or (s["time_created"] or 0) >= since
    root_cost = sum(s["cost"] for sid, s in sessions.items() if sid in root_ids and in_window(s))
    all_cost = sum(s["cost"] for s in sessions.values() if in_window(s))
    print("-- Root-only vs full-tree recorded cost (session.cost column) --")
    print(f"  root sessions only      : ${root_cost:.4f}   <- what the opencode TUI 'spent' shows per top-level session")
    print(f"  full tree (+ subagents) : ${all_cost:.4f}")
    print(f"  difference (subagent spend invisible in TUI root view) : ${all_cost - root_cost:.4f}")
    print()


def _oc_collect_by_model(con, since_ms=None, until_ms=None):
    """(provider_id, model_id) -> {"tokens":..., "recorded_cost":..., "variants": {variant: n_turns}}"""
    agg = {}
    for m in ocs.iter_assistant_messages(con, since_ms=since_ms, until_ms=until_ms):
        key = (m["provider_id"], m["model_id"])
        d = agg.setdefault(key, {"tokens": ocs.zero_tokens(), "recorded_cost": 0.0, "variants": {}})
        ocs.add_tokens(d["tokens"], m["tokens"])
        d["recorded_cost"] += m["cost"]
        d["variants"][m["variant"]] = d["variants"].get(m["variant"], 0) + 1
    return agg


def cmd_oc_money(args):
    prices = _load_json_data(args.prices)
    con = ocs.connect_ro(args.db)
    since = ocs.cutoff_ms(args.days) if args.days else None
    agg = _oc_collect_by_model(con, since_ms=since)

    wallets = {}
    for (provider, model), d in agg.items():
        wallets.setdefault(provider, {})[model] = d

    print("=" * 78)
    print(f"session-usage oc-money - opencode sessions, last {args.days} days, priced from {args.prices}")
    print("NOTE: 'recorded' is opencode's OWN cost, already booked at the model's base")
    print("(1x) price. 'recomputed(base)' is our own price.json recomputation from raw")
    print("token buckets, ALSO at base price - the two should agree closely for a")
    print("correctly priced model, and a gap is itself a diagnostic signal (see README).")
    print("'quota' = recomputed(base) x quota_multiplier - this is what actually draws")
    print("down the Go subscription's $12/5h $30/week $60/month caps for models that")
    print("carry a multiplier (see prices.json _quota_multiplier_comment). Never applied")
    print("to the deepseek wallet - that is a real per-token invoice, not a quota.")
    print("=" * 78)

    for provider in sorted(wallets, key=lambda p: -sum(d["recorded_cost"] for d in wallets[p].values())):
        label = ocs.wallet_label(provider)
        print(f"-- Wallet: {provider} ({label}) --")
        wallet_recorded = 0.0
        wallet_recompute = 0.0
        wallet_quota = 0.0
        wallet_has_quota = False
        for model, d in sorted(wallets[provider].items(), key=lambda x: -x[1]["recorded_cost"]):
            price_entry = ocs.price_for(prices, provider, model)
            recompute = ocs.bucket_cost(d["tokens"], price_entry)
            mult = ocs.quota_multiplier(price_entry)
            quota_cost = recompute * mult if recompute is not None else None
            wallet_recorded += d["recorded_cost"]
            if recompute is not None:
                wallet_recompute += recompute
            variants_str = ", ".join(f"{v}={n}" for v, n in sorted(d["variants"].items()))
            line = (f"  {model:26s} recorded={d['recorded_cost']:10.4f}  "
                    f"recomputed(base)={ocs.fmt_usd(recompute):>10s}")
            if provider == "opencode-go" and quota_cost is not None:
                wallet_has_quota = True
                wallet_quota += quota_cost
                line += f"  x{mult} quota={quota_cost:10.4f}"
            line += f"   variants: {variants_str}"
            print(line)
        print(f"  wallet recorded total    : ${wallet_recorded:.4f}")
        print(f"  wallet recomputed(base)  : ${wallet_recompute:.4f}")
        if wallet_has_quota:
            print(f"  wallet quota total       : ${wallet_quota:.4f}")
        print()


def cmd_oc_tree(args):
    con = ocs.connect_ro(args.db)
    sessions = ocs.load_sessions(con)
    if args.id:
        matches = [sessions[args.id]] if args.id in sessions else []
    else:
        matches = ocs.find_root_sessions_by_title(sessions, args.title)
    if not matches:
        print(f"No root session found matching {args.id or args.title!r}", file=sys.stderr)
        sys.exit(1)

    print("=" * 78)
    print("session-usage oc-tree - root vs full-subtree recorded cost (session.cost)")
    print("The opencode TUI's 'spent' figure for a session is ROOT-ONLY - it does not")
    print("include subagent sessions spawned under it (they are separate session rows")
    print("linked by parent_id). Both numbers below use opencode's OWN recorded cost.")
    print("=" * 78)
    for root in matches:
        ids = ocs.session_tree_ids(sessions, root["id"])
        tree_cost = sum(sessions[i]["cost"] for i in ids)
        print(f"root: {root['title']!r} ({root['id']})")
        print(f"  root cost   : ${root['cost']:.4f}")
        print(f"  tree cost   : ${tree_cost:.4f}  ({len(ids)} sessions incl. root)")
        print(f"  difference  : ${tree_cost - root['cost']:.4f}")
        for i in ids:
            if i == root["id"]:
                continue
            s = sessions[i]
            print(f"    child: {s['title']!r}  agent={s['agent']}  cost=${s['cost']:.4f}")
        print()


def cmd_reconcile(args):
    actual = _load_json_data(args.actual)
    prices = _load_json_data(args.prices)
    tz = actual.get("tz", args.tz)
    date = actual.get("date", args.date)
    if not date:
        print("ERROR: --date required (or an actual-numbers file with a top-level 'date')", file=sys.stderr)
        sys.exit(1)
    start_ms, end_ms = ocs.local_day_range_ms(date, tz)

    con = ocs.connect_ro(args.db)
    agg = _oc_collect_by_model(con, since_ms=start_ms, until_ms=end_ms)
    by_wallet = {}
    for (provider, model), d in agg.items():
        by_wallet.setdefault(provider, {})[model] = d

    print("=" * 78)
    print(f"session-usage reconcile - {date} (tz={tz}), our calc vs supplied actuals")
    print(f"Day boundary: {start_ms} .. {end_ms} (epoch ms)")
    print(f"Actuals file: {args.actual}")
    print("=" * 78)

    for wallet, models in actual.get("wallets", {}).items():
        print(f"-- Wallet: {wallet} ({ocs.wallet_label(wallet)}) --")
        wallet_ours = 0.0
        wallet_actual = 0.0
        our_models = by_wallet.get(wallet, {})
        for model, actual_usd in models.items():
            d = our_models.get(model)
            price_entry = ocs.price_for(prices, wallet, model)
            mult = ocs.quota_multiplier(price_entry)
            if d is None:
                print(f"  {model:20s} NO DATA in our archive for this day/wallet (actual=${actual_usd:.4f})")
                wallet_actual += actual_usd
                continue
            recompute = ocs.bucket_cost(d["tokens"], price_entry)
            ours = recompute * mult if recompute is not None else None
            wallet_actual += actual_usd
            if ours is None:
                print(f"  {model:20s} recorded=${d['recorded_cost']:.4f}  recomputed=no price  actual=${actual_usd:.4f}")
                continue
            wallet_ours += ours
            delta = ours - actual_usd
            ratio = (ours / actual_usd) if actual_usd else float("inf")
            flag = "OK" if abs(delta) <= 0.01 else "MISMATCH"
            mult_note = f" (base ${recompute:.4f} x{mult})" if mult != 1 else ""
            print(f"  {model:20s} ours=${ours:.4f}{mult_note}  actual=${actual_usd:.4f}  "
                  f"delta=${delta:+.4f}  ratio={ratio:.3f}  [{flag}]")
        wdelta = wallet_ours - wallet_actual
        print(f"  wallet total: ours=${wallet_ours:.4f}  actual=${wallet_actual:.4f}  delta=${wdelta:+.4f}")
        print()


# ------------------------------------------------------------------------ qwen (q-*)

def _collect_qwen_subagent_calls(qwen_dir, cutoff):
    """Returns (role_calls: dict[role] -> list of {"sums","turns","ts","file",
    "model"}, other_roles, meta_missing, parse_err, outside_window, no_timestamp).
    Windowed by CALL-FILE start time, same convention as archive.py's
    `roles` (not opencode's turn-level windowing) - qwen organizes usage as one
    file per call, same shape as the Claude Code leg."""
    role_calls = {}
    other_roles = {}
    meta_missing = 0
    parse_err = 0
    outside_window = 0
    no_timestamp = 0

    for jf, meta_path, _parent in qs.iter_subagent_calls(qwen_dir):
        if meta_path is None:
            meta_missing += 1
            continue
        try:
            with open(meta_path, encoding="utf-8") as mf:
                meta = json.load(mf)
        except Exception:
            parse_err += 1
            continue
        role = meta.get("agentType") or meta.get("subagentName") or "unknown"
        meta_model = (meta.get("persistedCliFlags") or {}).get("model")
        start_dt, turns, _telemetry, n_err = qs.parse_session(jf)
        parse_err += n_err
        if not turns:
            continue
        if start_dt is None:
            no_timestamp += 1
            continue
        if start_dt < cutoff:
            outside_window += 1
            continue
        sums = qs.zero_tokens()
        for t in turns:
            qs.add_tokens(sums, t["tokens"])
        role_calls.setdefault(role, []).append({
            "sums": sums, "turns": len(turns), "ts": start_dt, "file": jf,
            "model": meta_model, "description": meta.get("description"),
        })
        if role not in KNOWN_ROLES:
            other_roles[role] = other_roles.get(role, 0) + 1

    return role_calls, other_roles, meta_missing, parse_err, outside_window, no_timestamp


def _collect_qwen_root_totals(qwen_dir, cutoff):
    """(all_time, windowed, files_total, files_windowed, latency_windowed) for
    root sessions. `latency_windowed` is the flat list of ui_telemetry
    api_response samples from in-window root sessions ONLY - subagent call
    files carry no ui_telemetry lines at all (module docstring point 6)."""
    all_time = qs.zero_tokens()
    windowed = qs.zero_tokens()
    files_total = 0
    files_windowed = 0
    latency_windowed = []
    for _pname, _sid, path in qs.iter_root_sessions(qwen_dir):
        start_dt, turns, telemetry, _n_err = qs.parse_session(path)
        if not turns:
            continue
        sums = qs.zero_tokens()
        for t in turns:
            qs.add_tokens(sums, t["tokens"])
        if sum(sums.values()) == 0:
            continue
        files_total += 1
        for b in qs.TOKEN_BUCKETS:
            all_time[b] += sums[b]
        if start_dt is not None and start_dt >= cutoff:
            files_windowed += 1
            for b in qs.TOKEN_BUCKETS:
                windowed[b] += sums[b]
            latency_windowed.extend(telemetry)
    return all_time, windowed, files_total, files_windowed, latency_windowed


def _latency_stats(samples):
    durations = [s["duration_ms"] for s in samples if s.get("duration_ms") is not None]
    ttfts = [s["ttft_ms"] for s in samples if s.get("ttft_ms") is not None]
    non200 = [s for s in samples if s.get("status_code") not in (None, 200)]
    return durations, ttfts, non200


def cmd_q_roles(args):
    cutoff = qs.cutoff_dt(args.days)
    role_calls, other_roles, meta_missing, parse_err, outside_window, no_timestamp = (
        _collect_qwen_subagent_calls(args.qwen_dir, cutoff)
    )
    root_all_time, root_windowed, root_files_total, root_files_windowed, latency = (
        _collect_qwen_root_totals(args.qwen_dir, cutoff)
    )

    print("=" * 78)
    print(f"session-usage q-roles - qwen subagent calls, last {args.days} days (by call start time)")
    print("=" * 78)
    print(f"Qwen projects dir       : {args.qwen_dir}")
    print(f".meta.json missing      : {meta_missing}")
    print(f"Malformed JSON lines    : {parse_err}")
    print(f"Calls with no timestamp : {no_timestamp} (excluded)")
    print(f"Calls outside window    : {outside_window}")
    if other_roles:
        print(f"Other agentType values seen (not in KNOWN_ROLES): {other_roles}")
    print()

    roles_in_order = list(KNOWN_ROLES) + [r for r in role_calls if r not in KNOWN_ROLES]
    hdr = f"  {'role':28s} {'n_calls':>8s} " + " ".join(f"{qs.BUCKET_LABEL[b]+' sum':>14s}" for b in qs.TOKEN_BUCKETS)
    print("-- Per role: sum / median / p90 per bucket --")
    print(hdr)
    for role in roles_in_order:
        calls = role_calls.get(role, [])
        n = len(calls)
        if n == 0:
            print(f"  {role:28s} {0:8d}")
            continue
        print(f"  {role:28s} {n:8d} " + " ".join(
            f"{qs.fmt_int(sum(c['sums'][b] for c in calls)):>14s}" for b in qs.TOKEN_BUCKETS))
        for b in qs.TOKEN_BUCKETS:
            vals = [c["sums"][b] for c in calls]
            med = statistics.median(vals)
            p90 = statistics.quantiles(vals, n=10)[8] if n >= 2 else vals[0]
            print(f"    {qs.BUCKET_LABEL[b]:12s} median={qs.fmt_int(int(med)):>12s}  p90={qs.fmt_int(int(p90)):>12s}")
        models_seen = sorted({c["model"] for c in calls if c["model"]})
        if models_seen:
            print(f"    models: {', '.join(models_seen)}")
    print()

    print("-- Root / orchestrator sessions --")
    print(f"Root session files (usable): {root_files_total} all-time, {root_files_windowed} in window")
    print(f"  all-time : " + "  ".join(f"{qs.BUCKET_LABEL[b]}={qs.fmt_int(root_all_time[b])}" for b in qs.TOKEN_BUCKETS))
    print(f"  windowed : " + "  ".join(f"{qs.BUCKET_LABEL[b]}={qs.fmt_int(root_windowed[b])}" for b in qs.TOKEN_BUCKETS))
    print()

    durations, ttfts, non200 = _latency_stats(latency)
    print("-- API latency (ui_telemetry, root sessions only - see qwen_store.py docstring #6) --")
    print(f"  samples: {len(latency)} (NOT the same count as assistant turns above - retries/")
    print(f"           tool-call rounds fire their own api_response event; never used for tokens)")
    if durations:
        print(f"  duration_ms  median={statistics.median(durations):8.0f}  "
              f"p90={statistics.quantiles(durations, n=10)[8] if len(durations) >= 2 else durations[0]:8.0f}")
    if ttfts:
        print(f"  ttft_ms      median={statistics.median(ttfts):8.0f}  "
              f"p90={statistics.quantiles(ttfts, n=10)[8] if len(ttfts) >= 2 else ttfts[0]:8.0f}")
    print(f"  non-200 status_code: {len(non200)} / {len(latency)}")
    print()
    _print_footer()


def _qwen_collect_all_turns(qwen_dir, cutoff):
    """Every turn (root + subagent), tagged with role ('root/orchestrator' for
    root sessions) and model id, windowed by call/session-file start time (same
    convention as q-roles - see _collect_qwen_subagent_calls)."""
    out = []
    for _pname, _sid, path in qs.iter_root_sessions(qwen_dir):
        start_dt, turns, _telemetry, _n = qs.parse_session(path)
        if not turns or start_dt is None or start_dt < cutoff:
            continue
        for t in turns:
            out.append({"role": "root/orchestrator", "model": t["model"]})
            out[-1]["tokens"] = t["tokens"]
    for jf, meta_path, _parent in qs.iter_subagent_calls(qwen_dir):
        if meta_path is None:
            continue
        try:
            with open(meta_path, encoding="utf-8") as mf:
                meta = json.load(mf)
        except Exception:
            continue
        role = meta.get("agentType") or meta.get("subagentName") or "unknown"
        meta_model = (meta.get("persistedCliFlags") or {}).get("model")
        start_dt, turns, _telemetry, _n = qs.parse_session(jf)
        if not turns or start_dt is None or start_dt < cutoff:
            continue
        for t in turns:
            out.append({"role": role, "model": t["model"] or meta_model, "tokens": t["tokens"]})
    return out


def cmd_q_money(args):
    prices = _load_json_data(args.prices)
    cutoff = qs.cutoff_dt(args.days)
    turns = _qwen_collect_all_turns(args.qwen_dir, cutoff)

    by_wallet_model = {}  # wallet -> price_key -> {"tokens","n","efforts"}
    by_role = {}          # role -> {"tokens","n","cost","unpriced_n"}

    for t in turns:
        wallet, slug = qs.parse_model_id(t["model"])
        price_key, price_entry, effort = qs.resolve_price(prices, wallet, slug)

        wm = by_wallet_model.setdefault(wallet, {}).setdefault(
            price_key, {"tokens": qs.zero_tokens(), "n": 0, "efforts": set()})
        qs.add_tokens(wm["tokens"], t["tokens"])
        wm["n"] += 1
        if effort:
            wm["efforts"].add(effort)

        rd = by_role.setdefault(t["role"], {"tokens": qs.zero_tokens(), "n": 0, "cost": 0.0, "unpriced_n": 0})
        qs.add_tokens(rd["tokens"], t["tokens"])
        rd["n"] += 1
        turn_cost = qs.bucket_cost(t["tokens"], price_entry)
        if turn_cost is None:
            rd["unpriced_n"] += 1
        else:
            rd["cost"] += turn_cost

    print("=" * 78)
    print(f"session-usage q-money - qwen sessions, last {args.days} days, priced from {args.prices}")
    print("NOTE: qwen bakes the wallet into the model id itself (ds- = DeepSeek direct API,")
    print("real invoice; go- = opencode-go subscription quota, NOT a dollar charge). The two")
    print("are never summed. 'thoughts' tokens are already inside 'output' for this leg (NOT")
    print("added separately, unlike opencode's reasoning bucket) - see qwen_store.py docstring.")
    print("=" * 78)

    def wallet_total_cost(wallet):
        total = 0.0
        for price_key, d in by_wallet_model[wallet].items():
            c = qs.bucket_cost(d["tokens"], prices.get(price_key))
            if c is not None:
                total += c
        return total

    for wallet in sorted(by_wallet_model, key=lambda w: -wallet_total_cost(w)):
        print(f"-- Wallet: {wallet} --")
        wallet_cost = 0.0
        wallet_quota = 0.0
        has_quota = False
        for price_key, d in sorted(by_wallet_model[wallet].items(), key=lambda x: -(qs.bucket_cost(x[1]["tokens"], prices.get(x[0])) or 0)):
            price_entry = prices.get(price_key)
            cost = qs.bucket_cost(d["tokens"], price_entry)
            mult = qs.quota_multiplier(price_entry)
            bstr = "  ".join(f"{qs.BUCKET_LABEL[b]}={qs.fmt_int(d['tokens'][b])}" for b in qs.TOKEN_BUCKETS)
            line = f"  {price_key:32s} n={d['n']:4d}  {bstr}  cost={qs.fmt_usd(cost)}"
            if wallet == "opencode-go" and cost is not None:
                has_quota = True
                quota_cost = cost * mult
                wallet_quota += quota_cost
                line += f"  x{mult} quota={quota_cost:.4f}"
            if d["efforts"]:
                line += f"  efforts={sorted(d['efforts'])}"
            print(line)
            if cost is not None:
                wallet_cost += cost
        print(f"  wallet recomputed total : ${wallet_cost:.4f}")
        if has_quota:
            print(f"  wallet quota total      : ${wallet_quota:.4f}  "
                  f"(Qwen's share of opencode-go ONLY - opencode itself also draws on this")
            print(f"                             same subscription; see qwen_store.py docstring #5")
            print(f"                             and card session-usage-quota-window-and-price-dating)")
        print()

    print("-- By role (recomputed cost, all wallets combined - never summed with wallet $ above) --")
    for role, d in sorted(by_role.items(), key=lambda x: -x[1]["cost"]):
        note = f"  ({d['unpriced_n']} turns unpriced)" if d["unpriced_n"] else ""
        print(f"  {role:28s} n={d['n']:5d}  cost=${d['cost']:.4f}{note}")
    print()


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
                              "DIFFERENT format read by the oc-roles/oc-money/oc-tree/reconcile "
                              "subcommands instead (--db) - see README.")

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

    oc_common = argparse.ArgumentParser(add_help=False)
    oc_common.add_argument("--days", type=int, default=30, help="Lookback window in days (default 30)")
    oc_common.add_argument("--db", default=ocs.DEFAULT_DB_PATH,
                            help="Path to opencode.db (default ~/.local/share/opencode/opencode.db). "
                                 "Opened READ-ONLY - never touches the -wal/-shm files next to it.")

    p_oc_roles = sub.add_parser("oc-roles", parents=[oc_common],
                                 help="Per-agent usage stats from the opencode session database")
    p_oc_roles.set_defaults(func=cmd_oc_roles)

    p_oc_money = sub.add_parser("oc-money", parents=[oc_common],
                                 help="Recorded vs recomputed cost per model, by wallet (opencode DB)")
    p_oc_money.add_argument("--prices", default=os.path.join(HERE, "prices.json"),
                             help="Price list JSON (default prices.json next to this script).")
    p_oc_money.set_defaults(func=cmd_oc_money)

    p_oc_tree = sub.add_parser("oc-tree",
                                help="Root vs full-subtree recorded cost for one opencode session")
    p_oc_tree.add_argument("--db", default=ocs.DEFAULT_DB_PATH, help="Path to opencode.db")
    p_oc_tree.add_argument("--id", help="Exact session id (ses_...)")
    p_oc_tree.add_argument("title", nargs="?", help="Case-insensitive substring of a ROOT session title")
    p_oc_tree.set_defaults(func=cmd_oc_tree)

    p_reconcile = sub.add_parser(
        "reconcile",
        help="Our recomputed cost for one calendar day vs actual numbers you supply (JSON file)"
    )
    p_reconcile.add_argument("--db", default=ocs.DEFAULT_DB_PATH, help="Path to opencode.db")
    p_reconcile.add_argument("--prices", default=os.path.join(HERE, "prices.json"),
                              help="Price list JSON (default prices.json next to this script).")
    p_reconcile.add_argument("--actual", required=True,
                              help="JSON file with actual dashboard numbers to reconcile against "
                                   "(see actuals.example.json). NOT hardcoded - this is data, like "
                                   "prices.json.")
    p_reconcile.add_argument("--date", help="YYYY-MM-DD; overridden by a 'date' key in --actual if present")
    p_reconcile.add_argument("--tz", choices=["local", "utc"], default="local",
                              help="Calendar-day boundary timezone (default local - see README, this "
                                   "materially changes which calls land in a given day's bucket). "
                                   "Overridden by a 'tz' key in --actual if present.")
    p_reconcile.set_defaults(func=cmd_reconcile)

    q_common = argparse.ArgumentParser(add_help=False)
    q_common.add_argument("--days", type=int, default=30, help="Lookback window in days (default 30)")
    q_common.add_argument("--qwen-dir", default=qs.DEFAULT_QWEN_DIR,
                           help="Root of the Qwen Code per-project session folders "
                                "(default ~/.qwen/projects). Read-only.")

    p_q_roles = sub.add_parser("q-roles", parents=[q_common],
                                help="Per-subagent-role usage stats from the Qwen Code session archive")
    p_q_roles.set_defaults(func=cmd_q_roles)

    p_q_money = sub.add_parser("q-money", parents=[q_common],
                                help="Recomputed cost per model (by wallet) and per role, Qwen Code archive")
    p_q_money.add_argument("--prices", default=os.path.join(HERE, "prices.json"),
                            help="Price list JSON (default prices.json next to this script).")
    p_q_money.set_defaults(func=cmd_q_money)

    return ap


def main():
    args = build_parser().parse_args()
    args.func(args)


if __name__ == "__main__":
    main()
