# Инвентарь когорт: доска work (InProgress/Review/Blocked)
Дата: 2026-08-27. Агент: Claude Sonnet 5. Узлов размечено: 22 из 22 (Blocked: 0 — статус пуст на срезе).

## Сводка
| класс | шт |
|---|---|
| наблюдение | 5 |
| обязательство | 17 |
| смешанное | 0 |

## Когорты
| когорта | шт | класс (преобладающий) | одно решение, которое закрывает всю когорту |
|---|---|---|---|
| resharper-clt зонтик (in-pipeline + line-width + step5-dead-public-code) | 3 | обязательство | владелец смотрит: step5 функционально готов (9/9 детей Done, сам узел завис), line-width ждёт решения по двум путям (editorconfig vs raw-string-literal вынос описаний MCP-тулов), Qodana-вопрос зонтика не решён |
| roslyn-code-graph-research (умбрелла + step0) | 2 | обязательство | владелец читает 50-verdict.md: строить своё дальше (step1/2) или встать на roslyn-codelens-mcp — step0 закрыл предпосылку для решения |
| wiring-one-command бэки (wire-apply-root, wire-leaves-mcp) | 2 | обязательство | оба фикса закоммичены и не имеют открытых вопросов — просто Review→Done |
| newcomer-equivalent-experience умбрелла + 2 wiki-карточки | 3 | обязательство | все баг-дети умбреллы уже Done — осталось дописать 2 вики-статьи и закрыть зонтик |
| memory-телеметрия 2026-08-27 (autocapture-dedup-blind-to-canon, vectorization-jobs-flood-selflog, memory-telemetry-blind-paths) | 3 | обязательство | все три из одной сессии-разбора того же дня; vectorization уже смержена в main (commit 5bc9e797, см. Merge ae9d3172) — по сути готова к Done, два соседа ждут той же приёмки |
| canon banner budget (canon-split-into-skills, canon-write-gate-banner-budget) | 2 | смешанное по факту (1 набл. + 1 обяз.) | владелец отвечает на открытый вопрос «где живёт гейт» — это разблокирует обе карточки |
| kind-worlds-model-audit | 1 | наблюдение | владелец читает аудит-комментарий и решает, упрощать ли модель миров kind'а |
| node-get-relations-panel-drops-neutral-kinds | 1 | наблюдение | решение уже спроектировано в теле, нужен только go — код не начат |
| agent-plugins-spec-research | 1 | наблюдение | владелец принимает вывод «не брать» или оспаривает — код не пишется по DoD карточки |
| data-observability-optional-field-loss | 1 | наблюдение | владелец выбирает один из 4 вариантов (рекомендован №2) |
| ui-back-nav-no-bfcache | 1 | обязательство | владелец сам задал рамку фикса (2026-08-02) — фикс не начат, 25 дней без движения |
| comments-must-not-promise | 1 | обязательство | владелец решает, где фиксировать правило (AGENTS.md/doc), затем 9 оставшихся мест дочищаются |
| chore-home-endpoint-domain-netcraze | 1 | обязательство | владелец пушит уже смерженный локально коммит (не деплой — просто push) |

