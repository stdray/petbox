# 62 — Кандидаты: LiteLLM, Portkey, TensorZero, agentgateway, Helicone AI Gateway, Envoy AI Gateway

Формат по 60-FORMAT-candidates.md. Источники: GitHub raw README/исходники (текущие, дата фетча
2026-09-25), GitHub REST API (`api.github.com/repos/...`, license/stars/pushed_at/archived —
поле GitHub, не бейдж README), litellm-docs репо (raw), сайты кандидатов через `pfetch --text`
(прокси, т.к. `portkey.ai`/`agentgateway.dev` отдают 403 напрямую). Все файлы кэшированы в
`.tmp/candfetch/` (не коммитится, gitignored, временные).

## LiteLLM (proxy / AI Gateway)

**Кратко.** Python (FastAPI + Prisma), self-hosted через Docker/PyPI (`litellm[proxy]`). Core —
MIT (badge на README, не проверял отдельный LICENSE-файл, GitHub API license=`NOASSERTION` —
репо не декларирует SPDX машиночитаемо, см. ⚠️ ниже). **Enterprise-paywall**: README сам перечисляет
под «Enterprise» — коммерческая лицензия, feature prioritization, custom integrations, SSO,
custom SLA, dedicated support (github.com/BerriAI/litellm README §Enterprise). Активность: `pushed_at
2026-09-25` (сегодня), 59602★, YC W23 — живой проект, самый крупный по трекшену среди всех 62-candidates.

**Лёгкость.** Официальный образ `ghcr.io/berriai/litellm` бандлит Prisma-toolchain. **PostgreSQL
обязателен** для virtual keys, teams, users, spend logs, config API (`litellm-docs/docs/proxy/deploy.md:31`:
«PostgreSQL | Keys, teams, users, spend logs, config | Required for the proxy's auth and tracking
features»). Redis — «Required once you run more than one instance» (`deploy.md:32`), для одного
процесса на VPS не обязателен. Без `DATABASE_URL` прокси стартует (Dockerfile проверяет её наличие
перед инициализацией БД — `litellm-docs/docs/proxy/virtual_keys.md:27`), но тогда нет virtual keys/
budgets/hot-reload через API — только статический YAML. RAM idle: не нашёл цифры в доках; Python+FastAPI
процесс, оценочно 150–300 МБ (**не подтверждено источником**, честное «?»).

**Матрица**

