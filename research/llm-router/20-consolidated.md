# 20 — Сводка: ответы по Q1–Q7, противоречия, реальное использование, дефекты

Источники: 10-facts-code.md (F#), 11-facts-history.md (F#), 12-facts-external.md (F#) — сверены
против src/PetBox.LlmRouter* и PetBox MCP (memory/tasks) 2026-09-25. Расхождение с фактовым
файлом помечено **[ПРАВКА]**.

## Q1 — роутер как конечный автомат

Кода-автомата нет: `CapabilityRouter.RunChainAsync` (CapabilityRouter.cs:211-283) — линейный foreach
по приоритету с двумя ветками (breaker.IsOpen → skip; upstream call → success/non-transient/transient).
Явного состояния маршрута («на fallback», «исчерпан») нет — только `EndpointBreaker` per-endpoint
(closed/open/half-open через ConsecutiveFailures+OpenUntil, F3) и в логах/`ServedBy.degraded`.
Решение 2026-08-28 (код-комментарий CapabilityRouter.cs:9-18) сделало обход НЕ reason-driven: и
transient, и non-transient (кроме circuit-open) просто идут дальше по цепи — единственная развилка
осталась «прервать/продолжить брейкер», не «какой следующий шаг политики».

Прошлый рисерч владельца (2026-07-28, m-516ab8b4272a471f8dfb22270014840e) уже сформулировал 8
необратимых решений и настаивает на FSM **над попыткой**, не над маршрутом: алфавит = ~15
pre/inflight + ~8 post-классов исхода (не два булевых), переходы — политика как данные, инварианты
(классы эквивалентности типа `EmbedSpaceId`) поверх всего прогона. Это ближе к тому, что просил Q1,
чем нынешний код.

**[ПРАВКА]** 12-facts-external.md формулирует вывод по Q1 как «явного FSM нет ни у одного аналога».
Проверено против кода/доков: **это неверно на уровне breaker-состояния**. LiteLLM Router имеет
явный per-deployment cooldown-автомат (closed → cooled-down → recovery) с триггерами по типу ошибки
(429 немедленно, >50% failures/мин, невозвратные 401/404/408) и настраиваемым `allowed_fails`/
`cooldown_time` (docs.litellm.ai/docs/routing, раздел Cooldowns — прочитано целиком). Portkey имеет
отдельную одноимённую фичу «Circuit Breaker» в навигации AI Gateway (portkey.ai/docs/product/
ai-gateway/circuit-breaker). Bifrost жив (НЕ архивирован, см. ниже) и заявляет «automatic failover»
как основную фичу. То, что верно и остаётся сильным аргументом (из m-516ab8b4...): ни у одного из
трёх есть понятие **классов эквивалентности выбора** (`EmbedSpaceId`, `RerankQueryAsync` whole-query
fallback) — переизбор маршрута у них не ограничен семантикой потребителя. Это то, чего у аналогов
действительно нет, а не «состояния вообще».

## Q2 — codex: разные провайдеры для субагентов

Роутер готов принять запрос: одна цепь per capability, выбор провайдера по `model`→tier в REST/MCP.
Блокер — на стороне codex. Подтверждено живым инцидентом на observations (F5, `codex-spawn-agent-
model-ignores-configured-provider` + `obs-24ac23add089`, recurrence 2, последняя 2026-09-22):
`spawn_agent` проверяет модель против встроенного списка, а не `model_providers`; апстрим-фикс не
пришёл (`intake/codex-subagent-byok-model-blocked`, confirmed, ждём codex-rs).

Новое (сверено 2026-09-25, developers.openai.com/codex/config-reference): у codex ЕСТЬ
`agents.<name>.config_file` — отдельный TOML-слой конфигурации НА РОЛЬ (не на вызов spawn), плюс
`agents.default_subagent_model`/`agents.default_subagent_reasoning_effort`. Это **статический**, не
динамический механизм: роли можно один раз прописать разные `model_provider`/`model` через отдельные
config-файлы, и тогда КАЖДАЯ роль постоянно бьёт в свой `base_url`/провайдер. Это не решает «дай
оркестратору выбирать провайдера на лету при споне», но решает более узкую и, вероятно, достаточную
для владельца задачу «роль X всегда идёт на дешёвую модель, роль Y — на дорогую» без правки codex.
Второе новое: `model_providers.<id>.wire_api` в текущем справочнике codex документирован как
принимающий **только** `responses` («responses is the only supported value, and it is the default
when omitted») — т.е. кастомный провайдер codex говорит с ним по Responses API, не chat/completions.
Это прямо бьёт по разделу протоколов в proposal (см. 30-proposal.md §2): pass-through для codex
нельзя сделать «просто отдать наш OpenAI-совместимый /v1/chat/completions», нужна либо трансляция
Responses↔OpenAI-chat на шлюзе, либо шлюз сам говорит Responses с апстримом. (12-facts-external F13
эту деталь не поймал — «нет явной информации о model_providers».)

## Q3 — бесплатные модели + автопереключение при отвале

Реестр уже умеет приоритетные цепи с бесплатными leg'ами (F7: openrouter nvidia/free pri 20,
cohere pri 30 в rerank-цепи). Чего нет — **конфигурационного** переключения при исчерпании квоты:
брейкер открывает endpoint на фиксированные 30с (F3, default), это не «длительный cooldown до конца
месяца/дня», который нужен для случаев вида BlockInsight214 (≤20ч/день, F2/12-facts). Ключевой пробел,
подтверждён grep'ом кода: **нигде не читается заголовок `Retry-After`** — `LlmUpstreamException` несёт
только `Transient`+`RateLimited` (bool), без длительности (src/PetBox.LlmRouter/Http/
LlmUpstreamException.cs:14-21). LiteLLM (внешний рисерч + прямая проверка доков) различает триггеры
по типу (429 немедленно / high-failure-rate / non-retryable) и даёт per-deployment override
`cooldown_time` — прямой прототип для «квота кончилась → длинный cooldown вместо 30с».

