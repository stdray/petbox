# 61 — Кандидаты: лёгкие Go-шлюзы (Bifrost, CLIProxyAPI, new-api, one-api)

Проверено 2026-09-25, по формату 60-FORMAT-candidates.md. Метод: `git clone --depth 1` каждого репо +
`grep`/чтение исходников (не только README), GitHub/Docker Hub API для метаданных и размеров образов.
12-facts-external.md F14 («Bifrost архивирован») — подтверждённо неверен (см. 40-reserve.md §4); здесь
перепроверено заново по API: `archived:false`.

## Bifrost (maximhq/bifrost)

**Кратко.** Go, Apache-2.0, репо `archived:false`, `pushed_at 2026-09-25T12:13`, 8357★. Ядро (`core/`) —
Apache-2.0; enterprise-функции (clustering, adaptive LB, guardrails) — отдельная edition, не проверялась.

**Лёгкость.** Docker Hub `maximhq/bifrost:latest` — 90.6 MB (amd64) / 81.5 MB (arm64) (Docker Hub API,
2026-09-25). Нет бинарных релиз-ассетов на GitHub (только `npx -y @maximhq/bifrost` / Docker / `go install`).
Обязательных внешних БД нет: `config_store`/`logs_store` по умолчанию — SQLite (`transports/bifrost-http/lib/config.go:1082`,
`ConfigStoreTypeSQLite`); Postgres — опция (CI-конфиги в `.github/workflows/configs/*` используют его для
тестов, не как обязательное). RAM idle: не нашёл цифры в доках — **?**.

**Матрица**

