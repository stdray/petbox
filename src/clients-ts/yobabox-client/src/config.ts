/**
 * Immutable resolved config from a YobaBox fetch.
 *
 * For "flat" template: data is a nested JSON tree; `get("db.host")` traverses it.
 * For "dotnet" / "envvar" / "envvar-deep": data is a flat `Record<string, string>`;
 * `get("DB_HOST")` does a direct key lookup.
 */
export class ResolvedConfig {
	readonly etag: string | null;
	private readonly raw: unknown;

	constructor(raw: unknown, etag: string | null) {
		this.raw = raw;
		this.etag = etag;
	}

	/** The raw parsed JSON response body. */
	get data(): unknown {
		return this.raw;
	}

	/**
	 * Returns the string value at the given dotted path (flat template) or direct key (other templates).
	 * Returns undefined if the path doesn't exist.
	 */
	get(path: string): string | undefined {
		const v = this.resolve(path);
		if (v === undefined || v === null) return undefined;
		if (typeof v === "string") return v;
		return String(v);
	}

	/**
	 * Returns the numeric value at the given path, or undefined if missing / not a number.
	 * Accepts JSON numbers and numeric strings.
	 */
	getNumber(path: string): number | undefined {
		const v = this.resolve(path);
		if (v === undefined || v === null) return undefined;
		if (typeof v === "number") return Number.isFinite(v) ? v : undefined;
		if (typeof v === "string") {
			const n = Number(v);
			return Number.isFinite(n) ? n : undefined;
		}
		return undefined;
	}

	/**
	 * Returns the boolean value at the given path, or undefined if missing.
	 * Accepts JSON booleans and the strings "true"/"false" (case-insensitive).
	 */
	getBoolean(path: string): boolean | undefined {
		const v = this.resolve(path);
		if (v === undefined || v === null) return undefined;
		if (typeof v === "boolean") return v;
		if (typeof v === "string") {
			const lower = v.toLowerCase();
			if (lower === "true") return true;
			if (lower === "false") return false;
		}
		return undefined;
	}

	/**
	 * Returns the JSON value at the given path (object, array, scalar — whatever the server returned).
	 * Returns undefined if the path doesn't exist.
	 */
	getJson<T = unknown>(path: string): T | undefined {
		return this.resolve(path) as T | undefined;
	}

	/**
	 * Flattens the config into a `Record<string, string>` suitable for process env export.
	 * For flat template: expands nested objects to dotted keys. For other templates: returns as-is.
	 */
	toEnv(): Record<string, string> {
		if (this.raw === null || this.raw === undefined) return {};
		if (typeof this.raw !== "object") return {};
		const out: Record<string, string> = {};
		this.flatten(this.raw as Record<string, unknown>, "", out);
		return out;
	}

	private resolve(path: string): unknown {
		if (this.raw === null || this.raw === undefined) return undefined;
		if (typeof this.raw !== "object") return undefined;

		// If the raw data is a flat dictionary (dotnet/envvar/envvar-deep templates),
		// the keys don't contain dots — do a direct lookup.
		const obj = this.raw as Record<string, unknown>;
		if (!path.includes(".")) return obj[path];

		// Dotted path — traverse nested object (flat template).
		const segments = path.split(".");
		let current: unknown = obj;
		for (const seg of segments) {
			if (current === null || current === undefined) return undefined;
			if (typeof current !== "object") return undefined;
			current = (current as Record<string, unknown>)[seg];
		}
		return current;
	}

	private flatten(obj: Record<string, unknown>, prefix: string, out: Record<string, string>): void {
		for (const [k, v] of Object.entries(obj)) {
			const key = prefix ? `${prefix}.${k}` : k;
			if (v !== null && v !== undefined && typeof v === "object" && !Array.isArray(v)) {
				this.flatten(v as Record<string, unknown>, key, out);
			} else {
				out[key] = v === null || v === undefined ? "" : String(v);
			}
		}
	}
}
