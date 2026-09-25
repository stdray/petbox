# 80 — Резервная оценка вердикта 70: «требует ли модуль убийства?»

Проверено 2026-09-25 самостоятельно, не по синтезу: перезапуск стенда 64 (agentgateway
`ghcr.io/agentgateway/agentgateway:latest` = v1.6.0-alpha.2, `maximhq/bifrost:latest` =
transports/v2.2.3 от 2026-09-24, фейки `bench/u1,u2`), исходники `agentgateway@v1.6.0-alpha.2`
и `maximhq/bifrost@dev` (048492cf, 2026-09-25), живой лог `petbox` за 7 дней (`log_query`),
`llm_config_get`, код `src/PetBox.LlmRouter`. Формат: утверждение → почему неверно/рискованно →
что предлагаю. 40-reserve — моя прошлая позиция; где факты этапа 2 её опровергли, сказано прямо.

## 0. Итог в пять строк

Вердикт 70 стоит на двух опорах, и обе рухнули при перепроверке: (1) «agentgateway B3 не сработал»
— ошибка конфига стенда (CEL-поле `response.code`, не `response.status`), с исправлением failover
работает и для chat, и для embeddings; (2) «Bifrost — единственный с доказанным failover» — да,
но его circuit breaker (аналог `EndpointBreaker`, 79% реального fallback-трафика сервера) —
**Enterprise-функция**, в OSS-дереве нет реализации. Серверу шлюз сегодня не нужен: честный ответ —
**модуль не убивать (K3 навсегда), codex закрыть локальным agentgateway, S8 (usage) — день в PetBox.**

## 1. Самое сильное возражение: K2 покупает серверу то, что у него уже есть, за то, чего у него нет

**Утверждение (70 §3):** K2 — тонкий CapabilityRouter поверх Bifrost на сервере; K4 отвергнут,
потому что «не решает S8 usage/cost».

**Почему неверно.** Что сервер реально делает из «работы шлюза» — измерено, не предположено
(`events | where MessageTemplate startswith "llm {Capability} served" | summarize count() by
Capability, Endpoint, Attempt`, 7 дней):

| cap | endpoint | attempt 1 | attempt 2 | смысл |
|---|---|---|---|---|
| Chat | deepseek / openrouter | 813 / 0 | 0 / 2 | fallback — 0.25% |
| Embed | home / openrouter | 1652 / **368** | 0 / **126** | 368 — breaker открыт, home пропущен без коннекта; 126 — in-request fallback |
| Rerank | home / openrouter | 704 / **269** | 0 / **42** | то же: 269 breaker-open, 42 in-request |

Итого 805 «переездов» на openrouter за неделю: **637 (79%) — путь `EndpointBreaker` (порог 2,
30 s, `EndpointBreaker.cs:11-12`)**, 168 (21%) — обход цепи внутри запроса. Оба пути покрыты
20 тестами `CapabilityRouterTests` (40-reserve §4, F10 REFUTED). Это ~450 строк
(`CapabilityRouter.cs` 342 + `EndpointBreaker.cs` 46 + `CertPinningHttpClientProvider.cs` 57) из
2560 модуля — K2 удаляет именно ПРОТЕСТИРОВАННУЮ и ЕЖЕДНЕВНО РАБОТАЮЩУЮ часть, а оставляет
реестр/каскад уровней/admin UI/MCP-обёртки (~1500 строк), т.е. всё, что «убийством» не является.

Что K2 покупает взамен: (a) S8 usage/cost — но `usage` сегодня просто ВЫБРАСЫВАЕТСЯ
(`OpenAiCompatibleClient` возвращает только content; 40-reserve §3.6). Записать `usage` в событие
302 — правка одного клиента и одного LoggerMessage, день, без нового процесса. (b) Streaming для
REST `/v1/chat/completions` — но нет ни одного измерения, что 16 внешних проектов его просят
(`stream:true` → 400 сегодня, и никто не завёл intake). (c) Бюджеты per-project — их нет и в
требованиях. Цена: +1 процесс в проде (146 MiB), вторая admin-поверхность (70 сам это признаёт),
секреты из зашифрованной колонки core.db → в окружение/конфиг-стор чужого процесса, апстрим с
6 релизами за 16 дней (transports v2.1.0 08.09 → v2.2.3 24.09), непроверенная двойная регистрация
llama-server (70 §0), и — главное, §3 ниже — **потеря fast-down**, если шлюз — OSS Bifrost.

