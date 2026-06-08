import { expect, test } from "bun:test";
import { PetBoxDataClient, PetBoxError } from "../src/data.js";

interface Captured {
	url: string;
	init: RequestInit;
}

function stubFetch(status: number, body: string): { fetchImpl: typeof fetch; captured: () => Captured } {
	let cap: Captured | undefined;
	const fetchImpl = (async (input: RequestInfo | URL, init?: RequestInit) => {
		cap = { url: String(input), init: init ?? {} };
		return new Response(body, { status, headers: { "Content-Type": "application/json" } });
	}) as typeof fetch;
	return { fetchImpl, captured: () => cap as Captured };
}

test("query posts to the query path with X-Api-Key and parses rows", async () => {
	const { fetchImpl, captured } = stubFetch(200, JSON.stringify([{ id: 1, film: "Matrix" }]));
	const data = new PetBoxDataClient({ endpoint: "https://petbox.test", apiKey: "k", fetchImpl });

	const rows = await data.query<{ id: number; film: string }>(
		"kpvotes",
		"cache",
		"SELECT * FROM votes WHERE id = @id",
		[{ name: "@id", value: 1 }],
	);

	const c = captured();
	expect(c.url).toBe("https://petbox.test/api/data/kpvotes/cache/query");
	expect((c.init.headers as Record<string, string>)["X-Api-Key"]).toBe("k");
	expect(JSON.parse(c.init.body as string)).toEqual({
		sql: "SELECT * FROM votes WHERE id = @id",
		params: [{ name: "@id", value: 1 }],
	});
	expect(rows).toEqual([{ id: 1, film: "Matrix" }]);
});

test("exec posts to the exec path and returns affected", async () => {
	const { fetchImpl, captured } = stubFetch(200, JSON.stringify({ affected: 3 }));
	const data = new PetBoxDataClient({ endpoint: "https://petbox.test", apiKey: "k", fetchImpl });

	const affected = await data.exec("p", "db", "DELETE FROM votes");

	expect(captured().url).toBe("https://petbox.test/api/data/p/db/exec");
	expect(affected).toBe(3);
});

test("createDb posts to the dbs path with name (omitting undefined fields)", async () => {
	const { fetchImpl, captured } = stubFetch(201, "{}");
	const data = new PetBoxDataClient({ endpoint: "https://petbox.test", apiKey: "k", fetchImpl });

	await data.createDb("p", "cache", { maxPageCount: 262144 });

	expect(captured().url).toBe("https://petbox.test/api/data/p/dbs");
	expect(JSON.parse(captured().init.body as string)).toEqual({ name: "cache", maxPageCount: 262144 });
});

test("non-2xx throws PetBoxError carrying status and parsed body", async () => {
	const { fetchImpl } = stubFetch(404, JSON.stringify({ error: "DataDb not found" }));
	const data = new PetBoxDataClient({ endpoint: "https://petbox.test", apiKey: "k", fetchImpl });

	let err: unknown;
	try {
		await data.query("p", "nope", "SELECT 1");
	} catch (e) {
		err = e;
	}

	expect(err).toBeInstanceOf(PetBoxError);
	expect((err as PetBoxError).status).toBe(404);
	expect((err as PetBoxError).body).toEqual({ error: "DataDb not found" });
});
