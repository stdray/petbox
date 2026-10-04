# MCP parallel tool-call argument corruption — расследование

Материалы расследования порчи аргументов tool-call при параллельных вызовах через
MCP в сессиях pi. Карточки на доске `$system`/work:

- **`pi-mcp-parallel-call-arg-corruption`** — расследование ( Pending → Review).
- **`mcp-nullable-schema-anyof`** — митигация на нашей стороне (anyOf вместо array-form).
- **`opencode-go-gateway-issue`** — issue в `anomalyco/opencode` (upstream-трек).

## Выводы (2026-10-04)

### Категоризация исходных инцидентов

64 MCP-инцидента в 105 JSONL-транскриптах `~/.pi/agent/sessions`:

| Сторона | Инцидентов | Доля |
|---|---|---|
| Харнесс pi / граница модель↔pi (порча аргументов) | 31 | 48% |
| Модель (неверные имена/пропуски параметров) | 33 | 52% |
| Сервер PetBox (дефекты) | 0 | 0% |

> **⚠️ Агрегаты выше подлежат пересчёту** (замечание reviewer 2026-10-04:
> классификатор не был приложен как манифест, числа не пересчитаны независимо).
> Методика пересчёта — раздел «Методика» ниже. Пересчёт НЕ сделан; карточка
> `pi-mcp-parallel-call-arg-corruption` содержит упоминание об этом.

### Точный триггер

Дефект воспроизводится **мимо pi**, чистым HTTP к провайдеру, и зависит ровно
от трёх факторов:

1. **Форма nullable в JSON-схеме инструмента**: только array-form
   `"type": ["string","null"]` ломает. Эквивалентный `anyOf` — чист.
   `type: ["integer","null"]` на любом из параметров — достаточно.
2. **Шлюз OpenCode Go × модель mimo**: deepseek через тот же шлюз чист,
   longcat через тот же шлюз чист, mimo через openrouter чист.
