import os as _os, sys as _sys
_sys.path.insert(0, _os.path.dirname(_os.path.abspath(__file__)))
DATA = _os.environ.get("EVAL_DATA", _os.path.join(_os.path.dirname(_os.path.abspath(__file__)), "data"))
_os.makedirs(DATA, exist_ok=True)

#!/usr/bin/env python3
"""Digest-MODEL comparison on LoCoMo: which distillation model earns its price?

Same harness as eval_digest_strategy.py (full = ceiling, facts-digest = lazy tier,
lexical / semantic / hybrid, recall@5 + MRR), but the variable is the MODEL that
writes the facts digest, not the digest strategy:
  flash      = deepseek-v4-flash, thinking disabled
  pro        = deepseek-v4-pro,   thinking disabled
  pro-think  = deepseek-v4-pro,   thinking enabled (reasoning before the digest)
  qwen-local = unsloth/Qwen3.6-35B-A3B (local llama-server router, free)
Context: deepseek-chat/-reasoner aliases die 2026-07-24; the explicit v4 names
default to thinking ON and max_tokens covers reasoning+answer.
Needs DEEPSEEK_API_KEY; qwen arm needs LLAMA_API_KEY + the home router up.
"""
import json, re, sqlite3, ssl, time, urllib.error, urllib.request, numpy as np
from collections import defaultdict
import mcp_embed

DATA_FILE = _os.path.join(DATA, "locomo10.json")
CACHE_FILE = _os.path.join(DATA, "digest_models_cache.json")
DS_KEY = _os.environ["DEEPSEEK_API_KEY"]
LLAMA_KEY = _os.environ.get("LLAMA_API_KEY")
LLAMA_URL = _os.environ.get("LLAMA_URL", "https://roukek.keenetic.pro:65112")
QWEN_MODEL = "unsloth/Qwen3.6-35B-A3B-GGUF:Q4_K_M"
WORD = re.compile(r"[^\W\d_]+|\d+", re.UNICODE)
EV = re.compile(r"D(\d+):")
THINK_RE = re.compile(r"<think>.*?</think>\s*", re.DOTALL)
N_CONV, EMB_CAP, RRF_K, DIM = 3, 6000, 60, 1024

DATA_J = json.load(open(DATA_FILE, encoding="utf-8"))
_cache = json.load(open(CACHE_FILE, encoding="utf-8")) if _os.path.exists(CACHE_FILE) else {}
_SSL = ssl._create_unverified_context()  # home router: pinned self-signed cert

FACTS_PROMPT = ("Extract a compact, RETRIEVAL-ORIENTED digest of this conversation session. "
    "List densely: key entities (people, places, things), concrete facts and events with "
    "their dates, and decisions/plans/preferences stated. Keyword-rich, no narrative fluff, "
    "no preamble. Under 110 words.\n\nSESSION:\n")

def _post(url, body, headers, timeout=180, ctx=None):
    last = None
    for attempt in range(4):
        try:
            req = urllib.request.Request(url, data=json.dumps(body).encode(), headers=headers)
            with urllib.request.urlopen(req, timeout=timeout, context=ctx) as r:
                return json.loads(r.read())
        except (urllib.error.URLError, TimeoutError, json.JSONDecodeError) as e:
            last = e
            time.sleep(3 * (attempt + 1))
    raise RuntimeError(f"POST {url} failed after retries: {last}")

def chat_deepseek(model, thinking, prompt):
    body = {"model": model, "temperature": 0.1,
            "max_tokens": 4000 if thinking else 320,
            "thinking": {"type": "enabled" if thinking else "disabled"},
            "messages": [{"role": "user", "content": prompt}]}
    for max_tokens in (body["max_tokens"], 8000):
        body["max_tokens"] = max_tokens
        msg = _post("https://api.deepseek.com/chat/completions", body,
                    {"Authorization": f"Bearer {DS_KEY}", "Content-Type": "application/json"})["choices"][0]["message"]
        text = (msg.get("content") or "").strip()
        if text: return text
        # thinking ate the whole budget; retry once with a bigger one
    raise RuntimeError(f"{model} returned empty content twice (thinking budget)")

def chat_qwen(prompt):
    body = {"model": QWEN_MODEL, "temperature": 0.1, "max_tokens": 5000,
            "messages": [{"role": "user", "content": prompt}]}
    msg = _post(f"{LLAMA_URL}/v1/chat/completions", body,
                {"Authorization": f"Bearer {LLAMA_KEY}", "Content-Type": "application/json"},
                timeout=600, ctx=_SSL)["choices"][0]["message"]
    return THINK_RE.sub("", msg.get("content") or "").strip()

