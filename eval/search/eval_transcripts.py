import os as _os, sys as _sys
_sys.path.insert(0, _os.path.dirname(_os.path.abspath(__file__)))
DATA = _os.environ.get("EVAL_DATA", _os.path.join(_os.path.dirname(_os.path.abspath(__file__)), "data"))
_os.makedirs(DATA, exist_ok=True)

#!/usr/bin/env python3
"""Lazy-vs-full lexical eval on real Claude transcript sessions.

Measures how much a DIGEST-only (lazy two-stage discovery) lexical index loses vs a
FULL-text index, on real sessions with DeepSeek-generated digests + PARAPHRASED
questions. Lexical legs use SQLite FTS5 unicode61 + the same prefix-token MATCH as the
shipped Class-A SqliteFtsIndex, so the number reflects the real lexical floor. This is a
conservative bound on lazy's discovery quality — semantic enrichment (deferred) should
only narrow the gap.
"""
import json, os, re, sqlite3, urllib.request, sys

DS_KEY = os.environ["DEEPSEEK_API_KEY"]
SESSIONS = json.load(open(_os.path.join(DATA, "eval_sessions.json"), encoding="utf-8"))

def deepseek(prompt):
    body = json.dumps({
        "model": "deepseek-chat",
        "messages": [{"role": "user", "content": prompt}],
        "temperature": 0.2, "max_tokens": 700,
    }).encode("utf-8")
    req = urllib.request.Request("https://api.deepseek.com/chat/completions", data=body,
        headers={"Authorization": f"Bearer {DS_KEY}", "Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=120) as r:
        return json.loads(r.read())["choices"][0]["message"]["content"]

def gen_corpus():
    """Per session: DeepSeek digest + 2 paraphrased questions. Cached to disk."""
    cache = _os.path.join(DATA, "eval_corpus.json")
    if os.path.exists(cache):
        return json.load(open(cache, encoding="utf-8"))
    out = []
    for s in SESSIONS:
        prompt = (
            "Дан фрагмент сессии разработки. Верни СТРОГО JSON-объект:\n"
            '{"digest":"1-2 предложения о чём сессия","questions":["вопрос1","вопрос2"]}\n'
            "questions — 2 ЕСТЕСТВЕННЫХ вопроса, которые пользователь задал бы ПОЗЖЕ, чтобы "
            "найти эту сессию; ПЕРЕФРАЗ, НЕ копируй точные слова из текста.\n\nСЕССИЯ:\n"
            + s["full"][:3500]
        )
        raw = deepseek(prompt)
        m = re.search(r"\{.*\}", raw, re.S)
        obj = json.loads(m.group(0))
        out.append({"id": s["id"], "full": s["full"], "digest": obj["digest"],
                    "questions": obj["questions"]})
        print(f"  corpus {s['id']}: {len(obj['questions'])} q, digest {len(obj['digest'])} chars")
    json.dump(out, open(cache, "w", encoding="utf-8"), ensure_ascii=False, indent=1)
    return out

WORD = re.compile(r"[^\W\d_]+|\d+", re.UNICODE)  # \p{L}+ | \d+

def build_match(q):
    toks = [m.group(0).lower() + "*" for m in WORD.finditer(q)]
    return " OR ".join(toks) if toks else None  # OR: recall-oriented discovery

def fts_index(corpus, field):
    db = sqlite3.connect(":memory:")
    db.execute("CREATE VIRTUAL TABLE d USING fts5(id UNINDEXED, body, tokenize='unicode61')")
    db.executemany("INSERT INTO d(id, body) VALUES (?,?)", [(c["id"], c[field]) for c in corpus])
    return db

def ranked(db, query, k=5):
    m = build_match(query)
    if not m:
        return []
    rows = db.execute(
        "SELECT id FROM d WHERE d MATCH ? ORDER BY rank LIMIT ?", (m, k)).fetchall()
    return [r[0] for r in rows]

def evaluate(corpus, field):
    db = fts_index(corpus, field)
    hits1 = hits3 = hits5 = 0
    rr = 0.0
    n = 0
    for c in corpus:
        for q in c["questions"]:
            n += 1
            r = ranked(db, q, 5)
            pos = r.index(c["id"]) + 1 if c["id"] in r else 0
            if pos == 1: hits1 += 1
            if 0 < pos <= 3: hits3 += 1
            if 0 < pos <= 5: hits5 += 1
            if pos: rr += 1.0 / pos
    return dict(queries=n, recall1=hits1/n, recall3=hits3/n, recall5=hits5/n, mrr=rr/n)

def main():
    print("generating corpus via DeepSeek...")
    corpus = gen_corpus()
    full = evaluate(corpus, "full")
    digest = evaluate(corpus, "digest")
    print("\n=== LEXICAL lazy-vs-full (FTS5 unicode61, prefix-OR match) ===")
    print(f"sessions={len(corpus)} queries={full['queries']}")
    hdr = f"{'strategy':10} {'recall@1':>9} {'recall@3':>9} {'recall@5':>9} {'mrr':>7}"
    print(hdr)
    for name, m in [("full", full), ("digest", digest)]:
        print(f"{name:10} {m['recall1']:9.3f} {m['recall3']:9.3f} {m['recall5']:9.3f} {m['mrr']:7.3f}")
    gap5 = full["recall5"] - digest["recall5"]
    gapmrr = full["mrr"] - digest["mrr"]
    print(f"\nLAZY LOSS (full - digest): recall@5 {gap5:+.3f}  mrr {gapmrr:+.3f}")
    rel = (gap5 / full["recall5"] * 100) if full["recall5"] else 0
    print(f"relative recall@5 loss: {rel:.1f}%")
    json.dump(dict(full=full, digest=digest, gap_recall5=gap5, gap_mrr=gapmrr),
              open(_os.path.join(DATA, "eval_result.json"), "w"), indent=1)

if __name__ == "__main__":
    main()
