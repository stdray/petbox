---
timestamp: 2026-06-03T00:00:00+03:00
agent: claude-code
model: claude-opus-4-8
target: plan
action: create
target_file: petbox tasks board=ideas node=methodology-quartet (projectKey=$system)
---

## What
Новая top-level идея `methodology-quartet`: связать ideas+spec+work+intake в методологию как единый per-project юнит (1 каждого kind на проект); free-доски остаются свободными. Ревью relation-types вложено в эту же идею.

## Why
Пользователь заметил: квартет методологии плотно связан рёбрами и не работает по частям, а сейчас можно создавать сколько угодно spec/work/ideas/intake-досок и вязать `specBoard` вручную → неоднозначность связей и роллапов. Проверка board-kinds: `free|spec|ideas|intake|work` — все четыре УЖЕ есть (intake и work — полноценные kinds, я раньше ошибочно записал их в пробелы). Значит дело не в kind'ах, а в binding'е. relation-types пользователь решил не выделять отдельно — оно про рёбра квартета, кладём в ту же идею.

## Args
Тело `methodology-quartet` (nodeId 5777cde5…): квартет как per-project юнит; почему 1/проект (singular-связи/роллапы, области решаются внутри спеки тегами/ветками — `spec-flat-tags`, а не множеством досок); intake = борд по дизайну; чего не хватает — singleton (≤1 методологического kind/проект), авто-wiring `specBoard`, opt-in provision, единый view/API над квартетом (сейчас per-board `board_*`). Развилка per-project vs workspace-уровень (позже). Ревью relation-types: есть 6 (`task_spec|issue_task|idea_spec|blocks|nfr|dup`); add `partOf` (декомпозиция, group-by partOf), рассмотреть `supersedes`, `nfr` overlap с concern-тегом (req→req references?), governance связей как enum-словаря. Итог: keep 5 + partOf (+supersedes?), решить nfr.

## Outcome
Успех, борд `ideas` v16. Эта идея — структурное завершение методологии: из «набора досок, которые сам заводишь» → first-class per-project конструкт. relation-types не выделяли отдельным узлом (по решению пользователя — внутри quartet). Связи между идеями не проставлял.
