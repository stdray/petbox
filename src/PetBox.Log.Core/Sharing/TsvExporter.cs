using System.Globalization;
using System.Text;
using PetBox.Log.Core.Models;

namespace PetBox.Log.Core.Sharing;

public static class TsvExporter
{
	public static async Task WriteAsync(
		IAsyncEnumerable<LogEntry> events,
		IReadOnlyList<string> columns,
		FieldMaskingPolicy policy,
		ValueMasker masker,
		TextWriter writer,
		CancellationToken ct)
	{
		var visible = columns
			.Where(c => policy.ModeFor(c) != MaskMode.Hide)
			.ToArray();

		await writer.WriteLineAsync(string.Join('\t', visible)).ConfigureAwait(false);

		await foreach (var e in events.WithCancellation(ct).ConfigureAwait(false))
		{
			var first = true;
			foreach (var col in visible)
			{
				if (!first) await writer.WriteAsync('\t').ConfigureAwait(false);
				first = false;
				var mode = policy.ModeFor(col);
				var cell = RenderCell(col, e, mode, masker);
				await writer.WriteAsync(EscapeTsv(cell)).ConfigureAwait(false);
			}
			await writer.WriteAsync('\n').ConfigureAwait(false);
		}
	}

	static string RenderCell(string column, LogEntry e, MaskMode mode, ValueMasker masker)
	{
		var raw = LookupScalar(column, e) ?? LookupProperty(e, column);
		return mode == MaskMode.Mask ? masker.Mask(column, raw) : raw ?? "";
	}

	static string? LookupScalar(string column, LogEntry e) => column switch
	{
		"Id" => e.Id.ToString(CultureInfo.InvariantCulture),
		"Timestamp" => e.Timestamp.ToString("O", CultureInfo.InvariantCulture),
		"Level" => e.Level.ToString(),
		"MessageTemplate" => e.MessageTemplate,
		"Message" => e.Message,
		"Exception" => e.Exception ?? "",
		"ServiceKey" => e.ServiceKey,
		_ => null,
	};

	static string LookupProperty(LogEntry e, string key)
	{
		var props = e.GetProperties();
		if (!props.TryGetValue(key, out var v)) return "";
		return v.ValueKind switch
		{
			System.Text.Json.JsonValueKind.String => v.GetString() ?? "",
			System.Text.Json.JsonValueKind.Null or System.Text.Json.JsonValueKind.Undefined => "",
			_ => v.GetRawText(),
		};
	}

	static string EscapeTsv(string s)
	{
		if (string.IsNullOrEmpty(s))
			return "";
		if (s.IndexOfAny(['\t', '\n', '\r']) < 0)
			return s;
		var sb = new StringBuilder(s.Length);
		foreach (var c in s)
		{
			switch (c)
			{
				case '\t': sb.Append(' '); break;
				case '\n': sb.Append(' '); break;
				case '\r': break;
				default: sb.Append(c); break;
			}
		}
		return sb.ToString();
	}
}
