# 15 — Уже заведённые узлы. Аудит-реестр. 2026-09-10
Автор: petbox-worker · источники: [доска] tasks_node_get/tasks_search, [код] прямое чтение файлов, [владелец]/[координатор] сообщение в ходе задачи

Формат: `key | board | type | status | today/pre-existing | valid? | verdict`.
Read-only. Ни один узел не создан/не изменён/не закрыт/не продвинут.

## Узлы под work/umbrella-agent-behaviour (15, по part_of-рёбрам от узла, не по эвристике)

| key | board | type | status | when | valid | verdict |
|---|---|---|---|---|---|---|
| explore-role-never-instructed-in-orchestrator-prose | work | bug | Review | today 09:50 | yes | STANDS |
| consolidate-agent-prose-single-source | ideas | idea | raw | today 09:50 | yes | STANDS |
| agent-role-file-format-conditional-inserts | ideas | idea | raw | today 12:38 | yes | STANDS |
| session-usage-tool-language-and-packaging | ideas | idea | raw | today 11:03 | yes | STANDS |
| transcript-push-firing-semantics-durability-gap | ideas | idea | review | today 12:35 | yes | STANDS |
| opencode-background-subagent-axis2-finding | intake | issue | reported | today 12:40 | — | **[замер в работе]** |
| session-usage-qwen-leg | work | chore | Review | today 09:50 | yes | STANDS |
| qwen-isolation-worktree-location-hardcoded | ideas | idea | raw | today 12:53 | no | **MOOT** |
| canon-trim-budget-decision | intake | issue | confirmed | pre-existing 07-09 | yes | STANDS |
| spec-startup-budget-write-surface | intake | issue | reported | pre-existing 07-09 | yes | STANDS |
| wiring-startup-budget | spec | spec | defined | pre-existing 07-03 | partial | **NEEDS REWRITE** |
| canon-non-index-keys-no-size-budget | observations | observation | seen | pre-existing 08-28 | no | **NEEDS REWRITE** |
| apply-orphans-artifacts-of-a-deleted-role | observations | observation | seen | pre-existing 08-29 | yes | STANDS |
| new-role-never-gets-local-model-binding-on-existing-installs | observations | observation | seen | pre-existing 08-29 | yes | STANDS |
| highstakes-notes-say-what-not-when | work | chore | Pending | pre-existing 09-06 | no | **NEEDS REWRITE + вероятно мис-филинг, см. ниже** |

### Детали по «известным случаям»

**qwen-isolation-worktree-location-hardcoded — MOOT, подтверждено.** Тело узла целиком стоит на посылке «фон+`.claude/worktrees` даёт только `isolation`, а её путь захардкожен на `.qwen/worktrees`». Тело НЕ упоминает более позднее измерение того же дня: голый спавн + worktree по инструкции — 34 мс, фон, `.claude/worktrees/` сохранён, уборка на агенте [замер 10.09, `02-snapshot.md:14`]. Это быстрее (34 мс vs 510 мс `isolation`) и не имеет проблемы харкода вовсе — `isolation` просто не нужен. Посылка опровергнута. Замечание: сама превосходящая находка пока нигде не заведена карточкой — живёт только в `02-snapshot.md`; если её тоже заводить, это отдельное решение владельца, не моё.

**agent-role-file-format-conditional-inserts vs consolidate-agent-prose-single-source — genuinely two, not one filed twice.** Родитель — широкая программа («вынести прозу агентов в одно место», охватывает канон/бюджет/пять источников истины). Ребёнок — узкое исследование ОДНОГО технического вопроса внутри неё (формат файла роли: XML-обёртка vs markdown-с-инлайн-условиями vs текущий JSON-со-строками), с тремя конкретными техническими ограничениями и списком кандидатов. part_of здесь корректно отражает вложенность зума, а не дублирование одной идеи. Оставить обе.

**opencode-background-subagent-axis2-finding — [замер в работе], не судится**, по прямому указанию брифа.

