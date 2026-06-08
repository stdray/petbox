/**
 * TypeScript client for the PetBox Data module — raw parameterized SQL over HTTP plus DataDb
 * provisioning. Mirrors the .NET PetBoxDataClient. The server is a dumb pass-through: it knows
 * nothing about your types, it just runs the SQL against the project's SQLite file.
 *
 * @example
 * ```ts
 * const data = new PetBoxDataClient({ endpoint: "https://petbox.3po.su", apiKey: process.env.PETBOX_API_KEY! });
 * await data.createDb("kpvotes", "cache");
 * await data.exec("kpvotes", "cache", "INSERT INTO votes (id, film) VALUES (@id, @film)",
 *   [{ name: "@id", value: 1 }, { name: "@film", value: "Matrix" }]);
 * const rows = await data.query<{ id: number; film: string }>("kpvotes", "cache", "SELECT * FROM votes");
 * ```
 */

/** A single parameter for a parameterized query/exec. `name` matches the SQL placeholder (e.g. "@id"). */
export interface PetBoxSqlParam {
	readonly name: string;
	readonly value: unknown;
	readonly dbType?: string;
}

/** Options for PetBoxDataClient. */
export interface PetBoxDataClientOptions {
	/** Base URL of the PetBox server, e.g. "https://petbox.3po.su". Trailing slash optional. */
	readonly endpoint: string;
	/** Plaintext API key. Sent as the X-Api-Key header on every request. */
	readonly apiKey: string;
	/** Custom fetch implementation (for testing / proxy injection). */
	readonly fetchImpl?: typeof fetch;
}

/** Per-request options. */
export interface PetBoxRequestOptions {
	/** Server-side command timeout in seconds (capped at 300 by the server). */
	readonly timeoutSeconds?: number;
}

/** Structured error from a PetBox API call (non-2xx response or a transport failure with status 0). */
export class PetBoxError extends Error {
	readonly status: number;
	readonly body: unknown;

	constructor(message: string, status: number, body: unknown) {
		super(message);
		this.name = "PetBoxError";
		this.status = status;
		this.body = body;
	}
}

const enc = encodeURIComponent;

export class PetBoxDataClient {
	private readonly base: string;
	private readonly apiKey: string;
	private readonly fetchImpl: typeof fetch;

	constructor(options: PetBoxDataClientOptions) {
		if (!options.endpoint) throw new TypeError("endpoint is required");
		if (!options.apiKey) throw new TypeError("apiKey is required");
		this.base = options.endpoint.endsWith("/") ? options.endpoint : `${options.endpoint}/`;
		this.apiKey = options.apiKey;
		this.fetchImpl = options.fetchImpl ?? ((...args: Parameters<typeof fetch>) => globalThis.fetch(...args));
	}

	/** Runs a SELECT and returns the rows as objects keyed by column name. */
	async query<T = Record<string, unknown>>(
		projectKey: string,
		dbName: string,
		sql: string,
		params?: readonly PetBoxSqlParam[],
		opts?: PetBoxRequestOptions,
	): Promise<T[]> {
		const res = await this.send(`api/data/${enc(projectKey)}/${enc(dbName)}/query`, { sql, params }, opts);
		const body = await this.readJson(res);
		return (body ?? []) as T[];
	}

	/** Runs an INSERT/UPDATE/DELETE/DDL/PRAGMA and returns the affected row count. */
	async exec(
		projectKey: string,
		dbName: string,
		sql: string,
		params?: readonly PetBoxSqlParam[],
		opts?: PetBoxRequestOptions,
	): Promise<number> {
		const res = await this.send(`api/data/${enc(projectKey)}/${enc(dbName)}/exec`, { sql, params }, opts);
		const body = (await this.readJson(res)) as { affected?: number } | null;
		return body?.affected ?? 0;
	}

	/** Creates a DataDb. `maxPageCount` caps the file size (pages × 4KB); omit for the server default. */
	async createDb(
		projectKey: string,
		name: string,
		opts?: { readonly description?: string; readonly maxPageCount?: number },
	): Promise<void> {
		const res = await this.send(`api/data/${enc(projectKey)}/dbs`, {
			name,
			description: opts?.description,
			maxPageCount: opts?.maxPageCount,
		});
		await this.ensureOk(res);
	}

	/** Applies a named migration to a DataDb. Idempotent: same name+sql is a no-op; same name, different sql is a 409. */
	async applySchema(projectKey: string, dbName: string, migrationName: string, sql: string): Promise<void> {
		const res = await this.send(`api/data/${enc(projectKey)}/${enc(dbName)}/schema`, { name: migrationName, sql });
		await this.ensureOk(res);
	}

	// ── internals ──────────────────────────────────────────

	private async send(path: string, body: Record<string, unknown>, opts?: PetBoxRequestOptions): Promise<Response> {
		const headers: Record<string, string> = {
			"X-Api-Key": this.apiKey,
			"Content-Type": "application/json",
		};
		if (opts?.timeoutSeconds !== undefined) headers["X-PetBox-Timeout-Seconds"] = String(opts.timeoutSeconds);
		try {
			return await this.fetchImpl(`${this.base}${path}`, {
				method: "POST",
				headers,
				body: JSON.stringify(body),
			});
		} catch (cause) {
			throw new PetBoxError(`Failed to reach PetBox at ${this.base}${path}: ${String(cause)}`, 0, null);
		}
	}

	private async readJson(res: Response): Promise<unknown> {
		await this.ensureOk(res);
		const text = await res.text();
		return text ? (JSON.parse(text) as unknown) : null;
	}

	private async ensureOk(res: Response): Promise<void> {
		if (res.ok) return;
		const text = await res.text();
		let parsed: unknown = text;
		try {
			parsed = JSON.parse(text);
		} catch {
			/* not JSON — keep raw text */
		}
		const reason =
			parsed !== null && typeof parsed === "object" && "error" in parsed
				? `: ${String((parsed as Record<string, unknown>)["error"])}`
				: "";
		throw new PetBoxError(`PetBox request failed: HTTP ${res.status}${reason}`, res.status, parsed);
	}
}
