import { afterEach, beforeEach, describe, expect, test } from "bun:test";
import { mkdtemp, readFile, readdir, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { PetBoxConfigClient } from "../src/index.js";

const BASE = {
	endpoint: "https://petbox.test",
	apiKey: "key",
	tags: { env: "test" } as Record<string, string>,
	refreshIntervalMs: 0,
};

/** A fetchImpl returning a 200 with the given body + ETag, recording the request headers. */
function okFetch(body: unknown, etag: string, sink?: { headers?: Record<string, string> }): typeof fetch {
	return ((_input: string | URL | Request, init?: RequestInit) => {
		if (sink) sink.headers = (init?.headers as Record<string, string>) ?? {};
		return Promise.resolve(new Response(JSON.stringify(body), { status: 200, headers: { ETag: `"${etag}"` } }));
	}) as typeof fetch;
}

/** A fetchImpl that always rejects (server unreachable). */
const downFetch = (() => Promise.reject(new Error("ECONNREFUSED"))) as typeof fetch;

describe("disk cache (cacheDir)", () => {
	let dir: string;

	beforeEach(async () => {
		dir = await mkdtemp(join(tmpdir(), "petbox-cache-"));
	});
	afterEach(async () => {
		await rm(dir, { recursive: true, force: true });
	});

	async function cacheFiles(): Promise<string[]> {
		return (await readdir(dir)).filter((f) => f.endsWith(".json"));
	}

	test("writes a JSON { etag, config, savedAt } file on a successful 200", async () => {
		const client = new PetBoxConfigClient({
			...BASE,
			cacheDir: dir,
			fetchImpl: okFetch({ db: { host: "localhost" } }, "v1"),
		});

		await client.fetch();

		const files = await cacheFiles();
		expect(files.length).toBe(1);
		const parsed = JSON.parse(await readFile(join(dir, files[0] as string), "utf8"));
		expect(parsed.etag).toBe("v1");
		expect(parsed.config).toEqual({ db: { host: "localhost" } });
		expect(typeof parsed.savedAt).toBe("string");
		expect(Number.isNaN(Date.parse(parsed.savedAt))).toBe(false);
	});

	test("leaves no leftover .tmp files (atomic write)", async () => {
		const client = new PetBoxConfigClient({ ...BASE, cacheDir: dir, fetchImpl: okFetch({ a: 1 }, "v1") });
		await client.fetch();
		const leftovers = (await readdir(dir)).filter((f) => f.includes(".tmp"));
		expect(leftovers.length).toBe(0);
	});

	test("seeds If-None-Match from the cached etag on restart", async () => {
		// First process populates the cache.
		await new PetBoxConfigClient({ ...BASE, cacheDir: dir, fetchImpl: okFetch({ v: 1 }, "etag-1") }).fetch();

		// Second (restarted) process should send If-None-Match: "etag-1" on its first request.
		const sink: { headers?: Record<string, string> } = {};
		const client2 = new PetBoxConfigClient({
			...BASE,
			cacheDir: dir,
			fetchImpl: okFetch({ v: 2 }, "etag-2", sink),
		});
		await client2.fetch();

		expect(sink.headers?.["If-None-Match"]).toBe('"etag-1"');
	});

	test("returns cached config when the first fetch fails (optional=false)", async () => {
		await new PetBoxConfigClient({
			...BASE,
			cacheDir: dir,
			fetchImpl: okFetch({ db: { host: "cached-host" } }, "v1"),
		}).fetch();

		const client2 = new PetBoxConfigClient({ ...BASE, cacheDir: dir, fetchImpl: downFetch });
		const cfg = await client2.fetch();

		expect(cfg.get("db.host")).toBe("cached-host");
		expect(cfg.etag).toBe("v1");
	});

	test("cache fallback wins even when server returns an auth error", async () => {
		await new PetBoxConfigClient({ ...BASE, cacheDir: dir, fetchImpl: okFetch({ ok: true }, "v1") }).fetch();

		const failFetch = (() =>
			Promise.resolve(new Response(JSON.stringify({ error: "unauthorized" }), { status: 401 }))) as typeof fetch;
		const client2 = new PetBoxConfigClient({ ...BASE, cacheDir: dir, fetchImpl: failFetch });

		const cfg = await client2.fetch();
		expect(cfg.getBoolean("ok")).toBe(true);
	});

	test("no cache + failing fetch preserves prior behavior (throws)", async () => {
		const client = new PetBoxConfigClient({ ...BASE, cacheDir: dir, fetchImpl: downFetch });
		await expect(client.fetch()).rejects.toThrow();
	});

	test("no cache + failing fetch + optional=true returns empty config", async () => {
		const client = new PetBoxConfigClient({ ...BASE, cacheDir: dir, fetchImpl: downFetch, optional: true });
		const cfg = await client.fetch();
		expect(cfg.get("anything")).toBeUndefined();
	});

	test("304 on restart returns the cached config body", async () => {
		await new PetBoxConfigClient({
			...BASE,
			cacheDir: dir,
			fetchImpl: okFetch({ db: { host: "from-cache" } }, "v1"),
		}).fetch();

		const notModified = (() =>
			Promise.resolve(new Response(null, { status: 304, headers: { ETag: '"v1"' } }))) as typeof fetch;
		const client2 = new PetBoxConfigClient({ ...BASE, cacheDir: dir, fetchImpl: notModified });

		const cfg = await client2.fetch();
		expect(cfg.get("db.host")).toBe("from-cache");
		expect(cfg.etag).toBe("v1");
	});

	test("corrupt cache file is treated as absent", async () => {
		// Prime the cache to learn the exact file name, then corrupt it.
		await new PetBoxConfigClient({ ...BASE, cacheDir: dir, fetchImpl: okFetch({ v: 1 }, "v1") }).fetch();
		const [file] = await cacheFiles();
		await writeFile(join(dir, file as string), "{ this is not json", "utf8");

		// With a corrupt cache and a down server, there is no LKG → previous behavior (throw).
		const client2 = new PetBoxConfigClient({ ...BASE, cacheDir: dir, fetchImpl: downFetch });
		await expect(client2.fetch()).rejects.toThrow();

		// And no stale If-None-Match is sent when the server is up again.
		const sink: { headers?: Record<string, string> } = {};
		const client3 = new PetBoxConfigClient({ ...BASE, cacheDir: dir, fetchImpl: okFetch({ v: 2 }, "v2", sink) });
		await client3.fetch();
		expect(sink.headers?.["If-None-Match"]).toBeUndefined();
	});

	test("distinct tag-vectors use distinct cache files in the same dir", async () => {
		await new PetBoxConfigClient({
			...BASE,
			tags: { env: "test", project: "a" },
			cacheDir: dir,
			fetchImpl: okFetch({ v: 1 }, "v1"),
		}).fetch();
		await new PetBoxConfigClient({
			...BASE,
			tags: { env: "test", project: "b" },
			cacheDir: dir,
			fetchImpl: okFetch({ v: 2 }, "v2"),
		}).fetch();

		expect((await cacheFiles()).length).toBe(2);
	});

	test("no cacheDir => no files written", async () => {
		const client = new PetBoxConfigClient({ ...BASE, fetchImpl: okFetch({ v: 1 }, "v1") });
		await client.fetch();
		expect((await readdir(dir)).length).toBe(0);
	});

	test("write failure (unwritable dir) does not break fetch", async () => {
		// A path whose parent is a file, not a directory → mkdir/rename fail; fetch must still succeed.
		const filePath = join(dir, "not-a-dir");
		await writeFile(filePath, "x", "utf8");
		const client = new PetBoxConfigClient({
			...BASE,
			cacheDir: join(filePath, "sub"),
			fetchImpl: okFetch({ ok: true }, "v1"),
		});
		const cfg = await client.fetch();
		expect(cfg.getBoolean("ok")).toBe(true);
	});
});

describe("timeoutMs", () => {
	test("passes an AbortSignal configured with the timeout to fetch", async () => {
		let captured: AbortSignal | undefined;
		const capFetch = ((_input: string | URL | Request, init?: RequestInit) => {
			captured = init?.signal ?? undefined;
			return Promise.resolve(new Response("{}", { status: 200 }));
		}) as typeof fetch;

		const client = new PetBoxConfigClient({ ...BASE, timeoutMs: 30, fetchImpl: capFetch });
		await client.fetch();

		expect(captured instanceof AbortSignal).toBe(true);

		// The signal aborts on the configured timeout (keepalive so bun pumps the timer).
		const keep = setInterval(() => {}, 5);
		await new Promise<void>((resolve) => {
			const sig = captured as AbortSignal;
			if (sig.aborted) return resolve();
			sig.addEventListener("abort", () => resolve());
		});
		clearInterval(keep);

		expect(captured?.aborted).toBe(true);
		expect((captured?.reason as Error).name).toBe("TimeoutError");
	});

	test("a timed-out (rejecting) fetch falls back to the disk cache", async () => {
		const dir = await mkdtemp(join(tmpdir(), "petbox-cache-to-"));
		try {
			await new PetBoxConfigClient({
				...BASE,
				cacheDir: dir,
				fetchImpl: okFetch({ v: "lkg" }, "v1"),
			}).fetch();

			// A real timeout surfaces as a rejected fetch (aborted) — same path as downFetch.
			const client2 = new PetBoxConfigClient({ ...BASE, cacheDir: dir, timeoutMs: 5, fetchImpl: downFetch });
			const cfg = await client2.fetch();
			expect(cfg.get("v")).toBe("lkg");
		} finally {
			await rm(dir, { recursive: true, force: true });
		}
	});
});
