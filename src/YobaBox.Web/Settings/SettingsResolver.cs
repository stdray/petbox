using System.Globalization;
using System.Reflection;
using System.Text.Json;
using LinqToDB;
using YobaBox.Config;
using YobaBox.Core.Data;
using YobaBox.Core.Settings;

namespace YobaBox.Web.Settings;

public sealed class SettingsResolver(YobaBoxDb db, ISecretEncryptor encryptor) : ISettingsResolver
{
	public async Task<T> GetAsync<T>(Scope deepestScope, string deepestScopeKey, CancellationToken ct = default)
		where T : new()
	{
		var result = new T();
		var props = SettingPropertyCache.Get(typeof(T));
		if (props.Count == 0) return result;

		foreach (var prop in props)
		{
			var chain = await BuildChainAsync(deepestScope, deepestScopeKey, prop.Attribute.TopLevel, ct);
			foreach (var (scope, scopeKey) in chain)
			{
				var row = await db.Settings.FirstOrDefaultAsync(
					s => s.Scope == scope.ToString()
						&& s.ScopeKey == scopeKey
						&& s.Path == prop.Attribute.Key,
					ct);
				if (row is null) continue;

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
		var now = DateTime.UtcNow;

		foreach (var prop in props)
		{
			var newVal = prop.Property.GetValue(newValues);
			var oldVal = prop.Property.GetValue(oldValues);
			if (Equals(newVal, oldVal)) continue;

			var (typeTag, encoded) = Encode(prop.Property.PropertyType, newVal, prop.Attribute.IsSecret);

			var existing = await db.Settings.FirstOrDefaultAsync(
				s => s.Scope == scope.ToString()
					&& s.ScopeKey == scopeKey
					&& s.Path == prop.Attribute.Key,
				ct);

			if (existing is null)
			{
				await db.InsertAsync(new Setting
				{
					Scope = scope.ToString(),
					ScopeKey = scopeKey,
					Path = prop.Attribute.Key,
					Type = typeTag,
					Value = encoded,
					UpdatedAt = now,
					UpdatedBy = updatedBy,
				}, token: ct);
			}
			else
			{
				await db.Settings
					.Where(s => s.Scope == scope.ToString() && s.ScopeKey == scopeKey && s.Path == prop.Attribute.Key)
					.Set(s => s.Type, typeTag)
					.Set(s => s.Value, encoded)
					.Set(s => s.UpdatedAt, now)
					.Set(s => s.UpdatedBy, updatedBy)
					.UpdateAsync(ct);
			}
		}
	}

	public async Task ResetAsync<T>(Scope scope, string scopeKey, string propertyName, CancellationToken ct = default)
		where T : notnull
	{
		var props = SettingPropertyCache.Get(typeof(T));
		var match = props.FirstOrDefault(p => p.Property.Name == propertyName)
			?? throw new ArgumentException($"Property {propertyName} on {typeof(T).Name} is not a [Setting].", nameof(propertyName));

		await db.Settings
			.Where(s => s.Scope == scope.ToString() && s.ScopeKey == scopeKey && s.Path == match.Attribute.Key)
			.DeleteAsync(ct);
	}

	async Task<List<(Scope Scope, string ScopeKey)>> BuildChainAsync(Scope deepest, string deepestKey, Scope topLevel, CancellationToken ct)
	{
		// Skip scopes finer than the property's TopLevel — they're not reachable for this property.
		var chain = new List<(Scope, string)>();
		if ((int)topLevel <= (int)deepest)
			chain.Add((deepest, deepestKey));

		switch (deepest)
		{
			case Scope.System:
				break;
			case Scope.Workspace:
				AddIfReachable(chain, Scope.System, "$", topLevel);
				break;
			case Scope.Project:
				var ws = await db.Projects
					.Where(p => p.Key == deepestKey)
					.Select(p => p.WorkspaceKey)
					.FirstOrDefaultAsync(ct);
				if (ws is not null)
					AddIfReachable(chain, Scope.Workspace, ws, topLevel);
				AddIfReachable(chain, Scope.System, "$", topLevel);
				break;
			case Scope.Service:
				// ScopeKey format: "{projectKey}/{serviceKey}"
				var slash = deepestKey.IndexOf('/');
				if (slash > 0)
				{
					var projKey = deepestKey[..slash];
					AddIfReachable(chain, Scope.Project, projKey, topLevel);
					var svcWs = await db.Projects
						.Where(p => p.Key == projKey)
						.Select(p => p.WorkspaceKey)
						.FirstOrDefaultAsync(ct);
					if (svcWs is not null)
						AddIfReachable(chain, Scope.Workspace, svcWs, topLevel);
				}
				AddIfReachable(chain, Scope.System, "$", topLevel);
				break;
			case Scope.User:
				AddIfReachable(chain, Scope.System, "$", topLevel);
				break;
			case Scope.Membership:
				// ScopeKey format: "{userId}:{workspaceKey}"
				var colon = deepestKey.IndexOf(':');
				if (colon > 0)
				{
					var userPart = deepestKey[..colon];
					var wsPart = deepestKey[(colon + 1)..];
					AddIfReachable(chain, Scope.User, userPart, topLevel);
					AddIfReachable(chain, Scope.Workspace, wsPart, topLevel);
				}
				AddIfReachable(chain, Scope.System, "$", topLevel);
				break;
		}

		return chain;
	}

	static void AddIfReachable(List<(Scope, string)> chain, Scope scope, string key, Scope topLevel)
	{
		if ((int)topLevel <= (int)scope)
			chain.Add((scope, key));
	}

	(string TypeTag, string Encoded) Encode(Type clrType, object? value, bool isSecret)
	{
		if (isSecret)
		{
			if (clrType != typeof(string))
				throw new InvalidOperationException("[Setting(IsSecret=true)] requires string property type.");
			if (!encryptor.IsAvailable)
				throw new InvalidOperationException("Secret setting requires YOBABOX_MASTER_KEY to be configured.");

			var bundle = encryptor.Encrypt((string?)value ?? string.Empty);
			var blob = JsonSerializer.Serialize(new[] { bundle.Ciphertext, bundle.Iv, bundle.AuthTag });
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
		return ("json", JsonSerializer.Serialize(value));
	}

	object? Decode(Type clrType, string typeTag, string stored, bool isSecret)
	{
		if (isSecret && typeTag == "secret")
		{
			if (!encryptor.IsAvailable) return string.Empty;
			var triple = JsonSerializer.Deserialize<string[]>(stored);
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
			"json" => JsonSerializer.Deserialize(stored, clrType),
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
