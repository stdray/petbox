# Инвентарь когорт: доска intake
Дата: 2026-08-27. Агент: Claude Sonnet 5 (petbox-worker). Узлов размечено: 59 из 59.

## Сводка
| класс | шт |
|---|---|
| наблюдение | 57 |
| обязательство | 0 |
| смешанное | 2 |

## Когорты
| когорта | шт | класс (преобладающий) | одно решение, которое закрывает всю когорту |
|---|---|---|---|
| A. onboarding-headless (droid/CC headless, WSL, friend-onboarding, pet-scaffold) | 5 | наблюдение | владелец решает: инвестировать в headless-онбординг сейчас или принять как известный долг — закрывает все 5 разом |
| B. agent-wiring: budget & hook reliability (стартовый вброс, SessionStart-хук) | 3 | наблюдение (1 ждёт владельца) | владелец называет целевой бюджет вброса + чинит process.exit-краш — один заход по startup-пути |
| C. memory store: courание / хвосты батча | 2 | смешанное | подтвердить текущую позицию (флаг отложен, tails-коллектор продолжает жить) или закрыть накопитель явно |
| D. tasks/methodology engine: нарушенные инварианты (atomic, precondition, FSM-переходы, UI-подсказки) | 9 | наблюдение | один инженерный проход по движку эффектов/CAS-записи закрывает весь класс — все 9 суть варианты «engine не держит свой контракт» |
| E. methodology/process debt (regression-pack, идеи-FSM, authz-инвентаризация, process cost) | 4 | наблюдение | владелец решает приоритет process-инвестиций одним пакетом |
| F. docs drift (методология/llmrouter доки разошлись с кодом) | 3 | наблюдение | один проход правки трёх доков закрывает когорту |
| G. deploy/git process incidents (постмортемы: гонки тегов, общий worktree, CI-джобы глотают код) | 9 | наблюдение | владелец решает, брать ли git/deploy-гигиену в отдельный спринт — общий класс «параллельная работа топчет process-инварианты» |
| H. auth/security (регистрация хендлера, scope-семантика, утечка ключа в архив, ротация) | 4 | наблюдение | один auth-аудит закрывает все 4 — общая тема «ключи и авторизация недоделаны» |
| I. llm/config сиротские ссылки (registry, workspace, SystemShared) | 4 | наблюдение | один проход по реестрам LLM/workspace-конфига устраняет весь класс |
| J. infra reliability (WAL, econnreset, health-джобы, флейки тестов, KQL/OTLP корректность, gc) | 9 | наблюдение | владелец решает объём тех-долга инфры одним пакетом, поштучная адъюдикация не нужна |
| K. UI polish (permalink-и, markdown-рендер, updated-at, сырой stacktrace) | 5 | наблюдение | один UX-проход мелких доработок закрывает когорту |
| L. singleton exploration (Онто-находки, ranking-eval-инфра) | 2 | смешанное/наблюдение | НЕ когорта по сути — два независимых крупных вопроса, каждый решается отдельно |

