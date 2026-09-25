# 40 — Резервная оценка: возражения к 20-consolidated / 30-proposal

Проверено 2026-09-25 самостоятельно, не по синтезу: код `src/PetBox.LlmRouter*`, `src/PetBox.Web/LlmRouter`,
`tests/PetBox.Tests/LlmRouter`; MCP (`m-516ab8b4…`, `m-3370e52b…`, `ac-72d581037785`, observations
`codex-*`, work `route-chain-aborts-on-size-refusal`); внешние: developers.openai.com/codex/config-reference,
github openai/codex (`codex-rs/model-provider-info/src/lib.rs`, `codex-rs/core/src/client.rs`), dotnet/yarp
(csproj, releases, issues), opencode.ai/docs/go, docs.litellm.ai/docs/routing, openrouter.ai/docs (limits,
faq), api-docs.deepseek.com, maximhq/bifrost README + repo API, developers.openai.com/api/reference (responses).
Формат: утверждение → почему неверно/рискованно → что предлагаю.

## 0. Итог в четыре строки

Предложение строит не тот продукт: защищает уникальную ценность внутреннего роутера (классы эквивалентности)
аргументами, которые к шлюзу не относятся, и на этом основании отказывается от готовых шлюзов там, где они
закрывают все семь вводных владельца. Автомат нужен в объёме одного enum и одной таблицы cooldown; трансляция
Responses→chat — не «последним пунктом», а условие Q2 с первого дня (или Responses-нативный апстрим).

## 1. Самое сильное возражение: два продукта склеены в один, и строится тот, у которого есть замена

**Утверждение (30 §6, §8):** один `Gateway.Core`, из которого растут и `CapabilityRouter`, и HTTP-шлюз;
«просто поставить внешний гейтвей» не подтверждается фактами, потому что он не снимает cert-pinning,
level-atomic резолюцию, MCP-контракт и `EmbedSpaceId`.

**Почему неверно.** Все четыре — свойства ВНУТРЕННЕГО роутера (Embed/Rerank/Chat для Memory/Tasks/Sessions и
16 проектов через MCP). Ни одна из вводных Q2–Q7 их не требует: codex-субагентам не нужен `EmbedSpaceId`,
машинам владельца — уровни Project→Workspace→System, автономному хосту — MCP. Cert-pinning на самоподписанный
домашний серт закрывается доверенным CA-bundle у любого Go/Python-шлюза (`SSL_CERT_FILE`/custom CA) — другой
механизм, тот же результат для одного хоста (вывод). Вывод `m-3370e52b…` (2026-07-28) был про замену
`CapabilityRouter` — он верен и остаётся; 30 переносит его на шлюз, где он не работает.

Что реально нужно шлюзу: три входных протокола, трансляция между ними, failover/cooldown, переписывание
заголовков, бюджеты/учёт, виртуальные ключи, UI. Это есть сегодня: **Bifrost** — Go, Apache-2.0,
`npx -y @maximhq/bifrost`, inbound OpenAI + Anthropic drop-in, fallbacks, budgets, virtual keys, секреты
через env, Web UI; репо живое (`archived:false`, pushed 2026-09-25, 8354★) — 20 прав, 12-facts F14 неверен.
**CLIProxyAPI** — Go-бинарь, заявлен вход «OpenAI/Gemini/Claude/Codex» (Codex = Responses). **LiteLLM** —
`/v1/responses` inbound с бриджем на любой провайдер, но Python + Postgres/Redis (`m-3370e52b…`).

**Честный счёт для одиночки (оценка, не замер):** Bifrost — полдня на установку и цепи. Этапы 2–5 из 30 §9
(FSM + policy-as-data, pass-through со стримингом и переизбором до первого байта в трёх протоколах,
трансляция Responses→chat — обязательна, см. §3.3, два хоста, AOT, scope, admin UI, тесты) — 20–35
фокус-дней; у одиночки, который параллельно держит 16 проектов на этом же роутере, — квартал календарного.
Дальше — вечное: каждое изменение протоколов (Responses WebSocket, `prompt_cache_options`, новые
stream-events) чинится руками, у Bifrost/LiteLLM — комьюнити.

