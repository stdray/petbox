---
timestamp: 2026-06-01T09:05:00+03:00
agent: claude-code
model: claude-opus-4-8
target: memory
action: create
target_file: ~/.claude/projects/D--my-prj-petbox/memory/reference_prod_server_access.md
---

## What
Создана reference-память о доступе к прод-серверу `yoba-apps.3po.su`: временный пользователь `claude-diag` (key-only, NOPASSWD sudo), путь к приватному ключу на Windows, команда подключения, команда сноса доступа, и заметка о граблях с пустым паролем ключа в PowerShell (`-N '""'` задаёт литерал `""`, а не пустой пароль).

## Why
Пользователь попросил завести доступ для удалённой диагностики сервера (периодически перестаёт принимать https+ssh) и явно сказал «это и твой ssh ключ можно положить в память». Нужно, чтобы доступ и способ подключения переживали сессию.

## Args
- name: reference_prod_server_access
- type: reference
- description: How to SSH into the PetBox production server for sysadmin/diagnostics.
- body: host/стек, юзер claude-diag, путь к ключу (значение ключа НЕ включено — только расположение), connect/teardown команды, PowerShell passphrase gotcha. Линк на [[project_server_hang_incident]].

## Outcome
Успешно. petbox MCP в сессии был недоступен (инструменты не экспонированы), поэтому запись сделана в файловую память + этот record-файл как fallback. При доступности MCP — продублировать в доску.
