/**
 * Extra footer status line: `sid:xxxxxxxx · up 12m · compact×2` (dim).
 *
 * ctx.ui.setStatus() renders each entry on a dedicated footer line
 * (FooterComponent.render -> lines.push(statusLine)), so this is the built-in way
 * to add a line without replacing the whole footer. The built-in footer does not
 * dim status text and setStatus() receives no theme object, so dim styling is
 * applied here with raw SGR codes.
 *
 * session_start covers startup / reload / new / resume / fork, so state is reset
 * there rather than once at load: every one of those switches the session.
 * Compactions already stored in a resumed session are counted from the branch, so
 * the counter survives resume/fork instead of starting at zero.
 *
 * `since` is the time since your last submitted message: `input` is the precise
 * signal for it (the editor's submit), and on resume the timestamp is recovered
 * from the last user message in the branch instead of guessing.
 */

import type { ExtensionAPI, ExtensionContext, SessionEntry } from "@earendil-works/pi-coding-agent";

const DIM = "\x1b[2m";
const YELLOW = "\x1b[33m";
const RESET = "\x1b[0m";
const REFRESH_MS = 15_000;

function formatUptime(ms: number): string {
  const totalSeconds = Math.floor(ms / 1000);
  const hours = Math.floor(totalSeconds / 3600);
  const minutes = Math.floor((totalSeconds % 3600) / 60);
  const seconds = totalSeconds % 60;
  const two = (n: number) => String(n).padStart(2, "0");
  return hours > 0 ? `${hours}:${two(minutes)}:${two(seconds)}` : `${minutes}:${two(seconds)}`;
}

function countCompactions(entries: SessionEntry[]): number {
  return entries.filter((entry) => entry.type === "compaction").length;
}

/** Timestamp of the last user message in the branch, or undefined if there is none. */
function lastUserMessageAt(entries: SessionEntry[]): number | undefined {
  const entry = entries.findLast((item) => item.type === "message" && item.message.role === "user");
  if (!entry) return undefined;
  const parsed = Date.parse(entry.timestamp);
  return Number.isNaN(parsed) ? undefined : parsed;
}

export default function (pi: ExtensionAPI): void {
  let startedAt = Date.now();
  let compactions = 0;
  let lastInputAt: number | undefined;
  let timer: ReturnType<typeof setInterval> | undefined;
  let currentCtx: ExtensionContext | undefined;

  const paint = (ctx: ExtensionContext): void => {
    const sid = ctx.sessionManager.getSessionId().slice(0, 8);
    const parts = [`${DIM}sid:${sid}${RESET}`, `${DIM}up ${formatUptime(Date.now() - startedAt)}${RESET}`];
    if (lastInputAt !== undefined) parts.push(`${DIM}since ${formatUptime(Date.now() - lastInputAt)}${RESET}`);
    if (compactions > 0) parts.push(`${YELLOW}compact×${compactions}${RESET}`);
    ctx.ui.setStatus("session-meta", parts.join(`${DIM} · ${RESET}`));
  };

  pi.on("session_start", async (_event, ctx) => {
    if (!ctx.hasUI) return;
    currentCtx = ctx;
    startedAt = Date.now();
    compactions = countCompactions(ctx.sessionManager.getEntries());
    lastInputAt = lastUserMessageAt(ctx.sessionManager.getEntries());
    paint(ctx);
    // setStatus triggers a render, so the uptime ticks without other events.
    if (timer) clearInterval(timer);
    timer = setInterval(() => {
      if (currentCtx) paint(currentCtx);
    }, REFRESH_MS);
    timer.unref?.();
  });

  pi.on("input", async () => {
    lastInputAt = Date.now();
    if (currentCtx) paint(currentCtx);
  });

  pi.on("session_compact", async () => {
    compactions++;
    if (currentCtx) paint(currentCtx);
  });

  pi.on("session_shutdown", async () => {
    if (timer) clearInterval(timer);
    timer = undefined;
    currentCtx = undefined;
  });
}