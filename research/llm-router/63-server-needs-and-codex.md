# 63 — Что реально теряет сервер PetBox без модуля + локальный замер codex-субагентов

Проверено 2026-09-25 против `src/PetBox.LlmRouter*`, `src/PetBox.Web/LlmRouter`, `src/PetBox.Web/Mcp/LlmRouterTools.cs`,
`src/PetBox.Web/Pages/Llm`, `src/clients-{net,ts,py}` (grep, нет LLM-специфичного кода ни в одном SDK),
живой сервер (`llm_config_get`, `log_query` на логе `petbox`), и живым запуском `codex exec` против
подставного эндпоинта. Опирается на `10-facts-code.md`, `20-consolidated.md`, `40-reserve.md` — расхождений
с ними не найдено, только детализация.

## Часть 1 — что теряет PetBox-сервер при замене модуля внешним шлюзом

### Объём кода
- Модуль (`PetBox.LlmRouter.Contract` + `PetBox.LlmRouter`): 30 файлов, **2571** строк.
- Веб-обвязка (`LlmRouterApi.cs`, `Mcp/LlmRouterTools.cs`, `Pages/Llm/*`): 4 файла, **893** строк.
- Тесты (14 реально релевантных файлов — `tests.*llm` минус 3 ложных совпадения на подстроке
  "...fillM..." в `McpToolCallMetricsTests`/`MemoryStoreSystemBackfillMigrationTests`/
  `NodeIdentityBackfillMigratorTests`): **3348** строк, 11 файлов из `LlmRouter/` + `LlmAdminUiTests`,
  `LlmAdminPageTests`, `LlmChatEndpointAuthzTests`, `LlmRegistryResolverRaceReproTests`.
- Потребители (`LlmClientEmbedder`/`LlmClientReranker` в Memory/Sessions/Tasks, 6 тонких файлов) — не
  считаются частью модуля, это его клиенты; они остаются в любом сценарии (просто указывают на другую
  реализацию `ILlmClient`).
- Клиентские SDK (`src/clients-net`, `-ts`, `-py`): grep `\bllm\b` по файлам — **ноль** совпадений вне
  `clients-ts` (ложные срабатывания на `qwen`); ни один SDK не оборачивает `llm_*` MCP-тулы отдельным
  типом. Значит убийство модуля не трогает ни одного клиентского пакета.

### Классификация по функциям