ARMS = {
    "flash":     lambda p: chat_deepseek("deepseek-v4-flash", False, p),
    "pro":       lambda p: chat_deepseek("deepseek-v4-pro", False, p),
    "pro-think": lambda p: chat_deepseek("deepseek-v4-pro", True, p),
}
if LLAMA_KEY: ARMS["qwen-local"] = chat_qwen

def digest(arm, sid, n, text):
    k = f"{arm}:{sid}:{n}"
    if k not in _cache:
        t0 = time.time()
        _cache[k] = ARMS[arm](FACTS_PROMPT + text[:3500])
        json.dump(_cache, open(CACHE_FILE, "w"), ensure_ascii=False)
        print(f"  {k} ({len(_cache[k])} chars, {time.time()-t0:.1f}s)", flush=True)
    return _cache[k]

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
    strats = ["full"] + list(ARMS)
    convs, to_embed = [], set()
    print(f"arms: {list(ARMS)}")
    for item in DATA_J[:N_CONV]:
        sid = item["sample_id"]; conv = item["conversation"]
        full = {int(re.fullmatch(r"session_(\d+)", k).group(1)):
                "\n".join(f"{t.get('speaker','')}: {t.get('text','')}" for t in conv[k])
                for k in conv if re.fullmatch(r"session_(\d+)", k)}
        docs = {"full": full}
        for arm in ARMS:
            print(f"digesting {sid} via {arm} ({len(full)} sessions)...", flush=True)
            docs[arm] = {n: digest(arm, sid, n, full[n]) for n in full}
        qs = []
        for qa in item["qa"]:
            rel = {int(EV.match(e).group(1)) for e in (qa.get("evidence") or []) if EV.match(e)} & set(full)
            if rel: qs.append((str(qa["question"]), rel))
        convs.append((docs, qs))
        for d in docs.values():
            for v in d.values(): to_embed.add(v[:EMB_CAP])
        for q, _ in qs: to_embed.add(q)

    n_q = sum(len(c[1]) for c in convs)
    print(f"\nembedding {len(to_embed)} texts (dim {DIM})...")
    raw = mcp_embed.embed(list(to_embed), dim=DIM, batch=32)
    emap = {t: norm(v) for t, v in zip(list(to_embed), raw)}

    METHODS = ["lexical", "semantic", "hybrid"]
    agg = {m: {s: [0, 0.0] for s in strats} for m in METHODS}
    for docs, qs in convs:
        dbs = {s: fts(docs[s]) for s in strats}
        ids = {s: sorted(docs[s]) for s in strats}
        mats = {s: (np.stack([emap[docs[s][i][:EMB_CAP]] for i in ids[s]]) if ids[s] else None) for s in strats}
        for q, rel in qs:
            qv = emap[q]
            for s in strats:
                lr = lex_rank(dbs[s], q)
                sr = sem_rank(qv, ids[s], mats[s]) if ids[s] else []
                hr = rrf(lr, sr)
                for method, r in (("lexical", lr), ("semantic", sr), ("hybrid", hr)):
                    pos = next((i + 1 for i, sid in enumerate(r) if sid in rel), 0)
                    if 0 < pos <= 5: agg[method][s][0] += 1
                    if pos: agg[method][s][1] += 1.0 / pos

    print(f"\n=== Digest-model comparison on LoCoMo (n={n_q}, dim={DIM}) ===")
    print(f"{'method':9}{'model':12}{'recall@5':>10}{'mrr':>8}{'gap_to_full@5':>15}")
    out = []
    for m in METHODS:
        f5 = agg[m]["full"][0] / n_q
        for s in strats:
            r5 = agg[m][s][0] / n_q; mrr = agg[m][s][1] / n_q
            gap = f5 - r5
            print(f"{m:9}{s:12}{r5:10.3f}{mrr:8.3f}{('' if s=='full' else f'{gap:+.3f}'):>15}")
            out.append(dict(method=m, model=s, recall5=r5, mrr=mrr, gap_to_full=(0 if s == 'full' else gap)))
    json.dump(out, open(_os.path.join(DATA, "digest_models_result.json"), "w"), indent=1)
    print("\nKEY: does pro (or thinking) lift retrieval recall over flash? does local qwen keep up?")

if __name__ == "__main__":
    main()