| id | статус | как именно | источник |
|---|---|---|---|
| S1 | ✅ | `response_format={"type":"json_schema","json_schema":…,"strict":true}`, thinking через `reasoning_effort`/provider params; client-side JSON-schema валидация если модель не поддерживает нативно (`litellm.enable_json_schema_validation=True`) | litellm-docs `docs/completion/json_mode.md:70,86,239-244` |
| S2 | ✅ | `/v1/embeddings`, множество провайдеров | README таблица провайдеров |
| S3 | ✅ | `/rerank`, Cohere v1/v2 клиент, `litellm.rerank(model="cohere/rerank-english-v3.0",…)`, proxy `model_list` c `model: cohere/...` | litellm-docs `docs/providers/cohere.md:270-338` |
| S4 | ✅ | Router: cooldowns, retries (fixed+expo backoff), `order=`-уровни эскалации, `fallbacks` список | litellm-docs `docs/routing.md:27,1074` |
| S5 | ✅ | `litellm.ssl_verify: Union[str,bool]=True`, `litellm.ssl_certificate: Optional[str]=None` — глобальные переменные модуля | `litellm/__init__.py:322,324` (raw source, не доки) |
| S6 | ⚠️ | **Model groups**: `model_name` группирует deployments под одним именем; `enable_weighted_failover` — сначала ре-пик **внутри той же группы** (др. deployment с тем же весом), только когда вся группа исчерпана — эскалация на cross-group `fallbacks`; группа = своё ACL-имя, не расширяется на элементы. Это выражает «fallback только внутри группы», но НЕ проверяет EmbedSpaceId семантически — дисциплина конфигурации на пользователе (группа не должна содержать модели из разных embedding-пространств) | litellm-docs `docs/routing.md:864,1186,1195-1202` |
| S7 | ✅/⚠️ | Virtual keys, teams, budgets per-key (`max_budget`), project/team levels — но **требует Postgres** (см. Лёгкость); без БД мультитенантность недоступна | `deploy.md:31`; README §Key Features |
| S8 | ✅ | `usage`/cost tracking, spend logs в Postgres, OTel/observability callbacks (Lunary/MLflow/Langfuse) | README §Key Features |
| S9 | ✅ | Admin UI + REST Management API (`/key/generate`, `/key/update`, etc.), hot reload конфига через API (требует БД) | litellm-docs `docs/proxy/virtual_keys.md:622` |
| S10 | ⚠️ | Python — не легчайший рантайм; RAM idle не подтверждён числом; Postgres — жёсткая внешняя зависимость для S7/S9 | см. Лёгкость |
| X1 | ✅ | Полноценный `/v1/responses` эндпоинт (Create/Get/Delete), streaming ✅, WebSocket mode ✅, fallbacks/loadbalancing работают между Responses-моделями | litellm-docs `docs/response_api.md:1-24` |
| X2 | ✅ | **`/responses` → `/chat/completions` bridge**: явно для «Anthropic, Gemini и не-Responses OpenAI-совместимых моделей» (например DeepSeek/GLM/llama-server через `openai/`-префикс с custom `api_base`); включается автоматически по `mode` модели, либо принудительно флагом/префиксом `openai/chat_completions/` | `docs/response_api.md:1511-1600` |
| X3 | ✅ | Виртуальное имя = `model_name` в `model_list`, роутинг по имени в теле | routing.md (см. S6) |
| X4 | ⚠️ | Cooldowns/retries по HTTP-коду есть; «долгий cooldown по исчерпанной квоте» отдельным классом не нашёл — общий `allowed_fails`/`cooldown_time` таймер, не per-error-class (подтверждает 40-reserve.md §2b) | routing.md (общий обзор) |
| X5 | ✅ | `litellm_params.extra_headers`/per-deployment headers в `model_list`; proxy-level header forwarding | README F3 (12-facts) — не перепроверял отдельно, честно ⚠️ вторичный источник |
| X6 | ✅ | `/v1/messages` (Anthropic-формат) как inbound, отдельная колонка в таблице провайдеров README | README таблица провайдеров |
| X7 | ❌ | Нет — Python-процесс + (опционально) Postgres/Redis, не автономный бинарь/служба «без БД» в полном режиме | Лёгкость |
| X8 | ⚠️ | `session_affinity`/`deployment_affinity` — упомянуты как supported values для Responses routing, детали sticky-по-сессии не вычитывал глубже | `docs/response_api.md:1508` |
| A1 | ✅ | Vision content-parts, README таблица (`/chat/completions` для vision-моделей стандартно) | общеизвестно, не отдельно перепроверял узкий source |
| A2 | ⚠️ | `/embeddings` есть у многих провайдеров с image input (Vertex, Bedrock) — мультимодальный embed в ОДНОМ пространстве текст+картинка отдельно не подтверждал | не проверял глубоко |

**Модель отказоустойчивости.** Router: cooldowns (счётчик/окно), `allowed_fails`+`cooldown_time`
(default, `docs/routing.md`), retries с exponential backoff, `order=`-уровни (приоритетные тиры),
`enable_weighted_failover` для внутригруппового ре-пика, `max_parallel_requests`→429 без ожидания в
очереди (routing.md:1289). Нет закрытого enum классов исхода — плоские HTTP-коды/исключения.

**Пример конфига (X1–X4 + S2/S3/S4, ≤30 строк)**
```yaml
model_list:
  - model_name: chat-free            # X3 virtual name = model group
    litellm_params:
      model: openai/glm-4.6           # X2 bridge: custom api_base, non-Responses upstream
      api_base: https://glm.example/v1
      api_key: os.environ/GLM_KEY
    model_info: {mode: chat}
  - model_name: chat-free
    litellm_params:
      model: deepseek/deepseek-chat
      api_key: os.environ/DEEPSEEK_KEY
router_settings:
  routing_strategy: simple-shuffle
  fallbacks: [{"chat-free": ["chat-paid"]}]   # S4 cross-group fallback
  enable_weighted_failover: true               # S6-ish: try in-group first
  allowed_fails: 3
  cooldown_time: 30
# S2/S3 — тот же model_list c mode: embedding / model: cohere/rerank-*
```

