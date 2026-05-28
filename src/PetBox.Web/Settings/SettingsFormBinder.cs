using System.Globalization;
using System.Reflection;
using Microsoft.AspNetCore.Http;
using PetBox.Core.Settings;

namespace PetBox.Web.Settings;

// Helpers shared by leaf/Defaults pages that POST a settings record.
// Each page knows its record type at compile time; this just provides
// the reflection-driven form-value → typed-instance conversion so the
// page doesn't have to re-implement it.
public static class SettingsFormBinder
{
	// Build a new instance of T from request form fields, starting from
	// `current` (so unchanged properties keep their resolved value). Only
	// properties with [Setting] are bound.
	public static T BuildFrom<T>(IFormCollection form, T current) where T : notnull
	{
		var props = typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance);
		object result = current;

		foreach (var prop in props)
		{
			var attr = prop.GetCustomAttribute<SettingAttribute>();
			if (attr is null) continue;

			if (!form.TryGetValue(prop.Name, out var raw))
			{
				// Bool inputs absent from the form mean unchecked.
				if (prop.PropertyType == typeof(bool))
					result = SetProperty(result, prop, false);
				continue;
			}

			var s = raw.ToString();

			// Secret + blank means "leave unchanged" — skip.
			if (attr.IsSecret && string.IsNullOrEmpty(s))
				continue;

			var underlying = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;
			object? parsed;
			try
			{
				parsed = underlying switch
				{
					var t when t == typeof(string) => s,
					var t when t == typeof(int) => int.Parse(s, CultureInfo.InvariantCulture),
					var t when t == typeof(long) => long.Parse(s, CultureInfo.InvariantCulture),
					var t when t == typeof(bool) => s.Equals("true", StringComparison.OrdinalIgnoreCase) || s == "on",
					var t when t.IsEnum => Enum.Parse(t, s, ignoreCase: true),
					_ => s,
				};
			}
			catch (Exception ex) when (ex is FormatException or ArgumentException or OverflowException)
			{
				continue;
			}

			result = SetProperty(result, prop, parsed);
		}

		return (T)result;
	}

	static object SetProperty(object instance, PropertyInfo prop, object? value)
	{
		prop.SetValue(instance, value);
		return instance;
	}
}
