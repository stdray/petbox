import os as _os, sys as _sys
_sys.path.insert(0, _os.path.dirname(_os.path.abspath(__file__)))
DATA = _os.environ.get("EVAL_DATA", _os.path.join(_os.path.dirname(_os.path.abspath(__file__)), "data"))
_os.makedirs(DATA, exist_ok=True)

#!/usr/bin/env python3
"""Hydration-width sweep: how wide must lazy discovery be to stop losing recall?

The lazy two-stage pipeline: stage-1 queries the DIGEST index for top-K candidate
sessions, stage-2 HYDRATES those K (loads full text) and reads/retrieves over them. The
binding constraint is stage-1: is the ground-truth session in the digest's top-K? Once
hydrated, full-content retrieval over a small K is easy. So pipeline recall is gated by
digest discovery recall@K. This sweeps K and reports digest recall@K (generic & facts,
hybrid) against the full-content ceiling — telling us how many sessions to hydrate to
erase the residual lazy gap. Reuses cached digests + embeddings (no DeepSeek/embedder).
"""
import json, re, sqlite3, numpy as np
from collections import defaultdict
import mcp_embed

DATA_J = json.load(open(_os.path.join(DATA, "locomo10.json"), encoding="utf-8"))
FACTS = json.load(open(_os.path.join(DATA, "digest_facts.json"), encoding="utf-8"))
WORD = re.compile(r"[^\W\d_]+|\d+", re.UNICODE)
EV = re.compile(r"D(\d+):")
N_CONV, Q_PER_CONV, EMB_CAP, RRF_K, DIM = 3, 100000, 6000, 60, 1024
KS = [1, 3, 5, 10, 20, 9999]

def build_match(q):
    toks = [m.group(0).lower() + "*" for m in WORD.finditer(q)]
    return " OR ".join(toks) if toks else None

def fts(docs):
    db = sqlite3.connect(":memory:")
    db.execute("CREATE VIRTUAL TABLE d USING fts5(sid UNINDEXED, body, tokenize='unicode61')")
    db.executemany("INSERT INTO d(sid,body) VALUES (?,?)", [(str(k), v) for k, v in docs.items() if v])
    return db

def lex_rank(db, q, k):
    m = build_match(q)
    return [int(r[0]) for r in db.execute("SELECT sid FROM d WHERE d MATCH ? ORDER BY rank LIMIT ?", (m, k)).fetchall()] if m else []

def sem_rank(qv, ids, mat, k):
    return [ids[i] for i in np.argsort(-(mat @ qv))[:k]]

def rrf(a, b, k):
    sc = defaultdict(float)
    for r in (a, b):
        for rank, sid in enumerate(r): sc[sid] += 1.0 / (RRF_K + rank)
    return [s for s, _ in sorted(sc.items(), key=lambda x: -x[1])][:k]

def norm(v):
    a = np.asarray(v, dtype=np.float32); n = np.linalg.norm(a); return a / n if n else a

def main():
    convs, to_embed = [], set()
    for item in DATA_J[:N_CONV]:
        sid = item["sample_id"]; conv = item["conversation"]
        full = {int(re.fullmatch(r"session_(\d+)", k).group(1)):
                "\n".join(f"{t.get('speaker','')}: {t.get('text','')}" for t in conv[k])
                for k in conv if re.fullmatch(r"session_(\d+)", k)}
        gen = {int(re.fullmatch(r"session_(\d+)_summary", k).group(1)): v
               for k, v in (item.get("session_summary") or {}).items()
               if re.fullmatch(r"session_(\d+)_summary", k) and isinstance(v, str)}
        fac = {n: FACTS[f"{sid}:{n}"] for n in full if f"{sid}:{n}" in FACTS}
        qs = []
        for qa in item["qa"]:
            rel = {int(EV.match(e).group(1)) for e in (qa.get("evidence") or []) if EV.match(e)} & set(full)
            if rel: qs.append((str(qa["question"]), rel))
            if len(qs) >= Q_PER_CONV: break
        convs.append((full, gen, fac, qs))
        for d in (full, gen, fac):
            for v in d.values(): to_embed.add(v[:EMB_CAP])
        for q, _ in qs: to_embed.add(q)

    n_q = sum(len(c[3]) for c in convs)
    avg_sessions = sum(len(c[0]) for c in convs) / len(convs)
    emap = {t: norm(v) for t, v in zip(list(to_embed), mcp_embed.embed(list(to_embed), dim=DIM, batch=32))}

    # recall@K via hybrid for each strategy
    STRATS = ["full", "generic", "facts"]
    hits = {s: {K: 0 for K in KS} for s in STRATS}
    for full, gen, fac, qs in convs:
        docs = {"full": full, "generic": gen, "facts": fac}
        dbs = {s: fts(docs[s]) for s in STRATS}
        ids = {s: sorted(docs[s]) for s in STRATS}
        mats = {s: (np.stack([emap[docs[s][i][:EMB_CAP]] for i in ids[s]]) if ids[s] else None) for s in STRATS}
        for q, rel in qs:
            qv = emap[q]
            for s in STRATS:
                if not ids[s]: continue
                for K in KS:
                    r = rrf(lex_rank(dbs[s], q, K), sem_rank(qv, ids[s], mats[s], K), K)
                    if any(sid in rel for sid in r): hits[s][K] += 1

    print(f"=== Hydration-width sweep (hybrid, dim{DIM}, n={n_q}, avg {avg_sessions:.0f} sessions/conv) ===")
    print("digest discovery recall@K — fraction of queries whose ground-truth session is in top-K")
    print(f"{'K':>6}" + "".join(f"{s:>11}" for s in STRATS))
    out = []
    for K in KS:
        klabel = "all" if K >= 9999 else str(K)
        row = {s: hits[s][K] / n_q for s in STRATS}
        print(f"{klabel:>6}" + "".join(f"{row[s]:11.3f}" for s in STRATS))
        out.append(dict(K=klabel, **row))
    full5 = hits["full"][5] / n_q
    print(f"\nfull-content ceiling = full recall@5 = {full5:.3f}")
    for s in ("generic", "facts"):
        need = next((("all" if K >= 9999 else K) for K in KS if hits[s][K] / n_q >= full5), ">all")
        print(f"  {s} digest reaches full@5 ({full5:.3f}) at hydration width K = {need}")
    json.dump(out, open(_os.path.join(DATA, "hydration_result.json"), "w"), indent=1)

if __name__ == "__main__":
    main()
