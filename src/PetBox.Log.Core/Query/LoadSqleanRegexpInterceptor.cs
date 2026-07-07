using System.Data.Common;
using System.Runtime.InteropServices;
using LinqToDB.Interceptors;
using Microsoft.Data.Sqlite;

namespace PetBox.Log.Core.Query;

// Loads the vendored sqlean `regexp` SQLite extension (nalgeon/sqlean 0.28.3) on every SQLite
// connection linq2db opens for a LogDb. SQLite loadable extensions are per-connection, so we (re)load
// on ConnectionOpened.
//
// This makes sqlean's `regexp_*` SQL functions available so the KQL translator can map `matches regex`,
// `extract`, the lowered `has`/`has_cs`, and the well-formedness gates of the typed conversions
// (tolong/todouble) straight to native SQL (regexp_like / regexp_capture — see KqlSqlExpressions). This
// is the ONLY interceptor attached to LogDb — no .NET scalar UDFs remain in the KQL path.
//
// The binary is vendored per-RID and copied to the output under `sqlean/<rid>/regexp.<ext>` (see
// PetBox.Log.Core.csproj). We resolve the absolute path off AppContext.BaseDirectory by current OS and
// call SqliteConnection.LoadExtension — Microsoft.Data.Sqlite auto-enables extension loading, and the
// entry point is inferred as `sqlite3_regexp_init` from the `regexp.*` filename.
sealed class LoadSqleanRegexpInterceptor : ConnectionInterceptor
{
	public static readonly LoadSqleanRegexpInterceptor Instance = new();

	public override void ConnectionOpened(ConnectionEventData eventData, DbConnection connection)
	{
		Load(connection);
		base.ConnectionOpened(eventData, connection);
	}

	public override async Task ConnectionOpenedAsync(
		ConnectionEventData eventData, DbConnection connection, CancellationToken cancellationToken)
	{
		Load(connection);
		await base.ConnectionOpenedAsync(eventData, connection, cancellationToken).ConfigureAwait(false);
	}

	static void Load(DbConnection connection)
	{
		if (connection is not SqliteConnection c)
			return;
		// LoadExtension is idempotent enough per connection (re-loading the same extension is a no-op in
		// SQLite); a fresh connection each open means we simply load once per connection.
		c.LoadExtension(ResolveRegexpPath());
	}

	// Maps the current OS/arch to the vendored RID folder and returns the absolute path to regexp.<ext>
	// next to the app. We only vendor win-x64 + linux-x64 (x64 glibc) today; anything else throws with a
	// clear message so a missing RID surfaces loudly instead of as a cryptic native load failure.
	static string ResolveRegexpPath()
	{
		string rid, ext;
		if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
		{
			rid = "win-x64";
			ext = "dll";
		}
		else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
		{
			rid = "linux-x64";
			ext = "so";
		}
		else
		{
			throw new PlatformNotSupportedException(
				$"sqlean regexp is vendored only for win-x64/linux-x64; current OS "
				+ $"'{RuntimeInformation.OSDescription}' ({RuntimeInformation.ProcessArchitecture}) has no binary.");
		}

		var path = Path.Combine(AppContext.BaseDirectory, "sqlean", rid, $"regexp.{ext}");
		if (!File.Exists(path))
		{
			throw new FileNotFoundException(
				$"Vendored sqlean regexp extension not found for RID '{rid}'. Expected it at '{path}' "
				+ "(copied from src/PetBox.Log.Core/native/sqlean-regexp/<rid>/ by the csproj None/CopyToOutputDirectory "
				+ "item). Ensure PetBox.Log.Core built and the output layout is intact.",
				path);
		}
		return path;
	}
}
