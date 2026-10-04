/**
 * Capture raw provider REQUEST payload and RESPONSE stream events to JSONL.
 * Usage: pi -e <this file> --print ...
 * Env: PROBE_SSE_OUT — output JSONL path (required; otherwise capture is a no-op).
 */
import { appendFileSync } from "node:fs";
import type { ExtensionAPI } from "@earendil-works/pi-coding-agent";

interface Frame {
	kind: "request" | "stream" | "headers" | "response";
	turn: number;
	seq: number;
	value: unknown;
}

export default function (pi: ExtensionAPI) {
	const out = process.env.PROBE_SSE_OUT;
	if (!out) return;
	let turn = 0;
	let seq = 0;
	const write = (kind: Frame["kind"], value: unknown) => {
		const frame: Frame = { kind, turn, seq: seq++, value };
		try {
			appendFileSync(out, JSON.stringify(frame) + "\n");
		} catch {
			// never break the stream because of the probe
		}
	};

	pi.on("turn_start", () => {
		turn += 1;
		seq = 0;
	});

	pi.on("before_provider_request", (event) => {
		write("request", event.payload);
	});

	pi.on("before_provider_headers", (event) => {
		write("headers", event.headers);
	});

	pi.on("provider_stream_event", (event) => {
		write("stream", event.data);
	});
}
