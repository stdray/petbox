import os as _os, sys as _sys
_sys.path.insert(0, _os.path.dirname(_os.path.abspath(__file__)))
DATA = _os.environ.get("EVAL_DATA", _os.path.join(_os.path.dirname(_os.path.abspath(__file__)), "data"))
_os.makedirs(DATA, exist_ok=True)

#!/usr/bin/env python3
"""Minimal MCP Streamable-HTTP client to pull embeddings from petbox (llm_embed).

petbox /mcp is the ModelContextProtocol SDK Streamable-HTTP transport: initialize ->
notifications/initialized -> tools/call, with an Mcp-Session-Id header and SSE responses.
Embeddings are cached on disk by text hash so runs resume cheaply.
"""
import os, json, hashlib, time, requests

URL = "https://petbox.3po.su/mcp"
KEY = os.environ["PETBOX_API_KEY"]
HDR = {"X-Api-Key": KEY, "Content-Type": "application/json",
       "Accept": "application/json, text/event-stream"}
CACHE = _os.path.join(DATA, "loco_emb_full.json")   # stores FULL vectors keyed by text hash; slice per dim locally
_cache = json.load(open(CACHE, encoding="utf-8")) if os.path.exists(CACHE) else {}
_sid = None

def _parse(resp):
    ct = resp.headers.get("Content-Type", "")
    if "text/event-stream" in ct:
        out = []
        for line in resp.text.splitlines():
            if line.startswith("data:"):
                out.append(line[5:].strip())
        return json.loads("".join(out)) if out else None
    return resp.json() if resp.text.strip() else None

def _init():
    global _sid
    body = {"jsonrpc": "2.0", "id": 1, "method": "initialize", "params": {
        "protocolVersion": "2025-06-18", "capabilities": {},
        "clientInfo": {"name": "eval-embed", "version": "0"}}}
    r = requests.post(URL, headers=HDR, json=body, timeout=60)
    r.raise_for_status()
    _sid = r.headers.get("Mcp-Session-Id") or r.headers.get("mcp-session-id")
    h = dict(HDR)
    if _sid: h["Mcp-Session-Id"] = _sid
    requests.post(URL, headers=h, json={"jsonrpc": "2.0", "method": "notifications/initialized"}, timeout=60)
    return h

def _call(h, inputs):
    body = {"jsonrpc": "2.0", "id": 2, "method": "tools/call", "params": {
        "name": "llm_embed", "arguments": {"projectKey": "$system", "inputs": inputs}}}
    r = requests.post(URL, headers=h, json=body, timeout=300)
    r.raise_for_status()
    msg = _parse(r)
    if msg is None or "result" not in msg:
        raise RuntimeError(f"bad embed response: {str(msg)[:300]}")
    res = msg["result"]
    payload = res.get("structuredContent")
    if payload is None:
        payload = json.loads(res["content"][0]["text"])
    return payload["vectors"]

def embed(texts, batch=16, dim=256):
    """Return list of dim-truncated float vectors; FULL vectors cached by text hash."""
    todo = [t for t in texts if _key(t) not in _cache]
    if todo:
        h = _init()
        for i in range(0, len(todo), batch):
            chunk = todo[i:i + batch]
            vecs = _call(h, chunk)
            for t, v in zip(chunk, vecs):
                _cache[_key(t)] = v   # store full vector
            json.dump(_cache, open(CACHE, "w"))
            print(f"  embedded {min(i+batch,len(todo))}/{len(todo)}", flush=True)
            time.sleep(0.1)
    return [_cache[_key(t)][:dim] for t in texts]

def _key(t):
    return hashlib.sha1(t.encode("utf-8")).hexdigest()

if __name__ == "__main__":
    v = embed(["когда Caroline пошла в группу поддержки", "what is the weather"], dim=256)
    print("got", len(v), "vecs, dim", len(v[0]))
    import math
    a, b = v
    dot = sum(x*y for x, y in zip(a, b)); na = math.sqrt(sum(x*x for x in a)); nb = math.sqrt(sum(x*x for x in b))
    print("cosine(distinct texts):", round(dot/(na*nb), 3))
