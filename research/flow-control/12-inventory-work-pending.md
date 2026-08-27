# Инвентарь когорт: доска work (срез Pending)
Дата: 2026-08-27. Агент: Claude Sonnet 5 (worker). Узлов размечено: 107 из 107
(две страницы `tasks_search` с `bodyLen:0`, доведено через `nextCursor`; счётчики
проверены скриптом на дублей нет — 65+42=107, совпадает с `omitted:42` первой страницы).

## Сводка
| класс | шт |
|---|---|
| наблюдение | 59 |
| обязательство | 44 |
| смешанное (вкл. 2 накопителя) | 4 |

## Накопители (отдельное требование задания)
- **acc-memory-secrets** — заведён ПО ПРЯМОЙ ЦИТАТЕ владельца (2026-08-27, комментарий к
  `fact-usage-ledger`), с решённым порядком («сначала память, потом секреты»). Сам факт
  заведения и порядок — обязательство. Содержимое — 4 пункта, все наблюдения: (1) утечка
  ключей в память (owner-формулировка, детали не решены), (2) защита `ops` от автосвипа
  держится на списке имён, не механизме, (3) текст запроса в usage-ledger — приватностный
  вопрос без решения, (4) автозахват тянет тела записей во внешний LLM. Ничего не начинать
  без слова владельца — статус картотеки, не поручение.
- **acc-memory-analysis-tails-0827** — заведён агентом «чтобы не потерялось при смене
  сессии», owner-цитаты о создании нет (в отличие от acc-memory-secrets). Целиком
  контейнер наблюдений: (1) предложение TTL-метки корроборации (кандидат в идею, явно НЕ
  гейт), (2) два хвоста клиентского репорта `kek-devices` (статусы на доске `classic` не
  двигаются; canon хранит записи-статьи без лимита размера, режется до 400 симв. в дедупе),
  (3) находка ревизии — протухание памяти как класс дефекта, чинили точечно 8 раз, механизма
  нет, (4) указатель на черновики в scratchpad (не источник истины). Класс: наблюдение,
  ждёт слова владельца для старта по любому пункту.

## Когорты
| когорта | шт | класс (преобладающий) | одно решение, которое закрывает всю когорту |
|---|---|---|---|
| onboarding-run-0826 (methodology-фиксы) | 7 | обязательство (6/1) | владелец принимает пакет фиксов методологии одним актом |
| kind-worlds-c (каталог видов, роллаут) | 5 | обязательство | владелец утверждает дизайн-цепочку kind-catalog целиком |
| roslyn-code-graph-research | 5 | наблюдение | владелец говорит go/no-go по исследованию графа кода |
| mcp-surface-audit-0725 | 3 | наблюдение | закрыть umbrella (все 20 детей терминальны) + решить по 2 находкам |
| llm-router (баги/находки) | 6 | обязательство (4/2) | владелец даёт приоритет спек-фиксам, 2 находки — отдельно |
| newcomer-equivalent-experience (отложенное) | 3 | смешанное | владелец подтверждает список отложенного одним словом |
| testing-hygiene (флаки/слепые тесты) | 7 | наблюдение | владелец решает: разовая уборка тестов или норма процесса |
| methodology-gaps (процессные пробелы) | 4 | наблюдение | владелец решает, что из пробелов становится обязательством |
| agent-wiring-hygiene (мусор/дубли обвязки) | 6 | наблюдение | владелец санкционирует уборку одним пакетом |
| memory-накопители | 2 | смешанное | владелец говорит слово — тогда разбор по пунктам |
| area:access (разное) | 8 | смешанное (4/4) | нет единого — решать по каждому, cohort скорее тематический ярлык |
| прочие одиночные (без выраженной когорты) | 51 | смешанное (~26/25) | нет единого решения — каждая карточка сама по себе |

