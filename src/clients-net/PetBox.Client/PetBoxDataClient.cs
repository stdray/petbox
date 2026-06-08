using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PetBox.Client;

// A single SQL parameter for a parameterized query/exec. Name matches the placeholder in the
// SQL (e.g. "@id"); Value is any JSON-serializable CLR value (null → SQL NULL); DbType is an
// optional explicit SQLite type hint, usually unnecessary.
public sealed record PetBoxSqlParam(string Name, object? Value, string? DbType = null);

// Typed client for the PetBox Data module — raw parameterized SQL over HTTP plus DataDb
// provisioning. The server is a dumb pass-through: it knows nothing about the caller's POCOs,
// it just runs the SQL against the project's SQLite file. linq2db query-building lives in the
// PetBox.Client.Data.Linq2Db package on top of QueryAsync.
//
//   QueryAsync  → POST /api/data/{project}/{db}/query   (data:read)
//   ExecAsync   → POST /api/data/{project}/{db}/exec    (data:write)
//   CreateDbAsync   → POST /api/data/{project}/dbs        (data:schema)
//   ApplySchemaAsync→ POST /api/data/{project}/{db}/schema (data:schema)
public sealed class PetBoxDataClient
{
	static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
	{
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
	};

	readonly HttpClient _http;

	// Public so advanced callers can supply their own pre-configured HttpClient; normally you
	// reach this via PetBoxClient.Data.
	public PetBoxDataClient(HttpClient http)
	{
		ArgumentNullException.ThrowIfNull(http);
		_http = http;
	}

	// Runs a SELECT and returns the rows as loosely-typed maps (column name → value, where
	// value is string/long/double/bool/null). Typed materialization is the Linq2Db layer's job.
	public async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> QueryAsync(
		string projectKey, string dbName, string sql,
		IEnumerable<PetBoxSqlParam>? @params = null, int? timeoutSeconds = null, CancellationToken ct = default)
	{
		using var resp = await SendAsync($"api/data/{Esc(projectKey)}/{Esc(dbName)}/query", sql, @params, timeoutSeconds, ct).ConfigureAwait(false);
		await EnsureOkAsync(resp, ct).ConfigureAwait(false);

		var raw = await resp.Content.ReadFromJsonAsync<List<Dictionary<string, JsonElement>>>(Json, ct).ConfigureAwait(false)
			?? [];
		var rows = new List<IReadOnlyDictionary<string, object?>>(raw.Count);
		foreach (var row in raw)
		{
			var mapped = new Dictionary<string, object?>(row.Count, StringComparer.Ordinal);
			foreach (var (col, val) in row)
				mapped[col] = FromJson(val);
			rows.Add(mapped);
		}
		return rows;
	}

	// Runs an INSERT/UPDATE/DELETE/DDL/PRAGMA and returns the affected row count.
	public async Task<int> ExecAsync(
		string projectKey, string dbName, string sql,
		IEnumerable<PetBoxSqlParam>? @params = null, int? timeoutSeconds = null, CancellationToken ct = default)
	{
		using var resp = await SendAsync($"api/data/{Esc(projectKey)}/{Esc(dbName)}/exec", sql, @params, timeoutSeconds, ct).ConfigureAwait(false);
		await EnsureOkAsync(resp, ct).ConfigureAwait(false);
		var body = await resp.Content.ReadFromJsonAsync<ExecResponse>(Json, ct).ConfigureAwait(false);
		return body?.Affected ?? 0;
	}

	// Creates a DataDb (one logical SQLite store per project; a project may have several).
	// maxPageCount caps the file size (pages × 4KB); null uses the server default (>= 1024).
	public async Task CreateDbAsync(
		string projectKey, string name, string? description = null, long? maxPageCount = null, CancellationToken ct = default)
	{
		using var resp = await _http.PostAsJsonAsync(
			$"api/data/{Esc(projectKey)}/dbs",
			new CreateDbRequest(name, description, maxPageCount), Json, ct).ConfigureAwait(false);
		await EnsureOkAsync(resp, ct).ConfigureAwait(false);
	}

	// Applies a named migration to a DataDb. Idempotent: same name+sql is a no-op; same name
	// with different sql is a 409 (→ PetBoxClientException).
	public async Task ApplySchemaAsync(
		string projectKey, string dbName, string migrationName, string sql, CancellationToken ct = default)
	{
		using var resp = await _http.PostAsJsonAsync(
			$"api/data/{Esc(projectKey)}/{Esc(dbName)}/schema",
			new SchemaRequest(migrationName, sql), Json, ct).ConfigureAwait(false);
		await EnsureOkAsync(resp, ct).ConfigureAwait(false);
	}

	async Task<HttpResponseMessage> SendAsync(
		string path, string sql, IEnumerable<PetBoxSqlParam>? @params, int? timeoutSeconds, CancellationToken ct)
	{
		var req = new HttpRequestMessage(HttpMethod.Post, path)
		{
			Content = JsonContent.Create(new QueryRequest(sql, @params?.ToArray()), options: Json),
		};
		if (timeoutSeconds is { } secs)
			req.Headers.TryAddWithoutValidation("X-PetBox-Timeout-Seconds", secs.ToString());
		return await _http.SendAsync(req, ct).ConfigureAwait(false);
	}

	static async Task EnsureOkAsync(HttpResponseMessage resp, CancellationToken ct)
	{
		if (resp.IsSuccessStatusCode) return;
		var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
		throw new PetBoxClientException(resp.StatusCode, body,
			$"PetBox request failed: {(int)resp.StatusCode} {resp.ReasonPhrase}. {body}");
	}

	static object? FromJson(JsonElement e) => e.ValueKind switch
	{
		JsonValueKind.String => e.GetString(),
		JsonValueKind.Number => e.TryGetInt64(out var l) ? l : e.GetDouble(),
		JsonValueKind.True => true,
		JsonValueKind.False => false,
		JsonValueKind.Null or JsonValueKind.Undefined => null,
		// Nested array/object (rare for SQLite scalar columns) — hand back the raw JSON text.
		_ => e.GetRawText(),
	};

	static string Esc(string segment) => Uri.EscapeDataString(segment);

	sealed record QueryRequest(string Sql, PetBoxSqlParam[]? Params);
	sealed record ExecResponse(int Affected);
	sealed record CreateDbRequest(string Name, string? Description, long? MaxPageCount);
	sealed record SchemaRequest(string Name, string Sql);
}
