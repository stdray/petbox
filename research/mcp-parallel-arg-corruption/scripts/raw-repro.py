#!/usr/bin/env python3
"""Raw SSE repro: stream a tool-using chat.completion directly to a provider gateway,
bypassing pi entirely. Captures the ORIGINAL response bytes, then dissects tool_calls.

Usage: raw-repro.py <route:go|or> <model> <out.jsonl> [runs]
  go = https://opencode.ai/zen/go/v1 (OpenCode Go gateway, OPENCODE_API_KEY)
  or = https://openrouter.ai/api/v1 (OPENROUTER_API_KEY)
"""
import json
import os
import sys
import urllib.request
import uuid

ROUTE = sys.argv[1] if len(sys.argv) > 1 else "go"
MODEL = sys.argv[2] if len(sys.argv) > 2 else "mimo-v2.6-flash"
OUT = sys.argv[3] if len(sys.argv) > 3 else "raw.jsonl"
RUNS = int(sys.argv[4]) if len(sys.argv) > 4 else 1

ROUTES = {
    "go": ("https://opencode.ai/zen/go/v1/chat/completions", "OPENCODE_API_KEY"),
    "or": ("https://openrouter.ai/api/v1/chat/completions", "OPENROUTER_API_KEY"),
}

TOOLS = [
    {
        "type": "function",
        "function": {
            "name": "probe_alpha",
            "description": "Neutral probe tool A. Returns a fixed string.",
            "parameters": {
                "type": "object",
                "properties": {
                    "q": {"type": "string", "description": "search text"},
                    "limit": {"type": "integer", "description": "max results"},
                },
                "required": ["q", "limit"],
            },
        },
    },
    {
        "type": "function",
        "function": {
            "name": "probe_beta",
            "description": "Neutral probe tool B. Returns a fixed string.",
            "parameters": {
                "type": "object",
                "properties": {
                    "q": {"type": "string", "description": "search text"},
                    "limit": {"type": "integer", "description": "max results"},
                },
                "required": ["q", "limit"],
            },
        },
    },
]

PROMPT = (
    "Call EXACTLY TWO tools IN ONE response block, in parallel: "
    "probe_alpha with q='raw probe alpha' and limit=3, and "
    "probe_beta with q='raw probe beta' and limit=7. "
    "Nothing else. Then reply with just: DONE"
)


def run_once(url: str, key: str, headers: list) -> tuple[str, list]:
    body = json.dumps(
        {
            "model": MODEL,
            "stream": True,
            "messages": [{"role": "user", "content": PROMPT}],
            "tools": TOOLS,
        }
    ).encode()
    req = urllib.request.Request(url, data=body, method="POST")
    req.add_header("Content-Type", "application/json")
    req.add_header("Authorization", "Bearer " + key)
    req.add_header(
        "User-Agent",
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
        "(KHTML, like Gecko) Chrome/140.0 Safari/537.36",
    )
    req.add_header("Accept", "text/event-stream")
    for k, v in headers:
        req.add_header(k, v)
    raw_chunks: list[str] = []
    with urllib.request.urlopen(req, timeout=300) as resp:
        for line in resp:
            raw_chunks.append(line.decode("utf-8", "replace"))
    return "".join(raw_chunks), raw_chunks


def dissect(raw: str) -> list[dict]:
    """Print every SSE data frame carrying tool_calls, verbatim."""
    frames = []
    for line in raw.splitlines():
        line = line.strip()
        if not line.startswith("data:"):
            continue
        payload = line[5:].strip()
        if payload == "[DONE]":
            frames.append({"kind": "DONE"})
            continue
        try:
            obj = json.loads(payload)
        except Exception:
            frames.append({"kind": "unparsable", "raw": payload[:300]})
            continue
        for ch in (obj.get("choices") or [{}]):
            delta = ch.get("delta") or {}
            tc = delta.get("tool_calls")
            if tc:
                frames.append(
                    {
                        "kind": "tool_calls",
                        "finish_reason": ch.get("finish_reason"),
                        "tool_calls": tc,
                        "content": delta.get("content"),
                    }
                )
            elif ch.get("finish_reason"):
                frames.append({"kind": "finish", "finish_reason": ch.get("finish_reason")})
    return frames


def main() -> None:
    url, key_env = ROUTES[ROUTE]
    key = os.environ.get(key_env, "")
    if not key:
        sys.exit(f"missing env {key_env}")
    headers = []
    if ROUTE == "or":
        headers.append(("HTTP-Referer", "https://github.com/earendil-works/pi"))
        headers.append(("X-Title", "pi raw SSE repro"))
    else:
        # OpenCode Go gateway requires per-conversation routing header
        headers.append(("x-opencode-session", str(uuid.uuid4())))
    with open(OUT, "w", encoding="utf-8") as fh:
        for i in range(RUNS):
            raw, _ = run_once(url, key, headers)
            frames = dissect(raw)
            fh.write(json.dumps({"run": i, "frames": frames}, ensure_ascii=False) + "\n")
            # quick console summary
            tc_starts = [f for f in frames if f["kind"] == "tool_calls"]
            done = any(f["kind"] == "DONE" for f in frames)
            print(f"run {i}: frames={len(frames)} tool_call_frames={len(tc_starts)} DONE={done}")
            for f in frames:
                if f["kind"] == "tool_calls":
                    for t in f["tool_calls"]:
                        fn = t.get("function") or {}
                        print(
                            f"   idx={t.get('index')!r} id={t.get('id')!r} "
                            f"name={fn.get('name')!r} args={str(fn.get('arguments'))[:80]!r}"
                        )
                elif f["kind"] in ("finish", "DONE", "unparsable"):
                    print("   ", f)
    print("raw frames written to", OUT)


if __name__ == "__main__":
    main()
