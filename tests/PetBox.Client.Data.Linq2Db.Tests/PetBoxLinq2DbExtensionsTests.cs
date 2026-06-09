using LinqToDB;
using LinqToDB.Data;
using LinqToDB.DataProvider.SQLite;
using LinqToDB.Mapping;

namespace PetBox.Client.Data.Linq2Db.Tests;

public sealed class PetBoxLinq2DbExtensionsTests
{
	[Table("votes")]
	sealed class Vote
	{
		[Column] public int Id { get; set; }
		[Column] public string Film { get; set; } = "";
		[Column] public double Score { get; set; }
	}

	[Fact]
	public async Task QueryAsync_ExtractsParameterizedSql_SendsToCore_MaterializesRows()
	{
		var handler = new CapturingHandler("""[{"Id":1,"Film":"Matrix","Score":8.7}]""");
		using var client = new PetBoxClient(new PetBoxClientOptions
		{
			Endpoint = "https://petbox.test",
			ApiKey = "key",
			Handler = handler,
		});

		// SQLite dialect only — no connection is opened; ToSqlQuery just generates the SQL.
		using var dc = new DataConnection(new DataOptions().UseSQLite("Data Source=:memory:", SQLiteProvider.Microsoft));
		var film = "Matrix"; // captured local → parameterized (not inlined)
		var query = dc.GetTable<Vote>().Where(v => v.Film == film).OrderBy(v => v.Id);

		var rows = await client.Data.QueryAsync("kpvotes", "cache", query);

		// Hit the Data query endpoint with the generated SQL (references the table + a parameter).
		handler.LastRequest!.RequestUri!.AbsolutePath.Should().Be("/api/data/kpvotes/cache/query");
		handler.LastBody.Should().Contain("votes");
		handler.LastBody.Should().Contain("Matrix"); // parameter value serialized in the request body

		// Rows materialized into T (long→int via Convert, string, double).
		rows.Should().ContainSingle();
		rows[0].Id.Should().Be(1);
		rows[0].Film.Should().Be("Matrix");
		rows[0].Score.Should().Be(8.7);
	}

	sealed class CapturingHandler(string responseBody) : HttpMessageHandler
	{
		public HttpRequestMessage? LastRequest { get; private set; }
		public string? LastBody { get; private set; }

		protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			LastRequest = request;
			if (request.Content is not null)
				LastBody = await request.Content.ReadAsStringAsync(cancellationToken);
			return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
			{
				Content = new StringContent(responseBody, System.Text.Encoding.UTF8, "application/json"),
			};
		}
	}
}
