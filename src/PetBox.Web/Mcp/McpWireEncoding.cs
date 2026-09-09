using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;

namespace PetBox.Web.Mcp;

// ── \uXXXX on the MCP RESPONSE, and where it actually comes from ──────────────────────────────
// (card mcp-json-escaping-relaxed-encoder; observation mcp-response-strict-json-escaping-bloats-output)
//
// THE SYMPTOM. Measured on a real POST /mcp (tasks_node_get, bodyLen:300, a Russian-language node):
// 5601 bytes carrying 442 \u04xx Cyrillic escapes, 102 " and 22 ` — 578 escapes, each
// six ASCII bytes for a character that costs one or two raw. Claude Code decodes before display so
// it never showed; qwen-code prints the string as it arrives, which is how this was finally seen.
//
// WHERE IT IS NOT. A tool result is serialized TWICE: the tool's POCO -> JSON text (and
// structuredContent) with the options Program.cs builds, and then that text embedded into the
// JsonRpcResponse envelope by the SDK. The card assumed the second serializer just needed a relaxed
// JsonSerializerOptions. It does not have one to relax. ModelContextProtocol 2.0.0 writes the
// envelope in McpSseEventWriterExtensions.FormatJsonRpcMessage:
//
//     [ThreadStatic] private static Utf8JsonWriter? _jsonWriter;
//     ...
//     _jsonWriter = new Utf8JsonWriter(writer);                       // <- no JsonWriterOptions
//     JsonSerializer.Serialize(_jsonWriter, item.Data, McpJsonUtilities.JsonContext.Default.JsonRpcMessage!);
//
// When you serialize INTO an existing Utf8JsonWriter, escaping is governed by that writer's own
// JsonWriterOptions, not by the JsonTypeInfo's options — and this writer is constructed with the
// defaults, i.e. JavaScriptEncoder.Default, and then cached per thread. So no JsonSerializerOptions
// anywhere can change it: not McpJsonUtilities.DefaultOptions, not the source-generated context's
// copy, not ours. Verified by trying: relaxing every JsonSerializerOptions the SDK holds left the
// response byte-for-byte identical. Verified again upstream: the same three lines are unchanged in
// 2.1.0 and 2.2.0, and neither HttpServerTransportOptions nor McpServerOptions has an encoder knob
// in any of the three. This is an upstream defect, not a configuration mistake on our side.
//
// SO THE FIX IS AT THE TRANSPORT. The response body of /mcp is rewritten on its way out: every
// \uXXXX escape that JSON does not REQUIRE is respelled as the character it denotes. That is a
// spelling change and nothing else — JsonEscapeRelaxer only ever rewrites a complete \uXXXX
// sequence, copies every other byte through untouched, and leaves an escape alone whenever the raw
// spelling would not be legal (control characters) or would not be shorter. It streams: one branch
// per byte, no buffering of the response, so SSE frames still leave as they are written.
//
// WHY THIS IS NOT AN XSS HOLE. Dropping the escapes on < > & ' ` is only safe where the output is
// never markup. This rewrite is scoped to the /mcp path alone — a JSON-RPC body served as
// application/json / text/event-stream to an agent over HTTP, which PetBox itself never renders.
// Everything that DOES reach HTML is untouched and stays HTML-safe:
//   * WorkflowGraphJson keeps the DEFAULT encoder on purpose (its own comment says so) — it feeds
//     both @Html.Raw <script> islands in the app, TaskBoard/TaskBoardNode's workflow modal and
//     Admin/ProjectMethodology's previews, where status names come from user-defined methodologies.
//   * PetBoxJsonEncoder.Relaxed — behind ConfigureHttpJsonOptions, the Razor/MVC JsonOptions, the
//     log-property serializers and the @Html.Raw'd rendered log message — is UNCHANGED. This card
//     added a separate encoder rather than loosening that one.
//   * MethodologyJsonFormat already writes leaves with UnsafeRelaxedJsonEscaping into a
//     Razor-encoded <textarea>; nothing here changes it.
// McpWireEncodingTests pins the first two.
static class McpWireEncoding
{
	// The INNER of the two serializations (Program.cs's tool-serialization options). The MCP
	// transport is JSON over HTTP to an agent, never markup, so the only escaping worth paying for
	// is the escaping JSON itself requires: a quote costs \" here rather than ", which the
	// envelope would then spell \\u0022 — seven bytes for one character.
	public static readonly JavaScriptEncoder Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
}