**EmbedSpaceId в K2 ослабляется, а не «остаётся централизованно».** Сегодня инвариант — свойство
МАРШРУТА, который PetBox сам выбрал: `route.EmbedSpaceId ?? route.Model`
(`CapabilityRouter.cs:64-69`), и `llm_config_get` показывает ровно это: openrouter
`qwen/qwen3-embedding-4b` с `embedSpaceId:"qwen3-embed-4b"` = пространство home. За шлюзом PetBox
шлёт одно виртуальное имя и НЕ знает, какое плечо ответило (Bifrost отдаёт `routing_info`,
agentgateway — не проверял); гарантия «все плечи виртуальной модели — одно пространство» переезжает в
конфиг-дисциплину шлюза — ровно S6 ⚠️ из 61/62. Либо PetBox продолжает ходить по плечам сам
(тогда шлюз для embed ничего не добавляет), либо инвариант становится слабее сегодняшнего.

**Что предлагаю.** Назвать вещи своими именами: рекомендация 70 = K3 + отложенная миграция с
отрицательной чистой ценностью. Серверный шлюз — только по измеренному триггеру (§5), не «фазой 2».

**Про K1 («EmbedSpaceId и affinity — 50 строк у вызывающего»).** Код действительно мал
(`LlmClientEmbedder`/`Reranker` — 36-45 строк × 3 модуля, 238 строк всего; `RerankQueryAsync` —
~100 строк), но affinity требует ЗАКРЕПИТЬ плечо на все чанки — за шлюзом это «первый чанк через
виртуальную модель, прочитать served-model из ответа, остальные чанки — на конкретное имя»:
зависимость от нестандартных полей ответа конкретного шлюза (`routing_info` у Bifrost — есть; у
agentgateway — UNVERIFIED) плюс три копии. K1 удаляет протестированный код, чтобы написать
непротестированный в трёх местах, и тащит миграцию ключей 16 проектов. Хуже K3 по тем же причинам,
что K2, плюс дробление. Не предлагаю.

## 2. agentgateway B3 — REFUTED как «отсутствие»: ошибка конфигурации стенда

**Утверждение (64 B3, 70 §0/§2):** passive health/failover у agentgateway «не сработал эмпирически»,
Bifrost — единственный, у кого B3 сработал; отсюда выбор Bifrost для сервера.

**Что нашёл.** CEL-переменная ответа в agentgateway — `response.code`: собственный интеграционный
тест шлюза `crates/agentgateway/tests/tests/llm.rs:560-562` пишет `unhealthyExpression:
'response.code == 403'` и проверяет ровно сценарий B3 (первый запрос — 403 с priority-0, второй —
200 с priority-1, `:655-676`); резолвер полей — `cel/types.rs:535-539`. Стендовый
`bench/agentgateway/config.yaml:42` использует `response.status` — поле не существует, выражение
не даёт `true`, эвикции нет. Поле `health` per-model реально попадает в failover-backend
(`types/local.rs:4607-4610` → inline policy; `:4705-4740` собирает priority-группы).

**Живой повтор (те же u1/u2, u2 в `U1_FAIL_MODE=429`):**
- оригинальный конфиг: 6/6 → 429, u2 hit 7, u1 hit 1 (только мой прямой пробник) — воспроизвёл 64;
- `response.status`→`response.code` (единственная правка; копия конфига в scratchpad
  `bench-rerun/agw-code.yaml`): chat `req1 429, req2..6 200`, u1: 5, u2: 1;
  `/v1/embeddings` на ту же `route-fallback`: `429, 200, 200`.
- **CONFIRMED works**, с оговоркой семантики: failover у agentgateway — **следующий запрос**
  (эвикция плеча; `schema/config.md:84779` «requests will move onto the next priority group»), не
  повтор внутри запроса: первый запрос после падения плеча возвращает 429 клиенту. Это ровно
  модель `EndpointBreaker` (79% серверного fallback-трафика), но не `RunChainAsync` (21%).
  Policy `retry` (`binds[].listeners[].routes[].policies.retry`, `schema/config.md:6558-6563`,
  `codes`/`condition`) существует только для generic-роутов, в `llm:`-shorthand её нет; совмещение с
  AI-backend — **UNVERIFIED**, не прогонял.
