# 09a — Проверка представимости живого quartet в модели 08

Дата: 2026-08-26. Источник: `tasks_methodology_rules_get($system, "quartet")` v22,
`tasks_methodology_utility_get($system)` v2, `tasks_methodology_guide($system, "quartet")`.
Модель под проверкой: `08-model-draft.md`. Не решение — фиксация того, что показала раскладка.

---

## 1. Раскладка live quartet в атомах модели 08

### Kind доски × тип единицы × quickAddAllowed

| kind | types (default) | quickAdd | creation `have` на `*new*` |
|---|---|---|---|
| intake | issue | true | — |
| ideas | idea | true | — |
| spec | spec | **false** | have(link:idea_spec → ideas[accepted]) |
| work | feature(def), bug, chore | **false** | have(link:task_spec → spec) для feature/bug |
| wiki | page | true | — |

Совпадение 5/5: `quickAdd=false` ⇔ у дефолтного типа есть `have`-условие на `*new*`. Ни
одного контрпримера → quickAddAllowed **не самостоятельное понятие**, выводимо.

### State (class объявлен)

| kind | open | terminalok (`done`) | terminalcancel (`dropped`) |
|---|---|---|---|
| intake | reported*, triage, confirmed | done | duplicate, wontfix |
| ideas | raw*, exploring, review, deferred | accepted, shipped | rejected |
| spec | defined* | — | deprecated |
| work | Pending*, InProgress, Review, Blocked | Done | Cancelled |
| wiki | draft*, live | promoted | stale |

(* = initial). 3 statusKind ↔ модельные 3 class (`open`/`done`/`dropped`) — 1:1, без потерь.

### Transition × Condition (атомы `bring`/`have`/`field`)

| переход | атом(ы) 08 |
|---|---|
| intake triage→duplicate / →wontfix | bring:note (= requiresReason) |
| intake confirmed→done | bring:signature(by:owner), enforce=**honor** |
| ideas exploring→review | bring:artifact("spec_plan") |
| ideas review→accepted | bring:signature(by:owner), enforce=honor |
| ideas *→rejected (2 шт) | bring:note |
| ideas {raw,exploring,review,deferred}→shipped (4 шт) | bring:signature(owner)+note, enforce=honor |
| work Review→Done | bring:signature(owner), enforce=honor |
| wiki {live,draft}→stale | bring:note |
| spec defined→deprecated | — (нет условий) |
| work остальные переходы (6 шт) | — (нет условий) |

Итог по 7 живым approval-гейтам: **enforceApproval:false везде, без исключений** (0/7
enforced). `checklist`-атом в живом quartet **не встречается ни разу** — его
представимость проверить не на чем, только по описанию в `07-requirements.md`.

### Effect (глагол `move`)

| kind | on | эффект |
|---|---|---|
| work | →Done | move(target: incoming `issue_task`, to: done) |
| work | →Done | move(target: outgoing `blocks` **где target.state=Blocked**, to: InProgress) |

Оба эффекта кросс-юнитные: цель — не текущая единица, а единица, выбранная тем же
селектором связей, что `have` в Condition. Второй несёт доп. гард `onlyFrom` — фильтр по
ТЕКУЩЕМУ статусу цели, а не источника.

### Summary (delivery roll-up)

spec: requiredTypes=[feature], defectTypes=[bug], link=task_spec, roll-up по дереву
`part_of`. Один фиксированный агрегатор — ложится на `Summary` без остатка.

### Link (linkKinds)

| slug | category | direction | использует |
|---|---|---|---|
| idea_spec | process | ideas→spec | link-констрейнт на `*new*` spec, targetStatuses:[accepted] |
| task_spec | process | work→spec | link-констрейнт на `*new*` feature/bug, БЕЗ targetStatuses |
| issue_task | process | intake→work | effect `move` на work→Done |
| blocks, part_of, supersedes | структурные builtin, direction-less | — | part_of — дерево Summary; blocks — цель эффекта |
| relates_to, depends_on, mirrors | нейтральные | — | без процессного смысла |

### tagAxes, дубль kind'а

tagAxes: `area`, `concern` — идентичны в quartet-инстансе И в utility-слое (kind `wiki`
объявлен в ОБОИХ документах, версии v22 и v2 порознь) — живой пример «дубля kind'а» из
`01-legend.md`.I.

