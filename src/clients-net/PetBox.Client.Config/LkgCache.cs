using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PetBox.Client.Config;

// Last-known-good (LKG) disk cache for the config provider. Persists the flattened config
// tree + its ETag so a host can boot on its last good config after a restart even while
// petbox is unreachable (DC-outage survival). Every method here is best-effort: cache I/O
// must never break config loading, so read failures return null and write failures are
// swallowed (Debug at most). A corrupt/unreadable file is treated as "no cache".
static class LkgCache
{
	static readonly JsonSerializerOptions JsonOptions = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		PropertyNameCaseInsensitive = true,
	};

	// On-disk shape: { etag, data (flattened key→value), savedAt }.
	public sealed record Envelope(string? Etag, Dictionary<string, string?> Data, DateTimeOffset SavedAt);

	// Deterministic cache-file path: SHA-256 of BaseUrl + the canonical resolve query
	// (same string BuildQuery sends to the server, so a change in tags → a different file).
	public static string PathFor(string directory, string baseUrl, string canonicalQuery)
	{
		var material = baseUrl.TrimEnd('/') + "|" + canonicalQuery;
		var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
		return Path.Combine(directory, hash.ToLowerInvariant() + ".json");
	}

	public static Envelope? TryRead(string path)
	{
		try
		{
			if (!File.Exists(path)) return null;
			var json = File.ReadAllText(path);
			var env = JsonDeserialize(json);
			// A parsed-but-shapeless file (null data) counts as no cache.
			return env?.Data is null ? null : env;
		}
		catch (Exception)
		{
			// Corrupt/unreadable file = no cache.
			return null;
		}
	}

	public static void TryWrite(string path, Envelope env)
	{
		try
		{
			var dir = Path.GetDirectoryName(path);
			if (!string.IsNullOrEmpty(dir))
				Directory.CreateDirectory(dir);

			// Atomic replace: write a temp file next to the target, then move over it.
			var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
			File.WriteAllText(tmp, JsonSerializer.Serialize(env, JsonOptions));
			File.Move(tmp, path, overwrite: true);
		}
		catch (Exception ex)
		{
			// Cache writes are best-effort; never let them break the provider.
			System.Diagnostics.Debug.WriteLine($"PetBoxConfig cache write failed: {ex.Message}");
		}
	}

	static Envelope? JsonDeserialize(string json) => JsonSerializer.Deserialize<Envelope>(json, JsonOptions);
}
