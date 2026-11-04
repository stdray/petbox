# Что уже было в issues pi

Проверено `gh search issues --repo earendil-works/pi` 2026-11-04. Репозиторий: `earendil-works/pi`
(бывший `badlogic/pi-mono`).

## Севсионный заголовок: жалобы были, все закрыты исправлением

| # | дата | состояние | о чём |
|---|---|---|---|
| [#9230](https://github.com/earendil-works/pi/issues/9230) | 2026-09-06 | closed | `opencode-go` не шлёт `x-opencode-session`, который шлюз уже требует |
| [#9290](https://github.com/earendil-works/pi/issues/9290) | 2026-09-07 | closed | `modelRegistry.complete()` в extension API не шлёт заголовок для моделей opencode-go |
| [#9302](https://github.com/earendil-works/pi/issues/9302) | 2026-09-07 | closed | суммаризация вне цикла теряет заголовки → `400 MissingSessionID` |
| [#9326](https://github.com/earendil-works/pi/issues/9326) | 2026-09-08 | closed | `pi-ai` вообще никогда не шлёт `x-opencode-session` — это и есть CHANGELOG-строка 760 |
| [#9437](https://github.com/earendil-works/pi/issues/9437) | 2026-09-10 | closed | требует per-conversation заголовок; просят добавить формат `opencode` и включить в каталоге |
| [#10053](https://github.com/earendil-works/pi/issues/10053) | 2026-09-25 | closed | расширения, вызывающие `stream()` напрямую, заголовков не получают |

**Вывод:** вопрос «а ставит ли pi заголовок» задавали и закрывали четыре раза подряд; в 1.5.0
он закрыт (см. `01-findings.md`).

## Открытый и НЕ реализованный: lineage-идентификатор

[#11479](https://github.com/earendil-works/pi/issues/11479) (2026-10-27, closed) —
«OpenCode Go: send x-opencode-session from a conversation lineage id». Формулировка совпадает
с тем, что найдено здесь независимо: `StreamOptions.sessionId` — единственное, что pi шлёт в
`x-opencode-session`, а opencode шлёт туда **affinity** (для дочерней сессии — id родителя), а
сырой id — во втором заголовке `x-opencode-session-id`.

Закрыт **автоматикой** («All issues from new contributors are auto-closed by default»), не
мейнтейнером; в 1.5.0 реализации нет — в `pi-ai` поле `sessionAffinity` встречается только как
`compat.sessionAffinityFormat`, а не как опция запроса. То есть разрыв из `01-findings.md` п.3
апстрим знает, но он не исправлен.

## Промахи кеша: открытые жалобы

| # | состояние | о чём |
|---|---|---|
| [#9013](https://github.com/earendil-works/pi/issues/9013) | open | уведомления о промахе ложно срабатывают на локальном vLLM после облачной модели в той же сессии |
| [#10024](https://github.com/earendil-works/pi/issues/10024) | open | смена набора инструментов посреди прогона двигает голову системного промпта и пересчитывает разговор |
| [#11529](https://github.com/earendil-works/pi/issues/11529) | open | порядок инструментов зависит от порядка регистрации → тот же самый набор может ломать кеш |
| [#10489](https://github.com/earendil-works/pi/issues/10489), [#11114](https://github.com/earendil-works/pi/issues/11114) | open | `tool_search`/`codemode only` переобъявляют инструменты → промах |

Ни одна из них не про «заголовок не тот», и ни одна не про opencode-go: это про форму
запроса (порядок/состав инструментов). Для разобранной сессии они не подходят — там промахи
совпадают с простоем и со сменой семейства `responseId`.

## Прогрев кеша: закрытые

[#9810](https://github.com/earendil-works/pi/issues/9810) (closed) — метаданные Codex отключают
CacheWarmer как раз на промахах после простоя; [#11528](https://github.com/earendil-works/pi/issues/11528)
(closed) — прогрев не работает на Bedrock, потому что у моделей нет `promptCache`;
[#11493](https://github.com/earendil-works/pi/issues/11493) (closed) — полный промах после
простоя у Codex. Все три — про тот же механизм, который для opencode-go выключен по
экономике (`03-cache-warming.md`).