3. **Streaming + tools** (по аналогии с upstream #40888).

Симптомы в сыром потоке: оба tool_call стартуют с `index: 0` (коллизия),
нативные аргументы обрезаны, полный текст вызова утекает в `content` как XML,
`finish_reason: tool_calls` приходит нормально.

### Матрица raw-HTTP (без pi, без MCP)

См. `issue-repro-mimo-v26-flash.txt`; воспроизводится `scripts/issue-repro.py`:

| Модель | Маршрут | union (array-form) | anyOf | simple |
|---|---|---|---|---|
| mimo-v2.6-flash | opencode-go | **BROKEN [0,0]** | OK | OK |
| mimo-v2.6-flash | openrouter | OK | OK | OK |
| mimo-v2.6-pro | opencode-go | **BROKEN [0,0]** | — | OK |
| mimo-v2.6-pro | openrouter | **BROKEN (1 native call + XML)** | — | OK |
| longcat-2.5-preview-free | opencode-go | OK | — | OK |

### E2e через pi + MCP

Локальный stdio-сервер `mcp-probe-union/server.js` (union) /
`server-anyof.js` (anyOf), конфиги в `probe-project/`:

- union через `opencode-go/mimo-v2.6-flash`: 5/5 сессий clobber (все вызовы отклонены);
- anyOf через то же: 5/5 сессий с чистыми параллельными вызовами.

**Оговорка reviewer:** тест-сервер не валидирует аргументы — `limit: "3"`
(строкой) проходил благодаря client-side коэрсии pi (`validation.ts`), а не
серверу. Для подтверждения типов нужен валидирующий сервер (см. AC карточки
`mcp-nullable-schema-anyof`).

### Атрибуция: два фактора, не один

- **Модель mimo** реагирует на array-form (mimo-pro ломается и через openrouter);
- **Шлюз** добавляет свой вклад (mimo-flash чист на openrouter, 5/5 битых на
  opencode-go при идентичном payload).

Клиент pi: вторичный вклад — `parseStreamingJson` чинит обрывки в валидный JSON
вместо явной ошибки (отдельное issue в `earendil-works/pi`, если пойдём туда).

## Как воспроизвести

Все скрипты читают ключи из env (`OPENCODE_API_KEY`, `OPENROUTER_API_KEY`),
секретов в файлах и логах нет.

### 1. Raw-HTTP матрица (главное доказательство)

```bash
export OPENCODE_API_KEY=... OPENROUTER_API_KEY=...
python3 scripts/issue-repro.py mimo-v2.6-flash 5
```

Route `go` × schema `union` → BROKEN; `go` × `anyof` → OK; `or` × `union` → OK.

### 2. Через pi (матрица моделей на реальном MCP petbox)

```bash
bash scripts/ab-parallel-mcp.sh <label> <model-pattern> <N>
# напр.: bash scripts/ab-parallel-mcp.sh mimo opencode-go/mimo-v2.6-flash 20
```

Прогоняет `pi --print` с промптом, принуждающим два параллельных
`mcp__petbox__memory_search` в одном блоке; анализ — по JSONL в `./ab-mcp/<label>/`
(native args ≠ `{q:str, limit:int}` = дефект). Этот скрипт завязан на petbox
MCP (этап A/B); для публичного репро — `issue-repro.py`.

### 3. E2e через pi + локальный MCP (без petbox)

```bash
# конфиг положить как .pi/mcp.json в пустой проект (см. probe-project/*.json),
# путь к серверу поправить; затем:
pi --print --model opencode-go/mimo-v2.6-flash --session-dir ./s --session-id p1 \
   --no-skills --no-prompt-templates --no-context-files \
   "Вызови РОВНО ДВА раза mcp__probe_union__probe ОДНИМ блоком: ..."
```

### 4. Захват сырого транспорта в pi

`scripts/sse-capture.ts` — расширение pi: пишет `before_provider_request`,
`before_provider_headers` и `provider_stream_event` в JSONL
(`PROBE_SSE_OUT=<file> pi -e scripts/sse-capture.ts --print ...`).
Обратите внимание: `provider_stream_event` — chunk **до нормализации pi**, но
не исходные SSE-байты; для исходных байтов — `scripts/raw-repro.py` (свой HTTP-клиент).

### 5. Прямой raw-HTTP (двух-instrument вариант)

```bash
python3 scripts/raw-repro.py go mimo-v2.6-flash raw.jsonl 3
```

## Методика пересчёта агрегатов (не выполнена)

Чтобы числа выше стали воспроизводимыми, пересчёт должен:

1. **Манифест сессий**: явный список JSONL-файлов (`~/.pi/agent/sessions/**/*.jsonl`),
   попавших в выборку, с датами и моделями.
2. **Правило подсчёта дефекта**: native args toolCall ≠ ожидаемой схемы
   (пустые args; объект вместо скаляра; только первый параметр; аргументы
   соседнего вызова). Ровно это правило реализовано в анализаторах этих скриптов.
3. **Правило категории**: «харнесс» = порча аргументов при валидных параметрах в
   TEXT-блоке того же сообщения; «модель» = неверное имя/пропуск required/эхо
   response-полей; «сервер» = отказ при корректном вводе.
4. **Отчёт**: счётчики по категориям с путями к файлам, чтобы независимый
   пересчёт давал те же числа.

## Структура

```
research/mcp-parallel-arg-corruption/
├── README.md                            # этот файл
├── issue-repro-mimo-v26-flash.txt       # raw-HTTP A/B лог (union/anyof/simple × go/or)
├── scripts/
│   ├── issue-repro.py                   # минимальный raw-HTTP репро (главное доказательство)
│   ├── raw-repro.py                     # raw-HTTP, два инструмента, дамп кадров
│   ├── ab-parallel-mcp.sh               # A/B прогон через pi (petbox MCP)
│   └── sse-capture.ts                   # pi-расширение: захват request/stream events
├── mcp-probe-union/
│   ├── server.js                        # stdio MCP, union-схема (триггер)
│   └── server-anyof.js                  # stdio MCP, anyOf-схема (контроль)
└── probe-project/
    ├── mcp-array-form.json              # .pi/mcp.json для union-варианта
    └── mcp-anyof.json                   # .pi/mcp.json для anyOf-варианта
```

Не включены (большие/содержат локальные пути): JSONL-сессии пробников
(`.tmp/ab-mcp/`, `.tmp/probe-project*/`), клон `.tmp/pi-src`, сырые SSE-кадры
`.tmp/raw-go-mimo.jsonl` (15 КБ) — при необходимости воспроизводятся скриптами.