- Debug-лог не пишет строк про health/evict и когда failover РАБОТАЕТ — отсутствие строк в 64 не
  было свидетельством.

**Что предлагаю.** Исправить `bench/agentgateway/config.yaml:42` (одно слово), переписать B3 в 64 и
§2 в 70: аргумент «Bifrost для сервера, потому что только у него B3» — снят. Готча №9 из 64
(«без схемы легко угадывать поля») подтверждена ещё раз — CEL-контекст в схеме не описан, его
надо брать из тестов шлюза.

## 3. Bifrost: офлайн-старт CONFIRMED; circuit breaker — Enterprise (REFUTED S4 ✅ из 61)

**Офлайн-старт — CONFIRMED works.** `docker run --network none`, свежий `/app/data` (config.db не
существовал — холодный старт без прежних pricing-данных), `config.json` с `pricing_url:
"file:///app/data/pricing.json"` + `model_parameters_url: file://…` (файлы из
`examples/configs/withlocalpricingfiles/`), образ `maximhq/bifrost:latest` (v2.2.3): «Time spent in
Bifrost server bootstrap 9595 ms», «successfully started bifrost, serving UI on :8080», контейнер
`running`. Опасение 70 §0 («на чистом старте не перепрогнано») закрыто. Цена: sample-datasheet —
«a handful of models», полный каталог надо скачивать с `getbifrost.ai/datasheet` (через `pfetch`)
и обновлять руками — каталог питает cost-учёт (то самое S8, ради которого K2) и валидацию
provider↔model (`docs/features/governance/routing.mdx:110`); плюс на старте: admin недоступен без
`setup_token`, «git binary not found — Claude Code / Codex marketplace routes disabled».

**Circuit breaker — Enterprise.** `docs/enterprise/circuit-breaker.mdx:9`: «Circuit Breaker in
Bifrost Enterprise automatically reroutes … opens the circuit … until the cooldown window expires».
В OSS-дереве `grep -rl "CircuitBreaker\|circuit-breaker" --include=*.go` даёт только константу
`core/schemas/bifrost.go:485` и CLI-каталог имён — реализации нет. 61 S4 ✅ ссылался на эту константу
как на «отдельный движок» — REFUTED. OSS-Bifrost умеет только **явный список `fallbacks`** внутри
запроса (`core/bifrost.go:5163,5442`; embed идёт тем же `handleRequest`, `:1372`) и «automatic
fallbacks» из весов Virtual Key (`routing.mdx:112-124`) — т.е. КАЖДЫЙ запрос при спящем домашнем ПК
сначала ждёт его connect-timeout (3 s по `llm_config_get`), а это ровно то, ради чего написан
`EndpointBreaker` («the home PC asleep never costs a connect-timeout on every call»). K2 на OSS
Bifrost = регрессия fast-down на 637 запросах/неделю; adaptive LB, clustering, guardrails, audit —
тоже `docs/enterprise/`. 146 MiB ×8 — на VPS не блокер, но платить их за шлюз без breaker незачем.

**Что предлагаю.** Если серверный шлюз когда-нибудь понадобится — agentgateway (17.5 MiB, без
сетевых зависимостей, один `custom`-провайдер на chat+embed+rerank, eviction в OSS, Apache-2.0/LF),
но на **стабильном** релизе; Bifrost — только если Enterprise приемлем, что для одиночки — нет.

## 4. Цена чужого софта для одиночки

