# D2 Идеи как свалка — инвентарь

Дата 2026-09-25, автор: Claude Sonnet 5 (claude-sonnet-5). Источники: доска `ideas`
(228 узлов, все статусы, полный скан non-accepted + выборка 38/143 accepted),
`tasks_methodology_guide`, `doc/methodology.md`@origin/main, `research/spec-plan-convention.md`,
6 сессий (session_search), permalink-паттерн `https://petbox.3po.su/ui/$system/$system/tasks/ideas/<key>`.

## 1. Суть проблемы

Доска `ideas` официально — P1 (эволюция спеки): `raw идея → refined → spec_plan → accepted → spec`.
Фактически на неё пишут четыре разных рода материала: настоящие изменения спеки, research-вопросы
без ответа, product-хотелки не крупнее одного work-тикета, и голые стрёмные наблюдения замеченные
по ходу дела. Причина — не только название: канон сам предписывает это в правиле интейка
("wish/mысль без отражения в спеке → идея"), а отдельного типа под research/spike в методологии
нет вообще — исследованию буквально некуда больше пойти.

## 2. Доказательства

### 2a. Карточки — полный скан по статусам (не выборка: raw/exploring/review/deferred/rejected/shipped
прочитаны ПОЛНОСТЬЮ, 84 узла; accepted — 38 из 143 прочитаны напрямую)

Счётчики по статусам (228 узлов всего): raw 37, exploring 9, review 7, deferred 2,
**accepted 143 (63%)**, rejected 27, shipped 3. Гейт почти не фильтрует: почти 2/3 всего,
что когда-либо попало на доску, дошло до `accepted`.

Классификация 84 non-accepted узлов (полный проход) + 38 просмотренных accepted, всего 122:

| класс | ~кол-во | примеры (permalink = `/ideas/<key>`) |
|---|---|---|
| (a) genuine spec change | ~45 | `kind-worlds-decision-paths` (accepted, 11 spec-листьев), `async-vectorization` (accepted, 8 листьев), `delivery-pre-spec-primitive` (raw, спек-механика delivery), `settings-scope-effective-constraints` (exploring) |
| (b) research/investigation | ~5 | `agent-role-file-format-conditional-inserts` — заголовок буквально "(research)"; `llm-router-research-vs-litellm` — "разобрать litellm... признать велосипед"; `session-usage-tool-language-and-packaging` — "still open"; `subagent-session-graph` — "ценность под вопросом" |
| (c) wish/feature → should be work | ~50 | `board-saved-queries`, `recurring-run-scheduler`, `ui-page-metrics`, `ops-external-alerting`, `deploy-release-management` — все одноабзацные хотелки без исследовательской составляющей |
| (d) stray thought/observation | ~15 | `memory-body-not-rendered-and-link-seam` ("ссылки... не работают на практике" — дефект, не идея), `generic-mechanism-preset-drift-vs-materialized-instances` ("третий случай за один день"), `board-kind-irreversible-on-cold-create` ("ловушка с необратимым исходом"), `qwen-isolation-worktree-location-hardcoded`, `transcript-push-firing-semantics-durability-gap` (review) |
| (e) other/мета-процесс | ~7 | `consolidate-agent-prose-single-source`, `methodology-lexicon` (rejected), `docs-practice` |

(d) — ровно наблюдения-дефекты по определению `doc/methodology.md`/легенды, но написаны как idea
ДО того, как в проекте появилась доска `observations` (или агент о ней не вспомнил — пересекается с D1).

### Кросс-чек: accepted → реально ли есть spec?

