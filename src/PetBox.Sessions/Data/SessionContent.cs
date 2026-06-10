using System.IO.Compression;
using System.Text;
using System.Text.Json;
using PetBox.Sessions.Contract;

namespace PetBox.Sessions.Data;

// Codec for a session's content: messages <-> a Brotli-compressed JSONL blob (one JSON
// object per line, the whole thing Brotli-compressed). Transcripts are highly repetitive,
// so compression keeps the verbatim copy cheap to store alongside the (future) FTS index.
// The blob is the cold payload — only decoded on a read (session.get) or a delta query.
public static class SessionContent
{
	static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

	public static byte[] Encode(IReadOnlyList<SessionMessage> messages)
	{
		using var ms = new MemoryStream();
		using (var br = new BrotliStream(ms, CompressionLevel.Optimal, leaveOpen: true))
		using (var w = new StreamWriter(br, new UTF8Encoding(false)))
		{
			foreach (var m in messages)
				w.WriteLine(JsonSerializer.Serialize(m, Json));
		}
		return ms.ToArray();
	}

	public static IReadOnlyList<SessionMessage> Decode(byte[]? blob)
	{
		if (blob is null || blob.Length == 0) return Array.Empty<SessionMessage>();
		using var ms = new MemoryStream(blob);
		using var br = new BrotliStream(ms, CompressionMode.Decompress);
		using var r = new StreamReader(br, Encoding.UTF8);
		var list = new List<SessionMessage>();
		string? line;
		while ((line = r.ReadLine()) is not null)
		{
			if (line.Length == 0) continue;
			var m = JsonSerializer.Deserialize<SessionMessage>(line, Json);
			if (m is not null) list.Add(m);
		}
		return list;
	}
}
