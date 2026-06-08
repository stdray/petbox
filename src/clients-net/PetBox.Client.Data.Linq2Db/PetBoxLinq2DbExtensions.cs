using System.Globalization;
using LinqToDB;

namespace PetBox.Client.Data.Linq2Db;

// linq2db convenience over the core PetBoxDataClient: build a query with linq2db locally
// (a DataConnection configured for SQLite is enough — no connection is opened), extract its
// parameterized SQL, run it through petbox's raw-SQL pass-through, and materialize the rows
// into T. The server never sees the IQueryable or T — it just runs the SQL.
//
//     using var dc = new DataConnection(new DataOptions().UseSQLite("Data Source=:memory:", SQLiteProvider.Microsoft));
//     var q = dc.GetTable<Vote>().Where(v => v.Film == film).OrderBy(v => v.Id);
//     var rows = await client.Data.QueryAsync("kpvotes", "cache", q);
public static class PetBoxLinq2DbExtensions
{
	// Runs a linq2db IQueryable as a parameterized SELECT against a PetBox DataDb and
	// materializes each row into T by matching column names to T's writable properties.
	// Column names in the query must match T's property names (the common case; use linq2db
	// [Column]/[Table] mapping on T to control the generated SQL).
	public static async Task<List<T>> QueryAsync<T>(
		this PetBoxDataClient data, string projectKey, string dbName, IQueryable<T> query,
		int? timeoutSeconds = null, CancellationToken ct = default)
		where T : new()
	{
		ArgumentNullException.ThrowIfNull(data);
		ArgumentNullException.ThrowIfNull(query);

		var qs = query.ToSqlQuery();
		var @params = qs.Parameters.Select(p => new PetBoxSqlParam(p.Name ?? string.Empty, p.Value)).ToArray();
		var rows = await data.QueryAsync(projectKey, dbName, qs.Sql, @params, timeoutSeconds, ct).ConfigureAwait(false);
		return rows.Select(Materialize<T>).ToList();
	}

	static T Materialize<T>(IReadOnlyDictionary<string, object?> row)
		where T : new()
	{
		var instance = new T();
		foreach (var prop in typeof(T).GetProperties())
		{
			if (!prop.CanWrite) continue;
			if (!row.TryGetValue(prop.Name, out var value) || value is null) continue;

			var target = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;
			prop.SetValue(instance,
				target.IsInstanceOfType(value) ? value : Convert.ChangeType(value, target, CultureInfo.InvariantCulture));
		}
		return instance;
	}
}
