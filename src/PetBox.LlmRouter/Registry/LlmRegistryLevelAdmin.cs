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
	readonly PetBoxDb _db;
	readonly ISecretEncryptor _secrets;

	public LlmRegistryLevelAdmin(PetBoxDb db, ISecretEncryptor secrets)
	{
		_db = db;
		_secrets = secrets;
	}

	public async Task<LlmRegistry> GetAsync(Scope scope, string scopeKey, CancellationToken ct = default)
	{
		var level = Validate(scope, scopeKey);
		var name = level.Scope.ToString();

		var endpointRows = await _db.LlmEndpoints
			.Where(e => e.Scope == name && e.ScopeKey == level.ScopeKey)
			.ToListAsync(ct);
		var routeRows = await _db.LlmRoutes
			.Where(r => r.Scope == name && r.ScopeKey == level.ScopeKey)
			.ToListAsync(ct);

		// Secret-free by construction: the cipher columns are simply not projected out.
		var endpoints = endpointRows
			.Select(e => new LlmEndpoint(e.Name, e.BaseUrl, e.CertThumbprint, e.ConnectTimeoutMs, e.RequestTimeoutMs))
			.ToList();

		var routes = routeRows
			.Select(r => new LlmRoute(
				Enum.Parse<LlmCapability>(r.Capability, ignoreCase: true),
				r.Endpoint,
				r.Model,
				r.Priority,
				r.Tier,
				r.Thinking is null ? null : Enum.Parse<LlmThinking>(r.Thinking, ignoreCase: true)))
			.ToList();

		return new LlmRegistry(endpoints, routes);
	}

	public async Task SetAsync(
		Scope scope,
		string scopeKey,
		LlmRegistry registry,
		IReadOnlyDictionary<string, string> apiKeys,
		long? updatedBy = null,
		CancellationToken ct = default)
	{
		var level = Validate(scope, scopeKey);

		// Same validator the old store used: unique endpoint names, absolute http(s) base URLs, sane
		// timeouts, and every route pointing at an endpoint DECLARED IN THIS registry. That last rule
		// is the application-side twin of the composite FK — belt and database.
		var result = new LlmRegistryValidator().Validate(registry);
		if (!result.IsValid) throw new ValidationException(result.Errors);

		var name = level.Scope.ToString();
		var now = DateTime.UtcNow;

		// Keys already stored at THIS level, so an endpoint the caller did not hand a new key for
		// keeps the one it had. Read from the level being written — never from a resolved/inherited
		// one, which would be how somebody else's credentials get copied into this level.
		var existing = (await _db.LlmEndpoints
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

		var routeRows = registry.Routes.Select(r => new LlmRouteRow
		{
			Id = Guid.NewGuid().ToString("N"),
			Scope = name,
			ScopeKey = level.ScopeKey,
			Capability = r.Capability.ToString(),
			Endpoint = r.Endpoint,
			Model = r.Model,
			Priority = r.Priority,
			Tier = r.Tier,
			Thinking = r.Thinking?.ToString(),
			UpdatedAt = now,
			UpdatedBy = updatedBy,
		}).ToList();

		// Routes first: they are the FK children, and the FK is ON DELETE CASCADE — deleting the
		// endpoints would take them anyway, but doing it explicitly keeps the order legible.
		await using var tx = await _db.BeginTransactionAsync(ct);

		await _db.LlmRoutes.Where(r => r.Scope == name && r.ScopeKey == level.ScopeKey).DeleteAsync(ct);
		await _db.LlmEndpoints.Where(e => e.Scope == name && e.ScopeKey == level.ScopeKey).DeleteAsync(ct);

		foreach (var row in endpointRows) await _db.InsertAsync(row, token: ct);
		// If a route somehow names an endpoint outside this level, the composite FK rejects the
		// INSERT and the whole transaction rolls back. The validator should have caught it first;
		// the database is what makes that a guarantee rather than a habit.
		foreach (var row in routeRows) await _db.InsertAsync(row, token: ct);

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
