## Loops and retries — [own]

- The same tool with the same arguments repeats only after an input changed: another
  file, another flag, changed state, a server response. "Recheck just in case" is not a change.
- A second identical refusal (validation, auth, 404) on the same tool = stop: read the
  schema or docs, then one attempt. No third guess; if reading does not settle it, ask.
- `edit(X) → fail → edit(X) → fail` on the same file: the next call reads a *different*
  file (non-test code if X is a test, the test if X is impl). A third repetition =
  report `BLOCKED: loop on <X>` and stop, do not commit, do not try a fourth edit.