// Respells \uXXXX escapes in a JSON byte stream. Streaming and chunk-safe: an escape split across
// two writes is held until it completes, so the transformation never depends on where the transport
// happened to break the buffer.
//
// CONSERVATIVE BY CONSTRUCTION — the property the tests state, and the reason this is safe to put on
// the response path at all: the output parses to exactly the same JSON document as the input.
//   * only a COMPLETE \uXXXX sequence is ever rewritten; a malformed one is re-emitted verbatim,
//     byte for byte, and the byte that broke it is reprocessed rather than swallowed;
//   * U+0000-U+001F and U+007F stay escaped — JSON REQUIRES the first and the second gains nothing;
//   * " and \ become the two-byte \" and \\, never a bare character that would end the string;
//   * a surrogate PAIR becomes the one code point it denotes; an UNPAIRED surrogate has no raw
//     spelling in UTF-8 and is left exactly as it arrived;
//   * everything that is not part of an escape is copied through without being looked at.
sealed class JsonEscapeRelaxer
{
	enum State { Normal, Backslash, Hex }

	const byte Backslash = (byte)'\\';
	const byte LowerU = (byte)'u';

	State _state = State.Normal;
	int _hexCount;
	int _hexValue;
	readonly byte[] _hexDigits = new byte[4];

	// A high surrogate is only meaningful next to its low half, and the low half may arrive in the
	// next chunk — so it is held, with its original spelling, until the very next token decides.
	int _pendingHigh = -1;
	readonly byte[] _pendingHighDigits = new byte[4];

	public void Feed(ReadOnlySpan<byte> input, IBufferWriter<byte> output)
	{
		var i = 0;
		while (i < input.Length)
		{
			var b = input[i];
			switch (_state)
			{
				case State.Normal:
					if (b == Backslash)
					{
						_state = State.Backslash;
					}
					else
					{
						FlushPendingHigh(output);
						Write(output, b);
					}
					i++;
					break;

				case State.Backslash:
					// `\u` opens an escape; anything else (\\ \" \n …) consumes its own second byte,
					// which is what keeps a literal `\\u0441` in the text from being read as one.
					if (b == LowerU)
					{
						_state = State.Hex;
						_hexCount = 0;
						_hexValue = 0;
					}
					else
					{
						FlushPendingHigh(output);
						Write(output, Backslash);
						Write(output, b);
						_state = State.Normal;
					}
					i++;
					break;

				case State.Hex:
					var digit = HexValue(b);
					if (digit < 0)
					{
						// Not an escape after all (the JSON parser downstream will have its own
						// opinion). Put back exactly what came in and reprocess this byte, so a `\`
						// here still opens the next escape.
						FlushPendingHigh(output);
						WriteEscapeVerbatim(output, _hexDigits.AsSpan(0, _hexCount));
						_state = State.Normal;
						break;
					}
					_hexDigits[_hexCount] = b;
					_hexValue = (_hexValue << 4) | digit;
					_hexCount++;
					i++;
					if (_hexCount == 4)
					{
						Complete(output);
						_state = State.Normal;
					}
					break;
			}
		}
	}

	// End of the response body. Anything still held is emitted exactly as it arrived: this only
	// happens on a truncated body, and the rewriter must not be the thing that changed it.
	public void Finish(IBufferWriter<byte> output)
	{
		FlushPendingHigh(output);
		switch (_state)
		{
			case State.Backslash:
				Write(output, Backslash);
				break;
			case State.Hex:
				WriteEscapeVerbatim(output, _hexDigits.AsSpan(0, _hexCount));
				break;
		}
		_state = State.Normal;
	}

	void Complete(IBufferWriter<byte> output)
	{
		var unit = _hexValue;

		if (_pendingHigh >= 0)
		{
			if (unit is >= 0xDC00 and <= 0xDFFF)
			{
				var codePoint = 0x10000 + ((_pendingHigh - 0xD800) << 10) + (unit - 0xDC00);
				_pendingHigh = -1;
				WriteRune(output, codePoint);
				return;
			}
			// The high surrogate was unpaired: give it back untouched, then judge `unit` alone.
			FlushPendingHigh(output);
		}

		if (unit is >= 0xD800 and <= 0xDBFF)
		{
			_pendingHigh = unit;
			_hexDigits.CopyTo(_pendingHighDigits, 0);
			return;
		}

		// A lone low surrogate is not a code point and has no raw UTF-8 spelling.
		if (unit is >= 0xDC00 and <= 0xDFFF)
		{
			WriteEscapeVerbatim(output, _hexDigits);
			return;
		}

		switch (unit)
		{
			case '"':
				Write(output, Backslash);
				Write(output, (byte)'"');
				return;
			case '\\':
				Write(output, Backslash);
				Write(output, Backslash);
				return;
			// JSON requires the C0 controls to be escaped; U+007F is legal raw but saves nothing
			// worth a special case, so both stay exactly as they came.
			case < 0x20 or 0x7F:
				WriteEscapeVerbatim(output, _hexDigits);
				return;
			default:
				WriteRune(output, unit);
				return;
		}
	}