**Вердикт.** Закрывает **A (сервер)** почти полностью — единственная дыра S6 (нет семантической
проверки EmbedSpaceId, только дисциплина именования групп) и X4 (нет per-quota long-cooldown класса).
Закрывает **B (codex)** — X1/X2 подтверждены официальной документацией лучше, чем у любого другого
кандидата здесь. **C (archive)**: A1 вероятно да, A2 не подтверждена. Главная цена — Postgres как
жёсткая зависимость для S7/S9 (без неё — statically-configured router без мультитенантности); Python
runtime тяжелее Rust/Go аналогов.

## Portkey AI Gateway

**Кратко.** Node.js/TypeScript (`npx @portkey-ai/gateway`), MIT (`LICENSE` файл подтверждён,
Copyright Portkey Inc 2024). 13081★, `pushed_at 2026-05-25` — почти 4 месяца без пуша на дату
фетча (не «мёртвый», но заметно менее активный, чем LiteLLM/agentgateway/Envoy). README анонсирует
готовящийся **Gateway 2.0**, куда «core enterprise gateway» переезжает в open-source — сигнал, что
open-source версия сейчас урезана относительно облачного продукта.

**Лёгкость.** «Blazing fast (<1ms), tiny footprint (122kb)» — README формулирует это как размер
бандла core-роутера (вероятно для serverless/edge-деплоя), НЕ как RSS процесса; RAM idle Node.js-
процесса отдельно не указана — **не нашёл**. Деплой: Docker/Node.js/Cloudflare Workers/Replit.
Никакой обязательной БД в README/дереве репозитория не найдено (`.tmp/candfetch/portkey-tree.json`,
поиск по `budget|virtualKe` — совпадений в файловой структуре нет).

**Матрица**

| id | статус | как именно | источник |
|---|---|---|---|
| S1 | ✅ | OpenAI-совместимый `/chat/completions`, `output_guardrails`/`config` | README quickstart |
| S2 | ⚠️ | «Multi-modal LLM Gateway» упомянут (vision/audio/image), embeddings отдельно не подтвердил | README §Core Features |
| S3 | ❌ (не найдено) | Поиск `rerank` по полному дереву репозитория (935 путей) — 0 совпадений | `.tmp/candfetch/portkey-tree.json` |
| S4 | ✅ | Fallbacks + Automatic Retries (до 5, exponential backoff) + Load Balancing (веса) | README §Core Features, `docs/handlers/modelResponsesHandler.ts` дерево |
| S5 | ❓ | Нет выделенного TLS/CA конфиг-файла в дереве (`ssl|certificate|ca_?bundle` — 0 совпадений); Node.js уважает `NODE_EXTRA_CA_CERTS` глобально, но это не Portkey-специфичный механизм — не подтверждено | поиск по дереву репо |
| S6 | ❌ | Нет группового/scope-механизма в найденных файлах; `Configs`-объект — плоский список fallback/retry без группировки | README §3 Routing & Guardrails |
| S7 | ⚠️ | `docs.portkey.ai`: раздел навигации **«Virtual Keys [Deprecated]» → Budget Limits** — механизм существует, но именно Virtual Keys (ключ=провайдер-креды) помечены deprecated на сайте на момент фетча; текущая замена (Model Catalog?) не вычитывал глубже | pfetch `portkey.ai/docs/product/ai-gateway/virtual-keys/budget-limits`, nav breadcrumb |
| S8 | ✅ | Usage analytics, cost/latency/error-rate трекинг (hosted app; часть — enterprise/hosted-only, помечено `*` в README) | README §Cost Management |
| S9 | ⚠️ | `Configs` — декларативный JSON, прикрепляемый к клиенту (`client.with_options(config=…)`), не явный REST management API в OSS-ядре (Admin APIs есть в доках навигации — `docs.portkey.ai` nav «Admin APIs», не проверял глубину) | README + pfetch nav |
| S10 | ⚠️ | Node.js — легче Python, тяжелее Rust/Go; конкретных RAM-цифр процесса не нашёл | см. Лёгкость |
| X1/X2 | ⚠️ | В дереве репо есть `src/handlers/modelResponsesHandler.ts`, `src/handlers/services/responseService.ts`, `src/types/modelResponses.ts` — Responses API поддержка ЕСТЬ в коде, но глубину трансляции (streaming/tool_calls в bridge на chat-модели) не вычитывал построчно — честное ⚠️, не ✅ | `.tmp/candfetch/portkey-tree.json` пути |
| X5 | ✅ | «Conditional Routing» по заголовкам в nav доков, header rewriting как часть routing rules | 12-facts F6 (вторичный источник, не перепроверял) |
| X6 | ❓ | Anthropic — в списке провайдеров (chat/stream ✅), но `/v1/messages` как INBOUND формат (не только outbound-провайдер) не подтвердил | README таблица провайдеров |
| X7 | ⚠️ | `npx`/Docker/Cloudflare — не единый бинарь, Node.js-зависимость | README |

