using System.Text;
using LinqToDB.Data;

namespace PetBox.Log.Core.Data;

// IDEMPOTENT bulk insert for the OTLP signal tables: `INSERT OR IGNORE`, so a row whose natural key
// is already stored is silently skipped instead of blowing up (spans, PK = SpanId) or landing twice
// (metric points, unique index ux_metricpoints_identity — see M002).
//
// WHY THIS EXISTS INSTEAD OF BulkCopyAsync. A stock OTLP exporter RETRIES on timeout/5xx, re-sending
// the identical batch. BulkCopyAsync emits a plain multi-row INSERT: the retry hit the span PK and
// threw (→ 500, and the exporter retries again, forever), while metric points, having no natural key
// at all, silently doubled. Retry-safety belongs at the write boundary, not in the caller.
//
// The statement is generated from the linq2db mapping (EntityDescriptor), not a hand-kept column
// list, so a column added to SpanRecord/MetricPointRecord is carried automatically. Identity and
// skip-on-insert columns are omitted — SQLite assigns them.
//
// SQLite-only by construction (`INSERT OR IGNORE` is SQLite's conflict-clause spelling): the ingest
// write path is always the per-log SQLite file. The DuckDB LogDb is a read/bench store and never
// takes this path.
public static class InsertOrIgnore
{
	// SQLite's default SQLITE_MAX_VARIABLE_NUMBER is 999 on older builds; stay well under it and
	// chunk the VALUES list accordingly.
	const int MaxParametersPerStatement = 900;

	public static async Task<int> InsertOrIgnoreAsync<T>(
		this DataConnection db,
		IReadOnlyList<T> rows,
		CancellationToken ct = default)
		where T : notnull
	{
		if (rows.Count == 0) return 0;

		var entity = db.MappingSchema.GetEntityDescriptor(typeof(T));
		var columns = entity.Columns.Where(c => !c.IsIdentity && !c.SkipOnInsert).ToList();
		if (columns.Count == 0) return 0;

		var table = entity.Name.Name;
		var columnList = string.Join(", ", columns.Select(c => Quote(c.ColumnName)));
		var rowsPerStatement = Math.Max(1, MaxParametersPerStatement / columns.Count);

		var affected = 0;
		await using var tx = await db.BeginTransactionAsync(ct);
		for (var offset = 0; offset < rows.Count; offset += rowsPerStatement)
		{
			var chunk = rows.Skip(offset).Take(rowsPerStatement).ToList();
			var (sql, parameters) = BuildStatement(table, columnList, columns, chunk);
			affected += await db.ExecuteAsync(sql, parameters);
		}
		await tx.CommitAsync(ct);
		return affected;
	}

	static (string Sql, DataParameter[] Parameters) BuildStatement<T>(
		string table,
		string columnList,
		IReadOnlyList<LinqToDB.Mapping.ColumnDescriptor> columns,
		IReadOnlyList<T> chunk)
	{
		var sql = new StringBuilder("INSERT OR IGNORE INTO ").Append(Quote(table))
			.Append(" (").Append(columnList).Append(") VALUES ");
		var parameters = new DataParameter[chunk.Count * columns.Count];

		var p = 0;
		for (var r = 0; r < chunk.Count; r++)
		{
			if (r > 0) sql.Append(", ");
			sql.Append('(');
			for (var c = 0; c < columns.Count; c++)
			{
				if (c > 0) sql.Append(", ");
				var name = "p" + p.ToString(System.Globalization.CultureInfo.InvariantCulture);
				sql.Append('@').Append(name);
				var column = columns[c];
				parameters[p] = new DataParameter(name, column.MemberAccessor.GetValue(chunk[r]!), column.DataType);
				p++;
			}
			sql.Append(')');
		}

		return (sql.ToString(), parameters);
	}

	static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}
