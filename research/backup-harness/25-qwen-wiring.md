# Qwen Code: обвязка PetBox — две блокирующие проверки

Дата: 2026-09-08 | Исполнитель: Opus 5 (1M), petbox-worker-highstakes.
Всё в Docker (`node:22-bookworm`, контейнер `qwen-wire`, удалён `docker rm -f`).
Qwen Code **0.23.0** (`npm i -g @qwen-code/qwen-code@0.23.0`, `qwen --version` → `0.23.0`).
Модель — DeepSeek через OpenAI-совместимый `https://api.deepseek.com/v1`, `-m deepseek-chat`.
Ключи не печатались: `PETBOX_API_KEY` и ключ DeepSeek заезжали через `--env-file`, файл
удалён по завершении. В PetBox — только чтение (`whoami`, `memory_search limit=2`).

## Проверка 1 — MCP с нашим сервером: **НЕ блокирует D1**

Форма из `.factory/mcp.json` (`type:http` + `url` + `headers.X-Api-Key: ${PETBOX_API_KEY}`)
переложена в `~/.qwen/settings.json` как `httpUrl` + тот же `headers` (у Qwen Code
streamable-http живёт в поле `httpUrl`, а не `url`+`type`; `url` — это SSE).

| шаг | команда | результат |
|---|---|---|
| (а) сервер подключён | `qwen mcp list` | `✓ petbox: https://petbox.3po.su/mcp (http) - Connected` |
| (б) вербы в списке для модели | logging-proxy на `OPENAI_BASE_URL`, дамп `tools[]` исходящего запроса | **122 тула всего, из них 95 `mcp__petbox__*`** (`apikey_*`, `comments_*`, `config_binding_*`, …), рядом штатные `agent`, `edit`, `enter_worktree` |
| (в) реальный вызов | `qwen --approval-mode yolo -p "call petbox whoami…"` | `{"project":"$system","scopes":["config:read",…,"admin:provision"]}` |
| (в) второй читающий верб | `-p "…memory_search query=canon limit=2…"` | 2 результата, ключи `m-973eccdb…` (store `notes`), `ac-79784c1f…` (store `autocaptured`) |

Шаг (б) — не косвенный вывод: перехвачен ровно тот массив `tools`, который уходит в
модель. Транспорт HTTP поддержан, stdio-мост не понадобился.

### Ловушка, которую надо знать до внедрения (цена — одна строка конфига)

Наш репозиторный `.mcp.json` **не заработает как есть**, и по двум независимым причинам:

1. **Approval-гейт по scope.** `.mcp.json` (scope `project`) и `.qwen/settings.json`
   (scope `workspace`) — «untrusted», держатся за гейтом:
   ЗАМЕРЕНО `qwen mcp list` в каталоге с нашим `.mcp.json` → `● petbox: … - Pending approval`.
   Снимается `qwen mcp approve petbox` (работает headless).
   Код: `packages/core/src/config/mcp-server-config.ts:40-48` (`isGatedMcpScope`).
2. **`${VAR}` в `.mcp.json` НЕ раскрывается.** После `approve` сервер оставался
   `✗ Disconnected` (три прогона подряд), и модель честно отвечала «no `petbox` MCP tools
   are available». Подстановка кладётся в тот же `.mcp.json` литеральным ключом → сразу
   `✓ Connected`. Значит дело не в гейте, а в env-резолвере: `resolveEnvVarsInObject`
   применяется к system/user/workspace **settings**-файлам
   (`packages/cli/src/config/settings.ts:1212-1222`), а `.mcp.json` мимо него.

**Рабочие формы:** `~/.qwen/settings.json` (user-scope: `${VAR}` раскрывается И гейта нет —
именно так прошли все замеры выше) либо `--mcp-config` (верхний приоритет, тоже без гейта,
`packages/cli/src/config/config.ts:1210-1262`). Класть секрет литералом в репозиторный
`.mcp.json` — нельзя.