Из 38 просмотренных `accepted`-идей **35/38 (92%) несут непустой `spec[]`** (реальные листья спеки).
Без единого spec-листа: `config-runner`, `fast-deploy-ci`, `free-board-unified-statuses` — идея принята
владельцем, но так и не привела к правке дерева спеки (зависла между accepted и spec — либо ещё не
сделано, либо спека появилась под другим именем и линк потерян). Экстраполяция на все 143: очень
грубо ≈13 из 143 accepted без спеки, если пропорция держится — не проверено на полном множестве.
Обратная сторона (accepted → spec) подтверждена системным инвариантом: `spec`-узел НЕ МОЖЕТ
существовать без `links.idea_spec` на accepted-идею (`tasks_methodology_guide`, link_constraint) —
так что "spec без идеи" структурно невозможно, а "идея без spec" — возможно и наблюдается.

### 2c. Сессии

- `4499bc9b-9267-4544-975c-20cb26333688`: владелец прямым текстом — «оформляй идею, пиши spec_plan,
  гони до премки» — по задаче "перенести backup-harness research в repo, закоммитить, закрыть chore" —
  т.е. САМ владелец направляет инфраструктурно-организационную работу (перенос файлов + коммит) в
  idea-конвейер, не потому что это спека, а потому что это единственный видимый способ провести
  что-то формально.
- `54bb57df-64c1-47e0-9d99-4cf8cd5745fe`: агент себе news — «Идею не отклоняли. `board-saved-queries`
  (ideas, «Доски: сохранённые запросы...»)» — это чистая UI-фича уровня одного work-тикета, лежит
  в ideas с версии 327 без единого перехода в exploring.

### 2d. Канон

`doc/methodology.md`@origin/main, раздел Entities: **`2. Idea/Deliberation — a thread of thinking...
A topic → 0..N tasks. Separate from the plan.`** — формулировка "topic → 0..N tasks" в описании самой
сущности Idea противоречит остальному документу (Idea официально ведёт только к spec, не к tasks
напрямую) и, скорее всего, устарела с P1/P2-разделения; она буквально приглашает читать idea как
"тема, порождающая задачи", а не "issue против спеки".
Раздел **Intake — routing**: *"a product thought with no spec reflection → an idea"* — это ПРЯМОЕ
предписание методологии: любая хотелка без отражения в спеке идёт в ideas, независимо от того,
готова ли она стать нормативным требованием. Ничего не отличает "готовое требование" от "смутная
мысль" на входе.
Нигде в канон/skill/guide нет типа/дорожки под research-spike — `work.chore` явно "engineering
hygiene", не research; `intake` — только triage-инбокс, не место для содержательного исследования.

## 3. Что уже решено раньше

`research/spec-plan-convention.md` (2026-09-08) независимо подтвердил определение idea/spec_plan как
"issue против спеки" — там же зафиксирован разрыв: `tasks_methodology_guide` не транслирует СОДЕРЖАНИЕ
spec_plan, только факт его наличия. Смежно с D1, не решает D2. `research/flow-control` не разбирал
природу ideas-борда напрямую.

## 4. Механизм (факт)

1. Intake-routing правило канона буквально шлёт любую спек-less мысль в ideas — структурная причина,
   не терминологическая.
2. Нет отдельного kind/типа под research — агент вынужден class="idea", т.к. `work` требует
   `task_spec`→existing spec node, а такого узла для research-вопроса нет.
3. Гейт `exploring → review` требует `spec_plan`, но НЕ требует, чтобы содержимое исходной idea уже
   было спек-формы на входе (`raw`/`exploring` можно оставаться сколь угодно долго без проверки, что
   мысль вообще спек-уровня) — отсюда 37 raw и 9 exploring, часть из которых по сути work-тикеты.
3b. [гипотеза] Слово "idea" по-английски бытовое ("any idea") и не сигнализирует "issue против
   формального дерева требований" так, как сигнализировал бы, например, "spec change" или "RFC".

## 5. Индустрия (≤10 строк)

