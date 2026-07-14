using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace PetBox.Tests.Web;

// static-assets-compression-cache: measured on prod against the light /doc page, app.css
// (120,190 B) and site.js (210,643 B) came back with NO Content-Encoding at all — curl offering
// `Accept-Encoding: gzip, br` got the identical uncompressed bytes, and there was no Cache-Control
// header at all (only an ETag), so the browser revalidated on every navigation. DOM phase alone
// measured 3.16 s at ~50 KB/s effective throughput, against a 25 ms server render time in the
// access log. Program.cs now registers AddResponseCompression (Brotli + Gzip, EnableForHttps) and
// a StaticFileOptions.OnPrepareResponse that sets Cache-Control. favicon.svg is the one static
// asset actually checked into wwwroot (app.css/site.js are bun build output, not present in a test
// run) — small enough to hand-verify, and image/svg+xml is on the compression MIME list.
public sealed class StaticAssetCompressionCacheTests : IAsyncLifetime
{
	readonly WebApplicationFactory<Program> _factory;
	HttpClient _client = null!;

	public StaticAssetCompressionCacheTests()
	{
		_factory = new WebApplicationFactory<Program>()
			.WithWebHostBuilder(b =>
			{
				b.UseEnvironment("Testing");
				b.ConfigureAppConfiguration((_, cfg) =>
				{
					cfg.AddInMemoryCollection(new Dictionary<string, string?>
					{
						["ConnectionStrings:PetBox"] = TestSchema.NewTempConnectionString(),
						["Host:BackgroundServices"] = "false",
					});
				});
			});
	}

	public Task InitializeAsync()
	{
		// HttpClientHandler.AutomaticDecompression defaults to None, so a Content-Encoding the
		// server sent survives on the response for the assertions below to see — an
		// auto-decompressing handler would strip exactly the header this test exists to check.
		_client = _factory.CreateClient(new WebApplicationFactoryClientOptions
		{
			AllowAutoRedirect = false,
		});
		return Task.CompletedTask;
	}

	public async Task DisposeAsync()
	{
		_client.Dispose();
		await _factory.DisposeAsync();
	}

	[Fact]
	public async Task Static_asset_is_served_compressed_when_the_client_offers_encoding()
	{
		using var req = new HttpRequestMessage(HttpMethod.Get, "/favicon.svg");
		req.Headers.Add("Accept-Encoding", "br, gzip");
		using var resp = await _client.SendAsync(req);

		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		resp.Content.Headers.ContentEncoding.Should().Contain(
			e => e == "br" || e == "gzip",
			"AddResponseCompression registers Brotli + Gzip and image/svg+xml is in the MIME list — "
			+ "a client that offers either must get a compressed body back, not the raw 413 bytes");
	}

	[Fact]
	public async Task Static_asset_without_accept_encoding_is_served_uncompressed()
	{
		using var req = new HttpRequestMessage(HttpMethod.Get, "/favicon.svg");
		req.Headers.Add("Accept-Encoding", "identity");
		using var resp = await _client.SendAsync(req);

		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		resp.Content.Headers.ContentEncoding.Should().BeEmpty(
			"a client that does not offer br/gzip must not be handed a body it cannot decode");
	}

	[Fact]
	public async Task Unversioned_static_asset_gets_a_short_cache_control_not_immutable()
	{
		using var resp = await _client.GetAsync("/favicon.svg");

		resp.Headers.CacheControl.Should().NotBeNull(
			"OnPrepareResponse must set Cache-Control on every static file response");
		resp.Headers.CacheControl!.Public.Should().BeTrue();
		resp.Headers.CacheControl.MaxAge.Should().Be(TimeSpan.FromSeconds(600));
		resp.Headers.CacheControl.Extensions.Should().NotContain(
			e => e.Name == "immutable",
			"favicon.svg is requested WITHOUT a ?v= content-hash — marking it immutable would pin a "
			+ "stale copy across a deploy that changes its bytes at the same URL");
	}

	[Fact]
	public async Task Versioned_static_asset_gets_a_year_long_immutable_cache_control()
	{
		// asp-append-version="true" (used on app.css/site.js in _Layout.cshtml/_PublicLayout.cshtml)
		// stamps a content-hash query string; OnPrepareResponse only checks for the presence of `v`,
		// same as here — the file changing gives it a NEW url, so caching this one forever is safe.
		using var resp = await _client.GetAsync("/favicon.svg?v=abc123");

		resp.Headers.CacheControl.Should().NotBeNull();
		resp.Headers.CacheControl!.Public.Should().BeTrue();
		resp.Headers.CacheControl.MaxAge.Should().Be(TimeSpan.FromDays(365));
		resp.Headers.CacheControl.Extensions.Should().Contain(e => e.Name == "immutable");
	}
}
