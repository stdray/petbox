using System.Net;
using System.Text;
using PetBox.Client;

namespace PetBox.Client.Tests;

public sealed class PetBoxDataClientTests
{
	static PetBoxClient NewClient(CapturingHandler handler) => new(new PetBoxClientOptions
	{
		Endpoint = "https://petbox.test",
		ApiKey = "key-123",
		Handler = handler,
	});

	[Fact]
	public async Task QueryAsync_PostsToQueryPath_WithAuthHeader_AndMapsRowTypes()
	{
		var handler = new CapturingHandler("""[{"id":1,"film":"Matrix","score":8.7,"active":true,"note":null}]""");
		using var client = NewClient(handler);

		var rows = await client.Data.QueryAsync("kpvotes", "cache", "SELECT * FROM votes WHERE id = @id",
			[new PetBoxSqlParam("@id", 1)]);

		// Request shape.
		handler.LastRequest!.Method.Should().Be(HttpMethod.Post);
		handler.LastRequest.RequestUri!.AbsolutePath.Should().Be("/api/data/kpvotes/cache/query");
		handler.LastRequest.Headers.GetValues("X-Api-Key").Should().ContainSingle().Which.Should().Be("key-123");
		handler.LastBody.Should().Contain("SELECT * FROM votes").And.Contain("@id");

		// Row mapping: number→long/double, bool, null.
		rows.Should().ContainSingle();
		rows[0]["id"].Should().Be(1L);
		rows[0]["film"].Should().Be("Matrix");
		rows[0]["score"].Should().Be(8.7);
		rows[0]["active"].Should().Be(true);
		rows[0]["note"].Should().BeNull();
	}

	[Fact]
	public async Task ExecAsync_PostsToExecPath_ReturnsAffected()
	{
		var handler = new CapturingHandler("""{"affected":3}""");
		using var client = NewClient(handler);

		var affected = await client.Data.ExecAsync("kpvotes", "cache", "DELETE FROM votes");

		handler.LastRequest!.RequestUri!.AbsolutePath.Should().Be("/api/data/kpvotes/cache/exec");
		affected.Should().Be(3);
	}

	[Fact]
	public async Task ExecAsync_TimeoutSeconds_SetsHeader()
	{
		var handler = new CapturingHandler("""{"affected":0}""");
		using var client = NewClient(handler);

		await client.Data.ExecAsync("kpvotes", "cache", "SELECT 1", timeoutSeconds: 60);

		handler.LastRequest!.Headers.GetValues("X-PetBox-Timeout-Seconds").Should().ContainSingle().Which.Should().Be("60");
	}

	[Fact]
	public async Task CreateDbAsync_PostsToDbs_WithName()
	{
		var handler = new CapturingHandler("""{"name":"cache","maxPageCount":262144}""", HttpStatusCode.Created);
		using var client = NewClient(handler);

		await client.Data.CreateDbAsync("kpvotes", "cache", description: "vote cache", maxPageCount: 262144);

		handler.LastRequest!.RequestUri!.AbsolutePath.Should().Be("/api/data/kpvotes/dbs");
		handler.LastBody.Should().Contain("\"name\":\"cache\"").And.Contain("\"maxPageCount\":262144");
	}

	[Fact]
	public async Task ApplySchemaAsync_PostsToSchema_WithNameAndSql()
	{
		var handler = new CapturingHandler("""{"applied":true}""");
		using var client = NewClient(handler);

		await client.Data.ApplySchemaAsync("kpvotes", "cache", "M001", "CREATE TABLE t (id INTEGER)");

		handler.LastRequest!.RequestUri!.AbsolutePath.Should().Be("/api/data/kpvotes/cache/schema");
		handler.LastBody.Should().Contain("\"name\":\"M001\"").And.Contain("CREATE TABLE t");
	}

	[Fact]
	public async Task NonSuccess_ThrowsPetBoxClientException_WithStatusAndBody()
	{
		var handler = new CapturingHandler("""{"error":"DataDb not found"}""", HttpStatusCode.NotFound);
		using var client = NewClient(handler);

		var act = async () => await client.Data.QueryAsync("kpvotes", "nope", "SELECT 1");

		var ex = (await act.Should().ThrowAsync<PetBoxClientException>()).Which;
		ex.StatusCode.Should().Be(HttpStatusCode.NotFound);
		ex.ResponseBody.Should().Contain("DataDb not found");
	}

	sealed class CapturingHandler(string responseBody, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
	{
		public HttpRequestMessage? LastRequest { get; private set; }
		public string? LastBody { get; private set; }

		protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			LastRequest = request;
			if (request.Content is not null)
				LastBody = await request.Content.ReadAsStringAsync(cancellationToken);
			return new HttpResponseMessage(status)
			{
				Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
			};
		}
	}
}
