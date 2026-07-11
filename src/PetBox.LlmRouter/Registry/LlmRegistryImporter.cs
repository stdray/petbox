using System.Text.Json;
using LinqToDB;
using LinqToDB.Data;
using Microsoft.Extensions.Logging;
using PetBox.Config.Data;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Core.Settings;
using PetBox.LlmRouter.Contract;

namespace PetBox.LlmRouter.Registry;

// ONE-TIME, IDEMPOTENT IMPORT of the live LLM registry out of the Config module and into core.db's
// llm_endpoints/llm_routes (spec: llm-registry-own-store). It moves DATA, not schema, and the data
// lives in a DIFFERENT FILE (config/{ws}.db) than the target (petbox.db) — so this is deliberately
// NOT a FluentMigrator step: a core.db migration could only reach that file by ATTACHing a
// runtime-configured path in raw SQL, and would then have to cope with every host and test where
// the file simply is not there. It is a startup hook instead, alongside the other one-time folds in
// Program.cs (legacy task files, relations, flat nodes).
//
// WHAT IT COPIES. The old store kept ONE Plain binding `llm/registry` (the endpoints+routes JSON)
// plus ONE Secret binding `llm/secret/{endpoint}` per api key, in config/$system.db — the only
// workspace anything was ever entered in (which IS the bug being fixed: every other workspace
// resolved zero routes). Those become rows at level (Scope.System, "$").
//
// THE KEYS ARE COPIED AS CIPHERTEXT, BYTE FOR BYTE. Ciphertext/Iv/AuthTag go straight into
// KeyCipher/KeyIv/KeyAuthTag. There is NO decrypt and NO re-encrypt: same AES-GCM encryptor, same
// PETBOX_MASTER_KEY, so the blobs are portable verbatim. Three consequences, and they are the whole
// reason for doing it this way:
//   * a key cannot be lost or mangled in transit — the bytes are never interpreted;
//   * the import runs even where PETBOX_MASTER_KEY is not configured (nothing needs to decrypt);
//   * the plaintext key never exists — not in memory, not in a log line.
// That is also why this does NOT go through ILlmRegistryLevelAdmin: its SetAsync takes PLAINTEXT
// keys and encrypts them, which would mean decrypting the old bundle first — the one thing that can
// go wrong here.
//
// WHAT IT DOES NOT DO. It does not touch the old bindings: they stay active and read-only, and the
// router keeps reading them until the DI flip (a later step). Rolling this back is rolling back the
// binary. And it writes no level but System:$ — a workspace-level import has no source.
public sealed partial class LlmRegistryImporter
{
	// The marker, in Settings at (System, "$"). Belt to the "the tables are empty" brace: without it,
	// an operator who legitimately EMPTIES the new registry would have it silently refilled from the
	// stale bindings on the next restart.
	public const string MarkerPath = "llm.registry.importedAt";

	const string RegistryPath = "llm/registry";
	static string SecretPath(string endpoint) => $"llm/secret/{endpoint}";

	static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

	readonly PetBoxDb _core;
	readonly IConfigDbFactory _configFactory;
	readonly string _configDir;
	readonly ILogger _log;

	public LlmRegistryImporter(PetBoxDb core, IConfigDbFactory configFactory, string configDir, ILogger log)
	{
		_core = core;
		_configFactory = configFactory;
		_configDir = configDir;
		_log = log;
	}

	public enum Outcome
	{
		Imported,
		AlreadyDone,   // marker present, or the new tables already hold rows — nothing to do
		NoSource,      // no config/$system.db, or no active llm/registry binding in it
		Aborted,       // the source JSON did not parse — NOTHING was written
	}

	public sealed record Result(Outcome Outcome, int Endpoints, int Routes, int Keys, int DroppedRoutes)
	{
		public static Result None(Outcome outcome) => new(outcome, 0, 0, 0, 0);
	}