RFC/ADR — фиксируют ПРИНЯТОЕ архитектурное решение, не свалку; отдельно заводятся ДО решения.
GitHub/Jira "Discussion"/"Proposal" — ближе к текущим ideas, но обычно рядом есть отдельный
"Spike"/"Research" issue-type именно чтобы не путать с feature request. Kubernetes KEP — жёсткий
шаблон с обязательными секциями (motivation/goals/non-goals), фактически энфорсит spec_plan-подобную
форму на входе, а не только на выходе в review. Спайки повсеместно — отдельный тип с time-box и
"выход = знание, не код". Wishlist/feature request обычно отдельная лёгкая очередь (Jira "Idea"
portal, GitHub Discussions "Ideas" категория) — и это как раз тот самый термин "idea" в индустрии
ЧАЩЕ означает именно wishlist-хотелку, а не spec-issue — подтверждает подозрение владельца о слове.

## 6. Варианты решения

- **Переименовать `ideas` → `spec-change`/`proposals`.** Цена: низкая (label + текст в guide/SKILL/
  доке), FSM/статусы/линки не трогаются — `key` узлов и относящиеся к ним ссылки (`idea_spec`,
  permalink-паттерн `/tasks/ideas/<key>`) НЕ переименовываются автоматически, т.к. board `key`,
  вероятно, часть URL/wire-контракта — переименование board-key ломает существующие ссылки/скиллы/
  kit-текст, если оно меняет routing-key, а не только заголовок. Не чинит intake-routing правило и
  не даёт дом research/wish.
- **Завести отдельный `research`/`spike`-тип (или lightweight board) с time-box и выходом = знание,
  не спека.** Цена: средняя — новый kind в методологии (`tasks_methodology_rules_upsert`), доки, скилл.
  Чинит корень: ~5-10 из raw/exploring реально research, не спека.
- **Завести `wishlist`/`backlog`-вход для (c)-класса** — пересекается с D3 (intake↔work): большинство
  (c) — это просто work-тикеты без спеки; можно расширить `work.chore` под "product wish", либо
  разрешить work-фиче ссылаться на ещё-не-существующий spec placeholder. Цена: средняя, требует
  пересмотра link_constraint `task_spec`.
- **Переписать intake-routing-правило канона**: не "spec-less → idea" безусловно, а спросить агента
  различить (research / wish-that-fits-in-one-task / genuine spec change) и направить в 3 разных
  места. Цена: низкая (доки+skill), но требует создания destinations из варианта выше — иначе
  переписывание правила без новых досок ничего не меняет.
- **Entry gate на `raw`**: чеклист "это про дерево спеки, а не про реализацию/исследование/хотелку"
  перед принятием в `raw`. Цена: низкая (checklist knob уже есть в методологии), но конвенция, не
  энфорсится сервером — не остановит агента, который torn between "куда это положить".

## 7. Несогласия с постановкой владельца

Название **не главная причина**: канон САМ предписывает спек-less material → ideas (intake routing),
и research буквально некуда больше пойти (chore = hygiene, не research). Переименование доски без
создания destinations для (b)/(c) не уменьшит дамп — просто дамп будет называться иначе. Наблюдение
(d)-класса — отдельный узкий случай: часть из них написана ДО появления `observations`-доски или
агент не вспомнил о ней (пересекается с D1 «амнезия методологии»), не о природе ideas как таковой.

## 8. Связи и попутные дефекты (не заведены)

- Пересекается с **D3** (intake↔work): класс (c) "wish that should be work" — тот же паттерн
  дублирования, что и intake→work copy.
- Пересекается с **D1**: (d)-класс narrated as idea вместо наблюдения = забытый механизм observations.
- Попутный дефект [не заведён]: `doc/methodology.md` Entities/Idea — фраза "A topic → 0..N tasks"
  противоречит остальному документу (P1/P2 separation) — стоит поправить в том же файле.
- Попутный дефект [не заведён]: 3 accepted-идеи без единого spec-листа (`config-runner`,
  `fast-deploy-ci`, `free-board-unified-statuses`) — не проверено, зависли или spec есть под другим
  provenance.

## 9. Замечания к формату

Нет.
