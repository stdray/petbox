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
}
