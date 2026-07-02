namespace PetBox.Client.Config;

// Caller-provided knobs for AddPetBoxConfig. Tag-vector — not a single hierarchical
// path — selects which bindings resolve into the configuration tree. Matches petbox's
// canonical read API at `/v1/conf?tag1=v1&tag2=v2&…` (workspace derived from the key's
// project). This is the single config-read surface; there is no per-path resolve endpoint.
//
// `Handler` is an escape hatch for tests — inject WebApplicationFactory's
// Server.CreateHandler() so the SDK talks to an in-process petbox without touching
// the network. Production uses the default SocketsHttpHandler.
public sealed class PetBoxConfigOptions
{
	// e.g. "https://petbox.3po.su". Trailing slash optional — normalised internally.
	public string BaseUrl { get; set; } = string.Empty;

	// Plaintext token. Sent as the `X-Api-Key` header on every request (via the shared
	// PetBox.Client transport).
	public string ApiKey { get; set; } = string.Empty;

	// Tag-vector components. Resolve finds every binding whose tag-set is a subset of
	// this, merged by specificity. Populate via `WithTag` or direct add.
	public Dictionary<string, string> Tags { get; } = new(StringComparer.Ordinal);

	// How often to re-poll for changes. Each poll uses `If-None-Match: <etag>`, so 304s
	// are cheap. Set to TimeSpan.Zero to disable polling (one-shot load at startup only).
	public TimeSpan RefreshInterval { get; set; } = TimeSpan.FromMinutes(5);

	// Directory for the last-known-good (LKG) disk cache. When set, every successful 200
	// is persisted (etag + flattened data) to a deterministic file under this directory,
	// and startup preloads that file before the first network call. This lets a host boot
	// on its last good config after a container restart even while petbox is unreachable —
	// the key DC-outage survival property. Null (default) disables the disk cache entirely.
	// Mount a persistent volume here in containers so the file survives restarts.
	public string? CacheDirectory { get; set; }

	// HTTP timeout for the config provider's own HttpClient. Kept short (10s default, vs the
	// framework's 100s) so a slow/unreachable petbox never blocks host startup for long —
	// the initial fetch fails fast and, if a disk cache exists, boot continues on LKG data.
	public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(10);

	// When true, initial load failures (409, auth errors, network errors) don't throw —
	// the provider starts with empty data and retries on the next poll tick. Matches the
	// ConfigurationBuilder `optional: true` convention. Default false: missing config at
	// startup is usually a broken-deploy signal, fail-fast surfaces it.
	public bool Optional { get; set; }

	// Testing-only: inject a custom HttpMessageHandler (e.g. WebApplicationFactory's
	// TestServer handler). Production leaves this null and the provider builds its own
	// HttpClient with the default handler.
	public HttpMessageHandler? Handler { get; set; }

	public PetBoxConfigOptions WithTag(string key, string value)
	{
		ArgumentException.ThrowIfNullOrEmpty(key);
		ArgumentException.ThrowIfNullOrEmpty(value);
		Tags[key] = value;
		return this;
	}

	public PetBoxConfigOptions WithTags(IEnumerable<KeyValuePair<string, string>> tags)
	{
		ArgumentNullException.ThrowIfNull(tags);
		foreach (var (k, v) in tags) WithTag(k, v);
		return this;
	}
}