## Разметка
| key | класс | когорта | пометки | почему такой класс |
|---|---|---|---|---|
| admin-provision-usecase-analysis | обязательство | area:access | — | владелец 2026-08-14 оценил механизм и заказал анализ юзкейсов |
| batch-13-ops-findings-2026-08-20 | наблюдение | run-2026-08-20 | ? | заголовок сам называет находками; свежесть не проверял |
| boundary-gates-namespace-literals | наблюдение | прочие | ЖДЁТ ВЛАДЕЛЬЦА | «есть предложение, нужно решение владельца» |
| client-issues-board-cannot-carry-issue-task | наблюдение | methodology-gaps | — | несостыковка видов досок, решения в теле нет |
| codegraph-step1-fill-empty-cells | наблюдение | codegraph-research | — | инженерный план с оценкой трудозатрат, owner-подписи нет |
| codegraph-step2-query-language | наблюдение | codegraph-research | — | тот же трек, blockedBy step1 |
| codegraph-vector-csharp-ts-edges | наблюдение | codegraph-research | — | «вектор» — гипотеза для будущего решения |
| codegraph-vector-sdk-for-arch-tests | наблюдение | codegraph-research | — | тот же трек |
| codegraph-wiki-fix-graf-protiv-grep | наблюдение | codegraph-research | — | найдена фактическая ошибка в wiki (цифра 85) |
| delete-recreate-test-blind-to-missing-file | наблюдение | testing-hygiene | — | «тест слеп» — эталонная находка |
| done-cards-narrower-than-their-scope | наблюдение | mcp-surface-audit-0725 | — | тело: «Находка», 5 примеров паттерна |
| endpoint-concurrency-limit | обязательство | area:llm | — | 2 links.task_spec на defined-спеки |
| guide-relation-kinds-not-scoped | наблюдение | methodology-gaps | — | «хвост после скоупинга», расхождение доки с кодом |
| kql-pushdown-maximize-research | обязательство | area:core | — | тело цитирует владельца: «текущий дизайн не нравится» + гипотеза |
| llm-breaker-keyed-by-endpoint-name | обязательство | llm-router | — | task_spec-линк |
| llm-cost-per-search-measured | наблюдение | area:llm | — | нужная метрика, owner-заказа на карточке нет |
| llm-embedspace-chain-unvalidated | обязательство | llm-router | — | task_spec-линк |
| llm-rest-finish-reason-hardcoded | обязательство | llm-router | — | task_spec-линк |
| llm-router-config-409-ambiguous-path | обязательство | llm-router | — | task_spec-линк |
| methodology-issue-task-enforce-and-zombie-sweep | наблюдение | methodology-gaps | — | предложение без spec/owner-текста |
| methodology-skill-path-collision | наблюдение | methodology-gaps | — | «борются за один путь» — коллизия |
| node-key-logs-ingest-scope-dead | наблюдение | area:access | — | «скоуп мёртв» — дефект |
| reindex-mechanism-residual-gaps | наблюдение | area:tasks | — | «слайс сдан... остаточные пробелы» |
| rest-mcp-surface-asymmetry-map | наблюдение | mcp-surface-audit-0725 | — | карта расхождений — находка |
| roster-prune-derostered-roles | наблюдение | agent-wiring-hygiene | — | предусловие названо, owner-решения нет |
| umbrella-mcp-surface-consistency | наблюдение | mcp-surface-audit-0725 | ПРОТУХЛО? | relations: 19/20 детей Done, 1 Cancelled, открытых 0 |
| umbrella-methodology-engine | обязательство | umbrella-methodology-engine | — | 3 живых Pending-ребёнка со спеками |
| episodic-global-gate-serializes-all | обязательство | area:search | — | task_spec-линк, приоритет 1 |
| sanitizer-prerelease-and-mxss-test-followup | наблюдение | прочие | — | ждёт релиза библиотеки, не решения владельца |
| no-test-asserts-result-hrefs-on-any-search-screen | наблюдение | testing-hygiene | — | «то же слепое пятно» |
| onboarding-run-after-vocabulary-sweep | наблюдение | прочие | — | напоминание без owner-текста |
| playwright-161-actionability-stall | наблюдение | testing-hygiene | — | версия инструмента ломает E2E |
| profile-hot-paths-on-ultimate-window | наблюдение | прочие | — | оппортунистический chore, лицензионное окно |
| session-search-hydration-waste | наблюдение | area:search | — | измерение (8 из 30) |
| tasks-upsert-nonatomic-on-supersedes-throw | наблюдение | area:tasks | — | найден дефект неатомарности |
| resharper-clt-line-width-mcp-literal-storage | наблюдение | agent-wiring-hygiene | — | найдено ~500 строк, часть 2 темы |
| sessions-default-sort-not-applied | обязательство | area:sessions | — | task_spec-линк |
| src-toplevel-slot-hygiene | обязательство | area:architecture | — | commits непустой — работа начата |
| tasks-methodology-smoke-flake | наблюдение | testing-hygiene | — | флаки без регресса |
| test-assert-single-element-equal-form | обязательство | area:testing | — | заголовок: «решено владельцем» |
| adopt-skips-live-node-validation | обязательство | onboarding-run-0826 | — | task_spec-линк + тег когорты |
| no-crosslang-search-eval-on-bilingual-corpus | наблюдение | area:search | — | пробел измерений |
| orphan-legacy-log-file | наблюдение | area:ops | — | найден осиротевший файл |
| recall-toggles-usage-audit | наблюдение | area:memory | — | запрос на аудит, не решение |
| search-shadow-diff | обязательство | area:search | — | task_spec-линк |
| session-get-window-needs-an-upper-bound | наблюдение | area:mcp | — | найдена невыразимость |
| open-board-in-closed-world-zombie | обязательство | onboarding-run-0826 | — | task_spec-линк + тег |
| active-get-dangling-closed-pointer | обязательство | onboarding-run-0826 | — | task_spec-линк + тег |
| guide-lies-about-foreign-world-board | обязательство | onboarding-run-0826 | — | task_spec-линк + тег |
| e2e-kqlratelimit-test-is-flaky-in-gate | наблюдение | testing-hygiene | — | флаки-находка |
| memory-store-and-project-cleanup | наблюдение | area:memory | — | найдены артефакты для уборки |
| meta-backfill-classifies-with-project-runtime | обязательство | onboarding-run-0826 | — | task_spec-линк + тег |
| read-write-symmetry-guard | обязательство | area:mcp | — | task_spec-линк |
| drop-wiki-kind-duplicate-from-quartet | обязательство | onboarding-run-0826 | — | тег + blockedBy двумя обязательствами роллаута |
| kind-worlds-clean-slate-and-representability | наблюдение | onboarding-run-0826 | — | тело прямо: «пробел учёта, а не работы» |
| acc-memory-analysis-tails-0827 | смешанное | memory-накопители | ЖДЁТ ВЛАДЕЛЬЦА | накопитель наблюдений — см. секцию выше |
| acc-memory-secrets | смешанное | memory-накопители | ЖДЁТ ВЛАДЕЛЬЦА | накопитель; порядок задан, разбор — по слову владельца |
| kind-catalog-project-owns-kinds | обязательство | kind-worlds-c | — | spec + supersedes |
| llm-servedby-predicate-vs-comment | наблюдение | llm-router | — | расхождение предиката и комментария |
| role-worker-critical-deferred | смешанное | newcomer-equivalent-experience | — | отложено на разборе резерва (Fable 5), не прямой owner-текст |
| test-suite-residual-leaks-and-long-pole | наблюдение | testing-hygiene | — | измерения: 191 каталог, тест 70.6s |
| tool-call-fragment-leaked-into-memory-body | наблюдение | area:memory | — | найдена утечка обрывка XML |
| process-stops-owning-rules | обязательство | kind-worlds-c | — | 5 spec-линков + supersedes + blockedBy |
| kind-catalog-migration-rollout | обязательство | kind-worlds-c | — | spec + supersedes + blockedBy |
| empty-required-set-reads-as-default | обязательство | area:tasks | — | 2 spec-линка, измерено 15 из 442 |
| instance-to-process-vocabulary-in-agent-artifacts | обязательство | kind-worlds-c | — | часть роллаута, blockedBy process-stops-owning-rules |
| deploy-agent-service-config-factory-boundary | наблюдение | area:architecture | — | нарушение нового арх-правила |
| droid-opencode-deferred | обязательство | newcomer-equivalent-experience | — | тело: «Отложено владельцем... подтверждено 26.07» |
| strikethrough-test-names-and-anchors | наблюдение | testing-hygiene | — | найдена гигиеническая проблема тестов |
| template-over-project-catalog | обязательство | kind-worlds-c | ЖДЁТ ВЛАДЕЛЬЦА | spec+blockedBy, но заголовок — открытый вопрос владельцу |
| w5-schema-version-surface | обязательство | area:core | — | task_spec-линк |
| comments-upsert-size-guidance | наблюдение | area:mcp | — | пробел в описании тула |
| dedup-neighbor-selection-and-store-flag | обязательство | area:memory | — | task_spec-линк |
| key-attribution | обязательство | area:access | — | task_spec-линк |
| repair-surface-v1 | обязательство | area:access | — | task_spec-линк |
| role-surface-trim | наблюдение | newcomer-equivalent-experience | — | предложение урезать поверхность, owner-текста нет |
| tasks-node-type-reclassify | наблюдение | area:tasks | — | нет механизма реклассификации |
| session-search-cursor-invalidates-immediately | обязательство | area:sessions | — | task_spec-линк |
| admin-force-env-read-not-configurable | наблюдение | area:access | — | ограничение конфигурируемости |
| client-partial-apply-audit | обязательство | area:clients | — | task_spec-линк |
| kql-parity-artifact-audit | наблюдение | area:core | — | запрос на аудит, без spec/owner |
| unify-log-write-seam | наблюдение | area:core | — | архитектурное предложение, без спеки |
| llm-l7-retire-old-registry | наблюдение | area:config | — | предложение снести реестр + тест-страж |
| navigationcontext-http-null-consistency | наблюдение | area:core | — | несогласованность `Http?.`/`Http!.` |
| shutdown-path-untested-undocumented | наблюдение | area:deploy | — | пробел покрытия |
| cake-cannot-start-npm-on-windows | обязательство | area:dx | — | task_spec-линк |
| llm-setasync-cas-free-overwrite | наблюдение | llm-router | — | третья лазейка мимо CAS-защиты |
| search-reindex-memory-container-authz | обязательство | area:access | — | blockedBy на defined-спеку |
| wire-dead-code-and-duplication | наблюдение | agent-wiring-hygiene | — | перечень найденного мусора |
| low-ceremony-presets | обязательство | umbrella-methodology-engine | — | ребёнок активной инициативы |
| worker-dies-promising-a-verification-it-cannot-deliver | наблюдение | прочие | — | найден паттерн отказа воркеров |
| done-transition-verifies-commit-is-merged | наблюдение | прочие | — | предложение без spec/owner |
| kit-toolchain-bun-or-node | наблюдение | agent-wiring-hygiene | — | архитектурное предложение без спеки |
| kql-translation-options-impl | обязательство | area:core | — | 3 spec-линка |
| search-degraded-returns-empty-not-lexical | обязательство | area:search | — | task_spec-линк |
| fabricated-commit-sha-lands-silently | смешанное | umbrella-methodology-engine | — | «ИЗМЕРЕНО» находка внутри активной инициативы |
| deploy-tool-keepass | обязательство | area:deploy | — | task_spec-линк |
| discriminated-union-result-types | наблюдение | area:core | — | дизайн-дефект типа результата |
| fallback-definition-drift-unguarded | наблюдение | agent-wiring-hygiene | — | нет version-штампа защиты |
| wire-unified-artifact-delivery | наблюдение | agent-wiring-hygiene | — | предложение объединения путей |
| workspace-creation-has-no-api | наблюдение | area:access | — | отсутствие API |
| wildcard-admin-key-defaultproject-footgun | наблюдение | area:access | — | security footgun |
| apply-root-and-project-resolution-diverge | обязательство | area:agent-wiring | — | task_spec-линк |
| protocol-block-unowned-budget-hog | обязательство | area:agent-wiring | — | task_spec-линк |
| definition-key-choice-not-honoured | обязательство | area:agent-wiring | — | task_spec-линк |
| droid-dynamic-model-cell-unverified | обязательство | area:agent-wiring | — | task_spec-линк, «проверено живьём» |
| migrate-content-between-instances | обязательство | umbrella-methodology-engine | — | task_spec-линк, ребёнок umbrella |

