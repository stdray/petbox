using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace PetBox.Tests.Web;

public sealed class HealthEndpointTests : IAsyncLifetime
{
	readonly WebApplicationFactory<Program> _factory;
	HttpClient _client = null!;

	public HealthEndpointTests()
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

	[Theory]
	[InlineData("/health")]
	[InlineData("/version")]
	public async Task Get_Anonymous_ReturnsOk(string path)
	{
		using var resp = await _client.GetAsync(path);
		resp.StatusCode.Should().Be(HttpStatusCode.OK,
			$"AllowAnonymous() on the minimal-API {path} endpoint must return 200");
	}

	[Theory]
	[InlineData("/health")]
	[InlineData("/version")]
	public async Task Head_Anonymous_ReturnsOk(string path)
	{
		using var req = new HttpRequestMessage(HttpMethod.Head, path);
		using var resp = await _client.SendAsync(req);
		resp.StatusCode.Should().Be(HttpStatusCode.OK,
			$"HEAD {path} must return 200 — MapMethods must include HEAD alongside GET for health probes");
	}
}