## Разметка
| key | класс | когорта | пометки | одной строкой: почему такой класс |
|---|---|---|---|---|
| board-fields-unselectable-properties | наблюдение | D | | UI-несоответствие, воспроизводимо повторным взглядом |
| board-read-loads-all-bodies | наблюдение | D | | перф-находка в коде, решения владельца в теле нет |
| canon-trim-budget-decision | наблюдение | B | ЖДЁТ ВЛАДЕЛЬЦА | тело = измерение + явный список «Вопросы к владельцу», решения ещё нет |
| comment-action-buttons-hover-authz | наблюдение | D | | UX-находка с предложением фикса в заголовке, но без зафиксированного решения |
| droid-exec-no-hooks | наблюдение | A | | находка про отсутствие хуков в non-interactive режиме |
| intake-confirmed-has-no-cancel-path | наблюдение | D | | находка о дыре в FSM интейка |
| issue-tasks-upsert-tag-validation-after-persist | наблюдение | D | | баг воспроизводится повторным вызовом tasks_upsert |
| markdown-render-memory-sessions | наблюдение | K | | фиче-гэп, решения владельца нет |
| mcp-arg-error-raw-stacktrace | наблюдение | K | repro-комментарий с прогона 0714 | воспроизводимая находка, комментарий это подтверждает |
| memory-entry-permalink | наблюдение | K | | фиче-гэп |
| methodology-def-doc-drift | наблюдение | F | | дока разошлась с кодом — проверяемо по файлу |
| methodology-doc-conflates-intake-and-client-issues | наблюдение | F | | дока/поведение mismatch, воспроизводим |
| methodology-fsm-pending-to-done-steps | наблюдение | D | | наблюдение об эргономике перехода, не решение |
| onboard-0725-headless-claude-mcp-pending-approval | наблюдение | A | сессия onboarding-run-0725 | находка живого прогона онбординга |
| onboard-0725-wsl-login-shell-env-node | наблюдение | A | сессия onboarding-run-0725 | находка живого прогона онбординга |
| onto-nahodki-k-triazhu | наблюдение | L | | список внешних находок, ждущих триажа, решения нет |
| orphan-projects-reference-missing-workspace-rows | наблюдение | I | | data-integrity находка |
| session-permalink | наблюдение | K | | фиче-гэп |
| spec-startup-budget-write-surface | наблюдение | B | | «Находка» прямо в теле, спека не решение владельца |
| store-curation-flag-explicit | смешанное | C | требует расщепления | тело содержит и наблюдение (курируемость выводима), и зафиксированный отказ владельца от флага — два разных объекта в одной карточке |
| tails-memory-batch-2026-08-27 | наблюдение | C | накопитель (acc-паттерн) | карточка-коллектор трёх находок батча, сама не решение |
| remote-auth-handler-unregistered | наблюдение | H | status=confirmed | «confirmed» на интейке = верифицированная находка, не решение владельца |
| search-ranking-eval-infra | смешанное | L | требует расщепления | тело: наблюдение (n=9 мало) + явный блок «Решение владельца» — decision уже прилип |
| inspect-gate-intermittent-66-findings | наблюдение | J | | измерение (дважды воспроизведено), решения нет |
| audit-worktree-branch-cleanup | наблюдение | G | | аудит-находка (накопленный долг) |
| ideas-fsm-no-split-terminal | наблюдение | E | comment: clarification | наблюдение о дыре в FSM идей |
| ui-show-updated-at | наблюдение | K | | фиче-запрос, не решение |
| background-jobs-leave-no-trace | наблюдение | J | | наблюдение об отсутствии observability |
| llm-systemshared-off-orphans-system-projects | наблюдение | I | | воспроизводимый баг конфигурации |
| agent-key-cross-project-smoke | наблюдение | G | | находка про недоступность smoke-теста |
| bootstrap-concurrency-test-flake | наблюдение | J | | флейк воспроизводим под нагрузкой |
| effects-bypass-precondition-artifact | наблюдение | D | | баг движка, воспроизводим |
| llm-openai-rest-embeddings-streaming | наблюдение | I | comment: gap | явно названный residual-гэп поверхности |
| npm-targets-swallow-exit-code | наблюдение | G | | CI-баг, воспроизводим |
| otlp-logs-not-idempotent-on-retry | наблюдение | J | | воспроизводимое поведение при retry |
| quota-lowering-semantics | наблюдение | J | | найденная неопределённая семантика, решения нет |
| search-econnreset-under-concurrency | наблюдение | J | | явное измерение (7 из 16), тело говорит «НЕ гонка, НЕ поиск» — уже локализовано, но решения нет |
| config-and-log-dbs-have-no-wal | наблюдение | J | | проверяемый факт конфигурации |
| friend-onboarding-e2e-agent | наблюдение | A | | предложение теста, не решение владельца |
| pet-scaffold-skill-not-mcp | наблюдение | A | | архитектурное наблюдение |
| tasks-surface-mcp-regression-pack | наблюдение | E | | предложение инфры, не решение |
| methodology-presets-process-cost | наблюдение | E | comment (grok-build, пусто) | сборник наблюдений о process cost |
| cc-headless-commit-gap | наблюдение | G | comment: analysis:stop-hook-fix | харнесс-находка |
| deploy-ci-image-bump-rest | наблюдение | G | | предложение фичи деплоя |
| deploy-config-db-shared-datadir | наблюдение | G | | находка про process-global файлы |
| old-registry-never-imported-outside-system | наблюдение | I | пометка «ПРОВЕРИТЬ» в заголовке — это задача агенту, не вопрос владельцу | воспроизводимая находка о неимпортированных реестрах |
| agents-read-scope-redundant-and-fails-silently | наблюдение | H | | баг молчаливого отказа, воспроизводим |
| kql-unknown-column-returns-null | наблюдение | J | | воспроизводимое поведение KQL |
| orchestrator-integration-shared-primary-worktree | наблюдение | G | | постмортем инцидента, воспроизводимая причина |
| stale-llmrouter-rest-docs-memory | наблюдение | F | | устаревшая дока/память, проверяемо |
| upsert-writes-node-despite-error-envelope | наблюдение | D | status=confirmed | верифицированный баг атомарности |
| finding-authz-inventory-rots-under-parallel-leaves | наблюдение | E | заголовок явно «НАХОДКА О ПРОЦЕССЕ» | процессное наблюдение о протухании инвентаризации |
| finding-terminal-nodes-accept-gate-artifacts | наблюдение | D | заголовок явно «НАХОДКА О ПРОЦЕССЕ» | наблюдение о дыре в терминальности узлов |
| node-connect-timeout-to-prod-20pct | наблюдение | G | status=confirmed, но тело говорит «гипотеза НЕ подтверждена» — расхождение статуса и текста | недоверифицированная гипотеза, не решение |
| apikey-mint-only-path-leaks-raw-key-into-session-archive | наблюдение | H | | security-находка, воспроизводима |
| deploy-tag-race-stale-push | наблюдение | G | | постмортем гонки, воспроизводимая причина |
| master-key-rotation | наблюдение | H | ПРОТУХЛО? — источник: закрытый легаси-борд roadmap/polish 2026-06-04, не проверял глубоко | старый roadmap-пункт, не находка агента и не свежее решение — статус неясен |
| hook-hard-exit-crash-and-stdout-truncation-race | наблюдение | B | status=confirmed | верифицированный краш-баг |
| card-commits-can-rot-when-git-gc-prunes | наблюдение | J | | воспроизводимый факт про git gc |

