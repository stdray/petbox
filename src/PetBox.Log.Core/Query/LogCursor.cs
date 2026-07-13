using System.Buffers.Binary;

namespace PetBox.Log.Core.Query;

// THE ordering key of a log's event stream, and its wire form.
//
// The logs table renders and pages by `order by Timestamp desc, Id desc` (Pages/Logs/Index.cshtml.cs
// AppendPageLimits) — the Id half is not decoration: log timestamps collide constantly (a batch
// ingested in the same millisecond), and a cursor on Timestamp alone would either re-serve or skip
// every event sharing the boundary millisecond. Live-tail's catch-up (?since= / Last-Event-ID)
// therefore uses the SAME (TimestampMs, Id) key and the SAME encoding as the paging cursor, so a
// cursor lifted from a rendered row means exactly the same thing to the paging query and to the SSE
// stream — one comparison rule, defined here, not two that can drift apart.
//
// Encoding: 16 bytes, big-endian (TimestampMs, Id), base64. Big-endian so the byte order matches the
// numeric order; base64 because the value travels in a URL query (?cursor=, ?since=) and in an SSE
// `id:` field, which must be a single-line string.
public readonly record struct LogCursor(long TimestampMs, long Id)
{
	public static LogCursor From(DateTime timestampUtc, long id) =>
		new(new DateTimeOffset(timestampUtc, TimeSpan.Zero).ToUnixTimeMilliseconds(), id);

	public string Encode()
	{
		var bytes = new byte[16];
		BinaryPrimitives.WriteInt64BigEndian(bytes.AsSpan(0, 8), TimestampMs);
		BinaryPrimitives.WriteInt64BigEndian(bytes.AsSpan(8, 8), Id);
		return Convert.ToBase64String(bytes);
	}

	// null for anything that is not a well-formed cursor (absent, wrong length, not base64) — a
	// caller treats that as "no cursor", never as an error: a garbage ?since= must not 500 an SSE
	// stream, it must simply mean "from now on".
	public static LogCursor? TryDecode(string? s)
	{
		if (string.IsNullOrEmpty(s)) return null;
		Span<byte> bytes = stackalloc byte[16];
		if (!Convert.TryFromBase64String(s, bytes, out var written) || written != 16) return null;
		return new LogCursor(
			BinaryPrimitives.ReadInt64BigEndian(bytes[..8]),
			BinaryPrimitives.ReadInt64BigEndian(bytes[8..]));
	}
}