## Проверка 2 — транскрипт на завершении: **НЕ блокирует D1**

Утверждение из `14-cand-others.md` про родственный gemini-cli (SessionEnd без транскрипта)
**на Qwen Code не переносится**. Исход — лучший из трёх: **путь есть, и файл по нему лежит**.

По коду: `createBaseInput()` кладёт `transcript_path` во ВСЕ события, включая SessionEnd —
`packages/core/src/hooks/hookEventHandler.ts:932-947`; значение —
`<projectDir>/chats/<sessionId>.jsonl` из `getTranscriptPath()`,
`packages/core/src/config/config.ts:9003-9011`.

Живьём: хук `SessionEnd → cat > /tmp/sessionend.json` в `~/.qwen/settings.json`,
интерактивный `qwen -i` под pty, один ход, затем `/quit`. Пришло:

```json
{"session_id":"e9231075-…","transcript_path":"/root/.qwen/projects/-work/chats/e9231075-….jsonl",
 "cwd":"/work","hook_event_name":"SessionEnd","timestamp":"2026-09-08T10:17:58.949Z",
 "reason":"prompt_input_exit"}
```

Файл по этому пути на момент работы хука существует и **уже дописан**: 4063 байта, 6 строк
JSONL, среди них `user` (мой промпт) и `assistant` (ответ модели), схема с
`uuid`/`parentUuid`/`sessionId` — то есть выгружать можно прямо из хука, не гоняясь за
флашем. Порядок закреплён в дизайне: «SessionEnd hooks run after the writer phase»
(`docs/design/managed-session-writer-shutdown.md:59`).

### Две оговорки, каждая с ценой

- **В headless (`-p`) SessionEnd НЕ срабатывает.** ЗАМЕРЕНО: после `qwen -p "say hi"`
  (exit 0) файл хука не появился. По коду `fireSessionEndEvent` зовётся только из TUI
  (`AppContainer.tsx:1290`, `start-opentui-ui.tsx:410`, оба `prompt_input_exit`), из
  `/clear` (`clearCommand.ts:67`) и из ACP (`acpAgent.ts:2951`) — headless-пути там нет.
  **Но транскрипт headless всё равно пишет**: тот же прогон оставил
  `chats/3ceec2eb-….jsonl`, 17362 байта. Цена: для headless-прогонов выгрузку вешать не на
  хук, а на wrapper (путь выводится из `session_id`, каталог тот же).
- **`chatRecording: false` обнуляет путь.** `getTranscriptPath()` возвращает **пустую
  строку**, а не бросает (`config.ts:9004-9006`) — ровно те грабли, что записаны в D5 по
  DeepSeek Harness («`transcript_path` может приходить пустым»). Цена: адаптер обязан
  проверять пустую строку, иначе выгрузка молча запишет мусор.

## Вердикты

| проверка | исход | блокирует D1 |
|---|---|---|
| 1. MCP с нашим сервером | вербы доезжают до модели, реальные вызовы отрабатывают | **не блокирует** |
| 2. Транскрипт на SessionEnd | путь в хуке есть, файл на диске готов | **не блокирует** |

Решение D1 (Qwen Code запасной обвязкой) обеими проверками **устояло**.

## Что осталось непроверенным

- Пункты 3 и 4 списка «Что проверить ДО внедрения» — инжект канона в ПЕРВЫЙ ход (гонка из
  D5) и TUI-гигиена home/end. Не входили в задачу.
- `reason` проверен только для `prompt_input_exit`; `clear`/`logout`/
  `bypass_permissions_disabled` живьём не гонял (в enum есть — `hooks/types.ts:1050-1056`).
- Поведение MCP при перезапуске/`qwen mcp reconnect`, а также OAuth-ветка сервера — не
  трогал (наш сервер на статичном заголовке).
- Транскрипт субагента (отдельный ли файл, попадает ли в родительский) — не смотрел.
- Не проверял, доезжают ли ПИШУЩИЕ вербы: по границам задачи в PetBox только чтение.
