## Tool usage — [opencode]

- Use `read` for file contents, `grep` for content search, `find` for path search, `ls`
  for listing — not `cat`, `rg`/`grep`, `find`/`ls` in bash. Reach for `bash` when a tool
  cannot do the job, not as the default route.
- Prefer `edit` for targeted changes to an existing file and `write` for a new file or a
  full rewrite. Do not create a file that the task did not ask for.
- **Prefer a tool call to a script, and know what the script costs you.** A tool call is the
  observable act: its arguments and the diff it generates are in the transcript, a fragment
  that does not match is reported rather than guessed at, and a reader sees what changed
  without re-running anything. A `sed -i` / `perl -pi` / `python` write gives all three up —
  no diff, no uniqueness check, and a `str.replace` whose text went stale leaves the file
  untouched while the script exits 0 and prints whatever it was told to print. That is what
  makes it the wrong DEFAULT. It is not a prohibition, and a shell is not the enemy.
- Heredoc is not the problem; escaping is. `<<'EOF'` (quoted) passes the body through
  untouched and is fine for a long block, bare `<<EOF` still expands `$`, backticks and
  backslashes — the usual source of "the file now contains garbage". A body that is itself
  full of backslashes, quotes and JSON escapes is cheaper to hand to `write` than to get
  past two escaping layers by hand.
- **When a script genuinely wins, make it prove its work.** It wins for content that must not
  enter the transcript at all — a password, a token, anything read out of a secrets file.
  `edit` and `write` put the body into the tool call, and the tool call IS the transcript, so
  there the script is right, provided it prints nothing and neither its source nor its command
  line carries the secret (argv is shown and logged). It also wins for what a program itself
  produces: `bun build > dist/app.js`, a generator emitting a file, a `> log` capture of a
  command's result — that wrote what the program computed, and nobody retyped it. Either way,
  read the result back: a script's exit code says it ran, not that the file is right.
- If `edit` reports "did not match", that is a reason to `read` the file and retry with an
  exact fragment — not a reason to escalate to a script that will silently change nothing.
- **Never print a secret.** It comes out of a file, an environment variable or a config:
  name the path or the key instead of the value, and keep it out of your answer, out of
  `echo`, out of a diff and out of a tool argument. Once printed it is in the transcript,
  and a transcript is not something you can retract from.
- **Reading such a file is fine; dumping it is not.** The first bullet here says to use `read`
  for file contents — for a secrets file that collides with this one, so do not `read` it whole.
  Inspect the shape without printing a value: `grep -c .` for how many lines, `wc -l`, `cut -d= -f1`
  for which keys exist. If you genuinely need the value, take it inside a script that uses it and
  prints nothing; your answer names the path or the key, never the value.
- A replacement bigger than a screenful — a whole section, or a paragraph with real
  newlines and quotes — is a `write` of the file, not a fight with `edit`'s exact match.
- Make independent tool calls in parallel; dependent ones sequentially.
- Use absolute file paths when you refer to files.
- Do not chain shell commands with `;` or `&&` just to print separators — the output
  becomes noise for the reader.

## Two shells — pick by the job

You have `bash` (Git Bash on Windows) and PowerShell — exposed under one tool name, `pwsh`
or `powershell`, or under both; check the tool list. **Prefer `pwsh` when both are listed**:
same tool, and the name resolved first.

- **`pwsh` / `powershell`** — use it for Windows-native work: `Get-*`/`Start-Process`,
  services, the registry, event logs, `New-Item`, paths with backslashes and spaces, anything
  whose only documentation is PowerShell. A repo command documented as
  `./build.ps1 -Target X` is native here.
- **`bash`** — POSIX tooling and everything the repo already scripts that way: `grep`,
  `sed`, `awk`, pipelines, `find`, `rg`/`fd` (both ship with pi), `./build.sh`, `git`
  output parsing. `jq` is not shipped — it is there only if the machine has it. A repo
  command documented as `./build.sh --target=X` runs in bash unchanged.

Rules that follow from the split:

- **Never invoke bare `powershell` from bash** — it may select a different engine from the
  tool. Use the PowerShell tool, or invoke `pwsh` explicitly.
- Do not translate a working bash command into cmdlets just because PowerShell exists, and
  do not run bash to reach something Windows exposes only through PowerShell. The exit-code
  rule differs per shell — see `amd-exit-codes` (`$?` is `tail`'s status in a bash pipeline;
  `$LASTEXITCODE` in PowerShell).

## Project rules outrank these

Before acting in a repository, read the nearest `AGENTS.md` (or `CLAUDE.md`) walking up
from your working directory. It is project canon and outranks everything in this skill
set; when the two disagree, follow the project file and say so in your report.