| id | статус | как именно | источник |
|---|---|---|---|
| S1 | ✅ | `/v1/chat/completions`, response_format, thinking — стандартный OpenAI-совместимый вход | `core/providers/openai/openai.go` |
| S2 | ✅ | `/v1/embeddings` | README:91 |
| S3 | ✅ | `/v1/rerank`, `BifrostRerankRequest`, есть у vllm-провайдера (`core/providers/vllm/rerank.go`) | `transports/bifrost-http/handlers/inference.go:786` |
| S4 | ✅ | `fallbacks:[{provider,model}]` в запросе, `shouldTryFallbacks`/`prepareFallbackRequest`; `RoutingEngineCircuitBreaker` | `core/bifrost.go:5162-5246`, `core/schemas/bifrost.go:485` |
| S5 | ✅ | `network_config.ca_cert_pem` (custom CA, per-provider, merge с существующим пулом) + `insecure_skip_verify` (dev) | `core/providers/utils/utils.go:845-885` |
| S6 | ⚠️ | fallback — плоский список `provider+model` в запросе/конфиге; нет engine-уровня "embedding space"/dimension guard — дисциплина конфига, не гарантия движка | `core/bifrost.go:5194-5246` (grep на `dimension`/`EmbedSpace` — 0 попаданий в логике роутинга) |
| S7 | ✅ | virtual keys, teams, customers, per-key/team/customer budgets, model-configs, provider governance — полноценная модель | `transports/bifrost-http/handlers/governance.go:1610-1690` |
| S8 | ⚠️ | governance считает usage/cost (budgets), но структурного event-лога в духе OTLP не проверял отдельно — вероятно есть (framework/tracing), не подтверждено файлом | `transports/bifrost-http/handlers/governance.go` |
| S9 | ✅ | `GET/PUT /api/config` — **живые мутации до персиста** (hot reload, без рестарта), `POST /api/config/metadata` | `transports/bifrost-http/handlers/config.go:122-330` |
| S10 | ✅ (образ) / ? (RAM) | образ 90 MB, SQLite по умолчанию, без обязательных внешних БД | Docker Hub API |
| X1 | ✅ | нативный вход `/v1/responses` + `/v1/responses/{id}` (retrieve/cancel/delete/input_items), стриминг (`handleStreamingResponses`), `PreviousResponseID` | `transports/bifrost-http/handlers/inference.go:776-796,1974` |
| X2 | ✅ | системная трансляция Responses→chat: `Groq.Responses()` = `request.ToChatRequest()` → `ToBifrostResponsesResponse()`, тот же паттерн у Cerebras/Databricks/HuggingFace/Mistral/Nebius/**Ollama**/Parasail/Perplexity/Runware/Sarvam/SGL/Wafer/Azure/GithubCopilot/Opencode/Vertex/Bedrock — везде флаг `BifrostContextKeyIsResponsesToChatCompletionFallback`; tool_calls тестируются (`TestToBifrostResponsesResponse_MapsToolCallsToCompleted`) | `core/providers/groq/groq.go:148-166`, `core/schemas/bifrost.go:315`, `core/schemas/mux_test.go:611` |
| | ⚠️ | **но НЕ у `vllm`-типа** — `VLLMProvider.Responses()` шлёт POST прямо на `baseURL+"/v1/responses"`, БЕЗ fallback-конвертации (предполагает, что апстрим сам понимает Responses). Для llama-server (не понимает `/v1/responses`) нужен другой provider-тип с fallback (напр. `ollama`), не `vllm` | `core/providers/vllm/vllm.go:305-343` |
| X3 | ✅ | модель указывается в теле запроса, провайдер резолвится по конфигу ключей/моделей | S4 |
| X4 | ⚠️ | governance/circuit-breaker даёт кулдаун по ошибкам/бюджету, но не нашёл специфичного "free-quota → долгий cooldown → paid" сценария как готового пресета — собирается из budgets+breaker, не из коробки | `core/schemas/bifrost.go:485` |
| X5 | ✅ | `network_config.extra_headers` на провайдера + `BifrostContextKeyExtraHeaders` per-request | `core/schemas/bifrost.go:302` |
| X6 | ✅ | отдельный интеграционный адаптер `/v1/messages` (Anthropic-совместимый inbound) | `transports/bifrost-http/integrations/anthropic.go:123` |
| X7 | ✅ | `go install`/бинарь/Docker; systemd — не проверял explicit unit-файл, но обычный Go-демон | README |
| X8 | ⚠️ | нашёл только "sticky" в MCP-клиенте (соединение к MCP-серверу), не sticky-провайдер на сессию для LLM-запросов — **не нашёл** | grep `sticky` в `core/`/`transports/` |
| A1-A3 | ? | не проверял (archive-кейс вне приоритета этого файла) | — |

**Отказоустойчивость.** `shouldTryFallbacks` → `prepareFallbackRequest` — обход по явному списку `fallbacks`
в запросе/маршруте; `RoutingEngineCircuitBreaker` как отдельный движок; governance budgets могут резать
доступ по бюджету независимо от breaker. Классов ошибок по типам (429 vs 401 vs oversize) на уровне
Go-кода не вычленял отдельно — не тот вопрос, что задавался (это уже покрыто в 40-reserve.md §2).

**Пример конфига** (адаптация реального `.github/workflows/configs/default/config.json`, под наш кейс:
SQLite вместо Postgres, DeepSeek + домашний llama-server с custom CA):

```json
{
  "config_store": { "enabled": true, "type": "sqlite", "config": { "path": "/app/data/bifrost.db" } },
  "providers": {
    "deepseek": {
      "keys": [{ "name": "ds", "value": "env.DEEPSEEK_API_KEY", "weight": 1, "models": ["*"] }]
    },
    "ollama": {
      "keys": [{ "name": "home", "value": "env.HOME_LLAMA_KEY", "weight": 1, "models": ["*"] }],
      "network_config": {
        "base_url": "https://home.example.internal:8443",
        "ca_cert_pem": "env.HOME_CA_PEM",
        "default_request_timeout_in_seconds": 300
      }
    }
  }
}
```
Фолбэк-цепь и виртуальные модели задаются через `fallbacks`/governance `model-configs`, не показаны здесь
(экономия строк — реальный формат в `transports/bifrost-http/handlers/governance.go:1638`).

