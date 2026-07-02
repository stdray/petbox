# Search eval bench

The lab bench behind PetBox search-service design decisions: measures **lazy (digest-only
discovery) vs full (full-content) retrieval** across lexical / semantic / hybrid, on a
benchmark with ground truth. It is a client of the search read-contract only — same FTS5
tokenizer as the shipped `SqliteFtsIndex`, same RRF as `HybridMerge`, same MRL truncation
as `VectorSearchIndex` — so its numbers reflect the real system behaviour.

## Scripts

| script | what |
|---|---|
| `fetch_locomo.py` | download the LoCoMo benchmark into `data/` (not vendored) |
| `eval_locomo.py` | LoCoMo lazy-vs-full, **lexical** (SQLite FTS5) |
| `eval_locomo_sem.py` | LoCoMo lazy-vs-full, **lexical / semantic / hybrid** + MRL **dim sweep** |
| `mcp_embed.py` | minimal MCP Streamable-HTTP client to pull embeddings from petbox `llm_embed` (reads `PETBOX_API_KEY` from env); caches full vectors in `data/` |
| `eval_transcripts.py` | lazy-vs-full lexical on local Claude transcripts (needs private transcript data — see note) |
| `eval_digest_models.py` | which MODEL writes the facts digest: deepseek v4 flash / pro / pro+thinking / local Qwen3.6-35B (needs `DEEPSEEK_API_KEY`; qwen arm needs `LLAMA_API_KEY`) |

## Run

```bash
# from anywhere; outputs + caches land in eval/search/data/ (gitignored)
python3 eval/search/fetch_locomo.py
python3 eval/search/eval_locomo.py            # lexical
PETBOX_API_KEY=... python3 eval/search/eval_locomo_sem.py   # + semantic/hybrid (needs embedder)
```

`EVAL_DATA=/some/dir` overrides the data location.

## Findings (2026-06-10, full LoCoMo, n=1982 queries)

recall@5, `full / digest / relative gap`:

| method | dim | full | digest | gap |
|---|---|---|---|---|
| lexical | – | 0.881 | 0.761 | +13.6% |
| semantic | 1024 | 0.742 | 0.559 | +24.7% |
| hybrid | 1024 | 0.896 | 0.770 | +14.0% |
| hybrid | 2560 | 0.900 | 0.765 | +15.0% |

1. **The lazy/digest gap persists across every method and dim** — `digest@5` plateaus at
   ~0.77 while `full` reaches 0.90. Better retrieval helps `full` *more*: the loss is
   information-theoretic (the digest dropped content), not a retrieval-method artifact.
   → Mitigate on the **digest** side (retrieval-tuned digest: entities+facts; or hydrate a
   wider top-k), not the retriever.
2. **dim 1024 is the sweet spot** for qwen3-embed-4b (native dim 2560): semantic
   recall@5 0.66 (dim256) → 0.74 (dim1024); 2560 gives no further gain. This is why
   `VectorSearchIndex` defaults to `dim=1024`.
3. **Semantic alone < lexical** (0.74 vs 0.88) with single-vector-per-session; **hybrid is
   best absolute (0.90)** but only +1.5pp over lexical and slightly lower MRR — the lexical
   floor carries it; hybrid widens the net.

Caveats: digest = LoCoMo's generic `session_summary` (not a retrieval-tuned digest);
single vector per document (chunking untested). Full result JSONs in `results/`.

## Findings (2026-06-11, digest-model comparison, 3 convs, n=495)

Same facts-digest prompt, the MODEL varies; recall@5 hybrid (production default):

| model | recall@5 | gap to full (0.897) |
|---|---|---|
| deepseek-v4-pro (no think) | **0.846** | +0.051 |
| deepseek-v4-flash (no think) | 0.826 | +0.071 |
| qwen3.6-35b-a3b local | 0.808 | +0.089 |
| deepseek-v4-pro + thinking | 0.804 | +0.093 |

1. **pro (non-thinking) wins consistently** — +2pp hybrid, +4.4pp lexical over flash;
   the digest quality gap is real but modest.
2. **Thinking HURTS digest retrieval** (pro-think < flash < pro): reasoning produces
   more abstractive, less keyword-dense digests. Disable thinking for distillation.
3. **Local qwen3.6-35b is a viable free fallback** (~2pp under flash on hybrid, weaker
   semantic), at ~30–90 s/digest on the home GPU; needed 5/70 retries for
   thinking-budget truncation (cured by max_tokens 5000).

Caveat: LoCoMo is English-only; production sessions are RU/EN mixed.

## Notes on artifacts

- `data/` is **gitignored**: the LoCoMo dataset (fetched, not vendored — license), the
  embedding cache (~88 MB, regenerable, model-specific), and any transcript-derived corpora.
- `eval_transcripts.py` operates on **private** local Claude transcripts and a
  DeepSeek-generated corpus; those are **never committed** (public repo). Its run showed a
  confounded result (digest and questions shared a DeepSeek vocabulary) — LoCoMo is the
  authoritative, confound-free benchmark; the transcript script is kept for methodology only.
- Whether to version the large regenerable artifacts (embeddings / dataset) via Git LFS is
  deferred.
