import os as _os, sys as _sys
_sys.path.insert(0, _os.path.dirname(_os.path.abspath(__file__)))
DATA = _os.environ.get("EVAL_DATA", _os.path.join(_os.path.dirname(_os.path.abspath(__file__)), "data"))
_os.makedirs(DATA, exist_ok=True)

#!/usr/bin/env python3
"""Lazy-vs-full lexical eval on LoCoMo (stolen benchmark, no confound).

LoCoMo = 10 multi-session conversations with human QA whose `evidence` dia_ids
(D{session}:{turn}) pin the ground-truth session(s). Retrieval scope is within a
conversation: find the session that answers the question. Two strategies, same FTS5
unicode61 + prefix-OR MATCH as the shipped Class-A index:
  full   = index each session's full text
  digest = index LoCoMo's own session_summary (an independent digest — no shared-vocab
           confound with the questions, unlike the transcript run)
Metric: recall@k / MRR where a hit = any ground-truth session in top-k.
"""
import json, re, sqlite3
from collections import Counter, defaultdict

DATA = json.load(open(_os.path.join(DATA, "locomo10.json"), encoding="utf-8"))
WORD = re.compile(r"[^\W\d_]+|\d+", re.UNICODE)
EV = re.compile(r"D(\d+):")

def build_match(q):
    toks = [m.group(0).lower() + "*" for m in WORD.finditer(q)]
    return " OR ".join(toks) if toks else None

def fts(docs):  # docs: {session_no: text}
    db = sqlite3.connect(":memory:")
    db.execute("CREATE VIRTUAL TABLE d USING fts5(sid UNINDEXED, body, tokenize='unicode61')")
    db.executemany("INSERT INTO d(sid, body) VALUES (?,?)", [(str(k), v) for k, v in docs.items()])
    return db

def ranked(db, q, k=5):
    m = build_match(q)
    if not m: return []
    return [int(r[0]) for r in db.execute(
        "SELECT sid FROM d WHERE d MATCH ? ORDER BY rank LIMIT ?", (m, k)).fetchall()]

def sessions_of(conv):
    full, nums = {}, []
    for key in conv:
        m = re.fullmatch(r"session_(\d+)", key)
        if not m: continue
        n = int(m.group(1)); nums.append(n)
        full[n] = "\n".join(f"{t.get('speaker','')}: {t.get('text','')}" for t in conv[key])
    return full

def digests_of(item):
    out = {}
    for key, txt in (item.get("session_summary") or {}).items():
        m = re.fullmatch(r"session_(\d+)_summary", key)
        if m and isinstance(txt, str): out[int(m.group(1))] = txt
    return out

def evaluate(field_docs_per_conv):
    """field_docs_per_conv: list of (docs{n:text}, queries[(q, relevant_set)])"""
    h1 = h3 = h5 = n = 0; rr = 0.0
    bycat = defaultdict(lambda: [0, 0])  # cat -> [hit@5, total]
    for docs, queries, cats in field_docs_per_conv:
        db = fts({k: v for k, v in docs.items() if v})
        for (q, rel), cat in zip(queries, cats):
            n += 1
            r = ranked(db, q, 5)
            pos = next((i + 1 for i, s in enumerate(r) if s in rel), 0)
            if pos == 1: h1 += 1
            if 0 < pos <= 3: h3 += 1
            if 0 < pos <= 5: h5 += 1
            if pos: rr += 1.0 / pos
            bycat[cat][1] += 1
            if 0 < pos <= 5: bycat[cat][0] += 1
    return dict(q=n, r1=h1/n, r3=h3/n, r5=h5/n, mrr=rr/n), bycat

def main():
    full_set, dig_set = [], []
    total_qa = used = skipped = 0
    sess_total = sum_total = 0
    for item in DATA:
        conv = item["conversation"]
        full = sessions_of(conv)
        dig = digests_of(item)
        sess_total += len(full); sum_total += len(dig)
        qf, cf, qd = [], [], []  # queries for full / cats / queries for digest (same q, same rel)
        for qa in item["qa"]:
            total_qa += 1
            ev = qa.get("evidence") or []
            rel = {int(EV.match(e).group(1)) for e in ev if EV.match(e)}
            rel &= set(full)  # ground-truth sessions that exist
            if not rel:
                skipped += 1; continue
            used += 1
            q = str(qa.get("question", ""))
            qf.append((q, rel)); qd.append((q, rel)); cf.append(qa.get("category"))
        full_set.append((full, qf, cf))
        dig_set.append((dig, qd, cf))

    print(f"conversations={len(DATA)} sessions={sess_total} summaries={sum_total} "
          f"(coverage {sum_total/sess_total:.0%})")
    print(f"qa total={total_qa} evaluable={used} skipped(no/abstain evidence)={skipped}\n")

    full_m, full_cat = evaluate(full_set)
    dig_m, dig_cat = evaluate(dig_set)

    print("=== LoCoMo lazy-vs-full (lexical, FTS5 unicode61, prefix-OR; per-conversation scope) ===")
    print(f"{'strategy':10}{'recall@1':>10}{'recall@3':>10}{'recall@5':>10}{'mrr':>8}")
    for name, m in [("full", full_m), ("digest", dig_m)]:
        print(f"{name:10}{m['r1']:10.3f}{m['r3']:10.3f}{m['r5']:10.3f}{m['mrr']:8.3f}")
    gap = full_m["r5"] - dig_m["r5"]
    rel = gap / full_m["r5"] * 100 if full_m["r5"] else 0
    print(f"\nLAZY LOSS (full-digest): recall@5 {gap:+.3f}  ({rel:+.1f}% relative)  "
          f"mrr {full_m['mrr']-dig_m['mrr']:+.3f}")

    print("\nrecall@5 by QA category (1=multi-hop 2=temporal 3=open 4=single-hop 5=adversarial):")
    print(f"{'cat':>4}{'full@5':>9}{'digest@5':>10}{'n':>6}")
    for c in sorted(k for k in full_cat if k is not None):
        fh, ft = full_cat[c]; dh, dt = dig_cat[c]
        print(f"{c:>4}{fh/ft:9.3f}{dh/dt:10.3f}{ft:6}")
    json.dump(dict(full=full_m, digest=dig_m), open(_os.path.join(DATA, "eval_locomo_result.json"), "w"), indent=1)

if __name__ == "__main__":
    main()
