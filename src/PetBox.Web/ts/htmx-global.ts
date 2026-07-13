import htmx from "htmx.org";

// htmx.org's package entry is the ESM build: importing it wires htmx up to the document but does NOT
// publish `window.htmx`. Two things need that global and both were silently dead without it:
//
//   * the SSE extension (htmx.org/dist/ext/sse.js) is a plain script that calls
//     `htmx.defineExtension(...)` against the GLOBAL — bundled without one it throws on load;
//   * logs.ts calls `htmx.process()` on the live-tail container it creates at runtime, so htmx sees
//     an element that was never in the initial DOM. It used `(window as any).htmx?.process(...)` —
//     with the global undefined, the optional chain made that a silent no-op.
//
// This module exists to run the assignment BEFORE the extension module is evaluated: ESM evaluates
// imports depth-first in source order, so `import "./htmx-global"` ahead of the extension import in
// site.ts is the ordering guarantee. A statement in site.ts could not do it — import evaluation is
// hoisted above the module body.
declare global {
	interface Window {
		htmx: typeof htmx;
	}
}

window.htmx = htmx;