**Модель отказоустойчивости.** `retry.attempts` (до 5, exponential backoff), `Fallbacks` по
провайдеру/модели, `Load Balancing` по весам, `Request Timeouts`; свежее упоминание **Circuit
Breaker** в nav доков (`docs.portkey.ai`) — не вычитывал механику детально.

**Пример конфига.** Не привожу — глубина проверки (S3/S5/S6/S9 все ⚠️/❌/❓) недостаточна, чтобы
вывести конфиг, который не окажется фантазией по 60-FORMAT (сам формат требует «если реально
выводится из доков»).

**Вердикт.** Слабее LiteLLM/agentgateway по S3 (rerank отсутствует) и S6 (нет группировки); S7
в переходном состоянии (Virtual Keys deprecated на дату фетча — рискованно строить архитектуру на
уходящем механизме). X1/X2 код есть, но не проверен на глубину, которую требуют X1/X2 (потоковый
tool-call bridge). Не рекомендую как основной кандидат без отдельного глубокого прогона.

## TensorZero — ПРОЕКТ ОСТАНОВЛЕН, из дальнейшего рассмотрения исключён

**Критическая проверка (12-facts-external.md F9 недостоверен для сегодняшней даты).** GitHub API:
`archived: true`, `pushed_at: 2026-06-11` (репозиторий `tensorzero/tensorzero`, проверено
`api.github.com/repos/tensorzero/tensorzero`). Независимое подтверждение — сайт: `pfetch --text
https://www.tensorzero.com/` возвращает текст «TensorZero remains available on GitHub but is no
longer maintained.» README, который раздаёт `raw.githubusercontent.com` (main branch), всё ещё
рекламирует Autopilot/hiring/$7.3M seed — **устаревший маркетинговый текст на архивной ветке**, не
источник истины. Формально Rust, Apache-2.0, 11716★ — технически подходил бы (config-based
fallbacks/retries, gateway <1ms p99, наблюдаемость через ClickHouse), но проект не поддерживается
и не годится как зависимость на годы вперёд. Дальнейшую матрицу не строю — нерелевантно.

## agentgateway (agentgateway/agentgateway)

**Кратко.** Rust, Apache-2.0 (LICENSE подтверждён + GitHub API), Linux Foundation / Agentic AI
Foundation project (README). Активность: `pushed_at 2026-09-25` (сегодня), 5039★ — растущий, живой.

**Лёгкость.** Standalone-бинарь + Kubernetes-режим. Внутренний бюджет/spend-трекинг **поддерживает
и SQLite, и Postgres** как бэкенд (`crates/agentgateway/src/http/budget/database.rs:16-18,30,53` —
`DatabasePool::Sqlite`/`DatabasePool::Postgres`, отдельные `sqlite_schema.sql`/`postgres_schema.sql`)
— в отличие от LiteLLM, Postgres НЕ обязателен даже для budgets/multi-tenancy. RAM idle/размер
бинаря — не нашёл официальной цифры в README/доках (честное «?»); Rust статический бинарь, ожидаемо
в диапазоне Helicone (~30-60 МБ) по природе языка, но это оценка, не замер.

**Матрица**

