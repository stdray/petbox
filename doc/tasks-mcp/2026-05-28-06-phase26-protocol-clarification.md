---
timestamp: 2026-05-28T22:45:00+03:00
agent: claude-code
model: claude-opus-4-7
session: ui-polish-and-clients-roadmap
target: plan
action: update
target_file: D:\my\prj\yobabox\doc\plan.md
---

## What

Уточнил Phase 26 в `doc/plan.md`:
- Исправил неточную формулировку "MEC-compatible protocol" — MEC это .NET DI abstraction, не wire protocol. Config wire-format yobabox'а — свой tag-based, `YobaConf.Client` оборачивает в `IConfigurationProvider`
- Разделил Architecture на три части (Logs / Config / Data) с явным указанием где стандарт есть (CLEF/Seq), где наш формат (Config, Data)
- Добавил Phase 26.8: research Spring Cloud Config Server / Consul KV compat endpoint как opt-in upgrade path. Не реализация, разведка.
- Зафиксировал resolved decisions: modular, bun, bun test, точный перенос → core SDK extend order
- Drop logging adapter packages (Serilog.Sinks.Seq / @datalust/winston-seq работают drop-in)
- Phase 26.5 переписан: port build.cake + ci.yml publish jobs из yobaconf (GitVersion.yml уже в yobabox идентичен)
- Phase 26.6: kpvotes-ts не мигрирует winston-seq — pet просто меняет URL+key
- Repo structure показывает `src/clients-net/`, `src/clients-ts/`, `src/clients-py/` (Python placeholder)

## Why

Пользователь точно указал: "MEC-compatible config protocol" — не так, сейчас свой формат. Spring Cloud Config или подобные стандарты — будет рад если можно дотюнить + переиспользовать клиенты.

Поэтому я (а) исправил формулировку до реальной, (б) добавил research item в Phase 26 для Spring Cloud Config / Consul KV compat endpoint — если такой endpoint можно сделать без потери tag-based flexibility, pet'ы получат готовые клиенты в Java/.NET/Node/Python.

Также user в предыдущих сообщениях зафиксировал ряд решений (modular over monolith, bun, не делать Logging adapters, копировать GitVersion+Cake из yobaconf, Python тоже придёт) — все эти теперь в плане как "Resolved decisions" вместо open forks.

## Args

- File: `doc/plan.md`
- Sections touched: Phase 26 Architecture (rewrite), Phase 26.3 (Core SDK extend Config + Data raw — no Log adapter), Phase 26.5 (Cake/GitVersion publish порт из yobaconf), Phase 26.6 (kpvotes-ts migration без winston-seq replacement), Phase 26.8 (NEW — Spring Cloud Config research), Repo structure + Resolved decisions blocks
- Commit: `dc89099 docs(plan): clarify Phase 26 config protocol stance + Spring Cloud research`

## Outcome

Plan актуален. "Resolved decisions" зафиксированы — следующая сессия не пересматривает. Spring Cloud Config research остается open task в Phase 26.8 для phase execution time.

Phase 27 open forks остаются — будем решать когда Phase 26 завершится.
