using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PetBox.Deploy.Contract;
using PetBox.Tests.Migrations;

namespace PetBox.Tests.Web;

// THE PRODUCTION DEPLOY, REHEARSED END TO END — not the migration in isolation.
//
// ApiKeyHostIdMigrationTests proves the UPDATE moves the row. This proves the thing anyone actually
// cares about on deploy day: a database in exactly the state petbox.3po.su is in right now (schema
// v49, one node key for the live host `local-pc` with the node id sitting in ProjectKey) is handed
// to the NEW build, whose startup runs the migration, and afterwards the node's EXISTING key — the
// same secret the agent already holds, never re-minted — still polls successfully.
//
// That sequencing is the whole point. The migration is applied by application startup
// (Program.cs runs the core migration set before serving), so schema and code advance together
// inside one container start; there is no window in which new code reads a column an old database
// does not have. If this test can poll, the live node can poll.
public sealed class NodeKeyMigrationRehearsalTests : IDisposable
{
	// The real fleet host and the real key shape on petbox.3po.su (deploy_node_list, 2026-08-20):
	// one node, ephemeral, zero deployments, key named `node:local-pc`.
	const string LiveNodeId = "local-pc";
	const string LiveNodeKeyRef = "node:local-pc";
	const string LiveNodeSecret = "yb_key_node_0123456789abcdef0123456789abcdef";

	readonly string _dir;
	readonly string _cs;

	public NodeKeyMigrationRehearsalTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-m050-live-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		// A COPY of the shared v49 template — see PreM050CoreDb for why this is not built per test.
		_cs = PreM050CoreDb.CopyTo(Path.Combine(_dir, "petbox.db")) + ";Cache=Shared";
	}

	public void Dispose()
	{
		SqliteConnection.ClearPool(new SqliteConnection(_cs));
		TestDirs.CleanupOrDefer(_dir);
	}

	[Fact]
	public async Task Existing_Node_Key_Still_Polls_After_The_Deploy_Migrates_The_Database()
	{
		// 1) The database as production has it TODAY: schema v49, node id parked in ProjectKey.
		SeedTheLiveNodeKeyAsProductionHasIt();

		// 2) The new build starts against that file. Startup applies M050 — this is the deploy.
		await using var factory = BootNewBuild();
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

		// The node must exist in the deploy tier for poll to describe it, exactly as it does live.
		using (var scope = factory.Services.CreateScope())
			await scope.ServiceProvider.GetRequiredService<IDeployService>()
				.UpsertNodeAsync(new NodeInput(LiveNodeId, "local-pc", "", true, LiveNodeKeyRef));

		// 3) The agent polls with the key it ALREADY HOLDS — unchanged, not re-enrolled.
		using var req = new HttpRequestMessage(HttpMethod.Get, "/agent/poll");
		req.Headers.Add("X-Api-Key", LiveNodeSecret);
		using var resp = await client.SendAsync(req);

		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
		doc.RootElement.GetProperty("nodeId").GetString().Should().Be(LiveNodeId);

		// And the row really did move — the node is no longer masquerading as a tenant.
		Row().Should().Be((string.Empty, LiveNodeId));
	}

	// The db file is ALREADY at v49 (the constructor copied the template); what is left is the row
	// production has in it — written the way the pre-M050 mint path wrote it.
	void SeedTheLiveNodeKeyAsProductionHasIt()
	{
		using var conn = new SqliteConnection(_cs);
		conn.Open();
		using var cmd = conn.CreateCommand();
		cmd.CommandText =
			"INSERT INTO ApiKeys (Key, ProjectKey, Scopes, Name, CreatedAt) VALUES " +
			$"('{LiveNodeSecret}', '{LiveNodeId}', 'agent:poll,agent:heartbeat,logs:ingest', '{LiveNodeKeyRef}', '2026-01-01');";
		cmd.ExecuteNonQuery();
	}

	WebApplicationFactory<Program> BootNewBuild() =>
		new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
		{
			b.UseEnvironment("Testing");
			b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
			{
				["ConnectionStrings:PetBox"] = _cs,
				["Host:BackgroundServices"] = "false",
			}));
		});

	(string ProjectKey, string? HostId) Row()
	{
		using var conn = new SqliteConnection(_cs);
		conn.Open();
		using var cmd = conn.CreateCommand();
		cmd.CommandText = $"SELECT ProjectKey, HostId FROM ApiKeys WHERE Key = '{LiveNodeSecret}';";
		using var reader = cmd.ExecuteReader();
		reader.Read().Should().BeTrue();
		return (reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1));
	}
}