	public Result Import()
	{
		// ---- gate 1: has this already happened? ----
		var marker = _core.Settings.FirstOrDefault(s =>
			s.Scope == nameof(Scope.System) && s.ScopeKey == RegistryLevel.SystemScopeKey && s.Path == MarkerPath);
		if (marker is not null)
		{
			LogAlreadyImported(_log, marker.Value, MarkerPath);
			return Result.None(Outcome.AlreadyDone);
		}

		if (_core.LlmEndpoints.Any() || _core.LlmRoutes.Any())
		{
			// Rows but no marker: somebody already wrote the new store through the admin. Not ours to
			// overwrite — the import only ever populates an EMPTY registry.
			LogTablesNotEmpty(_log);
			return Result.None(Outcome.AlreadyDone);
		}

		// ---- gate 2: is there a source at all? ----
		// File.Exists first: the factory would CREATE (and schema-ensure) config/$system.db, and a
		// no-op must not leave a new file behind.
		var systemConfig = Path.Combine(_configDir, WorkspaceMemory.SystemWorkspace + ".db");
		if (!File.Exists(systemConfig))
		{
			LogNoConfigFile(_log, systemConfig);
			return Result.None(Outcome.NoSource);
		}

		using var cfg = _configFactory.NewConfigDb(WorkspaceMemory.SystemWorkspace);

		var registryBinding = ActiveByPath(cfg, RegistryPath);
		if (registryBinding is null || string.IsNullOrWhiteSpace(registryBinding.Value))
		{
			LogNoBinding(_log, RegistryPath, WorkspaceMemory.SystemWorkspace);
			return Result.None(Outcome.NoSource);
		}

		// ---- parse: a broken source ABORTS, it does not half-import ----
		LlmRegistry? registry;
		try
		{
			registry = JsonSerializer.Deserialize<LlmRegistry>(registryBinding.Value, Json);
		}
		catch (JsonException ex)
		{
			LogAborted(_log, RegistryPath, registryBinding.Version, ex);
			return Result.None(Outcome.Aborted);
		}

		if (registry is null)
		{
			LogAborted(_log, RegistryPath, registryBinding.Version, null);
			return Result.None(Outcome.Aborted);
		}

		// ---- build the rows ----
		var now = DateTime.UtcNow;
		var level = RegistryLevel.System;
		var scope = level.Scope.ToString();

		var endpointRows = new List<LlmEndpointRow>();
		var seen = new HashSet<string>(StringComparer.Ordinal);
		var keys = 0;

		foreach (var ep in registry.Endpoints)
		{
			// (Scope, ScopeKey, Name) is the PK — a duplicate name in the JSON would abort the INSERT.
			if (!seen.Add(ep.Name))
			{
				LogDuplicateEndpoint(_log, ep.Name);
				continue;
			}

			// VERBATIM. The three blobs are read and written; they are never decrypted, so a wrong or
			// absent master key cannot corrupt or drop a key here.
			var secret = ActiveByPath(cfg, SecretPath(ep.Name));
			var hasKey = secret is { Kind: BindingKind.Secret, Ciphertext: not null, Iv: not null, AuthTag: not null };
			if (hasKey) keys++;
			else if (secret is not null) LogIncompleteSecret(_log, SecretPath(ep.Name), secret.Kind.ToString(), ep.Name);

			endpointRows.Add(new LlmEndpointRow
			{
				Scope = scope,
				ScopeKey = level.ScopeKey,
				Name = ep.Name,
				BaseUrl = ep.BaseUrl,
				CertThumbprint = ep.CertThumbprint,
				ConnectTimeoutMs = ep.ConnectTimeoutMs,
				RequestTimeoutMs = ep.RequestTimeoutMs,
				KeyCipher = hasKey ? secret!.Ciphertext : null,
				KeyIv = hasKey ? secret!.Iv : null,
				KeyAuthTag = hasKey ? secret!.AuthTag : null,
				UpdatedAt = now,
				UpdatedBy = null,
			});
		}

		var routeRows = new List<LlmRouteRow>();
		var dropped = 0;
		foreach (var r in registry.Routes)
		{
			// The composite FK would reject such a row and roll the whole transaction back; the source
			// JSON was never FK-checked, so a stale route naming a removed endpoint is possible. Drop
			// it, loudly — it was already dead (it resolved to nothing in the old store either).
			if (!seen.Contains(r.Endpoint))
			{
				LogDanglingRoute(_log, r.Capability.ToString(), r.Endpoint);
				dropped++;
				continue;
			}

			routeRows.Add(new LlmRouteRow
			{
				Id = Guid.NewGuid().ToString("N"),
				Scope = scope,
				ScopeKey = level.ScopeKey,
				Capability = r.Capability.ToString(),
				Endpoint = r.Endpoint,
				Model = r.Model,
				Priority = r.Priority,
				Tier = r.Tier,
				Thinking = r.Thinking?.ToString(),
				UpdatedAt = now,
				UpdatedBy = null,
			});
		}

		// ---- write: all of it, or none of it (marker included) ----
		using (var tx = _core.BeginTransaction())
		{
			foreach (var row in endpointRows) _core.Insert(row);
			foreach (var row in routeRows) _core.Insert(row);

			_core.Insert(new Setting
			{
				Scope = scope,
				ScopeKey = level.ScopeKey,
				Path = MarkerPath,
				Type = "string",
				Value = now.ToString("O"),
				UpdatedAt = now,
				UpdatedBy = null,
			});

			tx.Commit();
		}

		var levelName = level.ToString();
		LogImported(_log, endpointRows.Count, keys, routeRows.Count, dropped, WorkspaceMemory.SystemWorkspace, levelName);
		return new Result(Outcome.Imported, endpointRows.Count, routeRows.Count, keys, dropped);
	}