| Функция | Классификация | Почему |
|---|---|---|
| Fallback-цепь по приоритету (`CapabilityRouter.RunChainAsync`) | **(a)** конфиг шлюза | Priority chain — ровно то, что делают LiteLLM/Bifrost/Portkey из коробки. |
| Circuit breaker (`EndpointBreaker`, open/half-open) | **(a)** конфиг шлюза | LiteLLM `allowed_fails`/`cooldown_time`, Portkey «Circuit Breaker», Bifrost failover — прямые аналоги (40-reserve §2b, §4). |
| Cert pinning домашнего эндпоинта (`CertPinningHttpClientProvider`) | **(a)** конфиг шлюза | Доверенный CA-bundle (`SSL_CERT_FILE`) на Go/Python-шлюзе закрывает то же самое другим механизмом (40-reserve §1). |
| `response_format` retry-on-400 | **(a)** вероятно конфиг/поведение шлюза, не проверено на конкретном кандидате | Провайдерская квирк-логика, обычно есть у зрелых шлюзов; в матрице кандидатов (61-/62-, не в этом файле) нужно сверить поштучно. |
| Registry level cascade (Project→Workspace→System, atomic CAS, shadow-guard) | **(a)/(c) гибрид** | Иерархия tag-based уровней с атомарной заменой ВСЕГО уровня — специфика PetBox-конфига, у внешних шлюзов вместо этого team/virtual-key модель. Сама многоуровневость ушла бы (a → потеряна, у шлюза нет аналога), а мультитенантность per-project осталась бы нужна PetBox (b, ниже). |
| **`EmbedSpaceId`** (инвариант пространства эмбеддингов) | **(b) тонкий клиент, обязателен** | Это ключ индекса векторных хранилищ Memory/Sessions/Tasks, а не свойство маршрутизации — внешний шлюз им не оперирует и не обязан. PetBox должен продолжать хранить, какой физический embed-маршрут отвечает за какое `EmbedSpaceId`, иначе смена fallback-модели у шлюза молча ломает сравнимость векторов (40-reserve подтверждает: ни у одного аналога такого класса эквивалентности нет). |
| **`RerankQueryAsync` whole-query fallback** (все чанки одного запроса — одна модель) | **(c) не выражается во внешнем шлюзе** | Это App-уровневая семантика ОДНОГО логического запроса, состоящего из N HTTP-вызовов (по чанку) — шлюз видит независимые HTTP-запросы, не знает, что они — части одного rerank. Останется кодом в PetBox независимо от бэкенда. |
| MCP `llm_chat`/`llm_embed`/`llm_rerank` | **(b) тонкий клиент** | Меняется на HTTP-вызов к внешнему шлюзу вместо `CapabilityRouter`; PetBox-специфичны scope-проверка (`llm:invoke`) и ответ-обёртка (`ServedBy`, `model.dim`). |
| MCP `llm_config_get`/`llm_config_upsert` | **(b) тонкий клиент — если шлюз даёт admin API; иначе не переносится** | Проверить у кандидата наличие управляющего REST/gRPC API; иначе PetBox либо теряет hot-conf (переходит на редактирование конфиг-файла шлюза руками), либо держит собственный маппинг project→virtual-key поверх шлюза. |
| REST `/v1/chat/completions` (non-agent consumers) | **(b) тонкий клиент** | Auth/project-resolution из ApiKey остаётся PetBox-специфичной; тело запроса проксируется в шлюз. |
| Admin UI (`Pages/Llm/Index.cshtml`) | **(a) удаляется** — если шлюз даёт свой UI (Bifrost — да, 40-reserve §1); **(c)** если нужна интеграция с PetBox-тегами project/workspace | Готовый UI шлюза не знает про PetBox-теги — по факту это two separate admin surfaces, не апгрейд одного в другой. |
| Usage/cost accounting | **не потеря, а приобретение** | Сегодня в PetBox вообще нет учёта (`usage` выбрасывается, `LlmRouterApi.cs:17` — «rate limits and usage policy are out of scope») — 40-reserve §3.6. Любой зрелый внешний шлюз (Bifrost budgets, LiteLLM spend tracking) добавляет то, чего нет сейчас. |
| Streaming | **не потеря** | Не реализовано (`LlmRouterApi.cs:131` → 400) — нечего терять; шлюз, наоборот, закрывает будущий пробел. |
| `Retry-After` — переиспользование заголовка апстрима | **(a) конфиг шлюза (частично)** | Сейчас нигде не читается (`40-reserve` Q7/Q3) — то же самое отсутствие, что и сегодня, пока шлюз не настроен явно. |

### Итог по объёму
Убивается насовсем: **вся регистро-специфичная атомарность/shadow-guard** (~800–1000 строк из 2571,
`LlmRegistryLevelResolver`/`LlmRegistryEditor`/`LlmRegistryImporter`/`LlmRegistryValidator` + их тесты,
грубая оценка по файлам) и Admin UI (893 строки веб-слоя частично). Остаётся неизбежно (не выражается
во внешнем шлюзе): `RerankQueryAsync`-инвариант и `EmbedSpaceId`-маппинг — вместе это малая часть кода
(`RerankQueryAsync` — src/PetBox.LlmRouter/Routing/CapabilityRouter.cs:75-90, счётный десяток строк;
`EmbedSpaceId` — LlmRegistry.cs:33-48 плюс место хранения в БД), но убрать их нельзя без риска тихой
порчи векторных индексов в Memory/Sessions/Tasks.