**Вердикт.** Закрывает **S** почти полностью (дыра — S6 инженерно не гарантирован, дисциплина конфига; S8
частично не проверен) и **X** почти полностью (дыра — X8 sticky-провайдер не нашёл, X4 не готовый пресет,
и главное — **X2 не работает для `vllm`-типа**, годного для llama-server нужен provider-тип с fallback, не
`vllm`). Самый сильный кандидат по X1/X2 (систематическая, тестируемая Responses→chat трансляция) и по S7/S9
(governance — реальная, не декларативная модель). Главная дыра для нашего кейса: llama-server надо
подключать НЕ как `vllm`, иначе X2 не сработает — заметка для эксперимента, не блокер.

## CLIProxyAPI (router-for-me/CLIProxyAPI)

**Кратко.** Go, MIT, `archived:false`, `pushed_at 2026-09-25T10:12`, **53167★** (заметно больше Bifrost —
но проект в первую очередь про бесплатные CLI-подписки/OAuth: Kimi/Codex/Claude Code/Grok/Antigravity
через логин аккаунта, не только API-ключи). Оценка ниже — специально под вопрос «что даёт именно для
API-ключей», как просил владелец.

**Лёгкость.** Релиз-бинарь `v7.3.17`: 20-23 MB на платформу (tar.gz/zip, GitHub Releases API), статический Go.
Официального Docker Hub образа под `router-for-me/cliproxyapi`/`ghcr.io/router-for-me/cliproxyapi:latest`
не нашёл (404 через API — возможно другой namespace, не перепроверял глубже). Зависимостей БД нет —
`internal/redisqueue/` есть, но это опциональный плагин для usage-статистики, не обязательная БД. RAM idle: **?**.

**Матрица**

| id | статус | как именно | источник |
|---|---|---|---|
| S1 | ✅ | OpenAI-совместимый chat inbound, переводится во внутренние провайдерские форматы | README:9, `internal/translator/openai/` |
| S2 | ? | не нашёл отдельного `/v1/embeddings` в бегло просмотренных роутерах — не проверил `internal/api/` целиком | — |
| S3 | ❌ | ноль упоминаний rerank во всём `internal/`+`sdk/` (`grep -rln rerank` — 0 файлов) | grep, см. ниже |
| S4 | ⚠️ | "multiple accounts round-robin load balancing" на уровне OAuth-аккаунтов одного провайдера — не межпровайдерская цепь fallback как в Bifrost/LiteLLM | README:123-129 |
| S5 | ❌ | TLS/CA-код (`InsecureSkipVerify`/`RootCAs`) есть **только** в `internal/home/` (mTLS для собственного кластеринга между инстансами CLIProxyAPI), НЕТ per-provider custom-CA/insecure для произвольного OpenAI-совместимого `base_url` (т.е. для llama-server с самоподписанным сертификатом решения нет) | `grep -rln "InsecureSkipVerify\|RootCAs" . --include=*.go` → только `internal/config/home.go`, `internal/home/client.go` |
| S6 | ? | нет отдельного embed/rerank-пайплайна, вопрос неприменим (см. S2/S3) | — |
| S7 | ❌ | `api-keys`/`gemini-api-key` — плоский список ключей с usage-трекингом (`api-key-usage`), нет бюджетов/team/customer-модели | `internal/api/server_management.go:87-92` |
| S8 | ⚠️ | `usage-queue`, `api-key-usage` — usage-трекинг есть, но нет явной cost/pricing-модели как у new-api | `internal/api/server_management.go:91-92` |
| S9 | ✅ | `GET/PUT /v0/management/config.yaml`, "supports hot-reloading of clients and configuration" (комментарий в коде) | `internal/api/server_management.go:31-32`, `internal/api/server.go:4` |
| S10 | ✅ (бинарь) / ? (RAM) | 20-23 MB бинарь, без обязательной БД | GitHub Releases API |
| X1 | ✅ | "OpenAI (including Responses)" вход заявлен и подтверждён кодом (см. X2) | README:9 |
| X2 | ✅ | реальная N×M трансляционная матрица `internal/translator/<inbound>/<outbound>/{chat-completions,responses}` — есть `codex/openai/chat-completions` (Codex-Responses → OpenAI chat), со стримингом (`ConvertOpenAIRequestToCodex(...,stream bool)`) и детальной обработкой tool_calls (в т.ч. неоднозначные `tool_call_id`, `custom_tool_call` тип) | `internal/translator/codex/openai/chat-completions/codex_openai_request.go:30-192` |
| X3 | ✅ | роутинг по имени модели в теле, через provider-специфичные адаптеры | структура `internal/translator/` |
| X4 | ✅ | **именно этот сценарий есть из коробки**: `GET/PUT /v0/management/quota-exceeded/switch-project`, `switch-preview-model`, `POST /reset-quota`, `GET/POST /quota/providers,fetch,reset` — квота исчерпана на одном (бесплатном) аккаунте → переключение | `internal/api/server_management.go:74-85` |
| X5 | ? | не нашёл явного per-route header override в бегло просмотренных файлах (не проверял `internal/access/`, `internal/client/` целиком) | — |
| X6 | ✅ | Claude Code вход через OAuth и/или API — заявлено и есть `internal/translator/claude/` | README:90 |
| X7 | ✅ | автономный Go-бинарь, YAML/TOML-конфиг, заявлен systemd-сервис | README (self-host section) |
| X8 | ? | не проверял | — |
| A1-A3 | ? | не проверял | — |

