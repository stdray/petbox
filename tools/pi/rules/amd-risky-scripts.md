## Scripts that touch security or system configuration — [own]

Some scripts are a one-way door: if they are wrong, the machine or the exposure stays wrong, and
it fails silently. Treat as such anything that touches:

- **external access** — what listens, on which interface, port forwarding, tunnels, firewall
  rules, allowed hosts, TLS termination;
- **access credentials** — passwords, tokens, keys: where they are stored, where they leak (env,
  argv, plaintext files, logs), and how they are rotated;
- **system configuration** — user/machine environment variables, registry keys, `PATH` and other
  composite variables, autostart entries, services, scheduled tasks.

Rules that follow:

1. **Written, reviewed, then run — in that order.** The review sees the script, the exact command
   line that will run it, and the **expected state as a before/after pair**: the command that
   shows the current state, and the command whose output after the run must match. Running it
   first and reviewing the damage is not review.
2. **A dry run is the artifact under review.** A script that cannot show what it would do without
   doing it is not ready: add a dry-run flag (`-DryRun` in PowerShell, `--dry-run` in a POSIX
   script), print before/after, review that output. If the change cannot be exercised safely even
   in dry-run form, say so and keep the change manual.
3. **Use the owner's existing launcher and keep its settings.** A service that has a launcher
   starts through that launcher, never by invoking the runtime directly — the launcher is where
   passwords, allowed hosts and timeouts live. After any restart, **verify authentication
   explicitly** (an unauthenticated endpoint answers 200 where it must answer 401); a restart that
   silently drops authentication is worse than a failed restart.
4. **A tool that repairs an environment must not depend on it.** If it fixes `PATH`, it must not
   call its own interpreter by bare name; if it fixes the registry, it must not rely on values
   from the shell that started it. Test it in a deliberately broken environment, not only a
   healthy one.
5. **Composite variables are composed, not assigned.** `PATH` and `PSModulePath` are machine half
   plus user half; assigning one half silently deletes the other. Deleting a setting and emptying
   it are different operations with different consequences — say which one you mean, and why.
6. **The script carries a blast-radius note**: what stops working while it runs, what does not
   roll back, what has to be redone by hand. On a service that hosts live sessions, restarting it
   is a blast radius, not a footnote.

**Exemption, deliberately narrow.** Only for a change that is (a) a variable set on a process the
script itself starts, or (b) a file it creates and deletes in the same run — and no other write
anywhere in the script. It is the reviewer who establishes this from the script; the author's
assurance does not count. For such a change, a short statement of scope and reversibility replaces
the dry run, the before/after pair and the blast-radius note — the review itself still happens.
The exemption never covers authentication, external exposure, **credentials**, persistent settings,
or environment reconstruction.

How to brief the review of such a script (main session): see `amd-review-briefs`.
