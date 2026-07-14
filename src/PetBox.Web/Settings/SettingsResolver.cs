using System.Globalization;
using System.Reflection;
using System.Text.Json;
using PetBox.Config;
using PetBox.Core.Json;
using PetBox.Core.Settings;

namespace PetBox.Web.Settings;

// The TYPED half of settings: reflection over [Setting] properties, the TopLevel cap, and the
// encode/decode of a CLR value to the stored (Type, Value) pair. The DATABASE half lives behind
// ISettingsStore, and this class holds no factory — it cannot open core.db even by accident, which
// is AGENTS.md's "the database is visible only in the service layer" applied here.
//
// That split is not bookkeeping. GetAsync is the hottest read in the app (a cross-scope search
// drives CapabilityRouter -> LlmRegistryLevelResolver -> GetAsync on EVERY embed, i.e. on every
// query), and while the SQL was inlined here it was an N+1 over the scope cascade: PER PROPERTY, one
// chain-building query plus a row query per link. The chain is the same for every property of a
// record — only the TopLevel CAP differs, and that is a FILTER over the chain, not a different one —
// so the store now takes the whole cascade in one snapshot and every property is answered from
// memory. Same one connection per call as before; the round-trips inside it collapse to two.
//
// The WRITE path used to stay on an injected scoped connection, justified by a comment claiming it
// "must remain able to join whatever the request's connection is doing". That was never true:
// SetAsync opened no transaction, so each property was its own autocommit and there was nothing to
// join. Worse — that made a MULTI-PROPERTY SAVE NON-ATOMIC: a throw on the 3rd of 5 properties (an
// unencryptable secret, a bad cast) left the first two committed and the rest not, i.e. the admin
// page silently half-applied. So the whole diff is now ENCODED first — that is where Encode() can
// throw, before anything is open — and handed to the store as one all-or-nothing write.
public sealed class SettingsResolver(ISettingsStore store, ISecretEncryptor encryptor) : ISettingsResolver
{
	public async Task<T> GetAsync<T>(Scope deepestScope, string deepestScopeKey, CancellationToken ct = default)
		where T : new()
	{
		var result = new T();
		var props = SettingPropertyCache.Get(typeof(T));
		if (props.Count == 0) return result;

		var snapshot = await store.LoadChainAsync(deepestScope, deepestScopeKey, ct);

		foreach (var prop in props)
		{
			foreach (var (scope, scopeKey) in snapshot.Chain)
			{
				// The property's TopLevel cap: a scope FINER than it is not reachable for this
				// property, so it is skipped rather than consulted. (The chain itself is shared by
				// every property — this is the only per-property part of the walk.)
				if (!IsReachable(prop.Attribute.TopLevel, scope)) continue;

				var row = snapshot.Find(scope, scopeKey, prop.Attribute.Key);
				if (row is null) continue;

				// First row wins: the chain is ordered deepest-first, so the nearest override is
				// the one that lands, and the rest of the cascade is not consulted.
				var value = Decode(prop.Property.PropertyType, row.Type, row.Value, prop.Attribute.IsSecret);
				prop.Property.SetValue(result, value);
				break;
			}
		}

		return result;
	}

	public async Task SetAsync<T>(Scope scope, string scopeKey, T newValues, T oldValues, long? updatedBy, CancellationToken ct = default)
		where T : notnull
	{
		var props = SettingPropertyCache.Get(typeof(T));

		// EVERYTHING is encoded before the store is touched. Encode() throws on a secret with no
		// master key configured and on a bad cast; doing it here means such a throw happens with no
		// connection open and no transaction started, so a failed save writes NOTHING and cannot
		// half-apply. (A settings form is a SINGLE edit by the user — half of it is never a state
		// they asked for.)
		var writes = new List<SettingWrite>();
		foreach (var prop in props)
		{
			var newVal = prop.Property.GetValue(newValues);
			var oldVal = prop.Property.GetValue(oldValues);
			if (Equals(newVal, oldVal)) continue;

			var (typeTag, encoded) = Encode(prop.Property.PropertyType, newVal, prop.Attribute.IsSecret);
			writes.Add(new SettingWrite(prop.Attribute.Key, typeTag, encoded));
		}

		await store.WriteAsync(scope, scopeKey, writes, updatedBy, ct);
	}