| id | статус | как именно | источник |
|---|---|---|---|
| S1 | ✅ | LLM Gateway: OpenAI-совместимый унифицированный API, tool-call трансляция между форматами (`tool_arguments_to_input`, OpenAI↔Anthropic) | README §Key Features; `crates/llm/src/conversion/mod.rs:20-32` |
| S2 | ✅ | `crates/llm/src/types/embeddings.rs` есть в дереве репозитория | file tree |
| S3 | ✅ | Rerank смоделирован явно на Cohere `/v2/rerank`: `Request{model, query, documents, top_n, return_documents,…}`, есть Bedrock/Vertex-трансляция rerank | `crates/llm/src/types/rerank.rs:1-40`; `conversion/rerank_tests.rs` |
| S4 | ✅ | Failover/load balancing заявлены в README; `crates/agentgateway/src/llm/model_router.rs` — `ModelRoute`, `WeightedTarget`, `ConditionalTarget` | README; model_router.rs:161-211 |
| S5 | ✅ | Backend TLS + **dynamic/custom CA cert** модуль: `crates/agentgateway/src/types/dynamic_ca_cert.rs`, `http/backendtls.rs`, `client/tls.rs` | file tree |
| S6 | ⚠️ | `VirtualModelRoute`/`ModelRouter` группирует под виртуальным именем (аналог model group), но явной проверки embedding-space-равенства в найденном коде нет — та же дисциплина конфигурации, что у LiteLLM | `model_router.rs:161-260` |
| S7 | ✅ | `http/budget/mod.rs` — per-api-key budget counters, rolling window, SQLite/Postgres persistence | budget/mod.rs:26-60; database.rs |
| S8 | ✅ | OpenTelemetry, `examples/llm-cost-routing/` | file tree |
| S9 | ⚠️ | «Configuration storage» + «Update your configuration» в доках (nav), управление через API/UI — не вычитывал REST-контракт детально | pfetch `agentgateway.dev/docs/...quickstart` nav |
| S10 | ✅/⚠️ | Rust, SQLite опционален вместо обязательного Postgres — заметно легче LiteLLM по зависимостям; точных RAM-цифр не нашёл | database.rs |
| X1/X2 | ✅ | **Полная Responses↔chat-трансляция в исходниках**: `crates/llm/src/conversion/responses.rs` (1713 строк) — `translate_request`, `translate_response`, `translate_stream` (потоковый путь, `passthrough_stream`), `saw_tool_call`/tool-call state в стриме | `agentgw-responses.rs:22,163,730,855,886,1095` (raw source, самый сильный источник среди всех кандидатов — не докстраница, а код) |
| X3 | ✅ | `VirtualModelRoute`, `RequestedModel`/`RequestedModelLocation` — маршрутизация по имени модели в теле | model_router.rs:161-239 |
| X4 | ❓ | Budget/cooldown-классы не проверял на предмет «долгий cooldown по квоте» отдельно от общих retry/budget механизмов | не проверял глубже |
| X5 | ✅ | «Header manipulation», «Rewrites» — отдельные разделы в доках nav (Traffic management) | pfetch nav |
| X6 | ⚠️ | Anthropic — провайдер есть (`crates/llm/src/anthropic.rs`), но `/v1/messages` как INBOUND не подтверждал отдельно от outbound-провайдера | file tree |
| X7 | ✅ | Standalone-бинарь, «Binary» install-раздел в доках отдельно от Docker/Helm/K8s | pfetch nav: Setup → Install → Binary/Docker/Helm |
| A1 | ⚠️ | Multi-modal не в явном списке README (в отличие от Portkey/LiteLLM), но `types/messages.rs`/conversion слой богатый — не исключено, не подтверждено | не проверял глубоко |

**Модель отказоустойчивости.** `http/budget/*` — счётчики с rolling window + persistence (SQLite/
Postgres), `ModelRoute`/`WeightedTarget`/`ConditionalTarget` для маршрутизации, «Resiliency» раздел
доков (Mirroring, Rate limiting, Retries, Timeouts, Fault injection) — ближе всего по духу к
явному автомату среди всех кандидатов здесь, но детальные классы исходов (по типу ошибки провайдера)
не вычитывал построчно.

**Пример конфига.** Не привожу (S9 конфиг-схема, YAML routes/backends, не вычитывал достаточно
глубоко, чтобы не выдумать синтаксис — 60-FORMAT требует «если реально выводится из доков»).

**Вердикт.** Сильнейший технический кандидат по X1/X2 (единственный, где трансляция подтверждена
чтением исходного кода трансляции, а не доков) и S3/S5/S7 (SQLite вместо жёсткого Postgres). Дыры:
S9 (management API глубина не проверена), не проверял X6 (Anthropic messages inbound) и S10 (нет
цифр RAM). Живой, растущий проект (LF), Rust — легковесность ожидаема, не измерена.

## Helicone AI Gateway

