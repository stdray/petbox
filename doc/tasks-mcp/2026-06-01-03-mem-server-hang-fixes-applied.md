---
timestamp: 2026-06-01T11:00:00+03:00
agent: claude-code
model: claude-opus-4-8
target: memory
action: update
target_file: ~/.claude/projects/D--my-prj-petbox/memory/project_server_hang_incident.md
---

## What
Обновлена project-память по баг-инциденту зависаний сервера: (1) гипотеза OOM подтверждена логами (oom-killer трижды убивал PetBox.Web 30–31 мая); (2) зафиксирована топология деплоя (3 docker-контейнера + Caddy по поддоменам); (3) записаны ПРИМЕНЁННЫЕ фиксы — 2 ГБ swap + swappiness=10, остановлены YobaLog/YobaConf; (4) ключевая оставшаяся находка — PetBox.Web под Server GC раздулся до ~560 МБ и остаётся главным потребителем; намечен опциональный фикс (workstation GC / GCHeapHardLimit / docker --memory).

## Why
Пользователь подтвердил диагностику, выбрал «только ПО» (без апгрейда ОЗУ): swap + раздеплой YobaLog/YobaConf. Caddy просил не трогать. Нужно зафиксировать факт выполненных изменений и оставшийся риск (аппетит PetBox.Web).

## Args
- target: memory/project_server_hang_incident.md
- разделы: добавлены «Deployment topology», «APPLIED 2026-06-01», «KEY REMAINING FINDING»; «Status» → MITIGATED.
- секреты не записаны (env-ключи приложений упомянуты только как имена в ходе работы, значения не сохранялись).

## Outcome
Успешно. На сервере: swap активен и в fstab; yobalog/yobaconf в Exited(0), порты 8082/8081 не слушаются; petbox жив (8083→302). Хардлоков больше не ожидается. Опционально остаётся ограничить память PetBox.Web (требует пересоздания контейнера). petbox MCP в сессии так и не появился — перенести в доску позже.