---

## 2. Что НЕ представимо

1. **`autoWireFrom`.** Правило «если ровно одна активная доска work и одна spec и
   WiredBoard пуст — связать» считает АКТИВНЫЕ ДОСКИ данного kind'а и пишет в конфиг
   доски. Ни Unit, ни Transition, ни Effect, ни Summary этого не берут — это не переход
   единицы и не связь единиц, а auto-конфигурация на уровне популяции досок. В 08 нет
   понятия для этого уровня вообще.
2. **Инварианты не на переходе.** `tagAxes` (белый список namespace для тегов) и,
   буквально, формулировка спек-констрейнта — «EVERY write of a `spec` must carry
   `idea_spec`» (не только создание) — держатся на КАЖДОЙ записи единицы, а не на
   конкретном Transition. Condition в 08 явно «висит на переходе»; точки подвеса для
   правила, не привязанного ни к одному Transition, в модели нет.
3. **Identity-гейт vs bring-евиденс.** `signature(by:owner)` в 08 — атом `bring`
   («принеси подпись»), то есть данные, приложенные к переходу. Реальный approval-гейт —
   не данные, а ограничение на то, КТО может дёрнуть переход. Модель занимает готовую
   форму `bring`, чтобы не заводить пятый род атома, но семантика другая: с
   `enforceApproval:true` сервер обязан блокировать вызов по идентичности вызывающего,
   а не проверять наличие поля в payload.

## 3. Что представимо, но хуже

1. `bring:note` для `requiresReason` — атом рассчитан на «принеси артефакт/чеклист/
   подпись»; обязательная инлайновая строка `reason` самого вызова перехода утрамбована
   в тот же карман без отдельного слова, туманнее сегодняшнего именованного поля.
2. `quickAddAllowed` выводим (§1), но КАК выбираются дефолтный тип kind'а и его
   `*new*`-условие — деталь, которую 08 нигде не называет узлом модели.
3. `tagAxes` — фиксированный список namespace на весь kind, не привязанный ни к одному
   Transition; выражать его `field`-атомами пришлось бы поштучно на каждом переходе,
   многословнее одной декларации.
4. `part_of` — одновременно обычный Link И единственное дерево, по которому катится
   Summary. Глоссарий Link в 08 этой привилегии не называет.

## 4. Что схлопнулось (подтверждено фактами)

- **Подтверждено.** Констрейнт `task_spec` на work (feature/bug, без targetStatuses,
  явно «edits don't re-require it») — учебный случай: 1:1 ложится на condition перехода
  из `*new*`, без остатка.
- **Не полностью подтверждено.** Констрейнт `idea_spec` на spec заявлен гайдом как «EVERY
  write», не только создание — если читать буквально, это НЕ схлопывается в один
  `*new*`-переход, а требует ещё и постоянного инварианта (см. §2.2).
- **Новое схлопывание, не заявленное в 08.** `quickAddAllowed` — не отдельная сущность:
  100% (5/5 kind'ов) совпадает с наличием `have`-условия на `*new*` у дефолтного типа.

## 5. Обе развилки — по факту раскладки

**Р-11 (где живут правила).** Голосует **(b) Blueprint**. Живой документ `quartet` уже
устроен как ОДИН кросс-kind контракт: linkKinds, effects и autoWireFrom пересекают
границы kind'ов и версионируются ЦЕЛИКОМ (v22) под одним ключом — раздельные доски такое
не удержат. Дубль kind'а `wiki` (инстанс + utility, §1) — живой пример болезни, которую
ветка (a) не лечит, а (b) исключает по конструкции (kind у Blueprint один дом).

**Р-12 (флаг enforce).** Голосует **(b) флаг есть**. Все 7 живых approval-гейтов несут
`enforceApproval:false` **без единого исключения**, при этом лежат в ТОМ ЖЕ JSON-документе,
что и безусловно проверяемые сервером `requiresReason`/`preconditionArtifact`. Место
хранения (данные документа rules) не может служить сигналом enforce/honor — ветка (a)
(«носитель и есть маркер») этому живому распределению противоречит напрямую; нужен
самостоятельный атрибут.