**Что предлагаю.** Разделить продукты окончательно: внутренний роутер — свой, там ценность; шлюз для машин —
внешний бинарь как эксперимент на 1–2 дня; свой шлюз — только после списка дыр, которые эксперимент показал
(§5). Где могу ошибаться: если «свой .NET-код, который я понимаю» — самостоятельная цель владельца, счёт
другой, но во вводных этого нет, и тогда это надо записать как цель, а не выводить из фактов.

## 2. Автомат: где оверинжиниринг, где конфиг аналогов реально хуже

**2a. Утверждение (30 §1C):** FSM попытки с ~15 pre + ~8 post классами, переходы — политика как данные.
**Почему рискованно.** Стриминга нет (`LlmRouterApi.cs:131` → 400), произвольного тела нет — post-ветка
пуста. Классов исхода, которые текущий трафик РЕАЛЬНО различает, четыре: transient / 429 / oversize /
прочие 4xx, и все четыре уже закодированы в `LlmUpstreamException` двумя bool плюс строковым матчем
(`OpenAiCompatibleClient.cs:160-190`). Policy-as-data для четырёх символов — таблица 4×2; цена «автомата»
здесь — не переходы, а именованные политики + редактор, что 30 сам признаёт минусом. Минус «больше состояний
без тестов» опирается на 10-facts F10, который неверен (§4): тесты обхода и брейкера есть с 2026-06-04.
**Что предлагаю — код:** исход попытки как закрытый enum, не пара bool:
`Transient | RateLimited(retryAfter?) | QuotaExhausted | AuthFailed | ClientRejected | Oversize`, плюс
`Committed` (охранник, как в `m-516ab8b4…` №5). **Конфиг:** per-endpoint cooldown по классу (таблица),
детекторы `QuotaExhausted` per-provider (status + regex), маршруты с приоритетом — как сейчас. **Не конфиг:**
порядок обхода, half-open, сама детекция. Это шаг 1 из 30 §9 — я с ним согласен, и это ВСЁ, что нужно из
автомата для внутреннего роутера сегодня.

**2b. Где конфиг аналогов хуже — только одно место, релевантное шлюзу.** LiteLLM cooldown — счётчик отказов в
минуту без причины: `allowed_fails`/`cooldown_time`, default 5 с (docs/routing:1086-1127); 401 и 429 кулдаунятся
одинаково, «квота до конца дня» не выражается. Классы эквивалентности — ни у кого, но они нужны только
Embed/Rerank, т.е. внутри PetBox. «Не переизбирать после первого байта» — плоский список у всех, но и у нас
этого нет. Итого конфиг хуже ровно в long-cooldown по квоте — это одно поле, не автомат.

## 3. Дыры, которых в 30 нет или они закрыты неверно

**3.1 Стриминг и ретраи после первого байта.** 30 §1C: `StreamBroken` → `PartialFailure`, видимо клиенту.
Дыра: в SSE после 200 нет канала для ошибки, кроме обрыва/`error`-события, и клиенты ретраят САМИ — codex:
`stream_max_retries` default 5, `request_max_retries` 4, `stream_idle_timeout_ms` 300000
(config-reference:1192-1198). Повтор придёт в шлюз новым запросом и снова пойдёт по цепи. Шлюзу нужно только
(а) не отдавать 200 клиенту до первого байта апстрима (TTFT-буфер — единственное окно, `ac-72d581037785`),
(б) идемпотентно пережить повтор. Post-ветка §1C на шлюзе не нужна; на внутреннем роутере — когда там
появится стриминг, а его там нет и не просят.

**3.2 Tool calls.** Pass-through безопасен. Трансляция Responses↔chat: `function_call` items ↔ `tool_calls` с
разными id-схемами, `reasoning` items без аналога в chat; codex шлёт `reasoning`, `include`, `store:false`,
`prompt_cache_key`, `text`, `service_tier` (client.rs:340-400). Это не «последним пунктом» — см. 3.3.

