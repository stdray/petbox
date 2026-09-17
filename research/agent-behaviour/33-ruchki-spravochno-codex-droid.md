# Ручки двух харнессов: codex, droid (справочно, вне проектирования)

Те же 7 осей, что в `32-ruchki-osnovnaya-troika.md` (claude-code/qwen/opencode). Только справка "что бывает и почему", без выводов про PetBox.

## Codex (OpenAI Codex CLI)

### 1. Каталог моделей
Закрыт по built-in, открыт двумя путями: провайдер и весь реестр целиком. `[model_providers.<id>]`: `name`, `base_url`, `env_key`, `wire_api`, `http_headers`, `query_params` [дока https://learn.chatgpt.com/docs/config-file/config-reference]. `model_catalog_json` — "Optional path to a JSON model catalog loaded on startup", оверрайдится per-profile [та же дока]: не одна модель, а подменяемый файлом реестр (`slug`, `context_window`, `supported_reasoning_levels`, `truncation_policy`...). Локально (иллюстрация): `~/.codex/petbox-model-catalog.json`.

### 2. Уровень рассуждения
Ключ `model_reasoning_effort`, словарь `minimal|low|medium|high|xhigh` ("Responses API only; `xhigh` model-dependent") [дока https://learn.chatgpt.com/docs/config-file/config-reference]. Дефолт для субагентов — `agents.default_subagent_reasoning_effort`, но "explicit spawn effort takes precedence" [та же дока] — то есть живёт и в параметре вызова `spawn_agent`. Отдельно `plan_mode_reasoning_effort` для режима планирования [та же дока]. Допустимые значения на модель сужает поле каталога `supported_reasoning_levels` (иллюстрация из локального файла).

### 3. Пределы: контекст и вывод
`model_context_window`: "Context window tokens available to the active model", объявляем [дока, тот же источник]. Ключа `model_max_output_tokens` в текущей схеме НЕТ (проверено построчным разбором JSON-дампа страницы) — упоминания в сторонних блогах не подтвердились [схема https://learn.chatgpt.com/docs/config-file/config-reference]. Рядом — иные по смыслу пределы: `tool_output_token_limit` (хранение вывода одного tool-вызова) и `model_auto_compact_token_limit` (порог компакции, не потолок). Поведение при необъявленном `model_context_window` — [НЕ УСТАНОВЛЕНО] явно.

### 4. Бюджет исполнения
Лимита ходов/шагов/времени на основной прогон не найдено; есть только лимит на параллелизм субагентов. `agents.max_concurrent_threads_per_session`: "Maximum...spawned-agent threads...concurrently. When unset, Codex chooses the default" [дока]. `features.rollout_budget.*` (токен-бюджет всего rollout) помечен "under development and off by default" [та же дока]. Полный обход ключей схемы не выявил `max_turns`/timeout для одиночного запуска.

### 5. Транспорт и авторизация
`base_url`+`env_key`+`wire_api`+заголовки на провайдера. Формат провода один: `wire_api`: "`responses` is the only supported value" [дока] (Chat Completions убран). `openai_base_url` — URL встроенного `openai`; `chatgpt_base_url` — отдельно для логина [та же дока]. `mcp_servers` — allowlist по имени+identity, несовпавшее отключается молча [та же дока]. `env_key` — имя ENV-переменной, не сам ключ.

### 6. Определение субагента
Нет файла-персоны (в отличие от droid/claude-code) — инструмент `spawn_agent` + глобальные дефолты. `agents.enabled`, `agents.default_subagent_model`, `agents.default_subagent_reasoning_effort`, `agents.max_concurrent_threads_per_session`, `agents.interrupt_message` [дока]. Вызов `spawn_agent` переопределяет модель и `reasoning_effort` за конкретный запуск — приоритетнее дефолта [та же дока, подтверждено issue https://github.com/openai/codex/issues/26948]. Пер-ролевая таблица (`developer_instructions`, `sandbox_mode`, MCP на роль, "multi_agent_v2") упомянута только в сторонних блогах, в официальной схеме такого ключа нет [блог https://codex.danielvaughan.com/2026/04/11/codex-cli-multi-agent-orchestration-v2-complete-guide/, НЕ подтверждено дока].

### 7. Зачем такой набор
`model_providers`/`model_catalog_json` — под сторонние гейтвеи и альт-источники моделей [обобщение по дока]. Ограничение `wire_api` до одного значения задокументировано как факт, причина не названа [НЕ УСТАНОВЛЕНО]. Отсутствие лимита ходов на прогон и дефолт параллелизма "Codex chooses the default" — без пояснения выбора [НЕ УСТАНОВЛЕНО].

## Droid (Factory)

### 1. Каталог моделей
Закрытый built-in список + открытый BYOK-каталог. Built-in — таблица [дока https://docs.factory.ai/models], источник истины для допустимых `reasoningEffort` на модель. BYOK (`customModels[]`): `model`, `displayName`, `baseUrl`, `apiKey`, `provider`, `maxOutputTokens`, `noImageSupport`, `extraArgs`, `extraHeaders`, `apiKeyHelper` (shell-команда для динамических кредов), `authMode` (`provider-default`|`bearer`) [дока https://docs.factory.ai/cli/byok/overview, https://docs.factory.ai/cli/byok/openai-anthropic]. `provider` = формат провода: `anthropic` (Messages API), `openai` (Responses API), `generic-chat-completion-api` (Chat Completions — OpenRouter/Fireworks/Ollama) [та же дока]. Локально (иллюстрация): `~/.factory/settings.json` → `customModels[]`, 13 записей поверх DeepSeek/opencode.ai-Zen.

### 2. Уровень рассуждения
Поле/флаг `reasoningEffort`, но не в записи модели — вокруг неё. Уровни модель-зависимы: `off|none|minimal|low|medium|high|xhigh|max|dynamic`, "Available levels and the default are set by each model" [дока https://docs.factory.ai/droid-cli/settings]. Места: `sessionDefaultSettings.reasoningEffort` (дефолт сессии), CLI `-r/--reasoning-effort`, фронтматтер droid `reasoningEffort` (игнор при `model: inherit`) [дока https://docs.factory.ai/cli/configuration/custom-droids], Mission Mode — `--worker-reasoning-effort`/`--validator-reasoning-effort` [дока https://docs.factory.ai/droid-cli/cli-reference]. Свой энкодинг мышления — через `extraArgs` (иллюстрация: `extraArgs.thinking.budget_tokens` у локальной записи).

### 3. Пределы: контекст и вывод
`maxOutputTokens` — поле `customModels[]`, объявляем [дока byok/overview]. Поведение при необъявленном значении — [НЕ УСТАНОВЛЕНО]. Контекст отдельным полем не декларируется; вместо потолка — порог авто-компакции: `compactionTokenLimit`/`compactionTokenLimitPerModel` [дока https://docs.factory.ai/droid-cli/settings] — это про то, когда сжимать историю, не жёсткий предел.

### 4. Бюджет исполнения
`--auto low|medium|high` — ось риска доступа (от правок файлов до деплоев/миграций БД), плюс `--skip-permissions-unsafe` [дока https://docs.factory.ai/droid-exec/overview]. В `cli-reference` лимит ходов/шагов/таймаут/токен-бюджет на прогон не задокументирован [дока https://docs.factory.ai/droid-cli/cli-reference, констатация отсутствия]. Mission Mode даёт отдельные модель+effort для worker/validator ролей, но не отдельный бюджет шагов.

### 5. Транспорт и авторизация
`FACTORY_API_KEY=fk-...` для хостед-аутентификации [дока droid-exec/overview]. На кастомную модель: `baseUrl`+`apiKey`(или `apiKeyHelper`)+`provider`(формат)+`extraHeaders`+`extraArgs` [дока byok/overview]. MCP — 3 транспорта (`stdio`/`http`/`sse`), конфиг `~/.factory/mcp.json` (user, приоритетнее) и `.factory/mcp.json` (project), `droid mcp add ... --env --header --no-oauth` [дока https://docs.factory.ai/cli/configuration/mcp].

### 6. Определение субагента
Файл `.md` с YAML-фронтматтером, сравнимо с claude-code, набор полей уже. `name` (обязательно), `description`, `model` (id или `inherit`), `reasoningEffort` (`low|medium|high`, игнор при `inherit`), `tools` (пусто=все / категория / массив id), `mcpServers` (пропуск = наследование родителя) [дока https://docs.factory.ai/cli/configuration/custom-droids]. НЕ документированы в этом фронтматтере: права на запись, изоляция/worktree, лимит шагов на субагента — [НЕ УСТАНОВЛЕНО]; worktree — только CLI-флаг верхнего уровня (`-w`), не поле субагента. Иллюстрация: `~/.factory/droids/worker.md` — только `name/description/model: inherit`.

### 7. Зачем такой набор
BYOK — прямая формулировка: "use your own OpenAI or Anthropic keys, connect to any open source model providers, or run models locally on your hardware" [дока https://docs.factory.ai/cli/byok/overview] — вендоронезависимость и self-hosting. `--auto` явно про риск операции (blast radius), не про стоимость [дока droid-exec/overview]. Модель-зависимые дефолты `reasoningEffort` без пояснения выбора — [НЕ УСТАНОВЛЕНО]. Отсутствие лимита ходов/времени в доке — факт без названной причины [НЕ УСТАНОВЛЕНО].

## Сравнение по осям

| Ось | Codex | Droid |
|---|---|---|
| 1. Каталог моделей | Открыт: `model_providers` (провайдер) + `model_catalog_json` (весь реестр файлом) | Закрытый built-in + открытый BYOK `customModels[]` (`provider`=формат провода) |
| 2. Effort | `model_reasoning_effort` в конфиге/профиле; на спавн — параметр `spawn_agent` приоритетнее дефолта | Сессионный дефолт / CLI-флаг / фронтматтер droid / роль Mission; модель-зависимый словарь |
| 3. Контекст/вывод | `model_context_window` объявляем; ключа потолка вывода НЕТ (есть смежные: tool_output_token_limit, auto-compact) | `maxOutputTokens` на модель объявляем; контекст не декларируется — только порог авто-компакции |
| 4. Бюджет исполнения | Лимит только на параллелизм субагентов; лимита ходов на прогон нет | `--auto` — ось риска операций, не длины; лимита ходов/времени на прогон нет |
| 5. Транспорт/авторизация | `base_url`+`env_key`+один `wire_api=responses`; `model_catalog_json` как отдельный канал | `baseUrl`+`apiKey`/`apiKeyHelper`+`provider`(=формат) на модель; MCP 3 транспорта, `FACTORY_API_KEY` |
| 6. Субагент переопределяет | Нет файла-персоны; `spawn_agent` — модель+effort по вызову; дефолты/рубильник в `agents.*` | `.md`+YAML: model, reasoningEffort, tools, mcpServers; права/изоляция/бюджет шагов не документированы |
| 7. Явная причина в доке | Гейтвеи/альт-источники — да; ограничение wire_api и дефолт параллелизма — не объяснены | Вендоронезависимость/self-hosting — да; `--auto` про риск операции; effort-дефолты и бюджет ходов — не объяснены |