	// Same read the old store does: the newest non-deleted version at that path.
	static ConfigBinding? ActiveByPath(ConfigDb cfg, string path) =>
		cfg.Bindings.Where(b => b.Path == path && !b.IsDeleted)
			.OrderByDescending(b => b.Version)
			.FirstOrDefault();

	[LoggerMessage(EventId = 320, Level = LogLevel.Information,
		Message = "llm registry import: already done at {At} (marker '{Marker}' in Settings) — skipping")]
	static partial void LogAlreadyImported(ILogger logger, string at, string marker);

	[LoggerMessage(EventId = 321, Level = LogLevel.Information,
		Message = "llm registry import: llm_endpoints/llm_routes are not empty — skipping (the new store is already in use)")]
	static partial void LogTablesNotEmpty(ILogger logger);

	[LoggerMessage(EventId = 322, Level = LogLevel.Information,
		Message = "llm registry import: there is no {Path} — nothing to import")]
	static partial void LogNoConfigFile(ILogger logger, string path);

	[LoggerMessage(EventId = 323, Level = LogLevel.Information,
		Message = "llm registry import: config/{Workspace}.db has no active '{Path}' binding — nothing to import")]
	static partial void LogNoBinding(ILogger logger, string path, string workspace);

	[LoggerMessage(EventId = 324, Level = LogLevel.Error,
		Message = "llm registry import ABORTED: the '{Path}' binding (version {Version}) does not parse as an LlmRegistry. NOTHING was written — the old bindings still serve the router, and the import is retried on the next start")]
	static partial void LogAborted(ILogger logger, string path, int version, Exception? ex);

	[LoggerMessage(EventId = 325, Level = LogLevel.Warning,
		Message = "llm registry import: endpoint '{Endpoint}' is declared twice in the source JSON — the second declaration is ignored")]
	static partial void LogDuplicateEndpoint(ILogger logger, string endpoint);

	[LoggerMessage(EventId = 326, Level = LogLevel.Warning,
		Message = "llm registry import: the '{Path}' binding exists but is not a complete Secret (kind={Kind}) — endpoint '{Endpoint}' is imported KEYLESS")]
	static partial void LogIncompleteSecret(ILogger logger, string path, string kind, string endpoint);

	[LoggerMessage(EventId = 327, Level = LogLevel.Warning,
		Message = "llm registry import: route {Capability} -> '{Endpoint}' names an endpoint the source registry does not declare — route DROPPED")]
	static partial void LogDanglingRoute(ILogger logger, string capability, string endpoint);

	[LoggerMessage(EventId = 328, Level = LogLevel.Information,
		Message = "llm registry import: {Endpoints} endpoint(s) ({Keys} with an api key — copied as CIPHERTEXT, never decrypted) and {Routes} route(s) ({DroppedRoutes} dangling one(s) dropped) imported from config/{Workspace}.db into core.db at level {Level}. The old bindings are untouched and still serve the router")]
	static partial void LogImported(ILogger logger, int endpoints, int keys, int routes, int droppedRoutes, string workspace, string level);
}