**3.3 Responses stateful и протоколы апстримов — самая дорогая ошибка синтеза.**
`store` у OpenAI по умолчанию TRUE («Defaults to true when omitted», reference:5541); codex на HTTP-пути шлёт
`store:false` и полный `input` (client.rs:1008), `previous_response_id` — только на WebSocket-пути
(`supports_websockets`; default для кастомного провайдера не проверен). Следствие: для codex по HTTP смена
провайдера между ходами БЕЗОПАСНА; для клиента с `previous_response_id`/`conversation` — нет, шлюз обязан
отвечать 400, не проксировать. Главное: **opencode-go — не «Anthropic Messages»** (30 §2 п.4, puml/01):
у Go эндпоинт зависит от модели — `/v1/responses` (Grok, GPT Luna, Muse), `/v1/chat/completions` (GLM, Kimi,
DeepSeek, MiMo, Hy, LongCat), `/v1/messages` (MiniMax, Qwen) (opencode.ai/docs/go, Endpoints). DeepSeek
напрямую — chat + `/anthropic`, без `/responses` (api-docs.deepseek.com:38-66). Codex говорит ТОЛЬКО Responses
(§4). Итого «codex-субагент на DeepSeek/Kimi через Go или напрямую» = трансляция Responses→chat на шлюзе в
ПЕРВЫЙ день. Единственный путь без трансляции — Responses-нативный апстрим: OpenRouter (observation
`codex-spawn-agent-model-ignores-configured-provider`: codex + openrouter, `wire_api=responses`, 200) — там
есть DeepSeek/GLM/Kimi; цена — комиссия при покупке кредитов, без наценки на инференс (faq:269-280; процент
рендерится клиентски, не снят).

**3.4 Prompt caching при фейловере.** Кэш живёт у провайдера: OpenAI `prompt_cache_key` (codex шлёт),
Anthropic `cache_control`, Go — заголовок `x-opencode-session`, который Go просит СОХРАНЯТЬ при форвардинге
(Validated Clients → Codex). Переезд плеча = холодный кэш: по таблице Go DeepSeek V4 Flash input $0.15 vs
cached $0.003 (50×); при ~71k cached-токенов на запрос (оценка Go) один переезд ≈ +$0.01 и повторяется каждый
ход, пока сессия на новом плече. «Прозрачный» фейловер по 429 может стоить в разы дороже ретрая на том же
плече. Это довод за sticky-по-сессии (LiteLLM: session pinning, routing:835) — 30 о sticky не говорит;
правило «сохранять session-заголовки» — обязательный пункт §5 (заголовки), тоже не упомянут.

**3.5 Токенайзеры и окна.** Виртуальная модель `route:chat-free` скрывает реальную модель → codex считает
контекст по своему `model_info` для slug, compaction срабатывает не там. Тот же класс, что 8192/10240 в
`CapabilityRouter.cs:9-18`. Предлагаю: виртуальное имя = имя primary-модели, окно цепи = min по плечам,
фолбэк только на модели с окном ≥ primary.

**3.6 Стоимость и учёт.** В коде нет ничего: `usage` из ответа выбрасывается (`OpenAiCompatibleClient`
возвращает только `content`), «rate limits and usage policy are out of scope» (`LlmRouterApi.cs:17`). 30 не
планирует учёт ни в одном из пяти этапов. Для шлюза на публичном домене это блокер этапа 4, не «потом».

**3.7 Безопасность ключей.** PetBox-хост: `/v1/chat/completions` уже на публичном домене под `llm:invoke`;
утечка одного ключа = расход с ключей владельца до провайдерского потолка (DeepSeek prepaid — ограничен
балансом; OpenRouter — per-key credit limit есть в их API, limits:200). Новый scope `gateway:proxy` сам по себе
не добавляет ничего. Автономный хост: `env_key` верно, но в §7 не сказано, слушать ли только 127.0.0.1 и
требовать ли ключ шлюза. Предлагаю: (1) провайдерские лимиты на ключах — сегодня, 0 кода; (2) на PetBox-хосте
per-key бюджет из `usage` ДО любого шлюза; (3) автономный — bind 127.0.0.1, ключ обязателен.

