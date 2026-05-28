import { describe, expect, test } from "bun:test";
import { YobaBoxConfigClient, YobaBoxConfigError } from "../src/index.js";

describe("YobaBoxConfigClient constructor", () => {
	test("requires endpoint", () => {
		expect(
			() =>
				new YobaBoxConfigClient({
					endpoint: "",
					apiKey: "key",
					tags: { env: "test" },
				}),
		).toThrow(TypeError);
	});

	test("requires apiKey", () => {
		expect(
			() =>
				new YobaBoxConfigClient({
					endpoint: "https://yobabox.test",
					apiKey: "",
					tags: { env: "test" },
				}),
		).toThrow(TypeError);
	});

	test("requires at least one tag", () => {
		expect(
			() =>
				new YobaBoxConfigClient({
					endpoint: "https://yobabox.test",
					apiKey: "key",
					tags: {},
				}),
		).toThrow(TypeError);
	});
});

describe("YobaBoxConfigClient.fetch", () => {
	test("returns ResolvedConfig on 200", async () => {
		const fetchImpl = ((_input: string | URL | Request, _init?: RequestInit) =>
			Promise.resolve(
				new Response(JSON.stringify({ db: { host: "localhost" } }), {
					status: 200,
					headers: { ETag: '"abc"' },
				}),
			)) as typeof fetch;

		const client = new YobaBoxConfigClient({
			endpoint: "https://yobabox.test",
			apiKey: "key",
			tags: { env: "test" },
			fetchImpl,
			refreshIntervalMs: 0,
		});

		const cfg = await client.fetch();
		expect(cfg.get("db.host")).toBe("localhost");
		expect(cfg.etag).toBe("abc");
	});

	test("throws YobaBoxConfigError on 4xx", async () => {
		const fetchImpl = ((_input: string | URL | Request, _init?: RequestInit) =>
			Promise.resolve(
				new Response(JSON.stringify({ error: "unauthorized", reason: "bad key" }), {
					status: 401,
				}),
			)) as typeof fetch;

		const client = new YobaBoxConfigClient({
			endpoint: "https://yobabox.test",
			apiKey: "key",
			tags: { env: "test" },
			fetchImpl,
			refreshIntervalMs: 0,
		});

		await expect(client.fetch()).rejects.toThrow(YobaBoxConfigError);
	});

	test("optional=true swallows errors and returns empty config", async () => {
		const fetchImpl = ((_input: string | URL | Request, _init?: RequestInit) =>
			Promise.resolve(new Response("nope", { status: 500 }))) as typeof fetch;

		const client = new YobaBoxConfigClient({
			endpoint: "https://yobabox.test",
			apiKey: "key",
			tags: { env: "test" },
			fetchImpl,
			refreshIntervalMs: 0,
			optional: true,
		});

		const cfg = await client.fetch();
		expect(cfg.get("anything")).toBeUndefined();
	});

	test("sends X-YobaConf-ApiKey header and tags in query string", async () => {
		let capturedUrl = "";
		let capturedHeaders: Record<string, string> = {};
		const fetchImpl = ((input: string | URL | Request, init?: RequestInit) => {
			capturedUrl = typeof input === "string" ? input : input.toString();
			capturedHeaders = (init?.headers as Record<string, string>) ?? {};
			return Promise.resolve(new Response("{}", { status: 200 }));
		}) as typeof fetch;

		const client = new YobaBoxConfigClient({
			endpoint: "https://yobabox.test",
			apiKey: "test-key",
			tags: { env: "prod", project: "kpvotes" },
			fetchImpl,
			refreshIntervalMs: 0,
		});

		await client.fetch();

		expect(capturedHeaders["X-YobaConf-ApiKey"]).toBe("test-key");
		expect(capturedUrl).toContain("env=prod");
		expect(capturedUrl).toContain("project=kpvotes");
	});
});