### Ключи провайдеров сегодня vs при внешнем шлюзе
Сегодня: `ApiKey` провайдера — зашифрованная КОЛОНКА на строке `LlmEndpoint` в core.db (F4, 10-facts-code.md),
редактируется только через `ILlmRegistryEditor` (MCP `llm_config_upsert` / admin UI), при чтении расшифровывается
в процессе; ошибка расшифровки — эндпоинт молча выпадает из цепи (не fallback на другой уровень).
При внешнем шлюзе: ключи переезжают в **окружение шлюза** — секрет в docker-compose `.env`
(`env_key`/`env_http_headers` у codex-провайдера — тот же паттерн, что PetBox использует сегодня для
собственных ссылок на переменные окружения). Это МЕНЯЕТ модель хранения секрета: из зашифрованной строки в
БД под контролем PetBox-приложения (ключ шифрования — часть appsettings) в открытый (или docker-secret)
файл на хосте шлюза — обычная практика для докер-compose secrets, но не то же самое свойство, что колонка
БД с прикладным шифрованием. `40-reserve.md` §3.7 отдельно фиксирует: PetBox-хост `/v1/chat/completions`
уже сегодня публичный под `llm:invoke`, при внешнем шлюзе то же самое повторяется на шлюзе — нужен bind
127.0.0.1 + собственный ключ шлюза для автономного хоста (это НЕ покрыто ни в 20-, ни в 40- как готовое
решение, а как рекомендация).

### Живая нагрузка (сервер $system, лог `petbox`, 7 дней)
```
cap    ep         count
Embed  home       1599
Chat   deepseek    792
Rerank home        698
Embed  openrouter  494   (fallback с home)
Rerank openrouter  311   (fallback с home)
Chat   openrouter    2   (fallback с deepseek — почти не встречается)
```
Вывод: Embed/Rerank реально фолбэкают с `home` на `openrouter` часто (≈24% и ≈31% трафика
capability соответственно) — именно здесь `EmbedSpaceId`-инвариант работает не как теория, а как
ежедневный путь выполнения. Chat почти никогда не уходит на fallback (2 из 794) — цепь `deepseek`
практически не деградирует. Активность по дням: 61–1561 событий/день за 14 дней (`llm_config_get`
за 2026-09-25 подтверждает текущий реестр: 3 endpoint, 7 routes, версия 11).

## Часть 2 — codex-субагенты на разных моделях через один custom model_provider

**Вопрос:** можно ли задать в `agents.<name>.config_file` разный `model`, при этом все роли ходят
через ОДИН custom `model_provider` (один base_url-шлюз)?

### Метод
Временный `CODEX_HOME` (scratchpad `codex-probe/codex-home`, не тронут `~/.codex`), `config.toml`:
`model_provider="probe"`, `model="probe-parent"`, один `[model_providers.probe]` (`base_url =
"http://127.0.0.1:8998/v1"`, `wire_api="responses"`), и `[agents.cheap]` → `config_file =
"agents/cheap.toml"` с `model = "probe-cheap-role-model"`. Поднят фейковый Node HTTP-сервер на
127.0.0.1:8998, отвечающий валидным SSE Responses-потоком; никакого реального провайдера, реальных
ключей (`PROBE_KEY=dummy-not-a-real-key`), сети наружу. Первый ответ фейка — не текст, а
`function_call` (`name:"spawn_agent"`, **`namespace:"multi_agent_v1"`** — обязательное поле в
`ResponseItem::FunctionCall`, без него роутер codex отвечает `unsupported call: spawn_agent`,
подтверждено первой неудачной попыткой) с аргументами `{"agent_type":"cheap","message":"Say hi in
one word and stop."}`, БЕЗ `model` — чтобы проверить именно путь наследования модели из роли, а не
явный override аргумента `model` спавна (это другой, отдельно заблокированный путь —
`codex-spawn-agent-model-ignores-configured-provider`, наблюдение на observations, recurrence 2).
`codex exec --skip-git-repo-check "please handle this"` против этого `CODEX_HOME` (codex-cli 0.153.4).

