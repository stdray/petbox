using Microsoft.Extensions.Logging;
using PetBox.Config;
using PetBox.Core.Data;
using PetBox.Core.Settings;
using PetBox.LlmRouter.Contract;

namespace PetBox.LlmRouter.Registry;

// Reads the levelled registry out of core.db and picks the ONE level that serves a project.
//
// LEVEL-ATOMIC, NOT MERGED. The chain is Project -> Workspace -> System, and the FIRST level that
// declares AT LEAST ONE ROUTE wins WHOLE: its endpoints, its routes, its keys. Levels are never
// merged. This is the central choice, and it exists to make one specific disaster unrepresentable:
// a merge would let a $system ROUTE pair up with a workspace's ENDPOINT (or the reverse), i.e. a
// call aimed at an endpoint whose key belongs to somebody else — or to nobody. The old store
// shipped exactly that failure mode and papered over it (`catch { /* treated as absent */ }`,
// call goes out unauthenticated, upstream answers 401 for no legible reason). Here:
//
//   * the key is a COLUMN of the endpoint row, so an endpoint always travels with its key;
//   * a composite FK forbids a route from naming an endpoint at another level;
//   * one level is taken whole, so nothing is ever recombined across levels.
//
// A key that will not decrypt is a HARD failure of that endpoint: the endpoint is dropped, its
// routes with it, and a Warning names it. It is NOT downgraded to "keyless". Falling back to the
// next level is likewise not on the table — the level that owns the routes owns them; if its
// endpoints are broken, the honest outcome is "no route", not somebody else's credentials.
//
// A level with routes but zero usable endpoints therefore returns an EMPTY route list at that
// level. That is deliberate: the caller gets an honest no-route error rather than silently being
// served by $system.
public sealed partial class LlmRegistryLevelResolver : ILlmRegistryLevelResolver
{
	// READ-ONLY resolver, but on the HOTTEST core-db path there is: EVERY embed (every hybrid search
	// query) resolves the level, and that is 4+ round-trips (Projects, Settings x2 via
	// ISettingsResolver, LlmRoutes, LlmEndpoints) — issued BEFORE the "no route" decision, so they
	// happen even for a project with no routes at all. A linq2db DataConnection is NOT thread-safe,
	// and this resolver sits under CapabilityRouter, which parallel fan-outs
	// (CrossScopeTaskSearchService) drive from several branches of one request scope. So it takes the
	// FACTORY and opens a fresh, call-owned connection per resolve. (It used to clone the scoped
	// PetBoxDb's DataOptions by hand; ICoreDbFactory is that mechanic, once — and it keeps the SHARED
	// mapping schema, a per-connection one being the prod OOM: see PetBoxDb.SharedMappingSchema.)
	readonly ICoreDbFactory _core;
	readonly ISecretEncryptor _secrets;
	readonly ISettingsResolver _settings;
	readonly ILogger<LlmRegistryLevelResolver> _log;

	public LlmRegistryLevelResolver(
		ICoreDbFactory core,
		ISecretEncryptor secrets,
		ISettingsResolver settings,
		ILogger<LlmRegistryLevelResolver> log)
	{
		_core = core;
		_secrets = secrets;
		_settings = settings;
		_log = log;
	}

	public async Task<ResolvedRegistryLevel> ResolveAsync(string projectKey, CancellationToken ct = default)
	{
		using var db = _core.Open();

		var workspaceKey = await db.Projects
			.Where(p => p.Key == projectKey)
			.Select(p => p.WorkspaceKey)
			.FirstOrDefaultAsync(ct)
			?? throw new LlmRouterException(LlmCapability.Embed, false, $"unknown project '{projectKey}'");

		var inheritAllowed = await InheritAllowedAsync(workspaceKey, ct);

		// Scope.Project is reserved: nothing writes that level today, so it simply never has routes
		// and the walk falls through it. It is in the chain so the day per-project registries land,
		// resolution already looks there.
		var chain = new List<RegistryLevel>
		{
			new(Scope.Project, projectKey),
			RegistryLevel.Workspace(workspaceKey),
		};
		if (inheritAllowed) chain.Add(RegistryLevel.System);

		foreach (var level in chain)
		{
			var scope = level.Scope.ToString();
			var routeRows = await db.LlmRoutes
				.Where(r => r.Scope == scope && r.ScopeKey == level.ScopeKey)
				.ToListAsync(ct);

			// LEVEL-ATOMIC: a level with no routes is not a level. Nothing of it is taken — not even
			// its endpoints (an endpoint without a route is unreachable anyway, and taking it would be
			// the first step of exactly the merge this design forbids).
			if (routeRows.Count == 0) continue;

			var endpointRows = await db.LlmEndpoints
				.Where(e => e.Scope == scope && e.ScopeKey == level.ScopeKey)
				.ToListAsync(ct);

			var (endpoints, keys) = ResolveEndpoints(endpointRows, level);
			var routes = ResolveRoutes(routeRows, endpoints, level);

			if (routes.Count == 0)
				LogLevelUnusable(_log, level.ToString(), routeRows.Count, projectKey);

			return new ResolvedRegistryLevel(
				level,
				new LlmRegistry(endpoints, routes),
				keys,
				InheritanceBlocked: !inheritAllowed,
				projectKey,
				workspaceKey);
		}

		return new ResolvedRegistryLevel(
			Level: null,
			LlmRegistry.Empty,
			new Dictionary<string, string>(StringComparer.Ordinal),
			InheritanceBlocked: !inheritAllowed,
			projectKey,
			workspaceKey);
	}

