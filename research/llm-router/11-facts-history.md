# 11 — Факты: прошлые размышления, карточки, конфигурация, реальное использование

## Факты

**F1: Ядро идеи роутера как конечного автомата (idea/llm-router, accepted)**
Идея `llm-router` (версия 68, принята; @ideas доска $system) формулирует роутер как модуль, абстрагирующий `Embed`/`Rerank`/`Chat` за политикой выбора провайдера с fallback-цепочкой. Primary = self-hosted (домашний ПК), fallback = облако (OpenRouter, DeepSeek). Модель переиспользуется дистилляцией PetBox и сторонними проектами.

**F2: Размышления про FSM, таксономия отказа = алфавит автомата (память, 2026-07-28)**
В memory store `notes` (ключ `m-516ab8b4272a471f8dfb22270014840e`, Project-scope) записана сводка рисерча 2026-07-28 на идею `llm-router-research-vs-litellm`. Ключные вводные: таксономия отказа = **инварианты** над всем прогоном FSM; ограничение эквивалентности задано семантикой ПОТРЕБИТЕЛЯ, не роутером; необратимые решения (1. таксономия отказа, 2. телеметрия проигравших, 3. видимость запроса, 4. устойчивость идентичности). Полный анализ + сравнение с LiteLLM/OpenRouter/Portkey в комментариях к ideas/`llm-router-research-vs-litellm`.

**F3: Концентрация, приоритеты, admission control (idea/llm-router-concurrency-and-priority-queue, accepted)**
Идея (версия 532, принята) про отсутствие видимости занятости провайдера. CapabilityRouter.RunChainAsync (CapabilityRouter.cs:221-268) имеет приоритеты и перебор; circuit breaker есть. Но llama-server уже отдаёт `/slots + ?fail_on_no_slot=1` — готовый admission control, который роутер не использует. Находка меняет подход соседней идеи `llm-router-concurrency-and-priority-queue`.

**F4: Три вида отказа и их различие (память, ac-72d581037785, Feedback)**
Автокорзина ( 2026-07 или ранее): владелец склеивает три триггера в один. TTFT-таймаут = единственное окно для незаметного переключения; как только первый байт клиенту, HTTP 200 зафиксирован, failover невозможен. Post-completion-шва в CapabilityRouter нет (RunChainAsync возвращает первый успех), бито дважды.

**F5: Codex не может спавнить субагента на модели другого провайдера (observations, seen)**
Две карточки (recurrence 2, последняя 2026-09-22): `codex-spawn-agent-model-ignores-configured-provider` и `obs-24ac23add089` — spawn_agent валидирует модель против hard-coded внутреннего списка, а не против `model_providers` из конфига. Работяга `intake/codex-subagent-byok-model-blocked` (confirmed, 2026-09-09) выложена наверх codex-rs; ждём апстрим. **Последствие для Q2:** codex не может выбирать разные провайдеры для разных ролей субагентов.

**F6: Qwen modelGrades — identity-map, не реальная трансляция (файл 31-modelgrades.md)**
Система `agents.modelGrades` в qwen-code 0.23.0 — `{grade: modelSelector}`. Приоритет резолюции: явная модель → разрешённый грейд → built-in Explore → модель родителя. В `~/.qwen/settings.json` грейды для девяти qwen-провайдеров есть, но это identity-map (`"openai:<id>": "openai:<id>"`), не переименование. Незнакомая модель отклоняется явной ошибкой. **Без явной поддержки провайдер-динамики в грейдах невозможна гибкость для Q2.**

