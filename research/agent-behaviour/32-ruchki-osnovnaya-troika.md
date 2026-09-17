# Ручки трёх харнессов: claude-code, qwen-code, opencode

Цель: пространство настраиваемых параметров по 7 осям. Не аудит окружения — обзор возможностей.

## Claude Code

### 1. Каталог моделей
Закрытый по вендору (семейство Claude), открытый по транспорту.
Built-in алиасы `opus/sonnet/haiku/fable/best`; picker показывает только id с префиксом `claude`/`anthropic` [дока https://code.claude.com/docs/en/model-config]. Один произвольный id можно добавить в picker через `ANTHROPIC_CUSTOM_MODEL_OPTION`, не заменяя built-in [дока https://code.claude.com/docs/en/model-config]. `ANTHROPIC_BASE_URL` и семейство `*_BASE_URL` (Bedrock/Vertex/Foundry/AWS) меняют эндпоинт, но wire-формат остаётся Anthropic Messages API — сторонние модели заводятся только через гейтвей-транслятор [дока https://code.claude.com/docs/en/env-vars].

### 2. Уровень рассуждения
Есть, ось `effort`, отдельно от каталога моделей.
Иерархия: `CLAUDE_CODE_EFFORT_LEVEL` / `--effort` / `/effort` → дефолт модели → сохранённый в settings уровень → `effortLevel` в settings → дефолт модели `high` [дока https://code.claude.com/docs/en/model-config]. Словарь: `low, medium, high, xhigh, max` (набор зависит от модели) [дока https://code.claude.com/docs/en/model-config]. Отдельно живёт «thinking budget» на уровне API (`budget_tokens`/adaptive thinking) — устройство зависит от версии модели [дока https://docs.claude.com/en/docs/build-with-claude/extended-thinking].

### 3. Пределы: контекст и вывод
Контекстное окно берётся из реестра модели; пользовательский override — только запасной вариант для нераспознанных id.
`CLAUDE_CODE_MAX_CONTEXT_TOKENS` — "assumed context window for unrecognized model IDs" [дока https://code.claude.com/docs/en/env-vars]; `CLAUDE_CODE_DISABLE_1M_CONTEXT=1` обрезает нативное 1M-окно до 200K [дока https://code.claude.com/docs/en/model-config]. `autoCompactWindow` управляет порогом компакции, не потолком. Явного user-facing override максимума вывода в документации не найден [НЕ УСТАНОВЛЕНО].

### 4. Бюджет исполнения
Задаётся на уровне запуска и на уровне субагента, глобального дефолта нет.
`--max-turns` — потолок ходов на headless-запуск (`claude -p`), без лимита по умолчанию [дока https://code.claude.com/docs/en/settings-reference (через поиск)]. `--max-budget-usd` — денежный бюджет запуска. `maxTurns` в frontmatter субагента — по агенту [дока https://code.claude.com/docs/en/sub-agents]. `CLAUDE_ASYNC_AGENT_STALL_TIMEOUT_MS` — таймаут простоя субагента (10 мин по умолчанию) [дока https://code.claude.com/docs/en/env-vars].

### 5. Транспорт и авторизация
Широкий набор base-URL/ключей, но один wire-протокол.
`ANTHROPIC_BASE_URL`, `ANTHROPIC_BEDROCK_BASE_URL`, `ANTHROPIC_VERTEX_BASE_URL`, `ANTHROPIC_FOUNDRY_BASE_URL`, `ANTHROPIC_AWS_BASE_URL`; ключи `ANTHROPIC_API_KEY`/`ANTHROPIC_AUTH_TOKEN`/AWS- и Foundry-специфичные; `ANTHROPIC_CUSTOM_HEADERS`; federation-переменные (`ANTHROPIC_ORGANIZATION_ID`+`ANTHROPIC_FEDERATION_RULE_ID`) [дока https://code.claude.com/docs/en/env-vars]. Формат провода — Anthropic Messages API; OpenAI-совместимость существует как отдельный продукт (OpenAI SDK compatibility layer), не как настройка Claude Code [дока https://platform.claude.com/docs/en/cli-sdks-libraries/libraries/openai-sdk].

### 6. Определение субагента
Наиболее полно документированная ось из трёх харнессов.
`model` (`sonnet/opus/haiku/fable/inherit`/full id), `tools`/`disallowedTools` (allow/denylist), `permissionMode`, `maxTurns`, `effort`, `skills`, `mcpServers`, `hooks`, `memory` (`user/project/local`), `background`, `isolation: worktree`, `color`, `initialPrompt`, `experimental.cacheTtl` [дока https://code.claude.com/docs/en/sub-agents]. Переопределяет: модель, усилие, набор инструментов, права (permissionMode), пределы (maxTurns), изоляцию (worktree), персистентность памяти.

### 7. Зачем
`--max-turns`/`--max-budget-usd` явно поданы как guardrails "for unattended execution" — против runaway cost/time в CI [дока-производные из https://code.claude.com/docs/en/env-vars, https://code.claude.com/docs/en/sub-agents]. Семейство `*_BASE_URL` — под корпоративные облака (Bedrock/Vertex/Foundry) и прокси/гейтвеи, а не под произвольных вендоров моделей. Иерархия effort — под настройку цена/качество на уровне сессии, модели и отдельного субагента.

## Qwen Code

### 1. Каталог моделей
Открытый: пользователь объявляет провайдеров и модели сам.
`modelProviders` в settings.json — bucket по authType (`openai/gemini/anthropic/vertex-ai/qwen-oauth`), внутри массив моделей с полями `id`, `envKey` (имя env-переменной, не сам ключ — читается из `process.env[envKey]` в рантайме и не персистится), `baseUrl`, `name`, `description`, `generationConfig` [дока https://github.com/QwenLM/qwen-code/blob/main/docs/users/configuration/model-providers.md]. Произвольный authType заводится через top-level `providerProtocol: {customId: "openai"}` — без него нестандартные записи молча пропускаются [та же дока].

### 2. Уровень рассуждения
Живёт в записи каталога модели (`generationConfig.reasoning`), не в определении агента.
`reasoning: {effort: "low"|"medium"|"high"|"max", budget_tokens: N}`, либо `reasoning: false` для отключения; wire-маппинг разный по провайдеру — OpenAI/DeepSeek: `reasoning_effort`; OpenAI Responses API: `reasoning:{effort,summary}`; Anthropic: `output_config:{effort}` + beta-заголовок; Gemini: `thinkingConfig:{includeThoughts, thinkingLevel}` [дока model-providers.md].

### 3. Пределы: контекст и вывод
Объявляются в записи модели, есть явный механизм override.
`generationConfig.contextWindowSize` — переопределяет окно контекста для модели; вывод — `generationConfig.samplingParams.max_tokens` либо env `QWEN_CODE_MAX_OUTPUT_TOKENS` [дока model-providers.md]. Поведение при необъявленных полях (реестр/угадывание/ошибка) в документации явно не описано [НЕ УСТАНОВЛЕНО].

### 4. Бюджет исполнения
Многоуровневый и самый детализированный из трёх: настройка модели, флаг запуска, поле субагента.
`model.maxSessionTurns`, `model.maxToolCalls`, `model.maxToolCallsPerTurn`, `model.maxWallTimeSeconds`, `model.sessionTokenLimit` — все `-1` = без лимита по умолчанию [дока https://github.com/QwenLM/qwen-code/blob/main/docs/users/configuration/settings.md]. CLI `--max-session-turns N` для headless/CI. Превышение бюджета — `FatalBudgetExceededError`, exit code 55 [та же дока / https://qwenlm.github.io/qwen-code-docs/en/users/features/headless/]. В frontmatter субагента (Claude-Code-совместимый слой) также есть `maxTurns`.

### 5. Транспорт и авторизация
На уровне записи модели, не глобально.
`baseUrl` + `envKey` (имя переменной с ключом) на модель; `customHeaders` (с `${session_id}` при включённом `allowDynamicHeaderValues`), `extra_body` (только для OpenAI-совместимых) [дока model-providers.md]. `providerProtocol` явно отвязывает произвольный id провайдера от wire-формата (openai/gemini/anthropic/vertex-ai) — то есть транспорт и "какой это вендор" разнесены.

### 6. Определение субагента
Два слоя полей: свои + Claude-Code-совместимые.
Свои: `name`, `description`, `model` (`inherit/fast/modelId/authType:modelId`), `approvalMode` (`default/plan/auto-edit/yolo/bubble`), `tools`/`disallowedTools`, fork-специфичные `subagent_type: fork`, `fork_turns`, `fork_tools`, `fork_profile`, `working_dir` (git worktree pin), `run_in_background`. Совместимые с Claude Code: `permissionMode`, `maxTurns`, `color`, `mcpServers`, `hooks` [дока https://qwenlm.github.io/qwen-code-docs/en/users/features/sub-agents/]. Переопределяет: модель, права (approvalMode/permissionMode), инструменты, пределы, изоляцию рабочей директории (worktree), фоновый режим.

### 7. Зачем
`modelProviders`/`providerProtocol` документированы как способ маршрутизации через AI-гейтвеи (пример: TrueFoundry) и держать несколько coding-plan эндпоинтов под одним authType [дока model-providers.md, вывод из issue-обсуждений https://github.com/QwenLM/qwen-code/issues/4814]. Бюджетные поля (`maxSessionTurns/maxToolCalls/maxWallTimeSeconds`) явно объяснены для headless/CI: "чтобы застрявший агент не прожёг ресурсы" [дока https://qwenlm.github.io/qwen-code-docs/en/users/features/headless/].

## OpenCode

### 1. Каталог моделей
Открытый, трёхслойный: реестр + провайдерские интеграции + конфиг проекта.
Каталог строится из `models.dev`, провайдерских интеграций и project config; в выдаче — только модели с доступным провайдером [дока https://opencode.ai/v2/docs/models/]. Кастомный провайдер объявляется пакетом (например openai-compatible), `baseURL`, `apiKey` (с подстановкой `{env:VAR}`), `modelID` — маппинг дружественного имени на upstream id [та же дока]. Точное имя npm-пакета из независимой проверки схемы не подтвердилось (репозиторий схемы, который удалось получить, разошёлся с текстом доки) [НЕ УСТАНОВЛЕНО].

### 2. Уровень рассуждения
Живёт в каталоге модели (провайдер/модель/вариант), может быть продублирован в определении агента как provider-passthrough.
`reasoningEffort` настраивается на уровне провайдера (дефолт), модели (override) и варианта модели (override), значения `low/medium/high` [дока https://opencode.ai/v2/docs/models/]. В определении агента задокументирован как один из «provider-specific parameters», прокидываемых напрямую [дока https://opencode.ai/docs/agents/].

### 3. Пределы: контекст и вывод
Объявляются в `model.limit.context` / `model.limit.output`; при отсутствии — жёсткий дефолт, не угадывание и не ошибка.
Дефолты: 200 000 токенов контекста, 32 000 токенов вывода; дока прямо называет их допущением: «These values are assumptions, not model discovery. Configure accurate…values whenever they are known» [дока https://opencode.ai/v2/docs/models/].

### 4. Бюджет исполнения
На уровне агента, не глобального флага сессии.
`steps` — «Maximum agentic iterations allowed. Limits costs by forcing text-only responses after threshold» [дока https://opencode.ai/docs/agents/]. Существование отдельного глобального/сессионного лимита ходов, помимо per-agent `steps`, в собранных источниках не подтверждено [НЕ УСТАНОВЛЕНО].

### 5. Транспорт и авторизация
Выбор wire-формата явный, через выбор пакета провайдера.
`package` (реализация протокола), `baseURL`, `apiKey` (`{env:VAR}`), плюс per-модельные `headers`/`body`/`settings` для донастройки запроса [дока https://opencode.ai/v2/docs/models/]. В отличие от Claude Code, формат провода не фиксирован — определяется выбранным пакетом провайдера (openai-compatible, anthropic и т.д.).

### 6. Определение субагента
Через общий формат «agent» с `mode: subagent`.
`mode` (`primary/subagent/all`), `description`, `model` (`provider/model-id`), `prompt` (файл), `temperature`, `top_p`, `permission` — объект по действиям (`read/edit/bash/glob/grep/list/task/external_directory/webfetch/websearch/lsp/skill`) со значениями `allow/ask/deny`, `steps`, `disable`, `hidden`, плюс provider-passthrough поля типа `reasoningEffort`/`textVerbosity` [дока https://opencode.ai/docs/agents/]. Легаси-поле `tools` (true/false по инструменту) помечено устаревшим в пользу `permission`. Переопределяет: модель, усилие (через passthrough), права по категориям действий (включая явный `task`-permission — какие субагенты может вызывать другой агент), пределы (`steps`), видимость (`hidden`).

### 7. Зачем
Реестр `models.dev` + пакетная архитектура провайдеров явно нацелены на широкий мульти-вендорный охват (документация заявляет поддержку 75+ моделей) и совместимость с гейтвеями (OpenRouter и т.п.) [дока https://opencode.ai/v2/docs/models/, https://open-code.ai/en/docs/models]. Формулировка «assumptions, not model discovery» для лимитов контекста/вывода — прямое признание, что реестр может быть неточным/устаревшим, и ручка override существует именно на этот случай. Permission-модель по категориям действий (а не только allow-list инструментов) — под контроль безопасности/изоляции на уровне субагента, а не только под цену.

## Сравнение по осям

| Ось | Claude Code | Qwen Code | OpenCode |
|---|---|---|---|
| 1. Каталог моделей | Закрыт по вендору (Claude family); транспорт гибкий через `*_BASE_URL` | Открыт: `modelProviders` + `providerProtocol` для произвольных id | Открыт: `models.dev` реестр + провайдерские пакеты + project config |
| 2. Effort | Ось сессии/subagent (`effort`, отдельно от каталога), словарь low…max | В записи модели (`generationConfig.reasoning`), словарь low/medium/high/max | В записи модели (provider→model→variant), также passthrough в агенте, low/medium/high |
| 3. Контекст/вывод | Из реестра модели; override — только фоллбек для неизвестных id | В записи модели (`contextWindowSize`, `max_tokens`), override явный | В записи модели (`limit.context/output`), явно объявленный дефолт-фоллбек |
| 4. Бюджет исполнения | `--max-turns`/`--max-budget-usd` на запуск, `maxTurns` на субагента | `maxSessionTurns/maxToolCalls/maxWallTimeSeconds` на модель + CLI-флаг + `maxTurns` на субагента | `steps` на агента; глобального аналога не найдено |
| 5. Транспорт/авторизация | Много `*_BASE_URL`+ключей, один wire-формат (Anthropic Messages) | `baseUrl`+`envKey` на модель, wire-формат через `providerProtocol` | `package`+`baseURL`+`apiKey` на провайдера, wire-формат = выбор пакета |
| 6. Субагент переопределяет | Модель, effort, tools, permissionMode, maxTurns, memory, isolation:worktree, background | Модель, approvalMode/permissionMode, tools, maxTurns, fork-параметры, working_dir(worktree) | Модель, permission по категориям действий (вкл. `task`), steps, hidden, provider passthrough |
| 7. Явная причина в доке | Guardrails для unattended-запуска + поддержка корп. облаков (Bedrock/Vertex/Foundry) | Маршрутизация через AI-гейтвеи + защита headless/CI от runaway-агента | Мульти-вендорный охват + признанная неточность реестра моделей + безопасность per-агент |