- **agentgateway.** Стабильный — v1.5.0 (2026-08-27); стенд — alpha. Breaking changes в КАЖДОМ
  минорном: v1.4.0 (удалены musl-образы, новый top-level `gateways`, `llm.port` — deprecated в
  схеме), v1.5.0 («Review token-based limits … `AGENTGATEWAY_LEGACY_LLM_USAGE_TOKEN_SEMANTICS`»,
  JWT `iss`), v1.6.0-alpha.2 («(breaking) llm: only match exact path instead of any suffix», PR
  #3539). Конфиг-схема живая: локально это ловится перезапуском, на сервере — простоем. Ключи:
  `params.apiKey.file`/env (`schema/config.md:81463-81464`), без шифрования at rest — права на файл.
  Лицензия Apache-2.0 (LICENSE), Linux Foundation.
- **Bifrost.** 6 релизов transports за 16 дней; ключи в SQLite-конфиг-сторе под
  `encryption_key`/`BIFROST_ENCRYPTION_KEY` (`transports/bifrost-http/lib/config.go:177,5570`) —
  сопоставимо с колонкой core.db; ядро Apache-2.0, но breaker/adaptive LB/clustering/guardrails/
  audit/access-profiles — Enterprise; каталог цен — ручной ритуал за RU-блокировкой.
- **Общее.** Ещё один процесс, который надо мониторить (HealthPoller умеет), бэкапить (конфиг-стор),
  и второй лог, куда смотреть при инциденте. Локально это стоит ноль (перезапустил); на сервере —
  постоянная плата без измеренной выгоды (§1).

## 5. Резервное предложение и минимальный первый шаг

**Не K2 и не K1. K3 как постоянный режим (модуль живёт), плюс шаг 2 из 40-reserve §5 внутри
PetBox.** Это отказ от моего же 40-reserve §5 шаг 1 в части «Bifrost» — там S4 принимался на веру
(«fallbacks, budgets»), сегодня breaker у него Enterprise.

1. **Часы:** `bench/agentgateway/config.yaml:42` → `response.code`; B3 в 64 → ✅ (next-request);
   70 §2 — снять «Bifrost для сервера». Один коммит документации.
2. **0.5 дня, сервер не тронут:** локальный agentgateway для codex (метод 63/64 B1): роли → модели
   через один `model_provider`, free-плечи с `health.unhealthyExpression: 'response.code == 429 ||
   response.code >= 500'` + `eviction`, домашний llama-server через `backendTLS`. Стабильный тег,
   не `latest`. Записать список того, что НЕ получилось — единственный легитимный вход для чего-либо
   большего.
3. **1–2 дня, PetBox:** `usage` из ответа → событие 302 (S8 без шлюза), `Retry-After` в breaker,
   enum исходов вместо двух bool — в существующие `CapabilityRouterTests`/`EndpointBreakerTests`.
4. **Триггеры для повторного рассмотрения серверного шлюза** (измерять, не гадать): intake от
   внешнего проекта на streaming/бюджеты; Chat attempt-2 > 5% за неделю; появление третьего
   capability (vision/multimodal embed из archive — но это правка `Content` в PetBox при любом
   шлюзе). Ни один не сработал — шлюз на сервер не ставится.

**Не делать:** двойную регистрацию llama-server в Bifrost (70 §3 п.1) — эксперимент нужен только
для сервера, которого не будет; миграцию ключей 16 проектов; alpha в проде.

## 6. Проверка утверждений

| Утверждение | Вердикт | Источник |
|---|---|---|
| agentgateway: passive failover не работает (64 B3) | **REFUTED** — конфиг стенда; работает с `response.code` | `tests/tests/llm.rs:562`, `cel/types.rs:535`; живой повтор §2 |
| agentgateway: failover внутри запроса (retry) | **UNVERIFIED** | `retry` только в generic-роутах, `schema/config.md:6558` |
| Bifrost стартует офлайн с `pricing_url: file://` на чистой БД | **CONFIRMED** | `--network none`, лог «successfully started», §3 |
| Bifrost OSS: circuit breaker / cooldown (61 S4 ✅) | **REFUTED** — Enterprise | `docs/enterprise/circuit-breaker.mdx:9`; grep OSS-дерева — только константа |
| Bifrost: fallback покрывает embed/rerank | **CONFIRMED** | `core/bifrost.go:1372→handleRequest:5442` |
| Chat fallback ≈ 2/794 (63) | **CONFIRMED**, уточнено 2/815 | `log_query`, 7 дней |
| Embed/Rerank fallback ≈ 24%/31% (63) | **CONFIRMED**, уточнено: 79% — breaker-open, 21% — in-request | таблица §1 |
| Bifrost `latest` содержит `file://`-путь (issue #4305) | **CONFIRMED** | v2.2.3 стартовал с ним |
| agentgateway `response.model`/served-leg в ответе (нужно для K1-affinity) | **UNVERIFIED** | не проверял |
| Внешние проекты просят streaming/бюджеты | **UNVERIFIED** — нет intake, не измерялось | — |

Уборка: контейнеры `agw-rerun`, `bifrost-offline` удалены (`docker rm -f`), node-фейки на 9101/9102
убиты; образы оставлены (agentgateway 147 MB, bifrost 360 MB on-disk). Ключи — только `dummy-*`.
