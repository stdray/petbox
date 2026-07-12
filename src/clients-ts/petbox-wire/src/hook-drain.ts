// Shared "let me exit already" helper for the SessionStart/Stop hooks (pull-memory.ts,
// push-session.ts, droid-pull-memory.ts, droid-push-session.ts).
//
// Empirically measured (not assumed — see the exit comments in those files): an aborted
// fetch() against a REAL remote server that is slow/stalled (not a fast ECONNREFUSED) can
// leave its underlying TLSSocket alive for several seconds — sometimes ~10s — AFTER the
// AbortController fires and our own await has already settled, even with `Connection: close`
// sent on the request. `Connection: close` does stop keep-alive from being the default
// blocker (confirmed: a completed request's socket closes immediately), but it does not
// speed up the teardown of a socket whose request never got a response before being aborted.
// Left alone, a hook process that just returns from main() would sit waiting for the OS/TLS
// stack to finish that teardown, turning one slow session start into an ~18s stall (2x the
// fetch budget, since two separate fetch attempts can each leave one behind).
//
// The fix is NOT to hard `process.exit()` — that is the exact race that caused the original
// crash (`Assertion failed: !(handle->flags & UV_HANDLE_CLOSING)`) when a handle was mid-close.
// Instead: mark any still-open handles as non-blocking for the event loop (`unref()`) once our
// own logical work is done. Node keeps tearing them down in the background exactly as it would
// have anyway (nothing is force-destroyed, so there is no close-teardown race) — it just stops
// waiting on them to decide the process is finished, so a natural exit follows immediately.
//
// process._getActiveHandles() is a private/undocumented Node API (no public equivalent exists;
// process.getActiveResourcesInfo() is public but returns descriptive strings, not handle
// references, so it cannot be used to unref anything). It has been stable across Node versions
// for a very long time and is the same mechanism debugging tools like wtfnode/why-is-node-running
// rely on. Best-effort: wrapped so a future Node removing/renaming it degrades to "do nothing"
// rather than throwing.
export function unrefLingeringHandles(): void {
  try {
    const handles = (process as unknown as { _getActiveHandles?: () => unknown[] })._getActiveHandles?.();
    if (!Array.isArray(handles)) return;
    for (const h of handles) {
      try {
        (h as { unref?: () => void }).unref?.();
      } catch {
        // best-effort per handle
      }
    }
  } catch {
    // best-effort: never let this stop the hook from exiting
  }
}