## Ждёт владельца
| key | вопрос, как он сформулирован в карточке |
|---|---|
| boundary-gates-namespace-literals | «есть готовое предложение, нужно решение владельца» |
| template-over-project-catalog | «снимок ЧЕГО — открытый вопрос к владельцу» |
| acc-memory-secrets | «разбор накопленного — отдельным заходом, когда владелец скажет» |
| acc-memory-analysis-tails-0827 | «ничего из нижеперечисленного не начинать без слова владельца» |

## Что мне мешало / где формат не подошёл
- 107 узлов при 200-строчном лимите не позволяют держать полные когорты — свёл 51 узел
  в «прочие одиночные», у каждого своя строка в разметке, но в таблице когорт это одна
  агрегатная строка без единого решения (честно так и написано).
- Класс «смешанное» пришлось применить и к двум накопителям, хотя формат описывает их
  отдельным термином («контейнер наблюдений в полосе обязательств»), не как третий класс
  из таблицы — решил не заводить четвёртую строку в сводке и не исказить сумму 107.
- ПРОТУХЛО проверил эмпирически только для umbrella-mcp-surface-consistency (через
  relations — 19 Done + 1 Cancelled, 0 открытых детей). Остальные ПРОТУХЛО-кандидаты
  (например batch-13-ops-findings-2026-08-20 — дата совпадает с легендой про протухание)
  не проверял по коду/origin — отмечено «?», времени на глубокую проверку не тратил.
- Класс определял по заголовку/тегам/spec-links почти для всех узлов; тело читал точечно
  для 14 карточек (2 накопителя целиком, остальные — сниппетом 600 симв.), где заголовок
  не давал сигнала. Возможна ошибка на границе для codegraph-* и newcomer-equivalent-
  experience подветок — там нет явного owner-текста в прочитанных фрагментах, классифицировал
  по умолчанию как наблюдение/смешанное, но не читал тела целиком.
- ДУБЛЕЙ явных не заметил при просмотре 107 заголовков; глубокого поиска дублей не делал —
  вне мандата этого прохода (не триаж).