## Разметка
| key | класс | когорта | пометки | одной строкой: почему такой класс |
|---|---|---|---|---|
| chore-home-endpoint-domain-netcraze | обязательство | standalone infra | ЖДЁТ ВЛАДЕЛЬЦА | коммит смержен, но не запушен — сказано прямо в теле |
| codegraph-step0-evaluate-codelens-mcp | обязательство | roslyn-code-graph-research | — | Review + непустые commits |
| kind-worlds-model-audit | наблюдение | standalone | ЖДЁТ ВЛАДЕЛЬЦА | аудит-находка, решения владельца в теле нет, гейт Review→Done explicit owner-only |
| node-get-relations-panel-drops-neutral-kinds | наблюдение | standalone | не ПРОТУХЛО (проверено origin/main: `RelationPanelSpecs` в TasksService.cs всё ещё не включает NeutralRelationKinds) | полная диагностика+план фикса, но 0 commits — работа не начата |
| roslyn-code-graph-research | обязательство | roslyn-code-graph-research | ЖДЁТ ВЛАДЕЛЬЦА | Review + commits; умбрелла, решение продолжать/стоп созрело после step0 |
| wire-apply-root-from-cwd-not-dir | обязательство | wiring-one-command | готово к приёмке | fix закоммичен, открытых вопросов в теле нет |
| wire-leaves-mcp-server-unapproved | обязательство | wiring-one-command | готово к приёмке | fix закоммичен, открытых вопросов в теле нет |
| comments-must-not-promise | обязательство | standalone | ЖДЁТ ВЛАДЕЛЬЦА; не двигалась с 2026-08-02 (25 дн.) | commits есть, но явно «Осталось» 9 мест + вопрос владельцу о месте фиксации правила |
| resharper-clt-in-pipeline | обязательство | resharper-clt зонтик | ЖДЁТ ВЛАДЕЛЬЦА (Qodana vs jb) | умбрелла жива только через 2 детей InProgress, сама не двигалась с 2026-07-29 |
| ui-back-nav-no-bfcache | обязательство | standalone | не двигалась с 2026-08-02 (25 дн.), похоже на брошенную — диагноз и план готовы, 0 commits | владелец сам задал рамку фикса словами «чиним ровно одно» — прямая договорённость, commits пусты |
| agent-plugins-spec-research | наблюдение | standalone | ЖДЁТ ВЛАДЕЛЬЦА | research-only chore по DoD, вывод «не брать» — рекомендация агента, не решение владельца |
| resharper-clt-line-width | обязательство | resharper-clt зонтик | не двигалась с 2026-07-29 (29 дн.) | Review-уровня разбор завершён, предложено расщепление на карточку, решение не принято |
| resharper-clt-step5-dead-public-code | обязательство | resharper-clt зонтик | ВСЕ 9 детей Done — карточка функционально готова, административно зависла в InProgress с 2026-07-29 | commits есть, InProgress |
| data-observability-optional-field-loss | наблюдение | standalone | ЖДЁТ ВЛАДЕЛЬЦА; не двигалась с 2026-07-17 (41 день, самая старая в срезе) | явно «Агентский потолок — Review, Done ставит владелец», 0 commits |
| canon-split-into-skills | наблюдение | canon banner budget | не двигалась с 2026-07-26 (32 дн.) | план без commits, без зафиксированного решения владельца — «может потребоваться промоушен... решит владелец» |
| canon-write-gate-banner-budget | обязательство | canon banner budget | ЖДЁТ ВЛАДЕЛЬЦА (открытый вопрос «где живёт гейт»); не двигалась с 2026-07-26 | commits есть, но открытый вопрос в теле нерешён |
| autocapture-dedup-blind-to-canon | обязательство | memory-телеметрия 2026-08-27 | готово к приёмке | fresh (2026-08-27), commits, чёткая «Приёмка» без открытых вопросов |
| vectorization-jobs-flood-selflog | обязательство | memory-телеметрия 2026-08-27 | ЖДЁТ ВЛАДЕЛЬЦА (выбор пути в теле) НО фикс уже в main — см. Merge ae9d3172/commit 5bc9e797 в git log | commits, fresh, по сути готова к Done |
| memory-telemetry-blind-paths | обязательство | memory-телеметрия 2026-08-27 | АНОМАЛИЯ: тело утверждает `blockedBy` на usage-delivery-mixes-machine-traffic, но relations-панель узла его не показывает (см. «Что мешало») | commits, fresh, явный порядок исполнения записан в теле |
| newcomer-setup-and-cycle-wiki | обязательство | newcomer-equivalent-experience | АНОМАЛИЯ: Review при 0 commits и теле в будущем времени «когда писать» — похоже на несостоявшийся переход статуса, а не на готовность; условие «после багов зонтика» теперь выполнено (все баг-дети Done) | «Заказ владельца» дословно в теле — прямая договорённость |
| wiki-three-layers-after-v10 | обязательство | newcomer-equivalent-experience | решение владельца уже записано в теле (2026-07-26); проверить фактическое исполнение на доске wiki — вне среза | «Решение владельца (2026-07-26)» дословно в теле |
| newcomer-equivalent-experience | обязательство | newcomer-equivalent-experience | жива: 8/13 детей Done, 3 сознательно ОТЛОЖЕНО владельцем, 2 в Review (см. выше) — не брошена | умбрелла с явным мотивом владельца в теле |

## Ждёт владельца
| key | вопрос, как он сформулирован в карточке |
|---|---|
| chore-home-endpoint-domain-netcraze | «мерж `85e39083` в main (НЕ запушено — ждёт владельца...)» |
| kind-worlds-model-audit | «Гейт: Review → Done — owner-only, карточка остановлена в Review» |
| data-observability-optional-field-loss | «Агентский потолок — Review, Done ставит владелец» + 4 варианта, рекомендован (2) |
| canon-write-gate-banner-budget | «## Открытый вопрос (где живёт гейт)» — два варианта (a)/(b) |
| vectorization-jobs-flood-selflog | «Выбор между двумя путями — за владельцем» + «после правки — решение владельца по ретенции» |
| comments-must-not-promise | «место — на решение владельца: AGENTS.md либо doc/» |
| resharper-clt-in-pipeline | «Стоит ли CI-обвязку строить на Qodana вместо голого `jb` — не решено» |
| canon-split-into-skills | «может потребоваться промоушен в idea/spec... решит владелец» |
| agent-plugins-spec-research | вывод «Не брать» с явными триггерами пересмотра — ждёт согласия/несогласия владельца |
| roslyn-code-graph-research | «Полная стройка... оправдана, только если шаг 0 покажет...» — шаг 0 закрыт, решение продолжать/остановиться созрело, но не принято |

## Что мне мешало / где формат не подошёл
- Ни одного узла в статусе `Blocked` на срезе `work` — секция для него в разметке пуста не потому, что я её пропустил, а потому что таких узлов нет (проверено `tasks_search` с `status:["InProgress","Review","Blocked"]`, ответ без `nextCursor`, т.е. полный).
- `memory-telemetry-blind-paths`: тело прямо утверждает связь `blockedBy` на `usage-delivery-mixes-machine-traffic` («отсюда blockedBy»), но панель `relations` узла её не показывает вовсе (нет группы blocks/blocked-by в ответе `tasks_node_get`). Это либо тот же класс дефекта, что чинит `node-get-relations-panel-drops-neutral-kinds` (только для `blocks`, не для нейтральных видов — проверять отдельно, я не проверял), либо связь в теле описана прозой, но никогда не создавалась через `links.blockedBy`/`relations_create`. Не проверял глубже — не мой слепок.
- Пограничный случай классификации: узлы-умбреллы (`resharper-clt-in-pipeline`, `newcomer-equivalent-experience`, `roslyn-code-graph-research`) сами по себе не несут commits — обязательство им присвоено по факту закоммиченной работы в детях, а не по букве правила «commits узла непустой». Формат явно про такой случай не говорит; я разрешил его в пользу обязательства, раз работа реально начата (в детях).
- `wiki-three-layers-after-v10` и `newcomer-setup-and-cycle-wiki` — обе про правки вики-страниц (доска `wiki`, не `work`), поэтому `commits` пуст по конструкции: движок коммитов не видит правок контента. Не проверял доску `wiki` — вне заданного среза.
