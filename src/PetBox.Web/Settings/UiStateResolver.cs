using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using PetBox.Core.Auth;
using PetBox.Core.Settings;
using PetBox.Web.Navigation;

namespace PetBox.Web.Settings;

// Resolves the combined DB+cookie UI-state record BEFORE render, the same shape ThemeHelper
// established for the theme: a pure/testable core (ResolveAsync/ApplyBrowserState/MergeCookieValue,
// no ASP.NET types) plus a thin wiring method (ResolveForCurrentUserAsync) that pulls the user id
// and the cookie out of the current request. Call it from a layout the same way ThemeHelper is
// called — before RenderBody — so a future [BrowserState]/[Setting] property never needs a
// post-load class correction, a redirect, or a reload to take effect.
public static class UiStateResolver
{
	// The ONE cookie every [BrowserState] property reads and writes through — never one cookie per
	// feature (that would grow every request's header on each new preference). Its value is a flat
	// JSON object keyed by each property's BrowserStateAttribute.Key.
	public const string CookieName = "petbox.ui";

	static readonly JsonSerializerOptions JsonOptions = new()
	{
		Converters = { new JsonStringEnumConverter() },
	};

	// Core resolution: the DB branch (only when a userId is given — Scope.User, same axis
	// UiSettings.Theme already uses) merged with the cookie branch. No HttpContext/INavigationContext
	// dependency, so it's directly unit-testable. `userId is null` (anonymous) skips the DB branch
	// entirely and never throws — requirement is cookie-branch-only, no exception, for anonymous
	// visitors.
	public static async Task<T> ResolveAsync<T>(
		ISettingsResolver settings, string? userId, string? cookieValue, CancellationToken ct = default)
		where T : new()
	{
		var result = string.IsNullOrEmpty(userId)
			? new T()
			: await settings.GetAsync<T>(Scope.User, userId, ct);
		return ApplyBrowserState(result, cookieValue);
	}

	// ASP.NET wiring: extracts the user id and the petbox.ui cookie from the current request the
	// same way ThemeHelper.ResolveForCurrentUserAsync extracts the theme, then defers to ResolveAsync.
	public static Task<T> ResolveForCurrentUserAsync<T>(
		INavigationContext nav, ISettingsResolver settings, HttpContext http, CancellationToken ct = default)
		where T : new()
	{
		var userId = nav.IsAuthenticated ? http.User.FindFirst(PetBoxClaims.UserId)?.Value : null;
		var cookieValue = http.Request.Cookies.TryGetValue(CookieName, out var v) ? v : null;
		return ResolveAsync<T>(settings, userId, cookieValue, ct);
	}

	// Applies the cookie branch onto `target` in place (mirrors SettingsResolver.GetAsync, which
	// also mutates a freshly-constructed T via reflection SetValue on an init-only property) and
	// returns it. Never throws: a missing cookie, malformed JSON, a JSON value of the wrong shape,
	// or one bad property all just leave that property at its record default — this is the codepath
	// an anonymous, first-time, or stale-cookie visitor hits on every request.
	public static T ApplyBrowserState<T>(T target, string? cookieValue) where T : new()
	{
		if (string.IsNullOrEmpty(cookieValue)) return target;

		JsonNode? node;
		try
		{
			node = JsonNode.Parse(cookieValue);
		}
		catch (JsonException)
		{
			return target;
		}

		if (node is not JsonObject obj) return target;

		foreach (var prop in BrowserStatePropertyCache.Get(typeof(T)))
		{
			if (!obj.TryGetPropertyValue(prop.Attribute.Key, out var value) || value is null) continue;

			try
			{
				var deserialized = value.Deserialize(prop.Property.PropertyType, JsonOptions);
				prop.Property.SetValue(target, deserialized);
			}
			catch (JsonException)
			{
				// This one key is malformed (wrong shape/type) — leave the property at its default
				// and keep applying the rest of the cookie rather than discarding it wholesale.
			}
		}

		return target;
	}

	// Merge-write: folds `newValues`'s [BrowserState] properties into whatever the existing cookie
	// already holds, so writing one feature's key never clobbers another's — the reason there is
	// ONE cookie, not N. A missing/malformed existing cookie is treated as an empty object, not an
	// error (symmetric with ApplyBrowserState's read side).
	public static string MergeCookieValue<T>(string? existingCookieValue, T newValues) where T : notnull
	{
		JsonObject obj = null!;
		if (!string.IsNullOrEmpty(existingCookieValue))
		{
			try
			{
				obj = JsonNode.Parse(existingCookieValue) as JsonObject ?? new JsonObject();
			}
			catch (JsonException)
			{
				obj = new JsonObject();
			}
		}
		else
		{
			obj = new JsonObject();
		}

		foreach (var prop in BrowserStatePropertyCache.Get(typeof(T)))
		{
			var value = prop.Property.GetValue(newValues);
			obj[prop.Attribute.Key] = value is null ? null : JsonSerializer.SerializeToNode(value, prop.Property.PropertyType, JsonOptions);
		}

		return obj.ToJsonString(JsonOptions);
	}

	static class BrowserStatePropertyCache
	{
		static readonly Dictionary<Type, IReadOnlyList<BrowserStateProperty>> _cache = [];
		static readonly Lock _lock = new();

		public static IReadOnlyList<BrowserStateProperty> Get(Type type)
		{
			lock (_lock)
			{
				if (_cache.TryGetValue(type, out var cached)) return cached;

				var props = new List<BrowserStateProperty>();
				foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
				{
					var attr = prop.GetCustomAttribute<BrowserStateAttribute>();
					if (attr is null) continue;
					props.Add(new BrowserStateProperty(prop, attr));
				}
				_cache[type] = props;
				return props;
			}
		}
	}

	sealed record BrowserStateProperty(PropertyInfo Property, BrowserStateAttribute Attribute);
}
