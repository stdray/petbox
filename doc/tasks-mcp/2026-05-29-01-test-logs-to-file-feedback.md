---
timestamp: 2026-05-29T09:00:00+03:00
agent: claude-code
model: claude-opus-4-7
target: memory
action: create
target_file: ~/.claude/projects/D--my-prj-petbox/memory/feedback_test_logs_to_file.md
---

# What

Saved feedback memory `feedback_test_logs_to_file.md` capturing the user's directive that test runs must be piped to a log file under `.tmp/` and then grep'd, instead of being re-run to scroll output. Added to `MEMORY.md` index.

# Why

User said: "сохрани инструкцию для будущих сессий, что тесты надо гонять с записью в файл". This came after an earlier session where the user had to call this out the hard way: "почему ты гоняешь логи без записи в файлы, чтобы искать в результатах без перезапуска?". Cake Test runs in PetBox are ~1 minute end-to-end, so re-running to inspect output is expensive — a saved log lets multiple greps run in seconds against the same artifact.

# Args

- Memory file: `feedback_test_logs_to_file.md`
- Type: `feedback`
- Linked from `MEMORY.md` under "Working style" section.
- Canonical pattern in the body: `./build.sh --target=Test > .tmp/test-run.log 2>&1; echo "exit=$?"` then `grep -E "^(Failed!|Passed!)" .tmp/test-run.log` etc.
