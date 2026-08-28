// editor-preview-renders-server-side, the RATCHET. The card's rejected alternative was "catch the
// client pipeline up", refused because parity between two implementations is a permanent tax. A
// test that only checks today's output would not stop someone re-adding a client-side renderer
// tomorrow and paying that tax again — so pin the STRUCTURAL fact instead: this app ships exactly
// one markdown pipeline, and it is the server's.

import { describe, expect, test } from "bun:test";
import { existsSync, readFileSync, readdirSync } from "node:fs";
import { join } from "node:path";

const webDir = join(import.meta.dir, "..");
const tsDir = join(webDir, "ts");

describe("one markdown pipeline", () => {
	test("ts/markdown.ts is gone and nothing imports it", () => {
		expect(existsSync(join(tsDir, "markdown.ts"))).toBe(false);

		for (const file of readdirSync(tsDir).filter((f) => f.endsWith(".ts"))) {
			const src = readFileSync(join(tsDir, file), "utf8");
			// Comments may (and do) explain what was removed; imports may not bring it back.
			expect(src).not.toMatch(/^\s*import[^\n]*["']\.\/markdown["']/m);
		}
	});

	test("no client-side markdown renderer is a dependency any more", () => {
		const pkg = JSON.parse(readFileSync(join(webDir, "package.json"), "utf8")) as {
			dependencies?: Record<string, string>;
			devDependencies?: Record<string, string>;
		};
		const all = { ...pkg.dependencies, ...pkg.devDependencies };
		// Orphaned deps are invisible to biome and to inspectcode alike — only this notices.
		expect(all["marked"]).toBeUndefined();
		expect(all["dompurify"]).toBeUndefined();
	});
});