**Кратко.** Rust, «Public Beta». **README-бейдж заявляет Apache, но GitHub API отдаёт `license:
gpl-3.0`** (`api.github.com/repos/Helicone/ai-gateway` → `license.spdx_id: GPL-3.0`) — прямое
расхождение между маркетингом README и фактической лицензией репозитория; GPLv3 существенно меняет
цену использования (copyleft) относительно того, что подразумевает бейдж. Активность: **последний
коммит `pushed_at 2025-11-21`, последний релиз `v0.2.0-beta.30` от `2025-07-21`** — на дату
фетча (2026-09-25) это ~10 месяцев без коммитов и ~14 без релиза, вопреки бодрому тону README
(«Public Beta», демо-видео) — самый несвежий кандидат из шести после мёртвого TensorZero.

**Лёгкость.** README сам публикует бенчмарк-таблицу: P95 <5ms, **RAM ~64MB**, ~3000 req/s, binary
~30MB, cold start ~100ms, «vs Typical Setup ~512MB» — это **self-reported** число (README, не
независимый замер, `benchmarks/README.md` ссылка есть но не читал). Запуск: `npx @helicone/ai-gateway`
или Docker, конфиг `config.yaml`. Провайдеры — курируемый статический список в
`ai-gateway/config/embedded/providers.yaml` (OpenAI/Anthropic/Gemini/…), НЕТ явного generic
custom-OpenAI-compatible provider в прочитанном фрагменте — для домашнего llama-server нужно
проверять отдельно, не подтверждено ни в какую сторону.

**Матрица**

| id | статус | как именно | источник |
|---|---|---|---|
| S1 | ✅ | `/chat/completions`-совместимый роутинг, модель `openai/gpt-4o-mini` формат | README quickstart |
| S2 | ❌ (не найдено) | `providers.yaml` (155 строк) содержит только chat-модели по каждому провайдеру, ни `rerank`, ни `embed` в тексте файла не встречается | `.tmp/candfetch/helicone-providers.yaml` (полный grep — 0 совпадений) |
| S3 | ❌ (не найдено) | То же — 0 упоминаний rerank в файловом дереве репо (478 путей) и в providers.yaml | `.tmp/candfetch/helicone-tree.json` |
| S4 | ✅ | «Fallbacks», load-balance стратегии (`model-latency`, P2C+PeakEWMA, weighted, cost) в `config.yaml` пример README | README §Smart provider selection, YAML-пример |
| S5 | ❓ | В дереве репо найден `infrastructure/self-signed-certs/openssl.conf` — но это, судя по пути (`infrastructure/`), генерация серта для **входящего** TLS самого гейтвея, не доверие кастомному CA на исходящих запросах к домашнему серверу; не подтверждено ни в какую сторону | file tree |
| S6 | ❌ | Нет группового механизма, только плоский `load-balance.models: […]` список | README YAML-пример |
| S7 | ⚠️ | `rate-limit.per-api-key` (capacity+refill) в примере конфига — это лимит скорости, не бюджет в $/токенах; полноценные budgets/multi-tenant projects не подтверждены в OSS self-host режиме (могут быть cloud-only через `HELICONE_CONTROL_PLANE_API_KEY`) | README §Self hosted configuration |
| S8 | ✅ | OTel logs/metrics/traces, Helicone observability интеграция | README §Simplified tracing |
| S9 | ❓ | Cloud-hosted router имеет «configuration wizard in the UI»; self-host — YAML-файл, не REST management API (или не нашёл) | README §Custom configuration |
| S10 | ✅ | Единственный кандидат с self-reported числом RAM (~64MB) и binary size (~30MB) прямо в README | README benchmark table |
| X1/X2 | ❓ | Ни `/responses`, ни bridge не упомянуты нигде в README/providers.yaml/file tree — вероятно НЕТ, но явного отрицания не нашёл (честное «?», не «❌», т.к. не искал в исходном Rust-коде как для agentgateway) | providers.yaml, README |
| X7 | ✅ | `npx`/Docker, компактный бинарь ~30MB — годится для «один бинарь на своей машине» | README |

**Модель отказоустойчивости.** Load-balance стратегии по заявленной latency/весу/cost (без явных
cooldown/breaker классов в прочитанном материале), rate-limit per-api-key как единственный
защитный механизм в самостоятельном self-host режиме.

**Вердикт.** Самый лёгкий по заявленным цифрам (единственный, кто их публикует), но: (1) лицензия
на деле GPLv3, не Apache, как утверждает бейдж — юридически значимое расхождение; (2) заброшен
почти на год; (3) нет rerank/embeddings вовсе — закрывает только часть S1/S4/S8/S10, не годится
для S2/S3/S6. Не рекомендую при наличии agentgateway (тот же Rust, тот же дух «NGINX для LLM»,
но живой, с rerank и SQLite budgets).