### Результат — CONFIRMED
Три реальных HTTP-запроса на ОДИН и тот же `127.0.0.1:8998`:
```
#1 model="probe-parent"            (родительский ход, вызывает spawn_agent)
#2 model="probe-parent"            (тот же родительский тред получает function_call_output)
#3 model="probe-cheap-role-model"  (дочерний агент — уже с моделью из agents.cheap.config_file)
```
Заголовки запроса #3: `x-codex-parent-thread-id`, `x-openai-subagent: collab_spawn`,
`"subagent_kind":"thread_spawn"`, `"thread_source":"subagent"` — то же `authorization: Bearer
dummy-not-a-real-key` и тот же `host: 127.0.0.1:8998`, что у родителя, но **другое** значение поля
`model` в теле запроса, взятое из статического `model =` роли `cheap`, а не из аргумента спавна (его
не передавали). Тело `#3.input` заканчивается сообщением `"Say hi in one word and stop."` — именно
task, переданный в `spawn_agent`.

Лог-выдержка (`requests.log`, урезано):
```
#1 POST /v1/responses model="probe-parent"
#2 POST /v1/responses model="probe-parent"   ... function_call_output: "unsupported call: spawn_agent"  (до фикса namespace)
--- после добавления "namespace":"multi_agent_v1" в function_call ---
collab: SpawnAgent
#1 POST /v1/responses model="probe-parent"
#2 POST /v1/responses model="probe-parent"   (function_call_output: spawn ok)
#3 POST /v1/responses model="probe-cheap-role-model"   x-openai-subagent=collab_spawn
tokens used
4
```

**Источник и согласованность с прошлым резервом.** Подтверждено и на уровне исходников (codex-rs
`main`, вероятно чуть новее локальной 0.153.4, но механизм неизменен по структуре типов):
`codex-rs/core/src/agent/role.rs:34-41` — структура `AgentRoleOverrides` явно копирует
`model`/`model_reasoning_effort`/`model_reasoning_summary`/`model_verbosity`/`personality`/
`service_tier`/`features`/`skills`/`developer_instructions` из TOML роли, **но не содержит поля
`model_provider`** — если роль укажет `model_provider` в своём `config_file`, оно будет молча
проигнорировано (роль не может сменить провайдера, только модель). `child_config.rs:138` —
`config.model_provider = turn.provider.info().clone()` — провайдер child ВСЕГДА наследуется от
родителя. Это ТОЧНО совпадает с наблюдением `codex-role-file-drops-model-provider` (40-reserve.md,
таблица §4) и объясняет, почему схема "один provider, разные model per role" — единственная рабочая:
`model_provider` в принципе нельзя переопределить на уровне роли, а `model` — можно.

### Итог
**CONFIRMED**: субагенты codex, порождённые через `spawn_agent` с ролью, чей `agents.<name>.config_file`
задаёт `model = "..."`, реально уходят на этот `model` через ОДИН родительский `model_provider`
(один base_url/шлюз) — без передачи `model` явным аргументом спавна и без правки кода codex. Ограничение:
это статическая, не per-spawn-динамическая привязка (роль ⇒ фиксированная модель), и `model_provider`
у роли переопределить нельзя (только у самого codex верхнего уровня) — что уже отмечено в `20-consolidated.md`
Q2 и `40-reserve.md` §4 как единственный практический путь без апстрим-фикса codex-rs.

### Уборка
Процесс node (порт 8998) убит (`taskkill`), `PROBE_KEY` — заведомо фиктивное значение, не credential.
Временный `CODEX_HOME` остался только в scratchpad (не в репозитории, не в `~/.codex`).
