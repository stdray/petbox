#!/usr/bin/env node
/**
 * Minimal stdio MCP server with a UNION-typed tool schema.
 * `type: ["string","null"]` is the exact shape MCP spec allows (JSON Schema)
 * and is what trips the OpenCode Go gateway's streaming tool_call indexer.
 *
 * No dependencies; speaks JSON-RPC over stdin/stdout (LSP-style framing is NOT
 * used by MCP stdio — messages are newline-delimited JSON).
 */
const readline = require("node:readline");

const TOOL = {
	name: "probe",
	description: "Neutral probe tool. Echoes q and limit.",
	inputSchema: {
		type: "object",
		properties: {
			q: { type: ["string", "null"], default: null, description: "search text" },
			limit: { type: ["integer", "null"], default: null, description: "max results" },
		},
		required: ["q", "limit"],
	},
};

function send(msg) {
	process.stdout.write(JSON.stringify(msg) + "\n");
}

const rl = readline.createInterface({ input: process.stdin });
rl.on("line", (line) => {
	if (!line.trim()) return;
	let req;
	try {
		req = JSON.parse(line);
	} catch {
		return;
	}
	const { id, method, params } = req;
	if (method === "initialize") {
		send({
			jsonrpc: "2.0",
			id,
			result: {
				protocolVersion: params?.protocolVersion || "2025-06-18",
				capabilities: { tools: {} },
				serverInfo: { name: "probe-union", version: "1.0.0" },
			},
		});
	} else if (method === "notifications/initialized") {
		// no response for notifications
	} else if (method === "tools/list") {
		send({ jsonrpc: "2.0", id, result: { tools: [TOOL] } });
	} else if (method === "tools/call") {
		const args = params?.arguments || {};
		send({
			jsonrpc: "2.0",
			id,
			result: {
				content: [{ type: "text", text: `q=${JSON.stringify(args.q)} limit=${JSON.stringify(args.limit)}` }],
				isError: false,
			},
		});
	} else if (id !== undefined) {
		send({ jsonrpc: "2.0", id, error: { code: -32601, message: "Method not found" } });
	}
});