**transcript-push-firing-semantics-durability-gap — не дубликат.** Проверено против `pushtranscript-droid-http400-not-503` (сегодняшний, другой механизм: пуш ДОШЁЛ и получил 400 — этот узел сам это разграничивает в своём тексте) и против уже починенного `push-transcript-503-session-not-persisted` (503/no-retry, другой механизм). Ни один pre-existing session/append-узел не покрывает именно «хук не срабатывает вовсе из-за занятой очереди ввода». STANDS.

**Кластер canon/budget — 4 узла, разбираемость + правка после сообщения координатора.**
Дословно проверено кодом (`src/PetBox.Memory/Services/MemoryService.cs:794-804`): `EnforceCanonBudget` гейтит КАЖДЫЙ ключ в сторе `canon` (`if (!string.Equals(store, CanonStore...)) return;` дальше цикл по ВСЕМ upserts), а не только `index`. Захардкожен на `index` — ТОЛЬКО read/injection-путь (`MemoryApi.cs`), не write-гейт. Это подтверждает правку координатора и опровергает исходную посылку `02-snapshot.md` (уже исправленную координатором) и узла `canon-non-index-keys-no-size-budget`, чей заголовок буквально утверждает обратное → **NEEDS REWRITE** (площадка верная — размер канона, механизм неверный — гейт уже общестраничный; длинные записи, если они реально существуют, объясняются либо данными ДО добавления гейта, либо путём мимо `UpsertAsync`, это не проверено).
Отдельно подтверждено кодом (`opencode-plugin.ts:158-160`): opencode пушит канон-блок БЕЗУСЛОВНО на КАЖДЫЙ запрос (`output.system.push(canon)`), без какой-либо лестницы урезания — это уже зафиксировано сегодня в теле `consolidate-agent-prose-single-source` («Лестница… вшита ТОЛЬКО в pull-memory.ts… droid-pull-memory.ts и opencode-plugin.ts инжектят… БЕЗ этой лестницы вообще»), так что это не пропущенный факт, а факт, попавший не в тот из четырёх узлов. `wiring-startup-budget` называется «Стартовый контекст» и требует бюджет именно на старте — но реальность opencode/droid — это бюджет НА КАЖДЫЙ ХОД без всякого потолка, шире, чем «стартовый». Заголовок/формулировка спека не покрывает то, что уже измерено → **NEEDS REWRITE** (не MOOT — требование верно по сути, но зона действия названа уже, чем факт).
`canon-trim-budget-decision` (число бюджета) и `spec-startup-budget-write-surface` (protocol.ts мимо write-гейта, площадка вообще не про canon-стор) остаются отдельными, невзаимозаменяемыми вопросами → разделять правильно, не сливать в один узел, но решать одним владельческим проходом.

**highstakes-notes-say-what-not-when — два независимых вопроса, не один.**

Маршрутизация (исправлено по слову координатора — было ошибочно принято за случайность): `tasks_node_get` показывает его каноническим предком `umbrella-friends-on-my-wiring` (создан 2026-09-06, ДРУГОЙ зонтик). `relations_list` с `umbrella-agent-behaviour` находит ВТОРОЕ part_of-ребро от этого узла, созданное **2026-09-10T12:53:14.292** — в той же секунде, что и остальные 14 рёбер зонтика. Общая метка времени — след ПАКЕТНОЙ записи (все 15 рёбер писались одним проходом), а не признак сбоя: координатор подтвердил, что явно перечислил этот узел в инструкции по привязке, наряду с пятью другими pre-existing узлами (`canon-trim-budget-decision`, `wiring-startup-budget`, `spec-startup-budget-write-surface`, `canon-non-index-keys-no-size-budget`, `new-role-never-gets-local-model-binding-on-existing-installs`, `apply-orphans-artifacts-of-a-deleted-role`). Двойное родительство — осознанное, оба ребра сохранены намеренно; владелец уже уведомлён об этом ранее. **Это открытый вопрос владельцу (оставаться ли под двумя зонтиками), а не дефект для уборки.** Ребро не трогал и трогать не предлагаю сам.