**3.8 ToS бесплатных.** Go: «designed for OpenCode and other coding agents… Traffic is monitored for abuse»,
«Only one member per workspace can subscribe» — один Go-ключ, раздаваемый шлюзом нескольким машинам/агентам,
в серой зоне; Muse Spark — «Model training: Yes». OpenRouter `:free` — дневной cap по all-time credits (числа
рендерятся клиентски, не снял), 402 при отрицательном балансе даже для free (limits:198). BlockInsight-
провайдеры — «на месяц», ToS в исследовании нет вовсе. Автопереключение не защищает от бана ключа за
агрегированный трафик — это ограничение сценария, а не шлюза.

## 4. Проверка утверждений, на которых держатся решения

| Утверждение | Вердикт | Источник |
|---|---|---|
| codex `wire_api` — только `responses` | **CONFIRMED** | config-reference:1204; `model-provider-info/src/lib.rs:104-108` — `enum WireApi { Responses }`, `:127` — `"chat" => Err(CHAT_WIRE_API_REMOVED_ERROR)` (chat удалён явно) |
| YARP AOT-совместим | **CONFIRMED**; 12-facts F12 «нет explicit declaration» — REFUTED | `Yarp.ReverseProxy.csproj:11` `<IsAotCompatible>true</IsAotCompatible>`; AOT-баг 2.2.0 закрыт в 2.3.0 (2025-02-27). НО retry/failover на другое назначение YARP не даёт — issue «Retry of "Safe" HTTP requests» open, Backlog → переизбор всё равно свой поверх `IHttpForwarder` |
| «Не сливать CapabilityRouter» | **CONFIRMED** как вывод, по другой причине | Не «стриминг утечёт в модули», а разные продукты (§1). `Impl → GwCore` в puml/02 — риск: общий примитив = общий релиз-цикл |
| Противоречие 2026-08-28 vs 2026-07-28 | **CONFIRMED частично** | 07-28 (`m-516ab8b4…` №1): ~15 классов, 401/404 → cooldown. 08-28 (`CapabilityRouter.cs:9-18`, work `route-chain-aborts-on-size-refusal` Done): «Deliberately NOT reason-classified». Мотив 08-28 — не заводить реестр лимитов плеч; к 401 это не относится, 401 однозначен без реестра. Решение стоит 0 и очевидно: классифицировать МОЖНО, лишь бы обход не зависел от per-leg лимитов |
| 10-facts F10: «NO tests for fallback chain walk, breaker, RerankQuery chunking, response_format retry» | **REFUTED** (все четыре) | `CapabilityRouterTests` (20 тестов: `Falls_back_to_secondary_on_transient_failure`, `Open_circuit_endpoint_is_skipped_without_attempt`, `RerankQuery_falls_back_whole_query_never_mixing_models`…, с 3ae8807c 2026-06-04), `EndpointBreakerTests` (3), `OpenAiCompatibleClientTests` (response_format retry, oversize). 20 не поймал; 30 §1 строит на этом минус |
| Bifrost архивирован (12-facts F14) | **REFUTED**, 20 прав | repos API: `archived:false`, `pushed_at 2026-09-25`, Go, 8354★ |
| `Retry-After` не читается | **CONFIRMED** | grep по LlmRouter* + Web/LlmRouter = 0 |
| «opencode-go шлёт Anthropic Messages» | **REFUTED** | три протокола по модели (§3.3) |
| «почти никто из провайдеров не поддерживает Responses нативно» | **REFUTED частично** | OpenRouter, opencode-go (часть моделей), Azure (`codex-api/src/provider.rs`) — да; DeepSeek напрямую — нет |
| `agents.<name>.config_file`, `default_subagent_model` | **CONFIRMED** | config-reference:838, 842 |
| §3 работает: role-file `model` = виртуальное имя через один parent-provider | **UNVERIFIED**, косвенно за | observation `codex-role-file-drops-model-provider`: `model` из роли доходит до провайдера РОДИТЕЛЯ как есть (Go ответил 401 «Model definitely-not-a-model-xyz is not supported»); `model_provider`/`profile` в роли игнорируются молча. Условие: оркестратор НЕ передаёт `model` в `spawn_agent` (там внутренний список из 5). Первый замер, который стоит сделать |
| LiteLLM cooldown per-deployment с триггерами по типу | **CONFIRMED** поле, **UNVERIFIED** «по типу» | routing:1086-1127: `allowed_fails`/`cooldown_time`, default 5s, счётчик/мин; таблицы Condition→Duration в прочитанном тексте нет |
| codex сам умеет заголовки на провайдера | **CONFIRMED**, не упомянуто ни в 12/20/30 | `model_providers.<id>.http_headers`, `env_http_headers` (config-reference:1178-1186) — Q7 для codex решается без шлюза |
| REST `stream:true` → 400 | **CONFIRMED** | `LlmRouterApi.cs:131-132` |
| Учёт usage/бюджеты есть где-то | **REFUTED** | §3.6 |

