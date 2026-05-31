---
timestamp: 2026-05-31T12:06:00+03:00
agent: claude-code
model: claude-opus-4-8
target: memory
action: create
target_file: C:\Users\stdray\.claude\projects\D--my-prj-petbox\memory\reference_linq2db_full_db_reach.md
---

## What
Создана memory-запись (reference): linq2db дотягивается практически до 100% возможностей БД и дёшево расширяется; в частности SQLite FTS5 маппится (пример FullTextTests.SQLite.cs в апстриме). Не делать вывод «linq2db не умеет» без проверки.

## Why
В планировании я ошибочно заявил, что «linq2db 6.3 не маппит FTS5»; пользователь поправил и попросил записать инвариант. Влияет на A5 (поиск памяти через FTS5/vec — делать через linq2db-расширение, а не сразу raw SqliteCommand).

## Args
name: reference-linq2db-full-db-reach; type: reference; body: инвариант + ссылка на FullTextTests.SQLite.cs + How-to-apply. Линк: [[reference-linq2db-clone]].

## Outcome
Успех. Добавлена строка в MEMORY.md (References).
