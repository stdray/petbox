#!/usr/bin/env bash
# A/B probe: force parallel MCP tool calls, record how args arrive in session JSONL.
# Usage: ab-parallel-mcp.sh <label> <model-pattern> <iterations>
set -u
LABEL="${1:?label}"
MODEL="${2:?model}"
N="${3:-10}"
# Output goes to ./ab-mcp/<label>/ relative to the CWD you run from.
ROOT="./ab-mcp/$LABEL"
mkdir -p "$ROOT"
rm -f "$ROOT"/*.jsonl 2>/dev/null

PROMPT='Вызови РОВНО ДВА инструмента mcp__petbox__memory_search ОДНИМ блоком (параллельно, два tool-call в одном ответе): первый с q="pi mcp probe alpha" и limit=3, второй с q="pi mcp probe beta" и limit=7. Не вызывай ничего больше. Ответь строго: DONE'

for i in $(seq 1 "$N"); do
  pi --print \
    --model "$MODEL" \
    --session-dir "$ROOT" \
    --session-id "probe-$(printf '%03d' "$i")" \
    --no-skills --no-prompt-templates --no-context-files \
    --exclude-tools read,bash,edit,write,subagent,web_search,source_check,fetch_content,get_search_content,codemode \
    "$PROMPT" > "$ROOT/run-$i.out" 2>&1
  echo "run $i exit=$?"
done
echo "LABEL=$LABEL MODEL=$MODEL N=$N sessions in $ROOT"