## Envoy AI Gateway → переименован в «Agent Router» (Agentic AI Foundation)

**Кратко.** Проект переименован: репозиторий переехал `envoyproxy/ai-gateway` →
`theagentrouter/agent-router` (README: «Envoy AI Gateway is now Agent Router… Same code, same
maintainers… The repository moved»). CRDs/API-группа (`aigateway.envoyproxy.io`), Helm chart,
Go-модуль путь — не изменились. Go (control plane) + Envoy (data plane, C++). Apache 2.0, 2142★,
`pushed_at 2026-09-25` — живой, активный (LF Projects). **Это НЕ факт из 12-facts-external.md —
переименование, судя по всему, недавнее и там не отражено; отдельно проверять не стал (внешний
ребрендинг, не релевантно для PetBox-канона).**

**Лёгкость — гипотеза «тяжёл для одного VPS» ПОДТВЕРЖДЕНА.** Релизный бинарь `aigw` (последний
`v1.1.0`, `2026-08-21`): **`aigw-linux-amd64` = 281.3 МБ** (`api.github.com/repos/theagentrouter/
agent-router/releases` assets). Для сравнения: agentgateway/Helicone/CLIProxyAPI — 30-60 МБ, .NET
AOT — 10-20 МБ. Причина размера — бинарь статически бандлит полный Envoy-прокси (C++ data plane)
внутри Go control-plane. Основной сценарий использования — Kubernetes (CRDs `AIGatewayRoute`,
`AIServiceBackend`, `BackendSecurityPolicy`, Gateway API), «Tier One/Tier Two Gateway» паттерн для
кластеров. Standalone-режим существует («Run Agent Router as a standalone, OpenAI-compatible
router on your laptop with one command: `aigw run`») и годится для локального теста, но вся
структура доков («Kubernetes Quickstart» как основной путь, endpoint-picker для self-hosted
inference кластеров) выдаёт продукт, спроектированный для многоузлового K8s, а не для одного
процесса рядом с PetBox на VPS.

**Матрица — не строю подробно** (по заданию — кратко/подтвердить-опровергнуть тяжесть, не полный
разбор). S1/S4 закрываются тривиально через Envoy как reverse-proxy + `AIServiceBackend`
(поддержаны OpenAI/Azure/Gemini/Vertex/Bedrock/Mistral/Cohere/Groq/Together/DeepInfra/DeepSeek/
Anthropic — таблица провайдеров в README), но S3 (rerank)/S6 (embed-space)/S9 (простой config API
без K8s CRD) не выглядят естественными для архитектуры, ориентированной на декларативные Gateway
API ресурсы, а не REST/YAML одного процесса.

**Вердикт.** Бинарь **281 МБ** и Kubernetes-first документация (Tier One/Tier Two, endpoint picker
для GPU-кластеров) подтверждают гипотезу: избыточно тяжёл и архитектурно не тот инструмент для
«один VPS рядом с PetBox». `aigw run` как локальный dev-режим существует, но платить 281 МБ ради
functionality, которую agentgateway (Rust, ~сравнимый feature set, LLM+MCP+A2A) даёт заметно
дешевле, не оправдано. Не рекомендую для дальнейшего рассмотрения без отдельного сильного довода
(например, если PetBox уже эксплуатирует Envoy Gateway для чего-то ещё).

## Итог по всем шести

Живые и профильные: **agentgateway** (сильнейший по X1/X2 — подтверждено чтением кода трансляции;
S3/S5/S7 закрыты; SQLite вместо жёсткого Postgres) и **LiteLLM** (самый полный функционально,
лучшая документация X1/X2, но Postgres обязателен для S7/S9, Python тяжелее). **Portkey** —
жизнеспособен, но S3 отсутствует и S7 в переходном состоянии (Virtual Keys deprecated). **Helicone
AI Gateway** — лёгкий, но без rerank/embeddings, лицензия на деле GPLv3 (не Apache как в бейдже),
заброшен ~10 месяцев. **TensorZero** — технически сильный набор фич, но проект остановлен
(`archived:true` + собственный сайт подтверждает «no longer maintained») — исключить из
рассмотрения. **Envoy AI Gateway/Agent Router** — жив и активен, но архитектурно и по весу (281 МБ
бинарь, Kubernetes-first) не подходит для одного VPS.
