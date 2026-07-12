using FluentValidation;
using LinqToDB.Data;
using PetBox.Config;
using PetBox.Core.Data;
using PetBox.Core.Settings;
using PetBox.LlmRouter.Contract;

namespace PetBox.LlmRouter.Registry;

// The WRITE side of the levelled registry. It cannot cascade, because it cannot even NAME a level
// implicitly: every method takes (Scope, ScopeKey) explicitly and there is no projectKey overload
// to derive one from. The classic bug this design forecloses — "the page showed me the inherited
// $system registry, I edited a field, and the save wrote it back to $system for every workspace on
// the box" — has no code path here: nothing that was READ carries a level into a WRITE.
//
// A write REPLACES the level (endpoints + routes), inside one transaction. Nothing outside the
// named level is touched, ever.
public sealed class LlmRegistryLevelAdmin : ILlmRegistryLevelAdmin
{
	// The FACTORY, not a context. This class holds the only core-db transaction on a request path
	// (SetSnapshotAsync: DELETE routes -> DELETE endpoints -> INSERT xN -> commit), and it is
	// self-contained inside the method — it opens the connection, owns it, and disposes it. That is
	// what makes the transaction safe to move off the shared scoped PetBoxDb: nothing else can be
	// mid-statement on this connection.
	readonly ICoreDbFactory _factory;
	readonly ISecretEncryptor _secrets;

	public LlmRegistryLevelAdmin(ICoreDbFactory factory, ISecretEncryptor secrets)
	{
		_factory = factory;
		_secrets = secrets;
	}

	public async Task<LlmRegistry> GetAsync(Scope scope, string scopeKey, CancellationToken ct = default)
	{
		var snapshot = await GetSnapshotAsync(scope, scopeKey, ct);
		return new LlmRegistry(snapshot.Endpoints, snapshot.Routes.Select(r => r.Route).ToList());
	}

	public async Task<LlmLevelSnapshot> GetSnapshotAsync(Scope scope, string scopeKey, CancellationToken ct = default)
	{
		var level = Validate(scope, scopeKey);
		var name = level.Scope.ToString();

		using var db = _factory.Open();

		var endpointRows = await db.LlmEndpoints
			.Where(e => e.Scope == name && e.ScopeKey == level.ScopeKey)
			.ToListAsync(ct);
		var routeRows = await db.LlmRoutes
			.Where(r => r.Scope == name && r.ScopeKey == level.ScopeKey)
			.ToListAsync(ct);

		// Secret-free by construction: the cipher columns are simply not projected out.
		var endpoints = endpointRows
			.Select(e => new LlmEndpoint(e.Name, e.BaseUrl, e.CertThumbprint, e.ConnectTimeoutMs, e.RequestTimeoutMs))
			.ToList();

		// The row's Id travels with the route — it is the handle the admin surface edits/deletes by.
		var routes = routeRows
			.Select(r => new IdentifiedRoute(r.Id, new LlmRoute(
				Enum.Parse<LlmCapability>(r.Capability, ignoreCase: true),
				r.Endpoint,
				r.Model,
				r.Priority,
				r.Tier,
				r.Thinking is null ? null : Enum.Parse<LlmThinking>(r.Thinking, ignoreCase: true))))
			.ToList();

		return new LlmLevelSnapshot(endpoints, routes);
	}

	// A whole-registry replace: every route is a new row, so every id is fresh.
	public Task SetAsync(
		Scope scope,
		string scopeKey,
		LlmRegistry registry,
		IReadOnlyDictionary<string, string> apiKeys,
		long? updatedBy = null,
		CancellationToken ct = default) =>
		SetSnapshotAsync(
			scope, scopeKey,
			registry.Endpoints,
			registry.Routes.Select(r => new IdentifiedRoute(string.Empty, r)).ToList(),
			apiKeys, updatedBy, ct);