## Ждёт владельца
| key | вопрос, как он сформулирован в карточке |
|---|---|
| canon-trim-budget-decision | «1. Какой целевой бюджет стартового вброса (protocol + оба canon-тела)? 2. Как enforce'ить (reject canon-write сверх цели...)?» |

## Что мне мешало / где формат не подошёл
- Две карточки (`store-curation-flag-explicit`, `search-ranking-eval-infra`) оказались СМЕШАННЫМИ не в смысле «наблюдение + решение рядом», а в смысле «наблюдение и решение — разные слои одной темы» (общий гэп + владелец уже решил часть вопроса). Формат просит расщепления — не делал его руками (доска read-only), только пометил.
- `node-connect-timeout-to-prod-20pct`: статус `confirmed`, но собственный текст заголовка говорит «гипотеза НЕ подтверждена» — противоречие статуса и содержимого, само по себе достойно отдельной пометки (не «наблюдение о системе», а «наблюдение о рассинхроне статуса и текста карточки»); формат явно такой класс не предусматривает.
- `master-key-rotation` пометил `ПРОТУХЛО?` по признаку «источник — закрытый легаси-борд», но не проверял код на предмет уже сделанной ротации — по правилу формата «не уверен → `?`, не тратить время».
- Комментарии читал только `comments_search` с `bodyLen:0` (только теги/авторы, без текста) — этого хватило распознать типовые артефакты (`artifact:verdict`, `repro`, `gap`, `clarification`) без затрат на полные тела; ни один явно не добавил новый вопрос владельцу сверх того, что уже в теле карточки.
- Часть «фиче-гэпов» (K, часть A) formально не «находки о состоянии системы» в узком смысле («нашёл бы снова, посмотрев туда же»), а предложения. Отнёс их к наблюдениям, т.к. в теле нет зафиксированного решения владельца — но легенда прямо про такой промежуточный случай («предложение без решения») не говорит; решил по правилу теста «молча закрыть → найдётся снова» — да, гэп в UI/фиче так и останется гэпом.
