namespace YobaBox.Client.Config;

// Caller-provided knobs for AddYobaBoxConfig. Tag-vector — not a single hierarchical
// path — selects which bindings resolve into the configuration tree. Matches yobabox
// resolve API shape at `/v1/conf?tag1=v1&tag2=v2&…`. (Phase 26.3 will adapt to
// yobabox's actual `/api/config/{workspaceKey}/resolve` shape; for now the wire
// format matches the legacy yobaconf protocol — port-as-is.)
//
// `Handler` is an escape hatch for tests — inject WebApplicationFactory's
// Server.CreateHandler() so the SDK talks to an in-process yobabox without touching
// the network. Production uses the default SocketsHttpHandler.
public sealed class YobaBoxConfigOptions
{
	// e.g. "https://yobabox.3po.su". Trailing slash optional — normalised internally.
	public string BaseUrl { get; set; } = string.Empty;

	// Plaintext token. Sent as `X-YobaConf-ApiKey` header on every request.
	public string ApiKey { get; set; } = string.Empty;

	// Tag-vector components. Resolve finds every binding whose tag-set is a subset of
	// this, merged by specificity. Populate via `WithTag` or direct add.
	public Dictionary<string, string> Tags { get; } = new(StringComparer.Ordinal);

	// How often to re-poll for changes. Each poll uses `If-None-Match: <etag>`, so 304s
	// are cheap. Set to TimeSpan.Zero to disable polling (one-shot load at startup only).
	public TimeSpan RefreshInterval { get; set; } = TimeSpan.FromMinutes(5);

	// When true, initial load failures (409, auth errors, network errors) don't throw —
	// the provider starts with empty data and retries on the next poll tick. Matches the
	// ConfigurationBuilder `optional: true` convention. Default false: missing config at
	// startup is usually a broken-deploy signal, fail-fast surfaces it.
	public bool Optional { get; set; }

	// Testing-only: inject a custom HttpMessageHandler (e.g. WebApplicationFactory's
	// TestServer handler). Production leaves this null and the provider builds its own
	// HttpClient with the default handler.
	public HttpMessageHandler? Handler { get; set; }

	public YobaBoxConfigOptions WithTag(string key, string value)
	{
		ArgumentException.ThrowIfNullOrEmpty(key);
		ArgumentException.ThrowIfNullOrEmpty(value);
		Tags[key] = value;
		return this;
	}

	public YobaBoxConfigOptions WithTags(IEnumerable<KeyValuePair<string, string>> tags)
	{
		ArgumentNullException.ThrowIfNull(tags);
		foreach (var (k, v) in tags) WithTag(k, v);
		return this;
	}
}
