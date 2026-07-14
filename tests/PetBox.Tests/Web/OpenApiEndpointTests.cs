using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.OpenApi;

namespace PetBox.Tests.Web;

// Bug openapi-not-served-in-prod: app.MapOpenApi() used to be wrapped in
// `if (app.Environment.IsDevelopment())`, so a production-like deployment exposed no live
// OpenAPI endpoint — languages with no client SDK had no schema and no link to one. Program.cs
// now maps it unconditionally + AllowAnonymous(). "Testing" (used everywhere else in this test
// suite) is deliberately NOT "Development", so this exercises exactly the environment that used
// to 404.
public sealed class OpenApiEndpointTests : IAsyncLifetime
{
	readonly WebApplicationFactory<Program> _factory;
	HttpClient _client = null!;

	public OpenApiEndpointTests()
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

	[Fact]
	public async Task Get_InNonDevelopmentEnvironment_ReturnsOk()
	{
		using var resp = await _client.GetAsync("/openapi/v1.json");
		resp.StatusCode.Should().Be(HttpStatusCode.OK,
			"MapOpenApi() must be reachable outside Development (bug openapi-not-served-in-prod) — " +
			"languages with no client SDK have no other way to get the schema");
	}

	[Fact]
	public async Task Get_Anonymous_DoesNotRequireAuth()
	{
		// No X-Api-Key, no cookie: the schema describes the API surface, not a secret — every
		// endpoint it documents still enforces its own auth policy unchanged.
		using var resp = await _client.GetAsync("/openapi/v1.json");
		resp.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);
		resp.StatusCode.Should().NotBe(HttpStatusCode.Forbidden);
	}

	[Fact]
	public async Task Get_ReturnsAValidOpenApiDocument()
	{
		using var resp = await _client.GetAsync("/openapi/v1.json");
		var json = await resp.Content.ReadAsStringAsync();

		var result = OpenApiDocument.Parse(json, "json");

		result.Document.Should().NotBeNull("the response body must parse as a well-formed OpenAPI document");
		result.Document!.Info.Should().NotBeNull();
		result.Document.Paths.Should().NotBeEmpty("the document must describe at least one real route");
	}
}
