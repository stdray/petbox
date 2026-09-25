# 64 — Практический стенд: Bifrost vs agentgateway

Проверено 2026-09-25. Финалисты: **Bifrost** (`maximhq/bifrost:latest`, Docker Hub) и
**agentgateway** (`ghcr.io/agentgateway/agentgateway:latest`, v1.6.0-alpha.2). Контрольная точка
LiteLLM **выброшена из стенда по решению владельца** (RAM были частично измерены — idle ~475 MiB,
образ 1.69 GB, старт ~12s без Postgres — но по прямому указанию не включены сюда и контейнер/образ
удалены).

Метод: docker (Docker Desktop, Windows), без реальных ключей/трат. Два фейковых апстрима на
127.0.0.1, `research/llm-router/bench/u{1,2}/server.js` (node, без зависимостей): **u1** (:9101) —
здоровый OpenAI-совместимый chat-completions (SSE, tool_calls, включая codex'овый `exec_command`
с реальным `echo hi`), `/v1/embeddings`, `/v1/rerank` (Cohere-форма), `/v1/models`; **u2** (:9102) —
тот же код с `U1_FAIL_MODE=429` (Retry-After) или `500`. Оба логируют каждый запрос
(`requests.log`, JSONL) и умеют дампить полные тела (`U1_DUMP_BODIES`). Шлюзы видят их как
`host.docker.internal:910{1,2}`. Конфиги — `research/llm-router/bench/{bifrost,agentgateway}/`.

## Таблица сценарий × финалист

| # | Сценарий | Bifrost | agentgateway |
|---|---|---|---|
| B1 | codex (Responses, wire_api=responses) → шлюз → u1 chat-completions: перевод + tool call + реальный shell round-trip | ✅ | ✅ |
| B2 | routing по имени виртуальной модели → u1 / u2 | ✅ | ✅ |
| B3 | fallback u2(429/500) → u1; Retry-After | ✅ (явный список) / Retry-After ✅ | ⚠️ Retry-After ✅, авто-failover не сработал в стенде |
| B4 | embeddings + rerank через шлюз к u1 | ✅ (нужен provider-type `vllm`, не `ollama`) | ✅ (один provider `custom` на все три) |
| B5 | header rewrite: снять клиентский, добавить свой | ✅ | ✅ |
| B6 | ресурсы: RAM idle/200req, образ, старт | см. ниже | см. ниже |

## B1 — codex через шлюз, детально

Метод переиспользован из `63-server-needs-and-codex.md` (временный `CODEX_HOME`, scratchpad
`codex-probe`): `model_provider` с `base_url` = шлюз, `wire_api="responses"`, без реальных ключей.
u1 при виде `tools` со схемой codex'ового `exec_command` (найдена дампом реального тела запроса —
`{cmd, workdir, tty, ...}`, обязателен только `cmd`) отвечает tool_call
`exec_command(cmd="echo hi")` вместо захардкоженного примера — так round-trip доказывает не только
транспорт, но и что codex реально ИСПОЛНИЛ команду и вернул её вывод через шлюз.

**Bifrost** (`model=ollama/m1`, ollama-провайдер → u1): `codex exec --skip-git-repo-check
"please run: echo hi"` →
```
exec
"C:\...\pwsh.exe" -Command 'echo hi' ... succeeded in 77ms:
hi
codex
shell said: Chunk ID: ... Output: hi
```
Дамп второго запроса к u1 (`tool` role message): `"content":"Chunk ID: 93e23a\nWall time: 0.0749s\n
Process exited with code 0\nOriginal token count: 1\nOutput:\nhi"` — полный, настоящий вывод
`echo hi`, не мок. Проверка на HTTP-уровне отдельно (curl → `/v1/responses`, `stream:true`) —
корректный SSE Responses-поток: `response.created` → `response.in_progress` →
`response.output_item.added` (`type:function_call`) → `response.function_call_arguments.delta` —
подтверждает X1/X2 из `61-candidates-go.md` эмпирически, не только по коду.

**agentgateway** (`model=u1-chat`, provider `custom` → u1): идентичный round-trip, тот же вывод
`hi`. Не-стриминговый HTTP-тест `/v1/responses` тоже работает (`"status":"completed"`,
`output:[{type:"function_call",...}]`). Оба шлюза реализуют Responses↔chat перевод рабочим
образом для кастомного (не своего) OpenAI-совместимого апстрима.

## B2/B3 — routing и fallback, детально

