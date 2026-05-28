/** Tag-vector — flat key=value pairs sent as query params to /v1/conf. */
export type TagVector = Readonly<Record<string, string>>;

/** Response-shape templates (spec §9.1). */
export type Template = "flat" | "dotnet" | "envvar" | "envvar-deep";

/** Options for PetBoxConfigClient. */
export interface PetBoxConfigClientOptions {
	/** Base URL of the PetBox server, e.g. "https://petbox.3po.su". Trailing slash optional. */
	readonly endpoint: string;
	/** Plaintext API key. Sent as X-YobaConf-ApiKey header on every request. */
	readonly apiKey: string;
	/** Tag-vector — every tag the request carries. Resolve finds bindings whose tag-set is a subset. */
	readonly tags: TagVector;
	/** Response template (default: "flat"). Controls the shape of the JSON response. */
	readonly template?: Template;
	/**
	 * Polling interval in ms. Each poll uses If-None-Match for cheap 304s.
	 * Set to 0 to disable polling (one-shot fetch only). Default: 5 minutes.
	 */
	readonly refreshIntervalMs?: number;
	/**
	 * When true, initial fetch failures (network, auth, 409) don't throw — the client
	 * starts with null config and retries on the next poll. Default: false.
	 */
	readonly optional?: boolean;
	/** Custom fetch implementation (for testing / proxy injection). */
	readonly fetchImpl?: typeof fetch;
}

/** Structured error from the PetBox config API. */
export class PetBoxConfigError extends Error {
	readonly status: number;
	readonly body: unknown;

	constructor(message: string, status: number, body: unknown) {
		super(message);
		this.name = "PetBoxConfigError";
		this.status = status;
		this.body = body;
	}
}

/** Events emitted by PetBoxConfigClient. */
export interface PetBoxConfigClientEvents {
	/** Fired when config changes (200 response with new data). */
	change: (config: import("./config.js").ResolvedConfig) => void;
	/** Fired on fetch errors during polling. Initial-fetch errors throw (unless optional). */
	error: (err: unknown) => void;
}

type Listener = (...args: never[]) => void;

/** Minimal typed event emitter — avoids node:events dependency for portability. */
export class TypedEmitter<Events extends { [K in keyof Events]: (...args: never[]) => void }> {
	private readonly listeners = new Map<keyof Events, Set<Listener>>();

	on<E extends keyof Events>(event: E, listener: Events[E]): this {
		let set = this.listeners.get(event);
		if (!set) {
			set = new Set();
			this.listeners.set(event, set);
		}
		set.add(listener as Listener);
		return this;
	}

	off<E extends keyof Events>(event: E, listener: Events[E]): this {
		this.listeners.get(event)?.delete(listener as Listener);
		return this;
	}

	protected emit<E extends keyof Events>(event: E, ...args: Parameters<Events[E]>): void {
		for (const fn of this.listeners.get(event) ?? []) (fn as (...a: Parameters<Events[E]>) => void)(...args);
	}

	protected listenerCount(event: keyof Events): number {
		return this.listeners.get(event)?.size ?? 0;
	}
}
