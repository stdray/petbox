---
timestamp: 2026-06-01T09:06:00+03:00
agent: claude-code
model: claude-opus-4-8
target: memory
action: create
target_file: ~/.claude/projects/D--my-prj-petbox/memory/project_server_hang_incident.md
---

## What
Создана project-память (BUG): прод `yoba-apps.3po.su` раз в 1–2 дня перестаёт принимать все подключения (https+ssh) и оживает только после жёсткой перезагрузки из панели хостера. Зафиксированы находки первичной диагностики и гипотеза причины.

## Why
Пользователь попросил «через mcp план и память … заведи там баг» про системное администрирование. MCP-доска недоступна в сессии, поэтому баг заведён как project-память + этот record (перенести в доску, когда MCP поднимется).

## Args
- name: project_server_hang_incident
- type: project
- description: BUG — prod periodically stops accepting https+ssh, needs hard reboot.
- body: подтверждённые «грязные» ребуты (last -x без shutdown 29/31 мая, 1 июня); главный подозреваемый — 956 MiB RAM и НОЛЬ swap → OOM/трешинг убивает caddy/sshd; persistent journald фактически не хранит прошлые загрузки (улики теряются при ребуте); conntrack/диск/inodes/fd в норме. Предложенный, НО НЕ ПРИМЕНЁННЫЙ фикс: swap 2G + swappiness, persistent journald + sysstat, поминутный сэмплер/earlyoom, при подтверждённой утечке — апгрейд RAM. Линк на [[reference_prod_server_access]].

## Outcome
Успешно записано в файловую память. Статус: расследование, фикс ожидает решения пользователя. Дублировать в petbox-доску при доступности MCP.
