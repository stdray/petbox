import os as _os, sys as _sys
_sys.path.insert(0, _os.path.dirname(_os.path.abspath(__file__)))
DATA = _os.environ.get("EVAL_DATA", _os.path.join(_os.path.dirname(_os.path.abspath(__file__)), "data"))
_os.makedirs(DATA, exist_ok=True)

#!/usr/bin/env python3
"""K-constancy at scale: does the hydration width K stay ~constant as the candidate set grows?

Per-conversation retrieval scopes the candidate set to ~23 sessions. The real episodic
archive is one big pool of many sessions. This pools ALL 272 LoCoMo sessions into a single
index (N=272, ~12x bigger) and sweeps hydration depth K, vs the per-conversation scope
(N~23) — if the K needed to capture the ground-truth session stays ~constant (not
proportional to N), lazy two-stage wins at scale. facts-digest + hybrid (the crystallized
discovery config). Generates all 272 facts-digests in parallel (resumable cache).
"""
import json, re, sqlite3, threading, urllib.request, numpy as np
from collections import defaultdict
from concurrent.futures import ThreadPoolExecutor
import mcp_embed

DATA_J = json.load(open(_os.path.join(DATA, "locomo10.json"), encoding="utf-8"))
FACTS_CACHE = _os.path.join(DATA, "digest_facts.json")
_facts = json.load(open(FACTS_CACHE, encoding="utf-8")) if _os.path.exists(FACTS_CACHE) else {}
_lock = threading.Lock()
DS_KEY = _os.environ["DEEPSEEK_API_KEY"]
WORD = re.compile(r"[^\W\d_]+|\d+", re.UNICODE)
EV = re.compile(r"D(\d+):")
EMB_CAP, RRF_K, DIM = 6000, 60, 1024
KS = [1, 3, 5, 10, 20, 30, 50, 999999]
FACTS_PROMPT = ("Extract a compact, RETRIEVAL-ORIENTED digest of this conversation session. "
    "List densely: key entities (people, places, things), concrete facts and events with "
    "their dates, and decisions/plans/preferences stated. Keyword-rich, no narrative fluff, "
    "no preamble. Under 110 words.\n\nSESSION:\n")