	public async Task ResetAsync<T>(Scope scope, string scopeKey, string propertyName, CancellationToken ct = default)
		where T : notnull
	{
		var props = SettingPropertyCache.Get(typeof(T));
		var match = props.FirstOrDefault(p => p.Property.Name == propertyName)
			?? throw new ArgumentException($"Property {propertyName} on {typeof(T).Name} is not a [Setting].", nameof(propertyName));

		await store.DeleteAsync(scope, scopeKey, match.Attribute.Key, ct);
	}

	// Scope is ordered coarse -> fine (System = 0 ... Membership = 5), so a property whose TopLevel
	// is Project is not readable at System or Workspace: the cap names the COARSEST scope that may
	// carry it.
	static bool IsReachable(Scope topLevel, Scope scope) => (int)topLevel <= (int)scope;

	(string TypeTag, string Encoded) Encode(Type clrType, object? value, bool isSecret)
	{
		if (isSecret)
		{
			if (clrType != typeof(string))
				throw new InvalidOperationException("[Setting(IsSecret=true)] requires string property type.");
			if (!encryptor.IsAvailable)
				throw new InvalidOperationException("Secret setting requires PETBOX_MASTER_KEY to be configured.");

			var bundle = encryptor.Encrypt((string?)value ?? string.Empty);
			var blob = JsonSerializer.Serialize(new[] { bundle.Ciphertext, bundle.Iv, bundle.AuthTag }, PetBoxJsonEncoder.SharedOptions);
			return ("secret", blob);
		}

		var underlying = Nullable.GetUnderlyingType(clrType) ?? clrType;

		if (underlying == typeof(string))
			return ("string", (string?)value ?? string.Empty);
		if (underlying == typeof(int))
			return ("int", ((int)(value ?? 0)).ToString(CultureInfo.InvariantCulture));
		if (underlying == typeof(long))
			return ("long", ((long)(value ?? 0L)).ToString(CultureInfo.InvariantCulture));
		if (underlying == typeof(bool))
			return ("bool", ((bool)(value ?? false)) ? "true" : "false");
		if (underlying.IsEnum)
			return ("enum", Enum.GetName(underlying, value ?? Activator.CreateInstance(underlying)!) ?? string.Empty);

		// Fallback: JSON for complex types.
		return ("json", JsonSerializer.Serialize(value, PetBoxJsonEncoder.SharedOptions));
	}

	object? Decode(Type clrType, string typeTag, string stored, bool isSecret)
	{
		if (isSecret && typeTag == "secret")
		{
			if (!encryptor.IsAvailable) return string.Empty;
			var triple = JsonSerializer.Deserialize<string[]>(stored, PetBoxJsonEncoder.SharedOptions);
			if (triple is null || triple.Length != 3) return string.Empty;
			try { return encryptor.Decrypt(triple[0], triple[1], triple[2]); }
			catch { return string.Empty; }
		}

		var underlying = Nullable.GetUnderlyingType(clrType) ?? clrType;
		return typeTag switch
		{
			"string" => stored,
			"int" => int.Parse(stored, CultureInfo.InvariantCulture),
			"long" => long.Parse(stored, CultureInfo.InvariantCulture),
			"bool" => stored.Equals("true", StringComparison.OrdinalIgnoreCase),
			"enum" => Enum.Parse(underlying, stored, ignoreCase: true),
			"json" => JsonSerializer.Deserialize(stored, clrType, PetBoxJsonEncoder.SharedOptions),
			_ => stored,
		};
	}

	static class SettingPropertyCache
	{
		static readonly Dictionary<Type, IReadOnlyList<SettingProperty>> _cache = [];
		static readonly Lock _lock = new();

		public static IReadOnlyList<SettingProperty> Get(Type type)
		{
			lock (_lock)
			{
				if (_cache.TryGetValue(type, out var cached)) return cached;

				var props = new List<SettingProperty>();
				foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
				{
					var attr = prop.GetCustomAttribute<SettingAttribute>();
					if (attr is null) continue;
					props.Add(new SettingProperty(prop, attr));
				}
				_cache[type] = props;
				return props;
			}
		}
	}

	sealed record SettingProperty(PropertyInfo Property, SettingAttribute Attribute);
}
