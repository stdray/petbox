using System.IO;
using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Configuration;
using PetBox.Client.Config;

namespace PetBox.Client.Config.Tests;

public class PetBoxConfigProviderTests
{
	[Fact]
	public void Constructor_throws_when_BaseUrl_missing()
	{
		var options = new PetBoxConfigOptions { ApiKey = "key" };
		var act = () => new PetBoxConfigProvider(options);
		act.Should().Throw<ArgumentException>().WithMessage("*BaseUrl is required*");
	}

	[Fact]
	public void Constructor_throws_when_ApiKey_missing()
	{
		var options = new PetBoxConfigOptions { BaseUrl = "https://petbox.test" };
		var act = () => new PetBoxConfigProvider(options);
		act.Should().Throw<ArgumentException>().WithMessage("*ApiKey is required*");
	}

	[Fact]
	public void Load_populates_data_from_fetched_json()
	{
		var options = new PetBoxConfigOptions
		{
			BaseUrl = "https://petbox.test",
			ApiKey = "key",
			RefreshInterval = TimeSpan.Zero, // no polling — one-shot
			Handler = new StubHandler("""{"db":{"host":"localhost"},"port":8080}""", etag: "abc"),
		};
		options.WithTag("env", "test");

		using var provider = new PetBoxConfigProvider(options);
		provider.Load();

		provider.TryGet("db:host", out var host).Should().BeTrue();
		host.Should().Be("localhost");
		provider.TryGet("port", out var port).Should().BeTrue();
		port.Should().Be("8080");
	}

	[Fact]
	public void Load_optional_swallows_initial_fetch_error()
	{
		var options = new PetBoxConfigOptions
		{
			BaseUrl = "https://petbox.test",
			ApiKey = "key",
			RefreshInterval = TimeSpan.Zero,
			Optional = true,
			Handler = new ThrowingHandler(),
		};
		options.WithTag("env", "test");

		using var provider = new PetBoxConfigProvider(options);
		var act = () => provider.Load();
		act.Should().NotThrow();
		// Data is initialized empty.
		provider.TryGet("anything", out _).Should().BeFalse();
	}

	[Fact]
	public void Load_non_optional_propagates_initial_fetch_error()
	{
		var options = new PetBoxConfigOptions
		{
			BaseUrl = "https://petbox.test",
			ApiKey = "key",
			RefreshInterval = TimeSpan.Zero,
			Optional = false,
			Handler = new ThrowingHandler(),
		};
		options.WithTag("env", "test");

		using var provider = new PetBoxConfigProvider(options);
		var act = () => provider.Load();
		act.Should().Throw<HttpRequestException>();
	}

	[Fact]
	public void Load_writes_cache_after_200()
	{
		using var cacheDir = new TempDir();
		var options = new PetBoxConfigOptions
		{
			BaseUrl = "https://petbox.test",
			ApiKey = "key",
			RefreshInterval = TimeSpan.Zero,
			CacheDirectory = cacheDir.Path,
			Handler = new StubHandler("""{"db":{"host":"localhost"}}""", etag: "abc"),
		};
		options.WithTag("env", "test");

		using var provider = new PetBoxConfigProvider(options);
		provider.Load();

		var files = Directory.GetFiles(cacheDir.Path, "*.json");
		files.Should().ContainSingle();
		var json = File.ReadAllText(files[0]);
		json.Should().Contain("db:host").And.Contain("localhost").And.Contain("abc");
	}

	[Fact]
	public void Load_uses_cache_when_server_unavailable_and_does_not_throw_even_when_required()
	{
		using var cacheDir = new TempDir();

		// First run against a working server seeds the disk cache.
		var seed = new PetBoxConfigOptions
		{
			BaseUrl = "https://petbox.test",
			ApiKey = "key",
			RefreshInterval = TimeSpan.Zero,
			CacheDirectory = cacheDir.Path,
			Handler = new StubHandler("""{"db":{"host":"localhost"}}""", etag: "abc"),
		};
		seed.WithTag("env", "test");
		using (var seedProvider = new PetBoxConfigProvider(seed))
			seedProvider.Load();

		// Second run: server is down, but the same (BaseUrl, tags) → same cache file.
		var options = new PetBoxConfigOptions
		{
			BaseUrl = "https://petbox.test",
			ApiKey = "key",
			RefreshInterval = TimeSpan.Zero,
			Optional = false, // required, yet a valid disk cache still boots
			CacheDirectory = cacheDir.Path,
			Handler = new ThrowingHandler(),
		};
		options.WithTag("env", "test");

		using var provider = new PetBoxConfigProvider(options);
		var act = () => provider.Load();
		act.Should().NotThrow();

		provider.TryGet("db:host", out var host).Should().BeTrue();
		host.Should().Be("localhost");
	}

	[Fact]
	public void Load_non_optional_without_cache_throws_when_server_unavailable()
	{
		using var cacheDir = new TempDir(); // empty — no cache file
		var options = new PetBoxConfigOptions
		{
			BaseUrl = "https://petbox.test",
			ApiKey = "key",
			RefreshInterval = TimeSpan.Zero,
			Optional = false,
			CacheDirectory = cacheDir.Path,
			Handler = new ThrowingHandler(),
		};
		options.WithTag("env", "test");

		using var provider = new PetBoxConfigProvider(options);
		var act = () => provider.Load();
		act.Should().Throw<HttpRequestException>();
	}

