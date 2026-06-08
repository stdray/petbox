import { ResolvedConfig } from "./config.js";
import {
	type PetBoxConfigClientEvents,
	type PetBoxConfigClientOptions,
	PetBoxConfigError,
	type TagVector,
	type Template,
	TypedEmitter,
} from "./types.js";

const DEFAULT_REFRESH_MS = 5 * 60 * 1000;

/**
 * TypeScript SDK client for PetBox config.
 *
 * Fetches resolved config from /v1/conf with ETag-aware polling. Supports all four
 * response templates (flat, dotnet, envvar, envvar-deep).
 *
 * @example
 * ```ts
 * const client = new PetBoxConfigClient({
 *   endpoint: 'https://petbox.3po.su',
 *   apiKey: process.env.PETBOX_API_KEY!,
 *   tags: { env: 'prod', project: 'kpvotes' },
 * });
 *
 * const config = await client.start();
 * console.log(config.get('db.host'));
 *
 * client.on('change', (cfg) => console.log('config updated', cfg.data));
 * ```
 */
export class PetBoxConfigClient extends TypedEmitter<PetBoxConfigClientEvents> {
	private readonly options: {
		readonly endpoint: string;
		readonly apiKey: string;
		readonly tags: TagVector;
		readonly template: Template;
		readonly refreshIntervalMs: number;
		readonly optional: boolean;
		readonly fetchImpl: typeof fetch | undefined;
	};
	private readonly fetchImpl: typeof fetch;
	private currentConfig: ResolvedConfig | null = null;
	private etag: string | null = null;
	private timer: ReturnType<typeof setInterval> | null = null;
	private disposed = false;

	constructor(options: PetBoxConfigClientOptions) {
		super();
		if (!options.endpoint) throw new TypeError("endpoint is required");
		if (!options.apiKey) throw new TypeError("apiKey is required");
		if (!options.tags || Object.keys(options.tags).length === 0) throw new TypeError("at least one tag is required");

		this.options = {
			endpoint: options.endpoint,
			apiKey: options.apiKey,
			tags: options.tags,
			template: options.template ?? "flat",
			refreshIntervalMs: options.refreshIntervalMs ?? DEFAULT_REFRESH_MS,
			optional: options.optional ?? false,
			fetchImpl: options.fetchImpl,
		};
		this.fetchImpl = options.fetchImpl ?? ((...args: Parameters<typeof fetch>) => globalThis.fetch(...args));
	}

	/** The most recently fetched config, or null if never fetched. */
	get current(): ResolvedConfig | null {
		return this.currentConfig;
	}

	/**
	 * One-shot fetch. Does NOT start background polling.
	 * Throws on auth errors, network errors, and 409 conflicts (unless optional=true).
	 */
	async fetch(): Promise<ResolvedConfig> {
		this.ensureNotDisposed();
		try {
			const cfg = await this.fetchOnce();
			this.currentConfig = cfg;
			this.etag = cfg.etag;
			return cfg;
		} catch (err) {
			if (this.options.optional) return new ResolvedConfig({}, null);
			throw err;
		}
	}

	/**
	 * Initial fetch + start background ETag polling. Returns the first resolved config.
	 * Fires 'change' events on subsequent updates, 'error' on polling failures.
	 */
	async start(): Promise<ResolvedConfig> {
		const cfg = await this.fetch();
		this.startPolling();
		return cfg;
	}

	/** Stop background polling. Does NOT clear the last config. */
	stop(): void {
		if (this.timer !== null) {
			clearInterval(this.timer);
			this.timer = null;
		}
	}

	/** Stop polling and remove all listeners. */
	dispose(): void {
		this.disposed = true;
		this.stop();
	}

	// ── internals ──────────────────────────────────────────

	private ensureNotDisposed(): void {
		if (this.disposed) throw new Error("PetBoxConfigClient is disposed");
	}

	private buildUrl(): string {
		const base = this.options.endpoint.endsWith("/") ? this.options.endpoint : `${this.options.endpoint}/`;
		const params = new URLSearchParams();
		// Sort for stable URLs (helps caching / log grepping).
		const sortedKeys = Object.keys(this.options.tags).sort();
		for (const k of sortedKeys) params.set(k, this.options.tags[k] ?? "");
		if (this.options.template !== "flat") params.set("template", this.options.template);
		return `${base}v1/conf?${params.toString()}`;
	}

	private async fetchOnce(): Promise<ResolvedConfig> {
		const url = this.buildUrl();
		const headers: Record<string, string> = {
			"X-YobaConf-ApiKey": this.options.apiKey,
		};
		if (this.etag !== null) headers["If-None-Match"] = `"${this.etag}"`;

		let response: Response;
		try {
			response = await this.fetchImpl(url, { headers });
		} catch (cause) {
			throw new PetBoxConfigError(`Failed to reach PetBox at ${url}: ${String(cause)}`, 0, null);
		}

		// 304 — unchanged. Return last-known-good config.
		if (response.status === 304) {
			const newEtag = this.stripEtag(response.headers.get("ETag"));
			return new ResolvedConfig(this.currentConfig?.data ?? {}, newEtag ?? this.etag);
		}

		const body = await response.text();

		if (!response.ok) {
			let parsed: unknown = null;
			try {
				parsed = JSON.parse(body);
			} catch {
				/* not JSON */
			}
			throw new PetBoxConfigError(this.errorMessage(response.status, parsed), response.status, parsed);
		}

		const etag = this.stripEtag(response.headers.get("ETag"));
		const data: unknown = JSON.parse(body);
		return new ResolvedConfig(data, etag);
	}

	private startPolling(): void {
		if (this.options.refreshIntervalMs <= 0) return;
		if (this.timer !== null) return;
		this.timer = setInterval(() => {
			this.poll().catch(() => {
				/* error emitted inside poll */
			});
		}, this.options.refreshIntervalMs);
	}

	private async poll(): Promise<void> {
		try {
			const cfg = await this.fetchOnce();
			if (cfg.etag !== this.etag) {
				this.currentConfig = cfg;
				this.etag = cfg.etag;
				this.emit("change", cfg);
			}
		} catch (err) {
			this.emit("error", err);
		}
	}

	private stripEtag(raw: string | null): string | null {
		if (raw === null) return null;
		// Server sends `"<hex>"` — strip quotes.
		return raw.startsWith('"') && raw.endsWith('"') ? raw.slice(1, -1) : raw;
	}

	private errorMessage(status: number, body: unknown): string {
		if (body !== null && typeof body === "object" && "error" in body) {
			const b = body as Record<string, unknown>;
			const reason = typeof b["reason"] === "string" ? `: ${b["reason"]}` : "";
			return `${b["error"]}${reason}`;
		}
		return `HTTP ${status}`;
	}
}

/**
 * Convenience: one-shot fetch without creating a client instance.
 * Same as `new PetBoxConfigClient(opts).fetch()`.
 */
export const fetchConfig = (options: PetBoxConfigClientOptions): Promise<ResolvedConfig> =>
	new PetBoxConfigClient(options).fetch();
