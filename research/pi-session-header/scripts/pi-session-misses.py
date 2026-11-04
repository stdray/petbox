"""Промахи промпт-кеша в JSONL-сессии pi.

    python pi-session-misses.py <session.jsonl> [--threshold 20000]

Промах = ответ ассистента с cacheRead == 0 и input > threshold. Рядом печатается простой
между ходами и семейство responseId: смена семейства означает, что запрос обслужил другой
апстрим, а не что префикс вытеснен по времени.
"""
import datetime
import json
import re
import sys


def family(response_id: str) -> str:
    if not response_id:
        return "(none)"
    m = re.match(r"^(gw-[0-9a-f]+|chatcmpl|[0-9a-f]{16})", response_id)
    return m.group(1) if m else response_id[:12]


def main() -> int:
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    if not args:
        print(__doc__)
        return 2
    threshold = 20000
    for a in sys.argv[1:]:
        if a.startswith("--threshold"):
            threshold = int(a.split("=", 1)[1]) if "=" in a else threshold

    rows = []
    with open(args[0], encoding="utf-8") as fh:
        for line in fh:
            try:
                entry = json.loads(line)
            except ValueError:
                continue
            msg = entry.get("message") or {}
            if entry.get("type") != "message" or msg.get("role") != "assistant":
                continue
            usage = msg.get("usage") or {}
            rows.append(
                {
                    "ts": datetime.datetime.fromisoformat(entry["timestamp"].replace("Z", "+00:00")),
                    "input": usage.get("input", 0),
                    "cache_read": usage.get("cacheRead", 0),
                    "cost": (usage.get("cost") or {}).get("total", 0),
                    "response_id": msg.get("responseId") or "",
                    "model": msg.get("model"),
                }
            )

    if not rows:
        print("no assistant messages found")
        return 1

    hits = sum(1 for r in rows if r["cache_read"] > 0)
    misses = 0
    missed_cost = 0.0
    print(f"assistant messages: {len(rows)}  cache-hit: {hits} ({100 * hits / len(rows):.1f}%)")
    print(f"\n{'idle':>8} {'input':>8} {'cost$':>8}  upstream family")
    for i in range(1, len(rows)):
        cur, prev = rows[i], rows[i - 1]
        if cur["cache_read"] == 0 and cur["input"] > threshold:
            misses += 1
            missed_cost += cur["cost"]
            idle = int((cur["ts"] - prev["ts"]).total_seconds())
            print(f"{idle:>7}s {cur['input']:>8} {cur['cost']:>8.4f}  {family(prev['response_id'])} -> {family(cur['response_id'])}")

    total = sum(r["cost"] for r in rows)
    print(f"\nmisses: {misses}  re-billed: ${missed_cost:.3f}  session total: ${total:.3f}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