def deepseek(prompt):
    body = json.dumps({"model": "deepseek-chat", "temperature": 0.1, "max_tokens": 320,
        "messages": [{"role": "user", "content": prompt}]}).encode()
    req = urllib.request.Request("https://api.deepseek.com/chat/completions", data=body,
        headers={"Authorization": f"Bearer {DS_KEY}", "Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=120) as r:
        return json.loads(r.read())["choices"][0]["message"]["content"].strip()

def ensure_facts(jobs):  # jobs: list of (key, text)
    todo = [(k, t) for k, t in jobs if k not in _facts]
    print(f"facts-digests: {len(_facts)} cached, {len(todo)} to generate", flush=True)
    if not todo: return
    done = [0]
    def work(kt):
        k, t = kt
        d = deepseek(FACTS_PROMPT + t[:3500])
        with _lock:
            _facts[k] = d; done[0] += 1
            if done[0] % 16 == 0:
                json.dump(_facts, open(FACTS_CACHE, "w"), ensure_ascii=False)
                print(f"  {done[0]}/{len(todo)}", flush=True)
    with ThreadPoolExecutor(max_workers=8) as ex:
        list(ex.map(work, todo))
    json.dump(_facts, open(FACTS_CACHE, "w"), ensure_ascii=False)
    print(f"  facts done ({len(_facts)} total)", flush=True)

def build_match(q):
    toks = [m.group(0).lower() + "*" for m in WORD.finditer(q)]
    return " OR ".join(toks) if toks else None

def fts(docs):  # docs: {id: text}
    db = sqlite3.connect(":memory:")
    db.execute("CREATE VIRTUAL TABLE d USING fts5(sid UNINDEXED, body, tokenize='unicode61')")
    db.executemany("INSERT INTO d(sid,body) VALUES (?,?)", [(k, v) for k, v in docs.items() if v])
    return db

def lex_rank(db, q, k):
    m = build_match(q)
    return [r[0] for r in db.execute("SELECT sid FROM d WHERE d MATCH ? ORDER BY rank LIMIT ?", (m, k)).fetchall()] if m else []

def sem_rank(qv, ids, mat, k):
    return [ids[i] for i in np.argsort(-(mat @ qv))[:k]]

def rrf(a, b, k):
    sc = defaultdict(float)
    for r in (a, b):
        for rank, sid in enumerate(r): sc[sid] += 1.0 / (RRF_K + rank)
    return [s for s, _ in sorted(sc.items(), key=lambda x: -x[1])][:k]

def norm(v):
    a = np.asarray(v, dtype=np.float32); n = np.linalg.norm(a); return a / n if n else a

def hybrid_at(db, ids, mat, q, qv, K):
    return rrf(lex_rank(db, q, K), sem_rank(qv, ids, mat, K), K)

def main():
    # build global facts digests + collect per-conv structures and queries
    convs = []
    fact_jobs = []
    for item in DATA_J:
        sid = item["sample_id"]; conv = item["conversation"]
        full = {n: "\n".join(f"{t.get('speaker','')}: {t.get('text','')}" for t in conv[k])
                for k in conv if (m := re.fullmatch(r"session_(\d+)", k)) for n in [int(m.group(1))]}
        qs = []
        for qa in item["qa"]:
            rel = {int(EV.match(e).group(1)) for e in (qa.get("evidence") or []) if EV.match(e)} & set(full)
            if rel: qs.append((str(qa["question"]), rel))
        convs.append((sid, full, qs))
        for n, txt in full.items():
            fact_jobs.append((f"{sid}:{n}", txt))
    ensure_facts(fact_jobs)

    # global facts docs keyed by global id "sid#n"
    gfacts = {f"{sid}#{n}": _facts[f"{sid}:{n}"] for sid, full, _ in convs for n in full}
    to_embed = set(v[:EMB_CAP] for v in gfacts.values())
    for sid, full, qs in convs:
        for q, _ in qs: to_embed.add(q)
    print(f"embedding {len(to_embed)} texts (dim {DIM}, mostly cached)...", flush=True)
    emap = {t: norm(v) for t, v in zip(list(to_embed), mcp_embed.embed(list(to_embed), dim=DIM, batch=32))}

    n_q = sum(len(qs) for _, _, qs in convs)

    # --- PER-CONV scope (N ~ 23) ---
    per = {K: 0 for K in KS}
    for sid, full, qs in convs:
        ids = [f"{sid}#{n}" for n in sorted(full)]
        docs = {gid: gfacts[gid] for gid in ids}
        db = fts(docs); mat = np.stack([emap[gfacts[g][:EMB_CAP]] for g in ids])
        for q, rel in qs:
            relg = {f"{sid}#{n}" for n in rel}
            qv = emap[q]
            for K in KS:
                if any(g in relg for g in hybrid_at(db, ids, mat, q, qv, K)): per[K] += 1

    # --- POOLED scope (N = 272) ---
    ids = list(gfacts)
    db = fts(gfacts); mat = np.stack([emap[gfacts[g][:EMB_CAP]] for g in ids])
    pool = {K: 0 for K in KS}
    for sid, full, qs in convs:
        for q, rel in qs:
            relg = {f"{sid}#{n}" for n in rel}
            qv = emap[q]
            for K in KS:
                if any(g in relg for g in hybrid_at(db, ids, mat, q, qv, K)): pool[K] += 1

    avg = sum(len(f) for _, f, _ in convs) / len(convs)
    print(f"\n=== K-constancy: facts-digest + hybrid, dim{DIM}, n={n_q} queries ===")
    print(f"per-conv scope N~{avg:.0f} sessions   |   pooled scope N={len(ids)} sessions")
    print(f"{'K':>7}{'per-conv@K':>13}{'pooled@K':>11}")
    out = []
    for K in KS:
        kl = "all" if K >= 999999 else str(K)
        a, b = per[K] / n_q, pool[K] / n_q
        print(f"{kl:>7}{a:13.3f}{b:11.3f}")
        out.append(dict(K=kl, per_conv=a, pooled=b))
    json.dump(out, open(_os.path.join(DATA, "kscale_result.json"), "w"), indent=1)

if __name__ == "__main__":
    main()
