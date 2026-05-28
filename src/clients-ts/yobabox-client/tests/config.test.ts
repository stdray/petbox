import { describe, expect, test } from "bun:test";
import { ResolvedConfig } from "../src/config.js";

describe("ResolvedConfig.get", () => {
	test("flat template: traverses dotted path into nested object", () => {
		const cfg = new ResolvedConfig({ db: { host: "localhost" } }, null);
		expect(cfg.get("db.host")).toBe("localhost");
	});

	test("flat template: returns undefined for missing path", () => {
		const cfg = new ResolvedConfig({ db: { host: "localhost" } }, null);
		expect(cfg.get("db.missing")).toBeUndefined();
	});

	test("flat template: returns undefined when traversing into non-object", () => {
		const cfg = new ResolvedConfig({ db: "string-value" }, null);
		expect(cfg.get("db.host")).toBeUndefined();
	});

	test("dotnet template: direct key lookup without traversal", () => {
		const cfg = new ResolvedConfig({ "Db:Host": "localhost" }, null);
		expect(cfg.get("Db:Host")).toBe("localhost");
	});

	test("coerces non-string scalars to string", () => {
		const cfg = new ResolvedConfig({ port: 8080, enabled: true }, null);
		expect(cfg.get("port")).toBe("8080");
		expect(cfg.get("enabled")).toBe("true");
	});

	test("returns undefined for null value", () => {
		const cfg = new ResolvedConfig({ x: null }, null);
		expect(cfg.get("x")).toBeUndefined();
	});
});

describe("ResolvedConfig.getNumber", () => {
	test("parses JSON number", () => {
		const cfg = new ResolvedConfig({ port: 8080 }, null);
		expect(cfg.getNumber("port")).toBe(8080);
	});

	test("parses numeric string", () => {
		const cfg = new ResolvedConfig({ port: "8080" }, null);
		expect(cfg.getNumber("port")).toBe(8080);
	});

	test("returns undefined for non-numeric string", () => {
		const cfg = new ResolvedConfig({ port: "abc" }, null);
		expect(cfg.getNumber("port")).toBeUndefined();
	});

	test("returns undefined for missing path", () => {
		const cfg = new ResolvedConfig({}, null);
		expect(cfg.getNumber("port")).toBeUndefined();
	});
});

describe("ResolvedConfig.getBoolean", () => {
	test("parses JSON boolean", () => {
		const cfg = new ResolvedConfig({ enabled: true }, null);
		expect(cfg.getBoolean("enabled")).toBe(true);
	});

	test('parses "true"/"false" strings case-insensitively', () => {
		const cfg = new ResolvedConfig({ a: "true", b: "FALSE", c: "True" }, null);
		expect(cfg.getBoolean("a")).toBe(true);
		expect(cfg.getBoolean("b")).toBe(false);
		expect(cfg.getBoolean("c")).toBe(true);
	});

	test("returns undefined for non-boolean string", () => {
		const cfg = new ResolvedConfig({ x: "yes" }, null);
		expect(cfg.getBoolean("x")).toBeUndefined();
	});
});

describe("ResolvedConfig.toEnv", () => {
	test("flattens nested objects to dotted keys", () => {
		const cfg = new ResolvedConfig({ db: { host: "localhost", port: 5432 } }, null);
		const env = cfg.toEnv();
		expect(env["db.host"]).toBe("localhost");
		expect(env["db.port"]).toBe("5432");
	});

	test("returns empty for non-object data", () => {
		expect(new ResolvedConfig(null, null).toEnv()).toEqual({});
		expect(new ResolvedConfig("scalar", null).toEnv()).toEqual({});
	});
});

describe("ResolvedConfig metadata", () => {
	test("etag is preserved", () => {
		const cfg = new ResolvedConfig({}, "abc123");
		expect(cfg.etag).toBe("abc123");
	});

	test("raw data is preserved", () => {
		const raw = { a: 1 };
		const cfg = new ResolvedConfig(raw, null);
		expect(cfg.data).toBe(raw);
	});
});