**Bifrost.** Два provider'а: `ollama` → u1 (`allow_private_network:true`, ключ через
`ollama_key_config.url`), `vllm` → u2 (`vllm_key_config.{url,model_name}`). `model:"ollama/m1"` →
200 от u1; `model:"vllm/m1"` → 429 от u2 (Retry-After:3 проброшен клиенту как есть). Явный fallback
через `"fallbacks":["ollama/m1"]` в теле — сработал: ответ пришёл от u1,
`extra_fields.routing_info:{is_fallback:true,primary_provider:"vllm"}`. **Формат fallback —
плоский список строк `"provider/model"`**, не объекты `{provider,model}` (та форма — для
внутреннего Go-типа/не-OpenAI путей; попытка отправить объекты дала generic `400 Invalid request
payload` без подсказки на поле — see готчи).

**agentgateway.** Один provider-тип `custom` (`formats:[completions,embeddings,rerank]`) на каждый
апстрим, две `virtualModels` с `routing.conditional` → маршрутизация по имени сработала сразу.
Для fallback у agentgateway есть заявленный (в JSON Schema, `agentgateway.dev/schema/config`)
механизм **сильнее** декларативного списка Bifrost: `virtualModels[].routing.failover.targets`
(приоритетные группы) + per-model `health.unhealthyExpression`/`eviction` (passive health,
default — "любой 5xx или обрыв соединения = unhealthy", **НЕ 429**). Настроили explicit
`unhealthyExpression: 'response.status == 429 || response.status >= 500'` +
`eviction:{duration:5s, consecutiveFailures:1}` на u2-chat, `failover.targets` = [u2 prio0, u1
prio1]. **Результат: 5 последовательных запросов к `route-fallback` все получили 429 от u2, ни
одного реального переключения на u1** — ни в обычном логе, ни под `RUST_LOG=agentgateway=debug`
не появилось ни одной строки про health/evict/failover. Либо нужна другая триггер-модель
(активные healthcheck'и, не пассивные по ответам конкретного запроса), либо это разрыв альфа-версии
(1.6.0-alpha.2) — честно **⚠️, не ❌**: конфиг-поверхность документирована и валидна по схеме,
поведение в стенде не подтвердилось за отведённое время.

## B4 — embeddings + rerank, детально

**Bifrost**: `POST /v1/embeddings {"model":"ollama/m1",...}` → 200, вектора от u1. `POST
/v1/rerank {"model":"ollama/m1",...}` → **400 `"rerank is not supported by ollama provider"`** —
подтверждает предупреждение `61-candidates-go.md` X2 буквально (только `vllm`-провайдер
объявляет rerank). Тот же запрос с `"model":"vllm/m1"` (переключили u2 временно в healthy-режим
для теста) → 200, `results` с `relevance_score`, `routing_info.provider:"vllm"`.

**agentgateway**: тот же провайдер `custom` (u1-chat) обслужил и `/v1/embeddings`, и `/v1/rerank`
без переключения provider-типа — `formats` в конфиге явно перечисляет все три поддерживаемые формы
на ОДНОМ backend. Меньше конфигурационных ловушек для нашего кейса (llama-server как единственный
домашний апстрим для chat+embed+rerank сразу).

## B5 — header rewrite, детально

Оба шлюза по умолчанию **не форвардят произвольный клиентский заголовок** (`x-client-should-be-
stripped` в запросе клиента ни разу не долетел до u1/u2 в логах ни у одного шлюза — заголовки,
которых нет в явном allow-списке транспорта, просто не проксируются) и оба **добавили свой** через
конфиг: Bifrost — `network_config.extra_headers` на provider; agentgateway — **не** `llm.policies.
requestHeaderModifier` (это поле не существует в LLM-роуте — `400 unknown field
requestHeaderModifier`, проверено по офиц. JSON Schema `agentgateway.dev/schema/config`), а
per-model `requestHeaders:{add,remove}` (`HeaderModifier`). Оба подтверждены логами u1:
`"x-gateway-injected":"bifrost-was-here"` / `"agentgateway-was-here"` дошли, `x-client-should-be-
stripped` — нет.

## B6 — ресурсы

| | Bifrost | agentgateway |
|---|---|---|
| Образ (Docker Hub API, compressed) | 90.6 MB (amd64) | не проверял (ghcr) |
| Образ (`docker images`, on-disk) | 360 MB | 147 MB |
| RAM idle (docker stats, после старта) | ~146 MiB | ~17.5 MiB |
| RAM после 200 req (curl-цикл, chat) | ~140 MiB (стабильно, без роста) | ~19 MiB (стабильно) |
| Старт (container run → первый 200) | ~6s, **но только с доступом к `getbifrost.ai`** (см. готчу №1) | <1s, без внешних зависимостей |
| Внешние зависимости при старте | Да (pricing catalog fetch) | Нет |

agentgateway (Rust) идёт по RAM в ~8× легче Bifrost (Go) в этом стенде — ожидаемо по 62-
candidates-other.md ("оценка, не замер"), теперь подтверждено числом.

## Найденные подводные камни конфигурации

1. **Bifrost падает насмерть на старте без сети до `getbifrost.ai`** (`failed to initialize
   pricing manager: ... unexpected EOF`) — контейнер стартует, инициализирует SQLite, и на шаге
   "initializing model catalog..." виснет ~90s, затем `fatal`. Прямое попадание под RU-блокировку
   этого домена (`curl` напрямую — timeout; через `pfetch`/прокси — 200 OK). Решение — HTTPS_PROXY/
   HTTP_PROXY в окружении контейнера (взято из `~/.pfetch.conf`, не напечатано в чат). Для
   реального VPS-деплоя это означает: Bifrost требует исходящий доступ к конкретному домену
   `maximhq`, иначе не стартует вовсе — не нашёл документированного offline/skip-pricing-sync
   флага за отведённое время проверки.
2. **Bifrost блокирует private-IP base_url по умолчанию** (SSRF-защита) — `allow_private_network:
   true` обязателен для локального теста; нерелевантно для реального деплоя (апстримы публичные),
   но ловушка для любого, кто повторяет этот стенд.
3. **Bifrost's key-status `list_models_failed` не блокирует трафик** — если апстрим не отдаёт
   `/v1/models`, статус ключа показывает ошибку, но `/v1/chat/completions`/`/v1/embeddings`/
   `/v1/rerank` всё равно работают. Статус — косметика для UI, не health gate.
4. **Bifrost: rerank жёстко привязан к provider-типу** (`ollama` — нет, `vllm` — да), не к факту
   наличия эндпоинта у апстрима — см. B4.
5. **Bifrost: `fallbacks` в OpenAI-совместимом REST — плоский список строк**
   `["provider/model",...]`, не объекты — объектная форма (используемая внутренним Go API) даёт
   generic `400` без указания на поле.
6. **agentgateway: `llm.policies.requestHeaderModifier` не существует** — правильное место
   `llm.models[].requestHeaders` (по-модельно), а не централизованно на уровне `llm.policies` (там
   это поле валидно только в generic HTTP-роутах `binds[].listeners[].routes[].policies`).
7. **agentgateway: hot-reload конфига не подхватил правку файла**, смонтированного bind-mount'ом с
   Windows-хоста в Docker Desktop, несмотря на лог `state_manager: Watching config file` — новая
   `virtualModel` не появилась (`404`) до explicit `docker restart`. Похоже на известный разрыв
   file-watch на Windows bind-mounts (virtiofs/grpcfuse), не специфично для agentgateway — но
   ловушка при итерации на конфиге в этой среде.
8. **agentgateway: passive health/failover не сработал эмпирически** в этом стенде — см. B3.
9. **agentgateway: понадобился реальный `Content-Type`/схема из офиц. JSON Schema
   (`agentgateway.dev/schema/config`)**, а не из README-примеров — README-примеры (`llm-cost-
   routing`, `llm-keyed-rate-limit`) не показывают header-rewrite вообще; без схемы легко было бы
   потратить итерации на угадывание поля.

## Итог для матрицы этапа 3

И Bifrost, и agentgateway реально закрывают X1/X2 (Responses↔chat, включая tool calls, до
конкретного `echo hi`, дошедшего до codex и обратно) — не только по докам/коду, но по живому
прогону через настоящий `codex exec`. B2/B4/B5 — оба ✅, с разной ценой конфигурации: agentgateway
даёт один provider-тип на chat+embed+rerank (проще для домашнего llama-server), Bifrost требует
`vllm`-тип specifically для rerank. Главное расхождение — B3: у Bifrost fallback предсказуем и
подтверждён (explicit-list), у agentgateway декларируемый health-based failover богаче на бумаге
(приоритетные группы + configurable unhealthy-CEL + eviction), но не подтвердился в этом стенде за
отведённое время — не блокер, но проверить отдельно перед тем как полагаться на него, с более
длинным окном наблюдения или на non-alpha релизе. По ресурсам agentgateway безоговорочно легче
(17.5 MiB vs 146 MiB idle, 147 MB vs 360 MB образ) и не имеет внешних сетевых зависимостей при
старте — Bifrost имеет одну specific и потенциально блокирующую (недокументированный offline mode)
зависимость от `getbifrost.ai`.
