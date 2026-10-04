#!/usr/bin/env python3
"""Minimal reproduction: streaming tool_calls index collision on OpenCode Go gateway.

Matrix: route (go|or) x tool schema (union|simple) x model.
No pi, no MCP, no session context — plain HTTP to the provider endpoint.

Claim:
  * union schema  -> both tool_calls start with index=0 (OpenCode Go only)
  * simple schema -> index=0, index=1 (both routes)

Usage: issue-repro.py [model] [runs]
Env: OPENCODE_API_KEY (for go), OPENROUTER_API_KEY (for or)
"""
import json
import os
import sys
import time
import urllib.request

MODEL = sys.argv[1] if len(sys.argv) > 1 else "mimo-v2.6-flash"
RUNS = int(sys.argv[2]) if len(sys.argv) > 2 else 3

PROMPT = (
    "Call the tool probe TWICE IN ONE block in parallel: "
    "first q='alpha one' limit=3, second q='beta two' limit=7. "
    "Nothing else. Reply: DONE"
)

SCHEMAS = {
    "union": {
        "type": "object",
        "properties": {
            "q": {"type": ["string", "null"], "default": None},
            "limit": {"type": ["integer", "null"], "default": None},
        },
        "required": ["q", "limit"],
    },
    "anyof": {
        "type": "object",
        "properties": {
            "q": {"anyOf": [{"type": "string"}, {"type": "null"}], "default": None},
            "limit": {"anyOf": [{"type": "integer"}, {"type": "null"}], "default": None},
        },
        "required": ["q", "limit"],
    },
    "simple": {
        "type": "object",
        "properties": {"q": {"type": "string"}, "limit": {"type": "integer"}},
        "required": ["q", "limit"],
    },
}

ROUTES = {
    "go": ("https://opencode.ai/zen/go/v1/chat/completions", "OPENCODE_API_KEY"),
    "or": ("https://openrouter.ai/api/v1/chat/completions", "OPENROUTER_API_KEY"),
}


def stream(route: str, schema_name: str):
    url, key_env = ROUTES[route]
    key = os.environ.get(key_env, "")
    if not key:
        return None, f"missing {key_env}"
    payload = {
        "model": MODEL,
        "stream": True,
        "messages": [{"role": "user", "content": PROMPT}],
        "tools": [
            {
                "type": "function",
                "function": {
                    "name": "probe",
                    "description": "Neutral probe tool.",
                    "parameters": SCHEMAS[schema_name],
                },
            }
        ],
    }
    body = json.dumps(payload).encode()
    last = None
    for _ in range(2):
        req = urllib.request.Request(url, data=body, method="POST")
        headers = [
            ("Content-Type", "application/json"),
            ("Authorization", "Bearer " + key),
            (
                "User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
                "(KHTML, like Gecko) Chrome/140.0 Safari/537.36",
            ),
            ("Accept", "text/event-stream"),
        ]
        if route == "go":
            import uuid

            headers.append(("x-opencode-session", str(uuid.uuid4())))
        else:
            headers.append(("HTTP-Referer", "https://github.com/anomalyco/opencode/issues"))
            headers.append(("X-Title", "tool_calls index repro"))
        for k, v in headers:
            req.add_header(k, v)
        starts = []
        finish = []
        content = []
        try:
            with urllib.request.urlopen(req, timeout=300) as resp:
                for line in resp:
                    line = line.decode("utf-8", "replace").strip()
                    if not line.startswith("data:"):
                        continue
                    p = line[5:].strip()
                    if p == "[DONE]":
                        break
                    try:
                        obj = json.loads(p)
                    except Exception:
                        continue
                    for ch in obj.get("choices") or [{}]:
                        d = ch.get("delta") or {}
                        for t in d.get("tool_calls") or []:
                            if (t.get("function") or {}).get("name"):
                                starts.append(t.get("index"))
                        if d.get("content"):
                            content.append(str(d["content"]))
                        if ch.get("finish_reason"):
                            finish.append(ch["finish_reason"])
            xml = any("invoke" in c or "<function" in c for c in content)
            return starts, f"finish={finish} xml_in_content={xml}"
        except Exception as e:  # noqa: BLE001
            last = e
            time.sleep(3)
    return None, f"ERROR {last}"


def main() -> None:
    print(f"model={MODEL} runs={RUNS}")
    for route in ROUTES:
        for schema_name in SCHEMAS:
            verdicts = []
            for _ in range(RUNS):
                starts, note = stream(route, schema_name)
                verdicts.append((starts, note))
            ok = all(s == [0, 1] for s, _ in verdicts if s is not None)
            flag = "OK" if ok else "BROKEN"
            print(f"  [{flag:6s}] route={route} schema={schema_name}")
            for starts, note in verdicts:
                print(f"           index={starts} {note}")


if __name__ == "__main__":
    main()