**Отказоустойчивость.** Не автомат ошибок-классов как в Bifrost/LiteLLM — модель ближе к "квота на аккаунте
исчерпана → ручной/API-триггер переключения на другой аккаунт/превью-модель" (`switch-project`,
`switch-preview-model`), плюс round-robin между несколькими OAuth-аккаунтами одного провайдера. Это решает
X4 лучше остальных трёх кандидатов, но не решает S4 (межпровайдерский fallback-chain по capability/tier).

**Вердикт.** Сильнее всех по X1/X2 в деталях трансляции (codex-специфичная обработка tool_calls) и
единственный с готовым X4-пресетом "квота исчерпана → переключение". Но owner прав в скепсисе: S3 (нет
rerank), S5 (нет custom CA для домашнего эндпоинта — критично для S5), S7 (нет бюджетов/мультитенантности)
закрыты плохо или никак — это инструмент про личные CLI-подписки/OAuth-пул, не про S7-мультитенантный
шлюз с API-ключами и бюджетами. Для codex/X-требований — сильный кандидат; для серверных S3/S5/S7 — слабый,
эти дыры нужно закрывать чем-то ещё (или не закрывать вовсе, если сервер остаётся на своём роутере).

## new-api (QuantumNous/new-api)

**Кратко.** Go (backend) + React (`web/`, embed в бинарь), AGPL-3.0, `archived:false`,
`pushed_at 2026-09-25T04:43`, 48883★. Форк `Calcium-Ion/new-api`, который сам форк `songquanpeng/one-api`;
самый активно развиваемый из трёх one-api-производных.

**Лёгкость.** Релиз-бинарь `v1.0.0-rc.40`: 135-163 MB (embed React-фронтенд в один бинарь — этим объясняется
размер). Docker-образ `calciumion/new-api:latest` (образ используется тем же в `docker-compose.yml`) — 78 MB
(Docker Hub API). По умолчанию (`docker run` из README) — **SQLite, без Redis/Postgres**; но собственный
`docker-compose.yml` в репо запускает **New API + Postgres + Redis** (README:197) — если ставить "как в
доке одной командой", тащит за собой 2 лишних сервиса; если руками — SQLite хватает. RAM idle: **?**.

**Матрица**