## Q4 — конфигурация моделей по API-ключам

Работает: `llm_config_upsert`/`llm_config_get`, уровни Project→Workspace→System (первый
непустой выигрывает целиком, F4), ключи — зашифрованная колонка на строке endpoint. CAS через
`version`, shadow-guard от случайного затенения всего уровня (F10-code). Административная запись
теперь идёт через `ILlmRegistryEditor` в те же таблицы `llm_endpoints`/`llm_routes`, что читает
рантайм (см. ниже — контроль дефекта из 11-facts F10/F11, который был закрыт).

## Q5 — пользовательский шлюз в PetBox

REST `/v1/chat/completions` (LlmRouterApi.cs, non-agent consumers, project из API-ключа) и MCP
`llm_chat`/`llm_embed`/`llm_rerank` уже есть и живые (F6, F7-code). Главное препятствие для
«шлюза как отдельного продукта» — `CapabilityRouter` жёстко завязан на `ICoreDbFactory`/проект-
воркспейс резолюцию (F4-code, `ILlmRegistryLevelResolver`), т.е. не отделим от core.db без
переписывания резолвера. `Streaming` не реализован — `stream:true` возвращает 400 с явным
сообщением (LlmRouterApi.cs:16,130-132, подтверждено чтением кода; **[ПРАВКА]** F5-code это тоже
верно фиксирует, без противоречий).

## Q6 — автономный локальный шлюз (dotnet tool / служба)

`PetBox.LlmRouter.Contract` реально dependency-free (проверено csproj: только `IsPackable=false`,
никаких PackageReference), подходит как ядро. `PetBox.LlmRouter` (Impl) тянет `Core`+`Config`+DI+
`FluentValidation`+`linq2db` — тянет и весь тенант-résolver core.db. AOT/trimming: F10/F11-external
дают реалистичную оценку ~10–20 МБ / <200мс старт для `CreateSlimBuilder()`-based Minimal API,
но `FluentValidation`/`linq2db` reflection-mapping не trim-safe «из коробки» — это ядру нужно
обойти явным IL-friendly резолвером (не текущий `LlmRegistryLevelResolver`, который читает core.db).

## Q7 — переписывание заголовков