Содержание (правка координатора по `11-roli-i-marshrutizatsiya.md`): посылка узла — «notes говорят ЧТО делает роль, но не КОГДА её звать» — слабее реального дефекта. КОГДА написано буквально («when a wrong result would be expensive OR would pass unnoticed»). Реальный дефект: этот триггер требует от оркестратора верно оценить дороговизну СВОЕЙ БУДУЩЕЙ ошибки ДО того, как она случилась — измеримо не делает этого: `worker-highstakes` включается только когда владелец ЛИЧНО ловит оркестратора на неверной маршрутизации (эпизод 2026-08-27), трафика на opencode-ноге нет, на qwen — почти пусто. Формулировка есть, но не самоприменима изнутри роли — это другой класс дефекта, чем у `explore` (там триггера не было вовсе, чинилось сегодня `3c70196b`, ноль маршрутизации). Предложенный в узле текст (+35 B) не чинит именно эту причину — он делает формулировку точнее, но не решает несамоприменимость. → **NEEDS REWRITE**, независимо от вопроса мис-филинга выше.

## Observations, заведённые сегодня (2026-09-10) — отдельный список

| key | created | in scope? | verdict |
|---|---|---|---|
| factory-droid-deepseek-key-stored-plaintext-not-env-ref | 06:22 | droid | **ВНЕ ОХВАТА** |
| droid-gateway-custommodels-nonfunctional-url-key-header | 07:41 | droid | **ВНЕ ОХВАТА** |
| codex-catalog-stale-context-window-uncorrected-default | 06:34 | yes | STANDS (не проверялось глубже — не входило в известные кейсы) |
| opencode-go-catalog-active-but-403-mimo-kimi-hy3 | 07:05 | yes | STANDS (аналогично) |
| append-retry-abort-of-our-own-deadline-reported-as-network-failure | 12:38 | yes | STANDS |
| wire-test-header-documents-bare-node-test-that-cannot-run | 12:06 | yes | STANDS |
| obs-bf86c812385c (doctor рапортует OK, не проверяет эндпоинты) | 12:01 | yes | STANDS |
| obs-7f3eb8165ed0 (--days 30 смешивает периоды) | 12:00 | yes | STANDS |
| pushtranscript-droid-http400-not-503 | 11:51 | droid-специфика внутри sessions-темы | STANDS (не droid-роль сама по себе, а транспорт; не исключаю по букве, но пограничный) |
| obs-812c57860e5f (правь qwen-файлы — узнай кто рендерит) | 11:50 | yes | STANDS |
| obs-5e30d9980abc (workflow pull_request_review_comment) | 10:59 | нет (CI/боты, не agent-behaviour) | не проверялось |
| obs-bc46f3c09571 (Qwen Code CI action_required) | 10:50 | нет (CI, не agent-behaviour) | не проверялось |
| obs-06df47828529 (публикация зависит от TsWireTest) | 05:30 | нет | не проверялось |
| codex-config-toml-sites-append-into-unparseable-toml-at-exit-0 | 05:21 | yes | STANDS |
| resolveproject-unresolvedenvref-swallowed-by-hook-besteffort-catch | 04:27 | yes | STANDS |
| registry-upsert-bypasses-readregistry-corrupt-projects-json-wipes-all | 03:55 | пограничный (wire kit, не agent-behaviour узко) | не проверялось |
| obs-f39772117027 (молчаливая пропажа petbox-тулов после ротации ключа qwen) | 03:08 | yes | STANDS |

Последние шесть строк (после первых одиннадцати) размечены «не проверялось» честно — бюджет прохода не позволил перечитать каждую против кода; их STANDS/ВНЕ ОХВАТА не гарантирован, только их существование и дата подтверждены доской.
