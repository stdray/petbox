using Microsoft.AspNetCore.Http;

namespace PetBox.Web.Mcp;

// ── \uXXXX-escape inflation, measured on the RAW WIRE BODY ────────────────────────────────────
// (card escape-inflation-warning; corrected by fix/escape-inflation-comparable-units)
//
// THE BUG THIS EXISTS TO PREVENT: the first cut of this detector divided
//     inflation = Request.ContentLength / <UTF-8 byte count of the reserialized tool ARGUMENTS>
// — a PART over a WHOLE. The numerator carries the JSON-RPC envelope (jsonrpc/id/method/
// params.name/_meta ≈ 178 bytes on a real call), the denominator does not, so EVERY small call
// measured (X + 178) / X and any call with under ~356 bytes of arguments tripped the 1.5x
// threshold — on prod a pure-ASCII 276-byte tasks_upsert was told it had inflated 2.8x. Pure
// ASCII cannot inflate at all: that number was the envelope, not escaping.
//
// The fix is to make both halves of the ratio statements about the SAME bytes: this scanner runs
// over the request body exactly as it arrived on the wire and answers "how many bytes would this
// same body cost if its non-ASCII were spelled raw UTF-8 instead of \uXXXX?". The envelope is now
// present in numerator AND denominator identically, so it cancels; a body with no escapes measures
// 1.0 no matter how big or how small it is.
//
// WHY A SCAN RATHER THAN A REPARSE. The alternative was Request.EnableBuffering() + reparse +
// reserialize with the relaxed encoder. Rejected on three counts: (1) EnableBuffering wraps the
// body in a FileBufferingReadStream that SPILLS TO DISK past its 30 KB memory threshold — a real
// operational change to the MCP hot path for the sake of a diagnostic; (2) a reserialized copy
// differs from the wire in whitespace and key order, so the denominator would be an approximation
// carrying its own noise; (3) it costs a whole extra parse + string allocation of the entire body,
// where this costs one branch per byte over bytes the server was already reading, with zero
// allocation. The scan is also EXACT: it changes only the escape spelling and nothing else.
//
// Conservative by construction — it only ever subtracts bytes a raw-UTF-8 client could genuinely
// have avoided, so RawUtf8Bytes <= WireBytes always and the ratio can never come out below 1.0:
//   * control characters U+0000-U+001F MUST stay escaped in JSON (and " / \ cost two raw
//     bytes, not one) - so nothing is claimed for any escape below U+0080;
//   * an UNPAIRED surrogate has no raw UTF-8 spelling at all → nothing claimed;
//   * a compressed (Content-Encoding) body simply contains no \u sequences → measures 1.0, silent.
sealed class JsonEscapeInflationScanner
{
	enum State { Normal, Backslash, Hex }

	const byte Backslash = (byte)'\\';
	const byte LowerU = (byte)'u';

	State _state = State.Normal;
	int _hexCount;
	int _hexValue;
	// The \uD800-\uDBFF half of a surrogate pair, held only until the very next token: a pair is
	// two ADJACENT escapes, so any other byte in between drops it.
	int _pendingHighSurrogate = -1;

	// Every byte the request body actually spent on the wire.
	public long WireBytes { get; private set; }

	// What those same bytes would have cost with non-ASCII spelled raw.
	public long RawUtf8Bytes => WireBytes - _savedBytes;

	long _savedBytes;

