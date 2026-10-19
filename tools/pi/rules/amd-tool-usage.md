## Tool usage — [opencode]

- Use `read` for file contents, `grep` for content search, `find` for path search, `ls`
  for listing — not `cat`, `rg`/`grep`, `find`/`ls` in bash. Reach for `bash` when a tool
  cannot do the job, not as the default route.
- Use `edit` for targeted changes to existing files; `write` for new files or a full
  rewrite. Do not create a file that the task did not ask for.
- Make independent tool calls in parallel; dependent ones sequentially.
- Use absolute file paths when you refer to files.
- Do not chain shell commands with `;` or `&&` just to print separators — the output
  becomes noise for the reader.

## Project rules outrank these

Before acting in a repository, read the nearest `AGENTS.md` (or `CLAUDE.md`) walking up
from your working directory. It is project canon and outranks everything in this skill
set; when the two disagree, follow the project file and say so in your report.
