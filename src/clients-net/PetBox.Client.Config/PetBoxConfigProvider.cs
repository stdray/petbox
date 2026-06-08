using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using PetBox.Client;

namespace PetBox.Client.Config;

// IConfigurationProvider that fetches resolved PetBox config over HTTP on initial
// load, then polls every RefreshInterval with ETag-aware conditional GETs.
//
// Lifecycle:
//   1. Load() — synchronous initial fetch. Blocks on the HTTP call (standard pattern —
//      JsonConfigurationProvider does the same for file I/O). On success, populates the
//      base class Data dictionary via the flattener. On failure, throws (or returns
//      empty if Options.Optional).
//   2. Background Timer calls PollAsync every RefreshInterval. Sends
//      `If-None-Match: <last-etag>`. 200 → replace Data + OnReload(). 304 → no-op.
//      Auth / 4xx / network errors → keep last-known-good state.
//   3. Dispose() → stop timer, dispose HttpClient.
//
// Data replacement races with IConfiguration.Get readers. The base class Data is
// IDictionary<string, string?> (not concurrent). We build the replacement dict and
// then assign in one go — readers observe either the old or new dict, never a
// partially-populated one.
public sealed class PetBoxConfigProvider : ConfigurationProvider, IDisposable
{
	readonly PetBoxConfigOptions _options;
	readonly HttpClient _http;
	readonly string _query;
	Timer? _timer;
	string? _lastEtag;
	readonly Lock _pollGate = new();
	bool _disposed;

	public PetBoxConfigProvider(PetBoxConfigOptions options)
	{
		ArgumentNullException.ThrowIfNull(options);
		if (string.IsNullOrWhiteSpace(options.BaseUrl))
			throw new ArgumentException("BaseUrl is required.", nameof(options));
		if (string.IsNullOrWhiteSpace(options.ApiKey))
			throw new ArgumentException("ApiKey is required.", nameof(options));

		_options = options;
		// Transport + auth (X-Api-Key, BaseAddress) come from the shared core SDK; this provider
		// owns only the config-specific logic (tags → /v1/conf, ETag polling, JSON flatten).
		_http = PetBoxTransport.CreateHttpClient(options.BaseUrl, options.ApiKey, options.Handler);
		_query = BuildQuery(options.Tags);
	}

	public override void Load()
	{
		// Sync-over-async: ConfigurationBuilder.Build() is synchronous, same constraint as
		// file-based providers. One-time at startup, not a hot path.
		try
		{
			FetchAsync(CancellationToken.None).GetAwaiter().GetResult();
		}
		catch (Exception) when (_options.Optional)
		{
			Data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
		}

		if (_options.RefreshInterval > TimeSpan.Zero)
		{
			_timer = new Timer(
				_ => _ = PollSafeAsync(),
				state: null,
				dueTime: _options.RefreshInterval,
				period: _options.RefreshInterval);
		}
	}

	async Task PollSafeAsync()
	{
		if (!_pollGate.TryEnter()) return;
		try
		{
			await FetchAsync(CancellationToken.None).ConfigureAwait(false);
		}
		catch (Exception)
		{
			// Last-known-good preserved. Polling providers must never lose working config
			// due to transient issues.
			System.Diagnostics.Debug.WriteLine("PetBoxConfig poll failed; keeping last-known-good data.");
		}
		finally
		{
			_pollGate.Exit();
		}
	}

	async Task FetchAsync(CancellationToken cancellationToken)
	{
		using var request = new HttpRequestMessage(HttpMethod.Get, _query);
		if (!string.IsNullOrEmpty(_lastEtag))
			request.Headers.IfNoneMatch.Add(new EntityTagHeaderValue(_lastEtag));

		using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);

		if (response.StatusCode == HttpStatusCode.NotModified) return;

		response.EnsureSuccessStatusCode();

		var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
		var etag = response.Headers.ETag?.Tag;

		using var doc = JsonDocument.Parse(body);
		var next = JsonFlattener.Flatten(doc);

		Data = next;
		_lastEtag = etag;
		OnReload();
	}

	static string BuildQuery(Dictionary<string, string> tags)
	{
		var parts = new List<string>(tags.Count);
		// Stable order — helps server-side log grepping and caching. The resolve endpoint
		// treats duplicate tag-keys with last-wins semantics, so ordering is just for our
		// own readability.
		foreach (var kv in tags.OrderBy(kv => kv.Key, StringComparer.Ordinal))
			parts.Add($"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}");
		return parts.Count == 0 ? "v1/conf" : $"v1/conf?{string.Join('&', parts)}";
	}

	public void Dispose()
	{
		if (_disposed) return;
		_disposed = true;
		_timer?.Dispose();
		_http.Dispose();
	}
}