**F7: Текущая конфигурация роутера (llm_config_get, версия 11)**
Уровень: `System:$` (workspace-level, не project-specific). 3 endpoints: home (https://roukek.netcraze.pro:65112, cert-pin), deepseek (api.deepseek.com), openrouter (api/openrouter.ai). 7 routes: Embed (home pri 10 / openrouter pri 20), Rerank (home 10, openrouter nvidia/free 20, cohere 30), Chat (deepseek v4-pro pri 10 + thinking:Disabled / openrouter v4-pro pri 20 + thinking:Disabled). **Home endpoint производительнее облака на embeddings/reranking (локальные qwen3-модели), chat — на deepseek.**

**F8: Home llama-server в работе с управлением сервисом (память, m-7b365903078349298c7ad6f3ce38ef7b, Project-notes)**
Локальный llama-server работает как WinSW-служба на Windows-машине (CUDA 13.3, b9490 build). HTTPS с self-signed, .NET cert-pinning. Exposed через Keenetic на нестандартный порт 65112. HF_HOME кешируется, no re-download. embed загружается ~6s из кеша. **Инфра-находка:** переключение на новую сборку llama.cpp ломает Firewall — правила привязаны к пути бинарника, новый путь = нет разрешения (набито 2026-08-27).

**F9: 16 проектов используют роутер в $system и $workspace (log_query, все доски, memory vect logs)**
Вызовы llm_chat/embed/rerank идут из $system себя (CI jobs, session digest), plus: animemov, agent-relay, yobapub, kpvotes, petbox, petsonde, pochtar, infra, kek-devices, prj1 в $system workspace. SessionDigestJob (фоновая работа) использует embed для семантики, rerank для поиска, chat для summary. **Роутер — единственный LLM-фронт для всех этих потребителей.**

**F10: Администрирование: L5 blocker, две ловушки целостности (work/llm-l5-admin-writes-new-registry, llm-config-upsert-full-replace-no-cas)**
LIVE-опасность (Pending): админка (Pages/Llm/Index.cshtml.cs) и MCP (LlmRouterTools) ПИШУТ в старый реестр (config/{ws}.db), а рантайм (CapabilityRouter) ЧИТАЕТ из нового (core.db). Следствие: владелец редактирует эндпоинт/маршрут в UI → видит "сохранено" → **ничего не изменяется в маршрутизации**. CAS защищает от конкурентной правки, но не от попадания не в тот проект. Wildcard admin-ключ имеет defaultProject=$system, и забытый projectKey в llm_config_upsert перезапишет боевой уровень System:$ без отката.

**F11: Открытые конфликты и баги (work и intake доски)**
- `llm-router-config-409-ambiguous-path` (Pending): /v1/conf выбрасывает 409 на неоднозначный path при нескольких маршрутах на capability (есть легитимный сценарий с разными приоритетами и тегами).
- `stale-llmrouter-rest-docs-memory` (intake/reported): документация памяти твердит «llm router только MCP, REST нет», но REST/OpenAI-совместимый endpoint уже live с ci.305+.
- `llm-thinking-toggle-impl` (work/Done): route-level thinking (enabled/disabled) реализован и покрыт (deepseek-v4-pro + thinking:disabled для eval jobs).

**F12: Внешние аналоги не закрывают требования (память, ac-95098643ffcd, Feedback)**
Рисерч 2026-07-28 сравнил с LiteLLM, OpenRouter, Portkey. Вывод: они multitenancy-SaaS гейтвеи, PetBox = локальный routing. Требования PetBox: (1) классы эквивалентности по семантике потребителя (EmbedSpaceId, RerankQueryAsync), (2) admission control на self-hosted (llama-server /slots), (3) таксономия отказа = инварианты FSM. LiteLLM/OpenRouter не имеют понятия эквивалентности вообще; LiteLLM тяжелый, требует Python/pip/интеграции; OpenRouter/Portkey видят бэкенд только постфактум.

## Пробелы и противоречия

- **Документация vs реальность:** REST-эндпоинт live, но memory/docs и memory/notes говорят "нет" (закрыто intake/stale-llmrouter-rest-docs-memory).
- **Администрирование не работает:** UI показывает "сохранено", но рантайм читает другое хранилище (live, Pending, L5-приоритет).
- **Codex блокирует Q2:** субагент не может ехать на модели провайдера, отличного от родителя (ждём апстрим, хотя кит обещает `dynamic_model_at_spawn`).
- **Таксономия отказа vs реализация:** память записала инварианты FSM, но CapabilityRouter.RunChainAsync не различает TTFT-таймаут и post-completion-отказ (бито дважды).

## Что это значит для вопросов владельца

- **Q1 (FSM):** Размышления о таксономии отказа как алфавите автомата существуют (память 2026-07), но реализация не следует модели инвариантов (RunChainAsync рано перебрасывает на 4xx, не тестирует TTFT). **Нужно уточнить состояния и переходы FSM.**
- **Q2 (codex):** Codex hard-coded список моделей при спавне. Обещание кита `dynamic_model_at_spawn` опровергнуто живым кодом (observations, recurrence 2). OpenCode/Qwen/Droid имеют modelGrades, но это не транспарт разных провайдеров, а белый список id. **Роутер не решает проблему codex — нужна правка в codex или wrapper поверх спавна.**
- **Q3 (free models + fallback):** Реестр показывает exactly это (openrouter :free модели как pri 20/30), но нет механизма динамичного переключения на по-токеновое отсчисление. **Есть цепочка, нет адаптации при исчерпании квоты.**
- **Q4 (конфигурация с ключами):** Текущая конфиг хранится в core.db (или config/{ws}.db для старого), версионируется CAS, но админка пишет в старое хранилище — **системно сломано на L5.**
- **Q5/Q6 (gateway в PetBox / локальный dotnet tool):** Home endpoint работает (llama-server за cert-pinning), но без явного шлюза-слоя перед ним — это просто маршрут 1 в цепочке. **Текущая архитектура не отделяет шлюз от роутера.**
- **Q7 (переписывание заголовков):** openrouter-маршруты добавляют `embedSpaceId` (память), но нет декларативной перезаписи заголовков на уровне шлюза (это уровень HTTP-proxy, выше текущей абстракции). **Требует нового слоя.**
