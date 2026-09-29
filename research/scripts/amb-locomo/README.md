# AMB / LoCoMo harness for PetBox memory

Runs the LoCoMo dataset of [vectorize-io/agent-memory-benchmark](https://github.com/vectorize-io/agent-memory-benchmark)
(AMB) against PetBox `memory_search`: ingest conversation chunks with `memory_upsert`, retrieve with
`memory_search`, answer and judge with `llm_chat`, using AMB's own dataset code and prompts.
It is a standalone harness, not an AMB provider plugin (AMB's provider registry imports mem0/cognee/hindsight).

Origin: card `work/memory-agent-benchmark-run`. First result: `results-2026-09-28.json` (86.9%, 1339/1540).
Read that card's `result` comments before quoting the number: it is comparable only to AMB's `hybrid-search`
baseline (79.1%), not to Hindsight/Mem0/Zep, and the answer/judge LLM differed from AMB's published runs.

## Smoke-only rule (AGENTS.md rule 7)

All writes go to project `smoke` (`sandbox=true`) with a **sandboxOnly** key. The script calls `whoami` on both keys
and aborts unless the project claim is `smoke`. It creates one memory store per conversation (`ambloc<conv>`) and
deletes them at the end (`--keep` to skip). Never point it at another project or use a `$system` key.

## Setup

- Python >= 3.11, `pip install httpx google-genai groq openai rich tiktoken` (AMB's dataset package imports the LLM
  modules).
- AMB source: fetch `src/` from the AMB repo (a full clone is ~GBs of datasets; GitHub may need `pfetch`) and put
  the LoCoMo data at `<AMB_DIR>/.datasets/locomo/locomo10.json`
  (`https://raw.githubusercontent.com/snap-research/locomo/main/data/locomo10.json`). Do not commit either.

## Environment

| Variable | Meaning |
|---|---|
| `AMB_DIR` | AMB checkout/dir containing `src/memory_bench` (default `amb`) |
| `PETBOX_SMOKE_API_KEY` | sandboxOnly key for `smoke` with `memory:read,memory:write` |
| `PETBOX_LLM_API_KEY` | sandboxOnly `smoke` key with `llm:invoke` (mint a short-TTL one, revoke after) |
| `PETBOX_MCP_URL` | optional, default `https://petbox.3po.su/mcp` |

## Run

```
python run_locomo.py --convs conv-26 --qlimit 30 --out pilot.json      # ~4 min
python run_locomo.py --convs all --par 12 --out full.json              # ~1.5 h
```

Flags: `--k` results per query (25), `--win` turns per entry (6), `--par` query workers, `--qlimit` questions per
conversation, `--keep` keep the stores. Ingest is resumable (existing keys are skipped) and batches 10 entries.

## Results

`--out` writes `{summary, results[]}` (per-question answer, gold, verdict, retrieved keys). Keep raw dumps out of the
repo; commit only a small summary like `results-2026-09-28.json`. The harness does not store the retrieved context,
so a result cannot be re-judged later; add it before the next run.

## Performance notes (2026-09-28 run)

Search = 1 embed + 1 rerank, both on the home llama-server (serialises): ~2.5 s serial service time per search, ~27 s
under 12 workers, so parallelism adds latency, not throughput. Writes cost ~2 embeds per entry (vector + dedup check),
~2.3 s/entry under 4 parallel conversations; a 20-entry batch can exceed 3 min, hence batches of 10.