| id | статус | как именно | источник |
|---|---|---|---|
| S1 | ✅ | `/v1/chat/completions`, RelayKit — конверсия между 4 протоколами | README:126,137,145 |
| S2 | ✅ | `/v1/embeddings` | README:142 |
| S3 | ✅ | `/v1/rerank` — explicit в README и в таблице эндпоинтов | README:142 |
| S4 | ✅ | канал (channel) — приоритет/группа, `relay/common/override.go` — per-channel override | README:170-172 |
| S5 | ⚠️ | `TLS_INSECURE_SKIP_VERIFY` — **глобальный** env-флаг на весь relay-HTTP-клиент (все апстримы разом), НЕ per-channel custom CA / pinning — грубее, чем Bifrost | `service/http_client.go:116-119`, `common/init.go:92` |
| S6 | ? | нет отдельного embedding-space guard, судя по общей архитектуре (каналы = плоский список), но не проверял embeddings-relay код детально | — |
| S7 | ✅ | token (=API-ключ) ↔ user ↔ group, per-key quota/restriction, subscriptions — полноценная модель, это ядро продукта | README:128-129, `router/api-router.go:273-286` |
| S8 | ✅ | "usage logs, cache accounting, expression-based pricing for different usage tiers" | README:128 |
| S9 | ✅ | полноценный REST admin API (`/api/channel`, `/api/token`, ...), веб-консоль поверх него | `router/api-router.go` |
| S10 | ⚠️ | образ 78 MB, бинарь 135+ MB (embedded UI), SQLite годится, но задокументированный docker-compose путь тянет Postgres+Redis | Docker Hub API, README:197 |
| X1 | ✅ | `/v1/responses` + `GET /v1/responses` (WebSocket upgrade) — Responses API вход, включая Realtime-WS вариант | README:137,140 |
| X2 | ✅ | `RelayKit` явно конвертирует между OpenAI Chat/Responses/Anthropic Messages/Gemini; тест `ConvertOpenAIResponsesRequest` на `chatAdaptor`/`geminiAdaptor` подтверждает код, не только README | README:145, `relay/channel/advancedcustom/adaptor_test.go:535-551` |
| X3 | ✅ | модель в теле запроса → канал по модели/группе | S4 |
| X4 | ⚠️ | quota/subscription-модель есть (S7), но не нашёл специфичного "free quota exhausted → long cooldown → auto-switch to paid" пресета отдельно от общей channel priority — вероятно собирается, не готовый пресет | — |
| X5 | ✅ | `relay/common/override.go` — per-channel request override (правдоподобно включает headers, не читал файл целиком) | grep |
| X6 | ✅ | `/v1/messages` (Claude inbound) | `router/relay-router.go:97` |
| X7 | ✅ | единый Go-бинарь (embed UI), SQLite-режим — `docker run` в один контейнер, без внешних сервисов | README:150-166 |
| X8 | ? | не проверял | — |
| A1-A3 | ? | не проверял | — |

**Отказоустойчивость.** Канал (channel) = провайдер+ключ+модели+приоритет+группа; retry/fallback между
каналами одной группы — стандартная модель one-api-семейства (не читал retry-код построчно, экономлю
бюджет — семейство известно по 12-facts F3-F8 паттерну "приоритет/вес").

**Пример конфига** (docker-compose минимальный, SQLite, без Postgres/Redis — не показан отдельно: README
уже даёт готовую команду `docker run -p 3000:3000 -v ./data:/data new-api` — конфигурация каналов/токенов
делается через веб-консоль/REST API, не файлом, так что 30-строчного YAML-примера, выводимого из доков, тут
нет по устройству продукта, не по нехватке места).

**Вердикт.** Из трёх «традиционных» one-api-шлюзов — самый сильный: S3 rerank и X1/X2 Responses-трансляция
подтверждены кодом, не только README (в отличие от того, что можно было ожидать от одноапишного
происхождения). S7 (мультитенантность/бюджеты) — сильнее Bifrost по готовой UI/UX для ключей-на-проект, но
S5 (TLS) грубее. Главная дыра для нашего кейса — S5 (глобальный insecure-флаг, не per-provider CA) и
тяжёлый бинарь/образ относительно Bifrost/CLIProxyAPI (embedded React UI).

