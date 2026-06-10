import os as _os, sys as _sys
_sys.path.insert(0, _os.path.dirname(_os.path.abspath(__file__)))
DATA = _os.environ.get("EVAL_DATA", _os.path.join(_os.path.dirname(_os.path.abspath(__file__)), "data"))
_os.makedirs(DATA, exist_ok=True)

#!/usr/bin/env python3
"""LoCoMo lazy-vs-full: lexical / semantic / hybrid, with a SEMANTIC DIM SWEEP.

Does vector/hybrid retrieval close the ~14% recall gap digest-only discovery showed
lexically? And is the weak semantic leg an over-truncation artifact (embedder is dim
2560; the design assumed 1024->256)? Sweeps dim in {256,1024,2560}. Lexical = shipped
FTS5; semantic = cosine over qwen3-embed (MRL slice); hybrid = RRF (HybridMerge).
Subsampled to bound embedder load.
"""
import json, re, sqlite3, numpy as np
from collections import defaultdict
import mcp_embed

DATA = json.load(open(_os.path.join(DATA, "locomo10.json"), encoding="utf-8"))
WORD = re.compile(r"[^\W\d_]+|\d+", re.UNICODE)
EV = re.compile(r"D(\d+):")
N_CONV, Q_PER_CONV, EMB_CAP, RRF_K = 10, 100000, 6000, 60
DIMS = [256, 1024, 2560]

def build_match(q):
    toks = [m.group(0).lower() + "*" for m in WORD.finditer(q)]
    return " OR ".join(toks) if toks else None

def fts(docs):
    db = sqlite3.connect(":memory:")
    db.execute("CREATE VIRTUAL TABLE d USING fts5(sid UNINDEXED, body, tokenize='unicode61')")
    db.executemany("INSERT INTO d(sid,body) VALUES (?,?)", [(str(k), v) for k, v in docs.items() if v])
    return db

def lex_rank(db, q, k=5):
    m = build_match(q)
    if not m: return []
    return [int(r[0]) for r in db.execute("SELECT sid FROM d WHERE d MATCH ? ORDER BY rank LIMIT ?", (m, k)).fetchall()]

def sem_rank(qv, ids, mat, k=5):
    return [ids[i] for i in np.argsort(-(mat @ qv))[:k]]

def rrf(*rankings, k=5):
    score = defaultdict(float)
    for r in rankings:
        for rank, sid in enumerate(r):
            score[sid] += 1.0 / (RRF_K + rank)
    return [s for s, _ in sorted(score.items(), key=lambda x: -x[1])][:k]

def norm(v):
    a = np.asarray(v, dtype=np.float32); n = np.linalg.norm(a)
    return a / n if n else a

def main():
    convs, to_embed = [], set()
    for item in DATA[:N_CONV]:
        conv = item["conversation"]
        full = {int(re.fullmatch(r"session_(\d+)", k).group(1)):
                "\n".join(f"{t.get('speaker','')}: {t.get('text','')}" for t in conv[k])
                for k in conv if re.fullmatch(r"session_(\d+)", k)}
        dig = {int(re.fullmatch(r"session_(\d+)_summary", k).group(1)): v
               for k, v in (item.get("session_summary") or {}).items()
               if re.fullmatch(r"session_(\d+)_summary", k) and isinstance(v, str)}
        qs = []
        for qa in item["qa"]:
            rel = {int(EV.match(e).group(1)) for e in (qa.get("evidence") or []) if EV.match(e)} & set(full)
            if rel: qs.append((str(qa["question"]), rel))
            if len(qs) >= Q_PER_CONV: break
        convs.append((full, dig, qs))
        for v in full.values(): to_embed.add(v[:EMB_CAP])
        for v in dig.values(): to_embed.add(v[:EMB_CAP])
        for q, _ in qs: to_embed.add(q)

    n_q = sum(len(c[2]) for c in convs)
    print(f"subsample: {N_CONV} convs, {n_q} queries, {len(to_embed)} texts to embed")
    texts = list(to_embed)
    raw = {t: v for t, v in zip(texts, mcp_embed.embed(texts, dim=max(DIMS), batch=32))}  # full vectors

    # lexical once (dim-independent)
    lex = {"full": [0, 0.0], "digest": [0, 0.0]}
    lex_ranks_cache = []
    for full, dig, qs in convs:
        fdb, ddb = fts(full), fts(dig)
        per = []
        for q, rel in qs:
            rf, rd = lex_rank(fdb, q), lex_rank(ddb, q)
            per.append((rf, rd, rel))
            for r, s in ((rf, "full"), (rd, "digest")):
                pos = next((i+1 for i, sid in enumerate(r) if sid in rel), 0)
                if 0 < pos <= 5: lex[s][0] += 1
                if pos: lex[s][1] += 1.0/pos
        lex_ranks_cache.append(per)

    rows = [("lexical", None, lex["full"][0]/n_q, lex["digest"][0]/n_q,
             lex["full"][1]/n_q, lex["digest"][1]/n_q)]

    for dim in DIMS:
        emap = {t: norm(v[:dim]) for t, v in raw.items()}
        sem = {"full": [0, 0.0], "digest": [0, 0.0]}
        hyb = {"full": [0, 0.0], "digest": [0, 0.0]}
        for (full, dig, qs), per in zip(convs, lex_ranks_cache):
            fids = sorted(full); fmat = np.stack([emap[full[i][:EMB_CAP]] for i in fids])
            dids = sorted(dig); dmat = np.stack([emap[dig[i][:EMB_CAP]] for i in dids]) if dids else None
            for (q, rel), (rf, rd, _) in zip(qs, per):
                qv = emap[q]
                sf = sem_rank(qv, fids, fmat); sd = sem_rank(qv, dids, dmat) if dids else []
                hf = rrf(rf, sf); hd = rrf(rd, sd)
                for r, bucket, s in ((sf, sem, "full"), (sd, sem, "digest"), (hf, hyb, "full"), (hd, hyb, "digest")):
                    pos = next((i+1 for i, sid in enumerate(r) if sid in rel), 0)
                    if 0 < pos <= 5: bucket[s][0] += 1
                    if pos: bucket[s][1] += 1.0/pos
        rows.append((f"semantic", dim, sem["full"][0]/n_q, sem["digest"][0]/n_q, sem["full"][1]/n_q, sem["digest"][1]/n_q))
        rows.append((f"hybrid", dim, hyb["full"][0]/n_q, hyb["digest"][0]/n_q, hyb["full"][1]/n_q, hyb["digest"][1]/n_q))

    print(f"\n=== LoCoMo lazy-vs-full, dim sweep (n={n_q} queries) ===")
    print(f"{'method':9}{'dim':>6}{'full@5':>9}{'digest@5':>10}{'gap':>8}{'rel%':>7}{'full_mrr':>10}{'dig_mrr':>9}")
    out = []
    for name, dim, f5, d5, fm, dm in rows:
        gap = f5 - d5; rel = gap/f5*100 if f5 else 0
        ds = "-" if dim is None else str(dim)
        print(f"{name:9}{ds:>6}{f5:9.3f}{d5:10.3f}{gap:+8.3f}{rel:+7.1f}{fm:10.3f}{dm:9.3f}")
        out.append(dict(method=name, dim=dim, full5=f5, digest5=d5, gap5=gap, rel=rel, full_mrr=fm, digest_mrr=dm))
    json.dump(out, open(_os.path.join(DATA, "eval_locomo_sem_result.json"), "w"), indent=1)

if __name__ == "__main__":
    main()
