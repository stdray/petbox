/**
 * Last-known-good (LKG) disk cache for resolved config.
 *
 * After a datacenter outage a config-driven process must be able to restart even while
 * the PetBox server is unreachable: the client seeds itself from the last config it saw
 * on disk. The cache is:
 *
 *  - opt-in (only active when `cacheDir` is set);
 *  - Node-only — in a browser (no `globalThis.process`) every operation silently no-ops;
 *  - atomic on write (temp file + rename), and never throws (write failures are swallowed);
 *  - self-healing — a corrupt or unparseable file is treated as absent.
 *
 * File format: `{ "etag": string|null, "config": <resolved body>, "savedAt": ISO-8601 }`.
 * File name inside `cacheDir` is a deterministic hash of endpoint + template + tags, so a
 * single directory can host caches for many distinct clients without collisions.
 */

/** On-disk cache record. */
export interface ConfigCacheEntry {
	/** ETag of the cached config, used for the first If-None-Match on restart. */
	readonly etag: string | null;
	/** The raw resolved config body (whatever the server returned). */
	readonly config: unknown;
	/** ISO-8601 timestamp of when this entry was written. */
	readonly savedAt: string;
}

/** Minimal slice of `node:fs/promises` we depend on (kept local to avoid a @types/node dep). */
interface FsLike {
	readFile(path: string, encoding: "utf8"): Promise<string>;
	writeFile(path: string, data: string, encoding: "utf8"): Promise<void>;
	rename(from: string, to: string): Promise<void>;
	mkdir(path: string, options: { recursive: boolean }): Promise<string | undefined>;
}

/** Minimal slice of `node:path`. */
interface PathLike {
	join(...parts: string[]): string;
}

interface NodeProcess {
	versions?: { node?: string };
	pid?: number;
}

function getProcess(): NodeProcess | undefined {
	return (globalThis as { process?: NodeProcess }).process;
}

/** True only in a Node-like runtime (has `process.versions.node`). */
function nodeAvailable(): boolean {
	const p = getProcess();
	return typeof p !== "undefined" && p.versions?.node != null;
}

/**
 * FNV-1a (32-bit) hash → 8-hex-char string. Deterministic and dependency-free; used only to
 * derive a stable cache file name from client identity, so cryptographic strength is irrelevant.
 */
function fnv1a(input: string): string {
	let h = 0x811c9dc5;
	for (let i = 0; i < input.length; i++) {
		h ^= input.charCodeAt(i);
		h = Math.imul(h, 0x01000193);
	}
	return (h >>> 0).toString(16).padStart(8, "0");
}

function isCacheEntry(value: unknown): value is ConfigCacheEntry {
	if (value === null || typeof value !== "object") return false;
	const v = value as Record<string, unknown>;
	if (!("config" in v)) return false;
	if (typeof v["savedAt"] !== "string") return false;
	const etag = v["etag"];
	return etag === null || typeof etag === "string";
}

/**
 * Disk-backed LKG cache. All operations are lazy: the `node:fs`/`node:path` modules are only
 * imported on first use, and in a non-Node runtime every method resolves to a no-op / null.
 */
export class ConfigCache {
	private readonly dir: string;
	private readonly fileName: string;
	private resolved: { fs: FsLike; filePath: string } | null = null;
	private initFailed = false;

	/**
	 * @param dir Directory to hold cache files (created on demand).
	 * @param identity Stable client identity (endpoint + template + tags); hashed into the file name.
	 */
	constructor(dir: string, identity: string) {
		this.dir = dir;
		this.fileName = `petbox-config-${fnv1a(identity)}.json`;
	}

	private async ensure(): Promise<{ fs: FsLike; filePath: string } | null> {
		if (this.resolved) return this.resolved;
		if (this.initFailed || !nodeAvailable()) return null;
		try {
			// Non-literal specifiers keep TS from requiring @types/node at build time, and keep
			// bundlers from statically pulling node builtins into browser output.
			const fsSpec = "node:fs/promises";
			const pathSpec = "node:path";
			const [fsMod, pathMod] = await Promise.all([
				import(fsSpec as string) as Promise<unknown>,
				import(pathSpec as string) as Promise<unknown>,
			]);
			const fs = fsMod as FsLike;
			const path = pathMod as PathLike;
			this.resolved = { fs, filePath: path.join(this.dir, this.fileName) };
			return this.resolved;
		} catch {
			this.initFailed = true;
			return null;
		}
	}

	/** Read the cached entry, or null if absent / corrupt / not in Node. Never throws. */
	async read(): Promise<ConfigCacheEntry | null> {
		const r = await this.ensure();
		if (!r) return null;
		try {
			const text = await r.fs.readFile(r.filePath, "utf8");
			const parsed: unknown = JSON.parse(text);
			return isCacheEntry(parsed) ? parsed : null;
		} catch {
			return null;
		}
	}

	/** Atomically write the entry (temp file + rename). Never throws. */
	async write(entry: ConfigCacheEntry): Promise<void> {
		const r = await this.ensure();
		if (!r) return;
		try {
			await r.fs.mkdir(this.dir, { recursive: true });
			const pid = getProcess()?.pid ?? 0;
			const tmp = `${r.filePath}.tmp-${pid}-${Date.now()}-${Math.floor(Math.random() * 1e6)}`;
			await r.fs.writeFile(tmp, JSON.stringify(entry), "utf8");
			await r.fs.rename(tmp, r.filePath);
		} catch {
			/* cache write failures must never break the caller */
		}
	}
}
