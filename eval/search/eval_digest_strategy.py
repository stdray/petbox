import os as _os, sys as _sys
_sys.path.insert(0, _os.path.dirname(_os.path.abspath(__file__)))
DATA = _os.environ.get("EVAL_DATA", _os.path.join(_os.path.dirname(_os.path.abspath(__file__)), "data"))
_os.makedirs(DATA, exist_ok=True)

#!/usr/bin/env python3
"""Digest-strategy comparison on LoCoMo: does a retrieval-tuned digest close the gap?

Tests the standing conclusion "fix the digest, not the retriever" by comparing THREE
document strategies for lazy discovery, across lexical / semantic / hybrid:
  full           = full session text (ceiling)
  digest-generic = LoCoMo's narrative session_summary (current lazy)
  digest-facts   = DeepSeek-generated entities+facts+decisions digest (retrieval-tuned,
                   m-1a5c37fe option 4) -- dense, keyword-rich, no narrative fluff
If digest-facts lifts recall toward `full`, the digest strategy IS the lever. If it
plateaus with digest-generic, the loss is irreducibly information-theoretic.
"""
import json, re, sqlite3, time, urllib.request, numpy as np
from collections import defaultdict
import mcp_embed

DATA_FILE = _os.path.join(DATA, "locomo10.json")
FACTS_CACHE = _os.path.join(DATA, "digest_facts.json")
DS_KEY = _os.environ["DEEPSEEK_API_KEY"]
WORD = re.compile(r"[^\W\d_]+|\d+", re.UNICODE)
EV = re.compile(r"D(\d+):")
N_CONV, Q_PER_CONV, EMB_CAP, RRF_K, DIM = 3, 100000, 6000, 60, 1024

DATA_J = json.load(open(DATA_FILE, encoding="utf-8"))
_facts = json.load(open(FACTS_CACHE, encoding="utf-8")) if _os.path.exists(FACTS_CACHE) else {}

def deepseek(prompt):
    body = json.dumps({"model": "deepseek-chat", "temperature": 0.1, "max_tokens": 320,
        "messages": [{"role": "user", "content": prompt}]}).encode()
    req = urllib.request.Request("https://api.deepseek.com/chat/completions", data=body,
        headers={"Authorization": f"Bearer {DS_KEY}", "Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=120) as r:
        return json.loads(r.read())["choices"][0]["message"]["content"].strip()

FACTS_PROMPT = ("Extract a compact, RETRIEVAL-ORIENTED digest of this conversation session. "
    "List densely: key entities (people, places, things), concrete facts and events with "
    "their dates, and decisions/plans/preferences stated. Keyword-rich, no narrative fluff, "
    "no preamble. Under 110 words.\n\nSESSION:\n")

def facts_digest(sid, n, text):
    k = f"{sid}:{n}"
    if k not in _facts:
        _facts[k] = deepseek(FACTS_PROMPT + text[:3500])
        json.dump(_facts, open(FACTS_CACHE, "w"), ensure_ascii=False)
        print(f"  facts {k} ({len(_facts[k])} chars)", flush=True)
    return _facts[k]

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
    return [int(r[0]) for r in db.execute("SELECT sid FROM d WHERE d MATCH ? ORDER BY rank LIMIT ?", (m, k)).fetchall()] if m else []

def sem_rank(qv, ids, mat, k=5):
    return [ids[i] for i in np.argsort(-(mat @ qv))[:k]]

def rrf(*rs, k=5):
    sc = defaultdict(float)
    for r in rs:
        for rank, sid in enumerate(r): sc[sid] += 1.0 / (RRF_K + rank)
    return [s for s, _ in sorted(sc.items(), key=lambda x: -x[1])][:k]

def norm(v):
    a = np.asarray(v, dtype=np.float32); n = np.linalg.norm(a); return a / n if n else a

def main():
    convs, to_embed = [], set()
    print("generating facts-digests via DeepSeek (cached)...")
    for item in DATA_J[:N_CONV]:
        sid = item["sample_id"]; conv = item["conversation"]
        full = {int(re.fullmatch(r"session_(\d+)", k).group(1)):
                "\n".join(f"{t.get('speaker','')}: {t.get('text','')}" for t in conv[k])
                for k in conv if re.fullmatch(r"session_(\d+)", k)}
        gen = {int(re.fullmatch(r"session_(\d+)_summary", k).group(1)): v
               for k, v in (item.get("session_summary") or {}).items()
               if re.fullmatch(r"session_(\d+)_summary", k) and isinstance(v, str)}
        fac = {n: facts_digest(sid, n, full[n]) for n in full}
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
    print(f"\nembedding {len(to_embed)} texts (dim {DIM})...")
    raw = mcp_embed.embed(list(to_embed), dim=DIM, batch=32)
    emap = {t: norm(v) for t, v in zip(list(to_embed), raw)}

    STRATS = ["full", "generic", "facts"]
    METHODS = ["lexical", "semantic", "hybrid"]
    agg = {m: {s: [0, 0.0] for s in STRATS} for m in METHODS}
    for full, gen, fac, qs in convs:
        docs = {"full": full, "generic": gen, "facts": fac}
        dbs = {s: fts(docs[s]) for s in STRATS}
        ids = {s: sorted(docs[s]) for s in STRATS}
        mats = {s: (np.stack([emap[docs[s][i][:EMB_CAP]] for i in ids[s]]) if ids[s] else None) for s in STRATS}
        for q, rel in qs:
            qv = emap[q]
            for s in STRATS:
                lr = lex_rank(dbs[s], q)
                sr = sem_rank(qv, ids[s], mats[s]) if ids[s] else []
                hr = rrf(lr, sr)
                for method, r in (("lexical", lr), ("semantic", sr), ("hybrid", hr)):
                    pos = next((i + 1 for i, sid in enumerate(r) if sid in rel), 0)
                    if 0 < pos <= 5: agg[method][s][0] += 1
                    if pos: agg[method][s][1] += 1.0 / pos

    print(f"\n=== Digest-strategy comparison on LoCoMo (n={n_q}, dim={DIM}) ===")
    print(f"{'method':9}{'strategy':10}{'recall@5':>10}{'mrr':>8}{'gap_to_full@5':>15}")
    out = []
    for m in METHODS:
        f5 = agg[m]["full"][0] / n_q
        for s in STRATS:
            r5 = agg[m][s][0] / n_q; mrr = agg[m][s][1] / n_q
            gap = f5 - r5
            print(f"{m:9}{s:10}{r5:10.3f}{mrr:8.3f}{('' if s=='full' else f'{gap:+.3f}'):>15}")
            out.append(dict(method=m, strategy=s, recall5=r5, mrr=mrr, gap_to_full=(0 if s=='full' else gap)))
    json.dump(out, open(_os.path.join(DATA, "digest_strategy_result.json"), "w"), indent=1)
    print("\nKEY: does 'facts' close the gap that 'generic' leaves vs full?")

if __name__ == "__main__":
    main()
