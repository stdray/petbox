using LinqToDB;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Data.Sqlite;
using PetBox.Core.Data;
using PetBox.Core.Features;
using PetBox.Core.Models;
using PetBox.Data;
using PetBox.Data.Schema;

namespace PetBox.Web.Pages.Admin;

// Detail page for a single DataDb: introspects tables via PRAGMA, lists
// applied migrations from __SchemaVersions, and exposes a paste-migration
// form. Apply uses SchemaRunner directly (the HTTP /schema endpoint is
// ApiKey-auth, this page is cookie-auth via WorkspaceAdmin policy).
[Authorize(Policy = "WorkspaceAdmin")]
public sealed class ProjectDataDbModel : PageModel
{
	readonly PetBoxDb _db;
	readonly FeatureFlags _features;
	readonly IDataDbFactory _factory;
	readonly SchemaRunner _runner;

	public ProjectDataDbModel(PetBoxDb db, FeatureFlags features, IDataDbFactory factory, SchemaRunner runner)
	{
		_db = db;
		_features = features;
		_factory = factory;
		_runner = runner;
	}

	// authz-bypass-project-create: route-only bind — see Admin/Projects.cshtml.cs for why.
	[FromRoute(Name = "workspaceKey")] public string WorkspaceKey { get; set; } = string.Empty;
	[FromRoute(Name = "projectKey")] public string ProjectKey { get; set; } = string.Empty;
	[FromRoute(Name = "dbName")] public string DbName { get; set; } = string.Empty;

	public DataDb? Db { get; private set; }
	public bool DbNotFound { get; private set; }
	public IReadOnlyList<TableInfo> Tables { get; private set; } = [];
	public IReadOnlyList<MigrationRow> Migrations { get; private set; } = [];
	public string? ErrorMessage { get; private set; }

	public async Task<IActionResult> OnGetAsync()
	{
		if (!_features.IsEnabled(Feature.Data)) return base.NotFound();

		Db = await _db.DataDbs.FirstOrDefaultAsync(
			(DataDb d) => d.ProjectKey == ProjectKey && d.Name == DbName);
		if (Db is null) { DbNotFound = true; return Page(); }

		await PopulateAsync();
		return Page();
	}

	public async Task<IActionResult> OnPostApplyAsync(string name, string sql)
	{
		if (!_features.IsEnabled(Feature.Data)) return base.NotFound();

		Db = await _db.DataDbs.FirstOrDefaultAsync(
			(DataDb d) => d.ProjectKey == ProjectKey && d.Name == DbName);
		if (Db is null) { DbNotFound = true; return Page(); }

		if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(sql))
		{
			ErrorMessage = "Migration name and SQL are required.";
			await PopulateAsync();
			return Page();
		}

		var cs = _factory.GetConnectionString(ProjectKey, DbName);
		var result = _runner.Apply(cs, name, sql);
		switch (result.Kind)
		{
			case SchemaApplyKind.Applied:
				// PRG: a successful migration redirects to the clean DB URL (no lingering
				// ?handler=Apply that a refresh would re-POST), carrying the success notice.
				this.NotifySuccess($"Applied {name} (hash {result.Hash[..8]}…).");
				return RedirectToPage(new { workspaceKey = WorkspaceKey, projectKey = ProjectKey, dbName = DbName });
			case SchemaApplyKind.AlreadyApplied:
				this.NotifySuccess($"{name} already applied — no-op.");
				return RedirectToPage(new { workspaceKey = WorkspaceKey, projectKey = ProjectKey, dbName = DbName });
			case SchemaApplyKind.Conflict:
				ErrorMessage = $"Conflict: {name} already exists with a different hash. "
					+ $"Existing {result.ExistingHash![..8]}… vs provided {result.Hash[..8]}…";
				break;
			case SchemaApplyKind.Failed:
				ErrorMessage = "Failed: " + (result.Error ?? "unknown error");
				break;
		}

		// Failure paths re-render in place so the pasted SQL and error stay together.
		await PopulateAsync();
		return Page();
	}

	async Task PopulateAsync()
	{
		var cs = _factory.GetConnectionString(ProjectKey, DbName);
		await using var conn = new SqliteConnection(cs);
		await conn.OpenAsync();

		var tables = new List<TableInfo>();
		await using (var cmd = conn.CreateCommand())
		{
			cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' "
				+ "AND name NOT LIKE 'sqlite_%' AND name <> @journal ORDER BY name";
			var p = cmd.CreateParameter();
			p.ParameterName = "@journal";
			p.Value = SchemaRunner.JournalTableName;
			cmd.Parameters.Add(p);
			await using var reader = await cmd.ExecuteReaderAsync();
			while (await reader.ReadAsync()) tables.Add(new TableInfo(reader.GetString(0), Array.Empty<string>()));
		}
		// Resolve columns per table.
		for (var i = 0; i < tables.Count; i++)
		{
			var cols = new List<string>();
			await using var cmd = conn.CreateCommand();
			cmd.CommandText = $"PRAGMA table_info({tables[i].Name})";
			await using var reader = await cmd.ExecuteReaderAsync();
			while (await reader.ReadAsync())
				cols.Add(reader.GetString(1) + " " + reader.GetString(2));
			tables[i] = tables[i] with { Columns = cols };
		}
		Tables = tables;

		// Migrations: read journal if present.
		var migs = new List<MigrationRow>();
		await using (var existsCmd = conn.CreateCommand())
		{
			existsCmd.CommandText = $"SELECT name FROM sqlite_master WHERE type='table' AND name='{SchemaRunner.JournalTableName}'";
			if (await existsCmd.ExecuteScalarAsync() is not null)
			{
				await using var cmd = conn.CreateCommand();
				cmd.CommandText = $"SELECT SchemaVersionID, ScriptName, Applied, Hash FROM {SchemaRunner.JournalTableName} ORDER BY SchemaVersionID DESC";
				await using var reader = await cmd.ExecuteReaderAsync();
				while (await reader.ReadAsync())
				{
					migs.Add(new MigrationRow(
						Id: reader.GetInt64(0),
						Name: reader.GetString(1),
						Applied: reader.GetDateTime(2),
						Hash: reader.GetString(3)));
				}
			}
		}
		Migrations = migs;
	}

	public sealed record TableInfo(string Name, IReadOnlyList<string> Columns);
	public sealed record MigrationRow(long Id, string Name, DateTime Applied, string Hash);
}