## 5. Резервное предложение: меньшая цена, польза за дни

**Шаг 0 — день 1, ноль кода.** (a) Провайдерские лимиты на ключах DeepSeek/OpenRouter. (b) Заголовки для
codex — через `http_headers` в `config.toml`, без шлюза. (c) Замер §3 живьём: codex parent →
OpenRouter (Responses-native), роли через `agents.<name>.config_file` с разными `model`, `spawn_agent` без
`model`. Если работает — Q2 закрыт для всех моделей OpenRouter без единой строки кода.

**Шаг 1 — 1–2 дня, автономный шлюз = готовый бинарь.** Bifrost (`npx`) на домашней машине: Go, DeepSeek,
OpenRouter, home llama-server (custom CA) как провайдеры; цепи free→paid; virtual keys; бюджеты. Codex →
шлюз `/v1/responses` — проверить, есть ли у Bifrost inbound Responses (UNVERIFIED; если нет — CLIProxyAPI,
у него Codex-вход заявлен). Записать всё, что НЕ получилось: этот список — единственный легитимный вход для
«своего» шлюза. Q3, Q4, Q6, Q7 закрываются здесь же.

**Шаг 2 — 2–3 дня, внутренний роутер, независимо от шлюза.** Шаг 1 из 30 §9 в моей редакции §2a: enum
исходов, `Retry-After`, `QuotaExhausted` → long cooldown, 401 → cooldown, таблица cooldown per class на
`LlmEndpoint`; плюс `usage` из ответа в лог-событие (основа учёта). Тесты — дописать в существующие
`CapabilityRouterTests`/`EndpointBreakerTests`, не новые классы.

**Шаг 3 — только если шаг 1 дал дыры.** Свой шлюз минимальный: Responses pass-through + virtual-model map +
sticky session + TTFT-буфер failover на `IHttpForwarder` (YARP) без FSM-фреймворка и без policy-as-data;
AOT потом (dotnet tool на JIT ≈ 70 МБ — не блокер для одной машины). Трансляцию протоколов не писать: либо
Responses-нативный апстрим, либо готовый шлюз.

**Не делать:** `Gateway.Core` как общий примитив двух продуктов; редактор политик; трансляцию своими
руками; PetBox-хост шлюза на публичном домене до учёта и бюджетов (§3.6–3.7).

**Что измерить, если сомнение останется:** (а) сколько запросов/день реально пойдут через codex-субагентов
и с каких моделей — `log_query` по событию 302 сегодня даёт базу только для MCP/REST-трафика, codex-трафика
в PetBox ещё нет; (б) доля cached-токенов в codex-сессиях — цена переезда плеча (§3.4); (в) наличие inbound
Responses у Bifrost и CLIProxyAPI — 15 минут с `curl`.
