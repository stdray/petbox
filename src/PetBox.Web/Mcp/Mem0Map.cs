using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PetBox.Memory.Contract;

namespace PetBox.Web.Mcp;

// Pure mapping helpers for the mem0-compatible adapter (Mem0Tools). No DI, no DB,
// no IMemoryService — just string/JSON shaping, so it stays clear of the memory
// boundary rules (MemoryBoundaryTests forbid the MCP layer touching MemoryDb/Store).
//
// Scope mapping: mem0 user_id -> PetBox store; agent_id/run_id -> tags. Entry id is
// reversible: "{store}__{key}" so get/update/delete can recover the store from the id.
public static partial class Mem0Map
{
	public const string DefaultStore = "default";

	// mem0 user_id is arbitrary (uppercase, '@', '.', unicode); PetBox store names are
	// strict ^[a-z][a-z0-9_-]{0,99}$. Deterministic: clean ids pass through unchanged
	// (so recall finds what add wrote); anything transformed gets a stable short hash
	// suffix to avoid collisions. null/empty -> "default".
	public static string StoreFromUserId(string? userId)
	{
		if (string.IsNullOrWhiteSpace(userId)) return DefaultStore;
		var id = userId.Trim();
		if (CleanStore().IsMatch(id)) return id; // already a valid store name

		var slug = Slugify(id);
		if (slug.Length == 0) slug = "u";
		if (slug[0] is < 'a' or > 'z') slug = "u-" + slug;
		var hash = ShortHash(id);
		var maxSlug = 100 - 1 - hash.Length; // room for '-' + hash, total <= 100
		if (slug.Length > maxSlug) slug = slug[..maxSlug].TrimEnd('-');
		return $"{slug}-{hash}";
	}

	static string Slugify(string s)
	{
		var sb = new StringBuilder(s.Length);
		foreach (var ch in s.ToLowerInvariant())
			sb.Append(ch is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_' or '-' ? ch : '-');
		return MultiDash().Replace(sb.ToString(), "-").Trim('-');
	}

	static string ShortHash(string s) =>
		Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)), 0, 4).ToLowerInvariant();

	// ---- entry id codec ----

	public static string MakeId(string store, string key) => $"{store}__{key}";

	public static bool TryDecodeId(string? id, out string store, out string key)
	{
		store = key = string.Empty;
		if (string.IsNullOrEmpty(id)) return false;
		var i = id.IndexOf("__", StringComparison.Ordinal);
		if (i <= 0 || i + 2 >= id.Length) return false;
		store = id[..i];
		key = id[(i + 2)..];
		return true;
	}

	public static string NewEntryKey() => "m-" + Guid.NewGuid().ToString("N");

	// ---- scope <-> tags ----

	public static string? ScopeTags(string? agentId, string? runId, string? extra = null)
	{
		var parts = new List<string>();
		if (!string.IsNullOrWhiteSpace(agentId)) parts.Add("agent:" + agentId.Trim());
		if (!string.IsNullOrWhiteSpace(runId)) parts.Add("run:" + runId.Trim());
		if (!string.IsNullOrWhiteSpace(extra)) parts.Add(extra.Trim());
		return parts.Count == 0 ? null : string.Join(',', parts);
	}

	// Tags are normalized (lowercased) by the service, so match case-insensitively.
	public static bool TagsMatchScope(string tags, string? agentId, string? runId)
	{
		var set = tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		if (!string.IsNullOrWhiteSpace(agentId) && !set.Contains("agent:" + agentId.Trim().ToLowerInvariant())) return false;
		if (!string.IsNullOrWhiteSpace(runId) && !set.Contains("run:" + runId.Trim().ToLowerInvariant())) return false;
		return true;
	}

	// ---- mem0 wire shaping ----

	// Join messages (string | [string|{role,content}]) into one verbatim body.
	public static string MessagesToBody(JsonElement messages)
	{
		switch (messages.ValueKind)
		{
			case JsonValueKind.String:
				return messages.GetString() ?? string.Empty;
			case JsonValueKind.Array:
				var lines = new List<string>();
				foreach (var m in messages.EnumerateArray())
				{
					if (m.ValueKind == JsonValueKind.String) { lines.Add(m.GetString() ?? string.Empty); continue; }
					if (m.ValueKind == JsonValueKind.Object)
					{
						var role = m.TryGetProperty("role", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null;
						var content = m.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : m.GetRawText();
						lines.Add(string.IsNullOrEmpty(role) ? content ?? string.Empty : $"{role}: {content}");
						continue;
					}
					lines.Add(m.GetRawText());
				}
				return string.Join("\n", lines);
			case JsonValueKind.Undefined or JsonValueKind.Null:
				return string.Empty;
			default:
				return messages.GetRawText();
		}
	}

	public static string? MetadataToString(JsonElement? metadata) =>
		metadata is { } m && m.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null) ? m.GetRawText() : null;

	public static object ToMem0Memory(MemoryEntryView v, string store, double? score) => new
	{
		id = MakeId(store, v.Key),
		memory = v.Body,
		metadata = ParseMetadata(v.Metadata),
		score,
		user_id = store,
		categories = v.Tags.Length == 0
			? Array.Empty<string>()
			: v.Tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
	};

	// Synthetic, rank-derived relevance score from list position (SearchAsync returns
	// rank-ordered but exposes no numeric score). Relevance order, NOT a calibrated
	// distance — clients should not threshold on it. count<=1 -> 1.0.
	public static double PositionScore(int index, int count) =>
		count <= 1 ? 1.0 : Math.Round(1.0 - (double)index / count, 4);

	static object? ParseMetadata(string raw)
	{
		if (string.IsNullOrWhiteSpace(raw)) return null;
		try { using var d = JsonDocument.Parse(raw); return d.RootElement.Clone(); }
		catch { return raw; }
	}

	[GeneratedRegex("^[a-z][a-z0-9_-]{0,99}$")]
	private static partial Regex CleanStore();

	[GeneratedRegex("-{2,}")]
	private static partial Regex MultiDash();
}
