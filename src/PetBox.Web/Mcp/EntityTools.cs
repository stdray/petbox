using System.ComponentModel;
using System.Text.Json;
using System.Text.RegularExpressions;
using LinqToDB;
using LinqToDB.Async;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using ModelContextProtocol.Server;
using PetBox.Config;
using PetBox.Config.Data;
using PetBox.Core.Auth;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Data;
using PetBox.Data.Schema;
using PetBox.Log.Core.Data;

namespace PetBox.Web.Mcp;

// Generic entity CRUD over MCP (named-logs Phase 2). One tool family —
// entity.create / entity.list / entity.delete / entity.describe — dispatches on
// a `type` discriminator to a per-type handler. This collapses the old
// per-entity tools (workspace.create_project, project.create_apikey,
// project.set_config_binding, data.{list,create,delete,describe}_db) into a
// uniform surface. Type-specific *operations* stay separate tools where they
// don't fit CRUD: data.query / data.exec / data.schema_apply, log.query.
//
// Each handler owns its own auth. Two families:
//   • provisioning (project, apikey, config_binding): admin:provision scope,
//     no per-project claim — these are cross-project onboarding ops.
//   • project-scoped (db, log): project-claim cross-check + module scope
//     (data:* / logs:*), same as the REST endpoints.
//
// Unsupported (type, op) pairs throw NotSupportedException — e.g. project has no
// delete (avoids silently orphaning logs/dbs/keys), only db supports describe.
[McpServerToolType]
public static class EntityTools
{
	// Shared key spec for project keys (db/log names are validated by their own stores).
	static readonly Regex KeyRegex = new("^[a-z][a-z0-9_-]{0,99}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

	static readonly IReadOnlyDictionary<string, EntityHandler> Handlers =
		new Dictionary<string, EntityHandler>(StringComparer.Ordinal)
		{
			["project"] = new ProjectHandler(),
			["apikey"] = new ApiKeyHandler(),
			["config_binding"] = new ConfigBindingHandler(),
			["db"] = new DbHandler(),
			["log"] = new LogHandler(),
		};

	static EntityHandler Resolve(string type)
	{
		if (string.IsNullOrWhiteSpace(type))
			throw new ArgumentException("type is required");
		return Handlers.TryGetValue(type, out var h)
			? h
			: throw new ArgumentException($"Unknown entity type '{type}'. Known: {string.Join(", ", Handlers.Keys)}");
	}

	[McpServerTool(Name = "entity.create", Title = "Create an entity")]
	[Description("""
		Creates an entity of `type` from the `props` object.
		  • project (admin:provision): props { workspaceKey, key, name, description? }
		  • apikey (admin:provision): props { projectKey, name, scopes, expiresInSeconds? } — returns the raw key ONCE
		  • config_binding (admin:provision): props { workspaceKey, path, value, tags, kind? } — tags must include 'ws:{workspaceKey}'; kind is 'Plain' (default) or 'Secret' (value stored encrypted; needs PETBOX_MASTER_KEY)
		  • db (data:schema, project-scoped): props { projectKey, name, description?, maxPageCount? }
		  • log (logs:admin, project-scoped): props { projectKey, name, description? }
		""")]
	public static Task<object> CreateAsync(
		IHttpContextAccessor http, PetBoxDb db, IDataDbFactory dataFactory,
		IConfigDbFactory configFactory, ILogStore logStore, ISecretEncryptor secrets,
		[Description("Entity type: project | apikey | config_binding | db | log.")] string type,
		[Description("Type-specific fields as a JSON object. See the tool description.")] JsonElement props,
		CancellationToken ct = default)
		=> ModuleMcp.GuardAsync(() => Resolve(type).CreateAsync(Ctx(http, db, dataFactory, configFactory, logStore, secrets), Normalize(props), ct));

	[McpServerTool(Name = "entity.list", Title = "List entities", ReadOnly = true)]
	[Description("""
		Lists entities of `type`. `filter` scopes the listing:
		  • project (admin:provision): filter { workspaceKey? } — all projects, optionally one workspace
		  • apikey (admin:provision): filter { projectKey }
		  • config_binding (admin:provision): filter { workspaceKey }
		  • db (data:read, project-scoped): filter { projectKey }
		  • log (logs:query, project-scoped): filter { projectKey }
		""")]
	public static Task<object> ListAsync(
		IHttpContextAccessor http, PetBoxDb db, IDataDbFactory dataFactory,
		IConfigDbFactory configFactory, ILogStore logStore, ISecretEncryptor secrets,
		[Description("Entity type: project | apikey | config_binding | db | log.")] string type,
		[Description("Optional scope/filter as a JSON object. See the tool description.")] JsonElement? filter = null,
		CancellationToken ct = default)
		=> ModuleMcp.GuardAsync(() => Resolve(type).ListAsync(Ctx(http, db, dataFactory, configFactory, logStore, secrets), Normalize(filter ?? default), ct));

	[McpServerTool(Name = "entity.delete", Title = "Delete an entity", Destructive = true)]
	[Description("""
		Deletes the entity identified by `key`. `project` does NOT support delete.
		  • apikey (admin:provision): key { key }
		  • config_binding (admin:provision): key { workspaceKey, id } — soft-delete
		  • db (data:schema, project-scoped): key { projectKey, name }
		  • log (logs:admin, project-scoped): key { projectKey, name }
		""")]
	public static Task<object> DeleteAsync(
		IHttpContextAccessor http, PetBoxDb db, IDataDbFactory dataFactory,
		IConfigDbFactory configFactory, ILogStore logStore, ISecretEncryptor secrets,
		[Description("Entity type: apikey | config_binding | db | log.")] string type,
		[Description("Identifier fields as a JSON object. See the tool description.")] JsonElement key,
		CancellationToken ct = default)
		=> ModuleMcp.GuardAsync(() => Resolve(type).DeleteAsync(Ctx(http, db, dataFactory, configFactory, logStore, secrets), Normalize(key), ct));

	[McpServerTool(Name = "entity.describe", Title = "Describe an entity", ReadOnly = true)]
	[Description("""
		Returns structural detail for the entity identified by `key`. Only `db`
		supports describe (returns tables + columns); other types throw.
		  • db (data:read, project-scoped): key { projectKey, dbName }
		""")]
	public static Task<object> DescribeAsync(
		IHttpContextAccessor http, PetBoxDb db, IDataDbFactory dataFactory,
		IConfigDbFactory configFactory, ILogStore logStore, ISecretEncryptor secrets,
		[Description("Entity type: db.")] string type,
		[Description("Identifier fields as a JSON object. See the tool description.")] JsonElement key,
		CancellationToken ct = default)
		=> ModuleMcp.GuardAsync(() => Resolve(type).DescribeAsync(Ctx(http, db, dataFactory, configFactory, logStore, secrets), Normalize(key), ct));

	static EntityCtx Ctx(IHttpContextAccessor http, PetBoxDb db, IDataDbFactory dataFactory,
		IConfigDbFactory configFactory, ILogStore logStore, ISecretEncryptor secrets)
		=> new(http.HttpContext ?? throw new InvalidOperationException("No HttpContext"), db, dataFactory, configFactory, logStore, secrets);

	// --- Context ---------------------------------------------------------

	internal readonly record struct EntityCtx(
		HttpContext Http, PetBoxDb Db, IDataDbFactory DataFactory,
		IConfigDbFactory ConfigFactory, ILogStore LogStore, ISecretEncryptor Secrets);

	// --- Handler base ----------------------------------------------------

	abstract class EntityHandler
	{
		public abstract string Type { get; }
		public virtual Task<object> CreateAsync(EntityCtx c, JsonElement props, CancellationToken ct) => Forbidden("create");
		public virtual Task<object> ListAsync(EntityCtx c, JsonElement filter, CancellationToken ct) => Forbidden("list");
		public virtual Task<object> DeleteAsync(EntityCtx c, JsonElement key, CancellationToken ct) => Forbidden("delete");
		public virtual Task<object> DescribeAsync(EntityCtx c, JsonElement key, CancellationToken ct) => Forbidden("describe");
		protected Task<object> Forbidden(string op) =>
			throw new NotSupportedException($"entity type '{Type}' does not support '{op}'");
	}

	// --- project ---------------------------------------------------------

	sealed class ProjectHandler : EntityHandler
	{
		public override string Type => "project";

		public override async Task<object> CreateAsync(EntityCtx c, JsonElement props, CancellationToken ct)
		{
			AssertScope(c.Http, ApiKeyScopes.AdminProvision);
			var workspaceKey = ReqStr(props, "workspaceKey");
			var key = ReqKey(props, "key");
			var name = OptStr(props, "name");
			var description = OptStr(props, "description");

			if (!await c.Db.Workspaces.AnyAsync((Workspace w) => w.Key == workspaceKey, ct))
				throw new InvalidOperationException($"Workspace '{workspaceKey}' not found");
			if (await c.Db.Projects.AnyAsync((Project p) => p.Key == key, ct))
				throw new InvalidOperationException($"Project '{key}' already exists");

			await c.Db.InsertAsync(new Project
			{
				Key = key,
				WorkspaceKey = workspaceKey,
				Name = string.IsNullOrWhiteSpace(name) ? key : name!,
				Description = description ?? string.Empty,
			}, token: ct);
			return new { key, workspaceKey, name, description };
		}

		public override async Task<object> ListAsync(EntityCtx c, JsonElement filter, CancellationToken ct)
		{
			AssertScope(c.Http, ApiKeyScopes.AdminProvision);
			var workspaceKey = OptStr(filter, "workspaceKey");
			var q = c.Db.Projects.AsQueryable();
			if (!string.IsNullOrEmpty(workspaceKey))
				q = q.Where(p => p.WorkspaceKey == workspaceKey);
			var rows = await q.OrderBy(p => p.Key)
				.Select(p => new { p.Key, p.WorkspaceKey, p.Name, p.Description })
				.ToListAsync(ct);
			return new { projects = rows };
		}
	}

	// --- apikey ----------------------------------------------------------

	sealed class ApiKeyHandler : EntityHandler
	{
		public override string Type => "apikey";

		public override async Task<object> CreateAsync(EntityCtx c, JsonElement props, CancellationToken ct)
		{
			AssertScope(c.Http, ApiKeyScopes.AdminProvision);
			var projectKey = ReqStr(props, "projectKey");
			var name = OptStr(props, "name");
			var scopes = ReqStr(props, "scopes");
			var expiresInSeconds = OptLong(props, "expiresInSeconds");

			if (!await c.Db.Projects.AnyAsync((Project p) => p.Key == projectKey, ct))
				throw new InvalidOperationException($"Project '{projectKey}' not found");

			var (valid, invalid) = ApiKeyScopes.Validate(scopes);
			if (invalid.Count > 0) throw new ArgumentException($"Unknown scopes: {string.Join(", ", invalid)}");
			if (valid.Count == 0) throw new ArgumentException("At least one valid scope is required");

			var keyValue = $"yb_key_{Guid.NewGuid():N}";
			DateTime? expiresAt = expiresInSeconds is { } secs and > 0 ? DateTime.UtcNow.AddSeconds(secs) : null;

			await c.Db.InsertAsync(new ApiKey
			{
				Key = keyValue,
				ProjectKey = projectKey,
				Scopes = string.Join(',', valid),
				Name = string.IsNullOrWhiteSpace(name) ? "agent-minted" : name!.Trim(),
				CreatedAt = DateTime.UtcNow,
				ExpiresAt = expiresAt,
			}, token: ct);
			return new { key = keyValue, projectKey, scopes = valid, expiresAt };
		}

		public override async Task<object> ListAsync(EntityCtx c, JsonElement filter, CancellationToken ct)
		{
			AssertScope(c.Http, ApiKeyScopes.AdminProvision);
			var projectKey = ReqStr(filter, "projectKey");
			var rows = await c.Db.ApiKeys
				.Where(k => k.ProjectKey == projectKey)
				.OrderBy(k => k.CreatedAt)
				.Select(k => new { k.Key, k.Name, k.Scopes, k.CreatedAt, k.ExpiresAt })
				.ToListAsync(ct);
			return new { keys = rows };
		}

		public override async Task<object> DeleteAsync(EntityCtx c, JsonElement key, CancellationToken ct)
		{
			AssertScope(c.Http, ApiKeyScopes.AdminProvision);
			var keyValue = ReqStr(key, "key");
			var deleted = await c.Db.ApiKeys.Where(k => k.Key == keyValue).DeleteAsync(ct);
			if (deleted == 0) throw new InvalidOperationException("ApiKey not found");
			return new { deleted = true, key = keyValue };
		}
	}

	// --- config_binding --------------------------------------------------

	sealed class ConfigBindingHandler : EntityHandler
	{
		public override string Type => "config_binding";

		public override async Task<object> CreateAsync(EntityCtx c, JsonElement props, CancellationToken ct)
		{
			AssertScope(c.Http, ApiKeyScopes.AdminProvision);
			var workspaceKey = ReqStr(props, "workspaceKey");
			var path = ReqStr(props, "path");
			var value = OptStr(props, "value") ?? string.Empty;
			var tags = ReqStr(props, "tags");
			var kind = ParseKind(OptStr(props, "kind"));

			if (!tags.Contains($"ws:{workspaceKey}", StringComparison.OrdinalIgnoreCase))
				throw new ArgumentException($"Tags must include 'ws:{workspaceKey}'");

			// A Secret is stored encrypted (ciphertext/iv/authTag), never as plaintext
			// in Value — mirrors the config Editor's write path so resolve can decrypt.
			var storedValue = value;
			string? cipher = null, iv = null, authTag = null;
			if (kind == BindingKind.Secret)
			{
				if (!c.Secrets.IsAvailable)
					throw new InvalidOperationException("Secret bindings require PETBOX_MASTER_KEY to be configured.");
				var bundle = c.Secrets.Encrypt(value);
				(cipher, iv, authTag) = (bundle.Ciphertext, bundle.Iv, bundle.AuthTag);
				storedValue = string.Empty;
			}

			var now = DateTime.UtcNow;
			var configDb = c.ConfigFactory.GetConfigDb(workspaceKey);
#pragma warning disable CA2016
			var id = Convert.ToInt64(await configDb.InsertWithIdentityAsync(new ConfigBinding
			{
				Path = path,
				Value = storedValue,
				Tags = tags,
				Kind = kind,
				Ciphertext = cipher,
				Iv = iv,
				AuthTag = authTag,
				Version = 1,
				ContentHash = BindingContentHash.Compute(path, tags, kind, storedValue, cipher),
				CreatedAt = now,
				UpdatedAt = now,
			}));
#pragma warning restore CA2016
			return new { id, path, tags, kind = kind.ToString() };
		}

		static BindingKind ParseKind(string? raw)
		{
			if (string.IsNullOrWhiteSpace(raw)) return BindingKind.Plain;
			if (Enum.TryParse<BindingKind>(raw, ignoreCase: true, out var k)) return k;
			throw new ArgumentException($"Unknown kind '{raw}'. Known: {string.Join(", ", Enum.GetNames<BindingKind>())}");
		}

		public override async Task<object> ListAsync(EntityCtx c, JsonElement filter, CancellationToken ct)
		{
			AssertScope(c.Http, ApiKeyScopes.AdminProvision);
			var workspaceKey = ReqStr(filter, "workspaceKey");
			var configDb = c.ConfigFactory.GetConfigDb(workspaceKey);
			// Project the enum column raw, then stringify in memory — linq2db does not
			// translate Enum.ToString() to SQL and throws on materialization otherwise.
			var rows = await configDb.Bindings
				.Where(b => !b.IsDeleted)
				.OrderBy(b => b.Path)
				.Select(b => new { b.Id, b.Path, b.Tags, b.Kind })
				.ToListAsync(ct);
			return new { bindings = rows.Select(b => new { b.Id, b.Path, b.Tags, Kind = b.Kind.ToString() }) };
		}

		public override async Task<object> DeleteAsync(EntityCtx c, JsonElement key, CancellationToken ct)
		{
			AssertScope(c.Http, ApiKeyScopes.AdminProvision);
			var workspaceKey = ReqStr(key, "workspaceKey");
			var id = ReqLong(key, "id");
			var configDb = c.ConfigFactory.GetConfigDb(workspaceKey);
			var now = DateTime.UtcNow;
			var updated = await configDb.Bindings
				.Where(b => b.Id == id && !b.IsDeleted)
				.Set(b => b.IsDeleted, true)
				.Set(b => b.DeletedAt, (DateTime?)now)
				.Set(b => b.UpdatedAt, now)
				.UpdateAsync(ct);
			if (updated == 0) throw new InvalidOperationException("Binding not found");
			return new { deleted = true, id };
		}
	}

	// --- db --------------------------------------------------------------

	sealed class DbHandler : EntityHandler
	{
		public override string Type => "db";

		public override async Task<object> CreateAsync(EntityCtx c, JsonElement props, CancellationToken ct)
		{
			var projectKey = ReqStr(props, "projectKey");
			AssertProject(c.Http, projectKey);
			AssertScope(c.Http, ApiKeyScopes.DataSchema);
			var name = ReqStr(props, "name");
			var description = OptStr(props, "description");
			var maxPageCount = OptLong(props, "maxPageCount");

			if (await c.Db.DataDbs.AnyAsync((DataDb d) => d.ProjectKey == projectKey && d.Name == name, ct))
				throw new InvalidOperationException($"DataDb '{name}' already exists");

			var quota = maxPageCount ?? DataDbFactory.DefaultMaxPageCount;
			await c.DataFactory.CreateAsync(projectKey, name, quota, ct);
			var now = DateTime.UtcNow;
			await c.Db.InsertAsync(new DataDb
			{
				ProjectKey = projectKey,
				Name = name,
				Description = description,
				MaxPageCount = quota,
				CreatedAt = now,
				UpdatedAt = now,
			}, token: ct);
			return new { name, description, maxPageCount = quota, createdAt = now };
		}

		public override async Task<object> ListAsync(EntityCtx c, JsonElement filter, CancellationToken ct)
		{
			var projectKey = ReqStr(filter, "projectKey");
			AssertProject(c.Http, projectKey);
			AssertScope(c.Http, ApiKeyScopes.DataRead);
			var rows = await c.Db.DataDbs
				.Where(d => d.ProjectKey == projectKey)
				.OrderBy(d => d.Name)
				.Select(d => new { d.Name, d.Description, d.MaxPageCount, d.CreatedAt, d.UpdatedAt })
				.ToListAsync(ct);
			return new { dbs = rows };
		}

		public override async Task<object> DeleteAsync(EntityCtx c, JsonElement key, CancellationToken ct)
		{
			var projectKey = ReqStr(key, "projectKey");
			AssertProject(c.Http, projectKey);
			AssertScope(c.Http, ApiKeyScopes.DataSchema);
			var name = ReqStr(key, "name");
			var deleted = await c.Db.DataDbs.Where(d => d.ProjectKey == projectKey && d.Name == name).DeleteAsync(ct);
			if (deleted == 0) throw new InvalidOperationException("DataDb not found");
			c.DataFactory.TryDelete(projectKey, name);
			return new { deleted = true, name };
		}

		public override async Task<object> DescribeAsync(EntityCtx c, JsonElement key, CancellationToken ct)
		{
			var projectKey = ReqStr(key, "projectKey");
			AssertProject(c.Http, projectKey);
			AssertScope(c.Http, ApiKeyScopes.DataRead);
			var dbName = ReqStr(key, "dbName");

			var row = await c.Db.DataDbs.FirstOrDefaultAsync(
				(DataDb d) => d.ProjectKey == projectKey && d.Name == dbName, ct);
			if (row is null) throw new InvalidOperationException("DataDb not found");

			var cs = c.DataFactory.GetConnectionString(projectKey, dbName);
			await using var conn = new SqliteConnection(cs);
			await conn.OpenAsync(ct);

			var names = new List<string>();
			await using (var cmd = conn.CreateCommand())
			{
				cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' "
					+ "AND name NOT LIKE 'sqlite_%' AND name <> @journal ORDER BY name";
				var p = cmd.CreateParameter();
				p.ParameterName = "@journal";
				p.Value = SchemaRunner.JournalTableName;
				cmd.Parameters.Add(p);
				await using var reader = await cmd.ExecuteReaderAsync(ct);
				while (await reader.ReadAsync(ct)) names.Add(reader.GetString(0));
			}

			var tables = new List<object>();
			foreach (var name in names)
			{
				var cols = new List<object>();
				await using var cmd = conn.CreateCommand();
				cmd.CommandText = $"PRAGMA table_info(\"{name.Replace("\"", "\"\"")}\")";
				await using var reader = await cmd.ExecuteReaderAsync(ct);
				while (await reader.ReadAsync(ct))
					cols.Add(new { name = reader.GetString(1), type = reader.GetString(2), notNull = reader.GetInt32(3) == 1, pk = reader.GetInt32(5) > 0 });
				tables.Add(new { name, columns = cols });
			}
			return new { tables };
		}
	}

	// --- log -------------------------------------------------------------

	sealed class LogHandler : EntityHandler
	{
		public override string Type => "log";

		public override async Task<object> CreateAsync(EntityCtx c, JsonElement props, CancellationToken ct)
		{
			var projectKey = ReqStr(props, "projectKey");
			AssertProject(c.Http, projectKey);
			AssertScope(c.Http, ApiKeyScopes.LogsAdmin);
			var name = ReqStr(props, "name");
			var description = OptStr(props, "description");

			if (await c.LogStore.ExistsAsync(projectKey, name, ct))
				throw new InvalidOperationException($"Log '{name}' already exists");
			var meta = await c.LogStore.CreateAsync(projectKey, name, description, ct);
			return new { name = meta.Name, description = meta.Description, createdAt = meta.CreatedAt };
		}

		public override async Task<object> ListAsync(EntityCtx c, JsonElement filter, CancellationToken ct)
		{
			var projectKey = ReqStr(filter, "projectKey");
			AssertProject(c.Http, projectKey);
			AssertScope(c.Http, ApiKeyScopes.LogsQuery);
			var rows = await c.LogStore.ListAsync(projectKey, ct);
			return new { logs = rows.Select(l => new { l.Name, l.Description, l.CreatedAt, l.UpdatedAt }) };
		}

		public override async Task<object> DeleteAsync(EntityCtx c, JsonElement key, CancellationToken ct)
		{
			var projectKey = ReqStr(key, "projectKey");
			AssertProject(c.Http, projectKey);
			AssertScope(c.Http, ApiKeyScopes.LogsAdmin);
			var name = ReqStr(key, "name");
			var deleted = await c.LogStore.DeleteAsync(projectKey, name, ct);
			if (!deleted) throw new InvalidOperationException("Log not found");
			return new { deleted = true, name };
		}
	}

	// --- auth + json helpers ---------------------------------------------

	static void AssertScope(HttpContext ctx, string required)
	{
		var scopes = ctx.User.Claims.FirstOrDefault(cl => cl.Type == "scopes")?.Value ?? "";
		var parts = scopes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		if (!parts.Contains(required, StringComparer.Ordinal))
			throw new UnauthorizedAccessException($"ApiKey lacks required scope '{required}'");
	}

	static void AssertProject(HttpContext ctx, string projectKey)
	{
		var claim = ctx.User.Claims.FirstOrDefault(cl => cl.Type == "project")?.Value;
		if (string.IsNullOrEmpty(claim) || !string.Equals(claim, projectKey, StringComparison.Ordinal))
			throw new UnauthorizedAccessException($"ApiKey is not scoped to project '{projectKey}'");
	}

	// MCP clients serialize an untyped JsonElement object param (props/filter/key) as
	// a JSON *string* — the generated tool schema has no `type: object` — so it can
	// arrive as "{\"workspaceKey\":...}" rather than a real object, and OptStr's
	// ValueKind==Object check then misses every field. Unwrap a stringified object
	// back into one. Mirrors ParseNodes/ParseEntries in TasksTools/MemoryTools (D6).
	// Clone() detaches the value from the parsed document so it outlives this method.
	static JsonElement Normalize(JsonElement e)
	{
		if (e.ValueKind != JsonValueKind.String) return e;
		var raw = e.GetString();
		if (string.IsNullOrWhiteSpace(raw)) return e;
		using var doc = JsonDocument.Parse(raw);
		return doc.RootElement.Clone();
	}

	static string? OptStr(JsonElement o, string name) =>
		o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.String
			? e.GetString()
			: null;

	static string ReqStr(JsonElement o, string name)
	{
		var v = OptStr(o, name);
		if (string.IsNullOrWhiteSpace(v)) throw new ArgumentException($"{name} is required");
		return v!;
	}

	static string ReqKey(JsonElement o, string name)
	{
		var v = ReqStr(o, name);
		if (!KeyRegex.IsMatch(v))
			throw new ArgumentException($"{name} '{v}' is invalid; must match ^[a-z][a-z0-9_-]{{0,99}}$");
		return v;
	}

	static long? OptLong(JsonElement o, string name) =>
		o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.Number && e.TryGetInt64(out var l)
			? l
			: null;

	static long ReqLong(JsonElement o, string name) =>
		OptLong(o, name) ?? throw new ArgumentException($"{name} is required");
}