	public void Feed(ReadOnlySpan<byte> buffer)
	{
		WireBytes += buffer.Length;
		var i = 0;
		while (i < buffer.Length)
		{
			var b = buffer[i];
			switch (_state)
			{
				case State.Normal:
					if (b == Backslash) _state = State.Backslash;
					else _pendingHighSurrogate = -1;
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
						_pendingHighSurrogate = -1;
						_state = State.Normal;
					}
					i++;
					break;

				case State.Hex:
					var digit = HexValue(b);
					if (digit < 0)
					{
						// Malformed JSON (the parser downstream will reject it). Abandon the escape
						// WITHOUT consuming this byte, so a `\` here still opens the next one.
						_pendingHighSurrogate = -1;
						_state = State.Normal;
						break;
					}
					_hexValue = (_hexValue << 4) | digit;
					i++;
					if (++_hexCount == 4)
					{
						Complete(_hexValue);
						_state = State.Normal;
					}
					break;
			}
		}
	}

	// One completed \uXXXX code UNIT. Six bytes went over the wire for it; how many would raw
	// UTF-8 have needed?
	void Complete(int unit)
	{
		if (_pendingHighSurrogate >= 0)
		{
			_pendingHighSurrogate = -1;
			if (unit is >= 0xDC00 and <= 0xDFFF)
			{
				// A surrogate PAIR: 12 wire bytes for a code point raw UTF-8 spells in 4.
				_savedBytes += 8;
				return;
			}
			// The high surrogate was unpaired — claim nothing for it, then judge `unit` on its own.
		}

		if (unit is >= 0xD800 and <= 0xDBFF)
		{
			_pendingHighSurrogate = unit;
			return;
		}
		// A lone low surrogate is not a code point; it has no raw spelling.
		if (unit is >= 0xDC00 and <= 0xDFFF) return;
		// Below U+0080 the escape is either MANDATORY (control chars) or already cheap raw (" and \
		// still cost two bytes) — never claim a saving there.
		if (unit < 0x80) return;

		_savedBytes += 6 - (unit < 0x800 ? 2 : 3);
	}

	static int HexValue(byte b) => b switch
	{
		>= (byte)'0' and <= (byte)'9' => b - '0',
		>= (byte)'a' and <= (byte)'f' => b - 'a' + 10,
		>= (byte)'A' and <= (byte)'F' => b - 'A' + 10,
		_ => -1,
	};
}

// A pass-through Stream that measures what flows through it. NOT a buffering stream: nothing is
// retained, nothing is copied, the body is still read exactly once by whoever consumes it — the
// scanner just sees each chunk on the way past. That is why this can sit on the MCP hot path.
sealed class EscapeMeasuringStream(Stream inner, JsonEscapeInflationScanner scanner) : Stream
{
	public override bool CanRead => inner.CanRead;
	public override bool CanSeek => false;
	public override bool CanWrite => false;
	public override long Length => inner.Length;

	public override long Position
	{
		get => inner.Position;
		set => throw new NotSupportedException();
	}

	public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

	public override int Read(Span<byte> buffer)
	{
		var read = inner.Read(buffer);
		if (read > 0) scanner.Feed(buffer[..read]);
		return read;
	}

	public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
		ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

	public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
	{
		var read = await inner.ReadAsync(buffer, cancellationToken);
		if (read > 0) scanner.Feed(buffer.Span[..read]);
		return read;
	}

	public override void Flush() => inner.Flush();
	public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
	public override void SetLength(long value) => throw new NotSupportedException();
	public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

// Installs the measuring stream for POST /mcp so ModuleMcp.SizeWarningOrNull — running later,
// inside a tool body — can read THIS request's own wire-vs-raw numbers off HttpContext.Items.
// Path- and method-guarded: nothing else in the app pays for it.
//
// The scanner is published to Items BEFORE the body is read, and read back AFTER (a tool body runs
// long after the SDK has deserialized the whole JSON-RPC message), which is why the live scanner
// object is stored rather than a snapshot record.
public sealed class McpWireBodyMeasurementMiddleware(RequestDelegate next)
{
	public async Task InvokeAsync(HttpContext ctx)
	{
		if (!HttpMethods.IsPost(ctx.Request.Method) || !ctx.Request.Path.StartsWithSegments("/mcp"))
		{
			await next(ctx);
			return;
		}

		var scanner = new JsonEscapeInflationScanner();
		var original = ctx.Request.Body;
		ctx.Request.Body = new EscapeMeasuringStream(original, scanner);
		ctx.Items[ModuleMcp.WireBodyMeasurementItemKey] = scanner;
		try
		{
			await next(ctx);
		}
		finally
		{
			ctx.Request.Body = original;
		}
	}
}