	[Fact]
	public void Load_with_cache_sends_if_none_match_and_304_keeps_data()
	{
		using var cacheDir = new TempDir();

		// Seed cache with etag "abc".
		var seed = new PetBoxConfigOptions
		{
			BaseUrl = "https://petbox.test",
			ApiKey = "key",
			RefreshInterval = TimeSpan.Zero,
			CacheDirectory = cacheDir.Path,
			Handler = new StubHandler("""{"db":{"host":"localhost"}}""", etag: "abc"),
		};
		seed.WithTag("env", "test");
		using (var seedProvider = new PetBoxConfigProvider(seed))
			seedProvider.Load();

		var conditional = new ConditionalHandler(expectedEtag: "abc");
		var options = new PetBoxConfigOptions
		{
			BaseUrl = "https://petbox.test",
			ApiKey = "key",
			RefreshInterval = TimeSpan.Zero,
			CacheDirectory = cacheDir.Path,
			Handler = conditional,
		};
		options.WithTag("env", "test");

		using var provider = new PetBoxConfigProvider(options);
		provider.Load();

		conditional.SawIfNoneMatch.Should().BeTrue();
		conditional.Returned304.Should().BeTrue();
		// Data preloaded from cache survives the 304.
		provider.TryGet("db:host", out var host).Should().BeTrue();
		host.Should().Be("localhost");
	}

	[Fact]
	public void Load_ignores_corrupt_cache_file()
	{
		using var cacheDir = new TempDir();

		// Seed a real cache file, then corrupt it.
		var seed = new PetBoxConfigOptions
		{
			BaseUrl = "https://petbox.test",
			ApiKey = "key",
			RefreshInterval = TimeSpan.Zero,
			CacheDirectory = cacheDir.Path,
			Handler = new StubHandler("""{"db":{"host":"localhost"}}""", etag: "abc"),
		};
		seed.WithTag("env", "test");
		using (var seedProvider = new PetBoxConfigProvider(seed))
			seedProvider.Load();

		var cacheFile = Directory.GetFiles(cacheDir.Path, "*.json").Single();
		File.WriteAllText(cacheFile, "{ this is not valid json ]");

		// Corrupt cache == no cache: a down server with Optional=false must throw.
		var options = new PetBoxConfigOptions
		{
			BaseUrl = "https://petbox.test",
			ApiKey = "key",
			RefreshInterval = TimeSpan.Zero,
			Optional = false,
			CacheDirectory = cacheDir.Path,
			Handler = new ThrowingHandler(),
		};
		options.WithTag("env", "test");

		using var provider = new PetBoxConfigProvider(options);
		var act = () => provider.Load();
		act.Should().Throw<HttpRequestException>();
	}

	[Fact]
	public void WithTag_returns_self_for_chaining()
	{
		var options = new PetBoxConfigOptions();
		var result = options.WithTag("env", "prod").WithTag("region", "eu");

		result.Should().BeSameAs(options);
		options.Tags.Should().Contain(new KeyValuePair<string, string>("env", "prod"));
		options.Tags.Should().Contain(new KeyValuePair<string, string>("region", "eu"));
	}

	sealed class StubHandler(string body, string? etag) : HttpMessageHandler
	{
		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			var response = new HttpResponseMessage(HttpStatusCode.OK)
			{
				Content = new StringContent(body),
			};
			if (etag is not null)
				response.Headers.ETag = new EntityTagHeaderValue($"\"{etag}\"");
			return Task.FromResult(response);
		}
	}

	sealed class ThrowingHandler : HttpMessageHandler
	{
		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
			throw new HttpRequestException("simulated network error");
	}

	// Returns 304 when the request carries the expected If-None-Match, else 200. Records what
	// it saw so tests can assert conditional-GET behaviour.
	sealed class ConditionalHandler(string expectedEtag) : HttpMessageHandler
	{
		public bool SawIfNoneMatch { get; private set; }
		public bool Returned304 { get; private set; }

		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			var match = request.Headers.IfNoneMatch.FirstOrDefault();
			if (match is not null)
			{
				SawIfNoneMatch = true;
				if (match.Tag.Trim('"') == expectedEtag)
				{
					Returned304 = true;
					return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotModified));
				}
			}

			var response = new HttpResponseMessage(HttpStatusCode.OK)
			{
				Content = new StringContent("""{"db":{"host":"localhost"}}"""),
			};
			response.Headers.ETag = new EntityTagHeaderValue($"\"{expectedEtag}\"");
			return Task.FromResult(response);
		}
	}

	// Throwaway temp directory for the disk cache, removed on dispose.
	sealed class TempDir : IDisposable
	{
		public string Path { get; }

		public TempDir()
		{
			Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "petbox-cfg-cache-tests", Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(Path);
		}

		public void Dispose()
		{
			try { Directory.Delete(Path, recursive: true); }
			catch { /* best-effort cleanup */ }
		}
	}
}