	// TWO independent reads, AND-ed — never one cascading property (see LlmRegistryInheritanceSettings):
	// the owner's system tap AND the workspace's own switch. Wider scope = stricter: System off means
	// nobody inherits, whatever a workspace asked for.
	async Task<bool> InheritAllowedAsync(string workspaceKey, CancellationToken ct)
	{
		var system = await _settings.GetAsync<LlmRegistryInheritanceSettings>(Scope.System, RegistryLevel.SystemScopeKey, ct);
		var workspace = await _settings.GetAsync<LlmRegistryInheritanceSettings>(Scope.Workspace, workspaceKey, ct);
		return system.SystemShared && workspace.WorkspaceInherits;
	}

	// An endpoint travels with its key or it does not travel. Three states, and only two are legal:
	//   all cipher columns NULL   -> a deliberately keyless endpoint (local model, no auth).
	//   all cipher columns set    -> decrypt; on failure the endpoint is DROPPED, loudly.
	//   anything in between       -> a corrupt row; DROPPED, loudly.
	// "Dropped" is the whole difference from the old store, which called out with no credentials.
	(List<LlmEndpoint> Endpoints, IReadOnlyDictionary<string, string> Keys) ResolveEndpoints(
		List<LlmEndpointRow> rows, RegistryLevel level)
	{
		var endpoints = new List<LlmEndpoint>(rows.Count);
		var keys = new Dictionary<string, string>(StringComparer.Ordinal);

		foreach (var row in rows)
		{
			var hasAll = row.KeyCipher is not null && row.KeyIv is not null && row.KeyAuthTag is not null;
			var hasNone = row.KeyCipher is null && row.KeyIv is null && row.KeyAuthTag is null;

			if (!hasAll && !hasNone)
			{
				LogEndpointDropped(_log, row.Name, level.ToString(), "its stored key is incomplete (cipher/iv/tag half-written)");
				continue;
			}

			if (hasAll)
			{
				if (!_secrets.IsAvailable)
				{
					LogEndpointDropped(_log, row.Name, level.ToString(), "it has an encrypted key but PETBOX_MASTER_KEY is not configured, so the key cannot be read");
					continue;
				}

				string plaintext;
				try
				{
					plaintext = _secrets.Decrypt(row.KeyCipher!, row.KeyIv!, row.KeyAuthTag!);
				}
				catch (Exception ex)
				{
					// NOT `catch { treat as absent }`. A key we cannot read means we cannot authenticate;
					// calling anyway is how you get an unauthenticated request and an inscrutable 401.
					LogEndpointDropped(_log, row.Name, level.ToString(), $"its stored key failed to decrypt ({ex.GetType().Name}) — refusing to call it unauthenticated");
					continue;
				}

				keys[row.Name] = plaintext;
			}

			endpoints.Add(new LlmEndpoint(row.Name, row.BaseUrl, row.CertThumbprint, row.ConnectTimeoutMs, row.RequestTimeoutMs));
		}

		return (endpoints, keys);
	}

	// Routes of the winning level, minus the ones whose endpoint did not survive. A route pointing
	// at a dropped endpoint is dead: keeping it would only re-open the "call it without a key" path
	// one layer up.
	List<LlmRoute> ResolveRoutes(List<LlmRouteRow> rows, List<LlmEndpoint> endpoints, RegistryLevel level)
	{
		var live = endpoints.Select(e => e.Name).ToHashSet(StringComparer.Ordinal);
		var routes = new List<LlmRoute>(rows.Count);

		foreach (var row in rows)
		{
			if (!live.Contains(row.Endpoint))
			{
				// The FK guarantees the endpoint ROW exists at this level; if it is not here, the
				// resolver dropped it (bad key) — or, harmlessly, it was never there because FK
				// enforcement was off on some other connection.
				LogRouteDropped(_log, row.Capability, row.Endpoint, level.ToString());
				continue;
			}

			if (!Enum.TryParse<LlmCapability>(row.Capability, ignoreCase: true, out var capability))
			{
				LogUnparsableRoute(_log, row.Id, "Capability", row.Capability, level.ToString());
				continue;
			}

			LlmThinking? thinking = null;
			if (row.Thinking is not null)
			{
				if (!Enum.TryParse<LlmThinking>(row.Thinking, ignoreCase: true, out var parsed))
				{
					LogUnparsableRoute(_log, row.Id, "Thinking", row.Thinking, level.ToString());
					continue;
				}
				thinking = parsed;
			}

			routes.Add(new LlmRoute(capability, row.Endpoint, row.Model, row.Priority, row.Tier, thinking));
		}

		return routes;
	}

	[LoggerMessage(EventId = 310, Level = LogLevel.Warning,
		Message = "llm registry: endpoint '{Endpoint}' at level {Level} is EXCLUDED — {Reason}. Its routes are dropped; no call will be made to it without credentials")]
	static partial void LogEndpointDropped(ILogger logger, string endpoint, string level, string reason);

	[LoggerMessage(EventId = 311, Level = LogLevel.Warning,
		Message = "llm registry: route for {Capability} at level {Level} references endpoint '{Endpoint}', which is not usable — route dropped")]
	static partial void LogRouteDropped(ILogger logger, string capability, string endpoint, string level);

	[LoggerMessage(EventId = 312, Level = LogLevel.Warning,
		Message = "llm registry: route '{RouteId}' at level {Level} has an unparsable {Field} '{Value}' — route dropped")]
	static partial void LogUnparsableRoute(ILogger logger, string routeId, string field, string value, string level);

	[LoggerMessage(EventId = 313, Level = LogLevel.Warning,
		Message = "llm registry: level {Level} owns {RouteCount} route(s) for project '{Project}' but NONE are usable — resolution stops here (it will NOT silently fall back to the system registry)")]
	static partial void LogLevelUnusable(ILogger logger, string level, int routeCount, string project);
}