	public async Task SetSnapshotAsync(
		Scope scope,
		string scopeKey,
		IReadOnlyList<LlmEndpoint> endpoints,
		IReadOnlyList<IdentifiedRoute> routes,
		IReadOnlyDictionary<string, string> apiKeys,
		long? updatedBy = null,
		CancellationToken ct = default)
	{
		var level = Validate(scope, scopeKey);
		var registry = new LlmRegistry(endpoints, routes.Select(r => r.Route).ToList());

		// Same validator the old store used: unique endpoint names, absolute http(s) base URLs, sane
		// timeouts, and every route pointing at an endpoint DECLARED IN THIS registry. That last rule
		// is the application-side twin of the composite FK — belt and database.
		var result = new LlmRegistryValidator().Validate(registry);
		if (!result.IsValid) throw new ValidationException(result.Errors);

		var name = level.Scope.ToString();
		var now = DateTime.UtcNow;

		// ONE connection for the whole write, opened here and disposed on the way out. Everything
		// below — the key read, the encryption, the replace transaction — runs on it and nothing
		// else touches it.
		using var db = _factory.Open();

		// Keys already stored at THIS level, so an endpoint the caller did not hand a new key for
		// keeps the one it had. Read from the level being written — never from a resolved/inherited
		// one, which would be how somebody else's credentials get copied into this level.
		// Read BEFORE the transaction opens: this is our own connection, so it is the same data
		// either way, but keeping reads out of the write transaction is the rule that stops a core-db
		// transaction from ever waiting on another connection (SQLITE_BUSY/SQLITE_LOCKED — core.db
		// runs Cache=Shared, and the busy handler does not retry LOCKED).
		var existing = (await db.LlmEndpoints
				.Where(e => e.Scope == name && e.ScopeKey == level.ScopeKey)
				.ToListAsync(ct))
			.ToDictionary(e => e.Name, StringComparer.Ordinal);

		var endpointRows = new List<LlmEndpointRow>(registry.Endpoints.Count);
		foreach (var ep in registry.Endpoints)
		{
			SecretBundle? bundle = null;
			if (apiKeys.TryGetValue(ep.Name, out var plaintext) && !string.IsNullOrWhiteSpace(plaintext))
			{
				if (!_secrets.IsAvailable)
					throw new InvalidOperationException("storing an api key requires PETBOX_MASTER_KEY to be configured");
				bundle = _secrets.Encrypt(plaintext);
			}
			else if (existing.TryGetValue(ep.Name, out var prior)
				&& prior.KeyCipher is not null && prior.KeyIv is not null && prior.KeyAuthTag is not null)
			{
				bundle = new SecretBundle(prior.KeyCipher, prior.KeyIv, prior.KeyAuthTag);
			}

			endpointRows.Add(new LlmEndpointRow
			{
				Scope = name,
				ScopeKey = level.ScopeKey,
				Name = ep.Name,
				BaseUrl = ep.BaseUrl,
				CertThumbprint = ep.CertThumbprint,
				ConnectTimeoutMs = ep.ConnectTimeoutMs,
				RequestTimeoutMs = ep.RequestTimeoutMs,
				KeyCipher = bundle?.Ciphertext,
				KeyIv = bundle?.Iv,
				KeyAuthTag = bundle?.AuthTag,
				UpdatedAt = now,
				UpdatedBy = updatedBy,
			});
		}

		// The id is KEPT when the caller hands one back. The rows are deleted and re-inserted (a level
		// is replaced whole), but a route that was on screen keeps the same handle across the save —
		// which is what makes "edit THIS row" mean the same row on the next request, whoever else has
		// saved in between.
		var routeRows = routes.Select(r => new LlmRouteRow
		{
			Id = string.IsNullOrWhiteSpace(r.Id) ? Guid.NewGuid().ToString("N") : r.Id,
			Scope = name,
			ScopeKey = level.ScopeKey,
			Capability = r.Route.Capability.ToString(),
			Endpoint = r.Route.Endpoint,
			Model = r.Route.Model,
			Priority = r.Route.Priority,
			Tier = r.Route.Tier,
			Thinking = r.Route.Thinking?.ToString(),
			UpdatedAt = now,
			UpdatedBy = updatedBy,
		}).ToList();

		// Routes first: they are the FK children, and the FK is ON DELETE CASCADE — deleting the
		// endpoints would take them anyway, but doing it explicitly keeps the order legible.
		await using var tx = await db.BeginTransactionAsync(ct);

		await db.LlmRoutes.Where(r => r.Scope == name && r.ScopeKey == level.ScopeKey).DeleteAsync(ct);
		await db.LlmEndpoints.Where(e => e.Scope == name && e.ScopeKey == level.ScopeKey).DeleteAsync(ct);

		foreach (var row in endpointRows) await db.InsertAsync(row, token: ct);
		// If a route somehow names an endpoint outside this level, the composite FK rejects the
		// INSERT and the whole transaction rolls back. The validator should have caught it first;
		// the database is what makes that a guarantee rather than a habit.
		foreach (var row in routeRows) await db.InsertAsync(row, token: ct);

		await tx.CommitAsync(ct);
	}

	// The only place a level is admitted. Scope.Project is RESERVED in the schema (the resolver
	// walks it) but nothing may write it yet — accepting a write there now would create rows no
	// migration or UI knows how to show.
	static RegistryLevel Validate(Scope scope, string scopeKey)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(scopeKey);

		return scope switch
		{
			Scope.System when scopeKey == RegistryLevel.SystemScopeKey => RegistryLevel.System,
			Scope.System => throw new ArgumentException(
				$"the system registry level has exactly one key, '{RegistryLevel.SystemScopeKey}' (got '{scopeKey}')", nameof(scopeKey)),
			Scope.Workspace => RegistryLevel.Workspace(scopeKey),
			Scope.Project => throw new ArgumentException(
				"Scope.Project is reserved for the LLM registry: the resolver walks it, but no writer may create project-level rows yet", nameof(scope)),
			_ => throw new ArgumentOutOfRangeException(nameof(scope), scope,
				"the LLM registry has levels System and Workspace only"),
		};
	}
}