Нет — подтверждено grep'ом (`grep -rn "Retry-After\|RetryAfter"` по LlmRouter* и Web/LlmRouter пуст,
никаких заголовочных правил вообще). Это целиком новый слой — что и код, и оба фактовых файла
согласно утверждают.

## Противоречия и фактические ошибки в фактовых файлах (не усреднять — вот что реально)

1. **[КРИТИЧНО] 11-facts-history.md F10/F11 устарели.** Текст говорит «LIVE-опасность (Pending):
   админка и MCP пишут СТАРЫЙ реестр, рантайм читает НОВЫЙ». Проверено: карточка
   `work/llm-l5-admin-writes-new-registry` — статус **Done**, коммит `7a3f1d9d`
   (2026-07-12, "fix(llm): admin + llm_config_* write the registry the runtime reads"). Текущий
   `Pages/Llm/Index.cshtml.cs:38-44,189` и MCP оба идут через `ILlmRegistryEditor` в те же
   `llm_endpoints`/`llm_routes`, что резолвер читает. Дефект был реальным и опасным, но закрыт
   более двух месяцев назад — доверять этому пункту факт-файла нельзя, он не перепроверялся.
2. **12-facts-external.md F14 (Bifrost «архивирован, read-only»)** — неверно. README живой
   (raw.githubusercontent.com/maximhq/bifrost/main/README.md), проект активно позиционируется
   («fastest way to build AI applications that never go down»), Docker-образ обновляется. Возможно,
   разведчик спутал с другим форком/датой — источник не проверялся вторым проходом.
3. **10-facts-code.md F10, пункт 4 («IsAvailableAsync — нет калл-сайтов»)** — неверно. 9 реальных
   вызовов: `SearchService.cs`, `AutocaptureDedup.cs`, `BehaviorPatternJob.cs`,
   `MemoryVectorizationJob.cs`, `SearchReindexService.cs`, `SessionDigestJob.cs`,
   `SessionFactsJob.cs`, `TasksVectorizationJob.cs`, `DuckDbSessionEpisodicIndex.cs` — используется
   как pre-flight gate перед фоновыми джобами. Верно то, что он НЕ используется ВНУТРИ
   `RunChainAsync` для выбора leg'а по ходу цепи — это разные утверждения, факт-файл спутал их.
4. **Q1-вывод 12-facts-external.md** разобран выше — «ни у кого нет FSM» неверно на уровне
   breaker/cooldown-состояния; верно на уровне классов эквивалентности (по памяти 2026-07-28).
5. **TTFT/post-completion таксономия (F4-history, ac-72d581037785) сейчас не проверяема на
   практике** — стриминга нет вообще (Q5 выше), поэтому «шов после первого байта» — гипотетическая
   проблема БУДУЩЕЙ фичи, а не текущий баг. Важно не терять это различение при проектировании
   автомата (30-proposal.md), но не выдавать его за уже случившийся инцидент вне контекста.

## Реальное использование сейчас

F9 (11-facts): 16 проектов зовут `llm_chat`/`embed`/`rerank` — $system сам (CI, session digest) +
animemov, agent-relay, yobapub, kpvotes, petbox, petsonde, pochtar, infra, kek-devices, prj1.
Роутер — единственный LLM-фронт для всех них; `IsAvailableAsync`-gate (п.3 выше) — то, что не даёт
фоновым джобам (`SessionDigestJob`, вектор-джобы) валиться в исключение, когда embed недоступен
(деградация тихая, `degraded:true`, а не ошибка).

## Найденные дефекты (для наблюдений — не заведены, решение владельца: не плодить карточки до конца
исследования)

- `Retry-After` не читается нигде в цепочке (п. Q3) — свежий gap, не в фактовых файлах явно.
- `ResponseFormat` недоступен через REST (`ac-72a5f41edd0c`, память) — DTO не принимает поле,
  только MCP/дистиллятор имеют доступ; асимметрия REST/MCP.
- `llm_config_get`/`llm_config_upsert` регистр capability не совпадает (Capitalized vs lowercase,
  `ac-6640307ede52`) — уже отдельно зафиксировано в памяти, не новое.