	void FlushPendingHigh(IBufferWriter<byte> output)
	{
		if (_pendingHigh < 0) return;
		_pendingHigh = -1;
		WriteEscapeVerbatim(output, _pendingHighDigits);
	}

	static void WriteEscapeVerbatim(IBufferWriter<byte> output, ReadOnlySpan<byte> digits)
	{
		Write(output, Backslash);
		Write(output, LowerU);
		foreach (var d in digits) Write(output, d);
	}

	static void WriteRune(IBufferWriter<byte> output, int codePoint)
	{
		var span = output.GetSpan(4);
		var written = new Rune(codePoint).EncodeToUtf8(span);
		output.Advance(written);
	}

	static void Write(IBufferWriter<byte> output, byte b)
	{
		var span = output.GetSpan(1);
		span[0] = b;
		output.Advance(1);
	}

	static int HexValue(byte b) => b switch
	{
		>= (byte)'0' and <= (byte)'9' => b - '0',
		>= (byte)'a' and <= (byte)'f' => b - 'a' + 10,
		>= (byte)'A' and <= (byte)'F' => b - 'A' + 10,
		_ => -1,
	};
}

// A write-only pass-through that respells escapes on the way to the real response body. NOT a
// buffering stream: each write is transformed and forwarded immediately, so a flushed SSE frame is
// still a flushed SSE frame. The scratch buffer is reused across writes and never grows past the
// largest single write.
sealed class EscapeRelaxingStream(Stream inner) : Stream
{
	readonly JsonEscapeRelaxer _relaxer = new();
	readonly ArrayBufferWriter<byte> _scratch = new(4096);

	public override bool CanRead => false;
	public override bool CanSeek => false;
	public override bool CanWrite => true;
	public override long Length => throw new NotSupportedException();

	public override long Position
	{
		get => throw new NotSupportedException();
		set => throw new NotSupportedException();
	}

	public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

	public override void Write(ReadOnlySpan<byte> buffer)
	{
		_scratch.ResetWrittenCount();
		_relaxer.Feed(buffer, _scratch);
		if (_scratch.WrittenCount > 0) inner.Write(_scratch.WrittenSpan);
	}

	public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
		WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

	public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
	{
		_scratch.ResetWrittenCount();
		_relaxer.Feed(buffer.Span, _scratch);
		if (_scratch.WrittenCount > 0) await inner.WriteAsync(_scratch.WrittenMemory, cancellationToken);
	}

	// Deliberately does NOT drain the relaxer: a flush lands on a frame boundary, never inside an
	// escape, and emitting a half-read escape early is the one way this could corrupt a response.
	public override void Flush() => inner.Flush();

	public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

	// Called once, after the endpoint is done, so a body that ended mid-escape still leaves intact.
	public async ValueTask FinishAsync()
	{
		_scratch.ResetWrittenCount();
		_relaxer.Finish(_scratch);
		if (_scratch.WrittenCount > 0) await inner.WriteAsync(_scratch.WrittenMemory, CancellationToken.None);
	}

	public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
	public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
	public override void SetLength(long value) => throw new NotSupportedException();
}

// Installs the rewriter for /mcp — every method, not just POST: notifications leave on the GET SSE
// stream too, and they carry the same node text. Path-guarded, so nothing else in the app pays for
// it.
public sealed class McpResponseEscapeRelaxingMiddleware(RequestDelegate next)
{
	public async Task InvokeAsync(HttpContext ctx)
	{
		if (!ctx.Request.Path.StartsWithSegments("/mcp"))
		{
			await next(ctx);
			return;
		}

		var original = ctx.Response.Body;
		var relaxing = new EscapeRelaxingStream(original);
		ctx.Response.Body = relaxing;
		// The rewrite only ever shortens, so a Content-Length computed upstream would strand the
		// client waiting for bytes that are never coming. The streamable-HTTP transport answers
		// chunked anyway; this is belt and braces for any framing it might grow.
		ctx.Response.OnStarting(static state =>
		{
			((HttpContext)state).Response.ContentLength = null;
			return Task.CompletedTask;
		}, ctx);

		try
		{
			await next(ctx);
			await relaxing.FinishAsync();
		}
		finally
		{
			ctx.Response.Body = original;
		}
	}
}
