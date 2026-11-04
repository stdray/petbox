"""Промахи промпт-кеша в сессиях opencode — для сравнения с pi на том же шлюзе.

    python opencode-session-misses.py <opencode.db> [provider] [model]

По умолчанию считает opencode-go / deepseek-v4.1-flash. База открывается READ-ONLY,
WAL-файлы не трогаются — можно запускать при живом opencode.
"""
import collections
import json
import sqlite3
import sys


def main() -> int:
    if len(sys.argv) < 2:
        print(__doc__)
        return 2
    db = sys.argv[1]
    provider = sys.argv[2] if len(sys.argv) > 2 else "opencode-go"
    model = sys.argv[3] if len(sys.argv) > 3 else "deepseek-v4.1-flash"

    con = sqlite3.connect("file:%s?mode=ro" % db, uri=True)
    by_session = collections.defaultdict(list)
    for session_id, created, data in con.execute("select session_id, time_created, data from message"):
        entry = json.loads(data)
        if entry.get("role") != "assistant":
            continue
        if entry.get("providerID") != provider or entry.get("modelID") != model:
            continue
        tokens = entry.get("tokens") or {}
        by_session[session_id].append(
            (
                created,
                tokens.get("input", 0),
                (tokens.get("cache") or {}).get("read", 0),
                entry.get("cost", 0),
            )
        )

    total = hit = 0
    misses = []
    for session_id, messages in by_session.items():
        messages.sort()
        for i, (created, input_tokens, cache_read, cost) in enumerate(messages):
            total += 1
            if cache_read > 0:
                hit += 1
            elif input_tokens > 20000 and i > 0:
                misses.append((int((created - messages[i - 1][0]) / 1000), input_tokens, round(cost, 4)))

    if total == 0:
        print(f"no assistant messages for {provider}/{model}")
        return 1

    print(f"{provider}/{model}: {total} assistant messages in {len(by_session)} session(s)")
    print(f"cache-hit: {hit} ({100 * hit / total:.1f}%)   misses: {len(misses)}   re-billed: ${sum(m[2] for m in misses):.3f}")
    for idle, input_tokens, cost in sorted(misses, reverse=True):
        print(f"  idle={idle}s input={input_tokens} cost=${cost}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
