namespace PetBox.Core.Data;

// Shared on-disk file layout for scope-keyed SQLite databases. Both the generic
// ScopedDbFactory (Log, Config) and DataDbFactory (user-data) resolve their file
// paths through here so the `{baseDir}/{scopeKey}.db` vs
// `{baseDir}/{scopeKey}/{name}.db` convention lives in exactly one place.
public static class ScopedDbFiles
{
	// {baseDir}/{scopeKey}.db          when name is null  (e.g. config/{ws}.db)
	// {baseDir}/{scopeKey}/{name}.db   when name is set   (e.g. logs/{project}/{log}.db)
	public static string PathFor(string baseDir, string scopeKey, string? name) =>
		name is null
			? Path.Combine(baseDir, $"{scopeKey}.db")
			: Path.Combine(baseDir, scopeKey, $"{name}.db");

	// Deletes the main file plus its WAL/SHM sidecars. Best-effort: if any file is
	// locked (Windows in-flight connection), returns false so a cleanup pass can
	// retry later. Mirrors the original DataDbFactory.TryDelete semantics.
	public static bool TryDelete(string path)
	{
		foreach (var f in new[] { path, path + "-wal", path + "-shm" })
		{
			if (!File.Exists(f)) continue;
			try { File.Delete(f); }
			catch (IOException) { return false; }
			catch (UnauthorizedAccessException) { return false; }
		}
		return true;
	}

	// Enumerates `.db` file names (without extension) under {baseDir}/{scopeKey}.
	// Used by orphan-cleanup to compare on-disk files against metadata rows.
	public static IReadOnlyList<string> ListNames(string baseDir, string scopeKey)
	{
		var dir = Path.Combine(baseDir, scopeKey);
		if (!Directory.Exists(dir)) return [];
		return Directory.GetFiles(dir, "*.db")
			.Select(Path.GetFileNameWithoutExtension)
			.Where(n => !string.IsNullOrEmpty(n))
			.Select(n => n!)
			.ToList();
	}

	// Enumerates the scope-key subdirectories under baseDir. Used by orphan-cleanup
	// to find scope keys that have files on disk but no metadata rows.
	public static IReadOnlyList<string> ListScopeKeys(string baseDir)
	{
		if (!Directory.Exists(baseDir)) return [];
		return Directory.GetDirectories(baseDir)
			.Select(Path.GetFileName)
			.Where(n => !string.IsNullOrEmpty(n))
			.Select(n => n!)
			.ToList();
	}
}