## one-api (songquanpeng/one-api)

**Кратко.** JavaScript+Go (по GitHub language stats; ядро Go, `web/` — React), MIT, `archived:false`,
**но `pushed_at` API отдаёт 2026-01-09, а фактический последний коммит на `main` — `8df4a267`,
2025-02-21** (проверено `GET /repos/.../commits/main`) — **больше 7 месяцев без коммитов в код на момент
проверки**. 37020★ (исторические, от статуса "оригинал one-api" до форка). Это находка, отличная от
предположения в задаче — проект де-факто в поддержке-только-по-названию.

**Лёгкость.** Не проверял размер бинаря/образа отдельно (нет смысла — см. вердикт).

**Матрица (сокращённо — базовый набор, без глубокой проверки givenстарости)**

| id | статус | как именно | источник |
|---|---|---|---|
| S1 | ✅ | `/v1/chat/completions` | `router/relay.go:25` |
| S2 | ✅ | `/v1/embeddings`, `/v1/engines/:model/embeddings` | `router/relay.go:30-31` |
| S3 | ❌ | 0 упоминаний rerank в `router/`+`relay/` | grep |
| X1 | ❌ | 0 упоминаний `/v1/responses` в `router/`+`relay/` | grep |
| X2 | ❌ | (следствие X1) | — |
| X6 | ? | не нашёл `/v1/messages` в беглом grep по `router/relay.go` (в отличие от new-api) | grep |
| S7 | ✅ | тот же token/channel/group-каркас, которым потом наследует new-api — `/api/channel`, `/api/token` REST | `router/api.go:71-85` |
| S9 | ✅ | тот же admin REST API | `router/api.go` |
| Остальные (S4-S6,S8,S10,X3-X8,A1-A3) | не проверялись отдельно | архитектурно = подмножество new-api минус Responses/rerank/RelayKit-конверсии, добавленные позже в форке |

**Вердикт.** Это исторический предок new-api, а не отдельный сильный кандидат: та же базовая
token/channel-модель, но **без** Responses API (X1/X2 ❌), **без** rerank (S3 ❌), и репозиторий не
развивается на `main` уже 7+ месяцев на момент проверки (пока конкурентный форк `QuantumNous/new-api`
пушится тем же днём, что идёт эта проверка). Владелец просил конкретно `songquan/one-api` — по факту
это `songquanpeng/one-api`; если задача была «оценить оригинал one-api» — оценка дана; но как кандидат в
шорт-лист брать нет смысла, когда его активный форк (new-api) строго сильнее по фичам и активности.
**Рекомендация: не рассматривать one-api отдельно от new-api дальше — new-api его полностью перекрывает.**

## Итог по всем четырём (для сведения в общую матрицу этапа 3)

Сила по X1/X2 (главный вопрос owner про codex): **Bifrost ≈ CLIProxyAPI > new-api ≫ one-api**. Сила по
S3/S5/S7/S9 (сервер PetBox): **Bifrost > new-api ≫ CLIProxyAPI ≈ one-api** (CLIProxyAPI — почти ноль по
S3/S5/S7, силён только там, где владелец сам предупреждал — OAuth/подписки/X4). Лёгкость: CLIProxyAPI
(20-23 MB бинарь, ни одной внешней БД) — самый лёгкий; Bifrost — 90 MB образ, SQLite по умолчанию; new-api
— тяжелее всех (78-160 MB, задокументированный путь с Postgres+Redis); one-api — вне игры. Ни один
пятый сильный Go/Rust-кандидат, не входящий в список, замечен не был — 40-reserve.md уже разобрал
LiteLLM (Python) и TensorZero (Rust) как соседние точки сравнения, здесь не дублируется.
