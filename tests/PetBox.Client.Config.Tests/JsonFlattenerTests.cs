using System.Text.Json;
using PetBox.Client.Config;

namespace PetBox.Client.Config.Tests;

public class JsonFlattenerTests
{
	[Fact]
	public void Flatten_object_uses_colon_separator()
	{
		using var doc = JsonDocument.Parse("""{"db":{"host":"localhost","port":5432}}""");

		var result = JsonFlattener.Flatten(doc);

		result.Should().ContainKey("db:host").WhoseValue.Should().Be("localhost");
		result.Should().ContainKey("db:port").WhoseValue.Should().Be("5432");
	}

	[Fact]
	public void Flatten_array_uses_numeric_index_keys()
	{
		using var doc = JsonDocument.Parse("""{"features":["a","b","c"]}""");

		var result = JsonFlattener.Flatten(doc);

		result.Should().ContainKey("features:0").WhoseValue.Should().Be("a");
		result.Should().ContainKey("features:1").WhoseValue.Should().Be("b");
		result.Should().ContainKey("features:2").WhoseValue.Should().Be("c");
	}

	[Fact]
	public void Flatten_preserves_number_raw_text()
	{
		// Raw text matters: downstream IConfiguration.GetValue<double>("ratio") parses
		// the original literal without rounding through a .NET numeric here.
		using var doc = JsonDocument.Parse("""{"ratio":3.14159,"count":42}""");

		var result = JsonFlattener.Flatten(doc);

		result["ratio"].Should().Be("3.14159");
		result["count"].Should().Be("42");
	}

	[Fact]
	public void Flatten_booleans_as_lowercase_strings()
	{
		using var doc = JsonDocument.Parse("""{"enabled":true,"debug":false}""");

		var result = JsonFlattener.Flatten(doc);

		result["enabled"].Should().Be("true");
		result["debug"].Should().Be("false");
	}

	[Fact]
	public void Flatten_null_stored_as_null()
	{
		using var doc = JsonDocument.Parse("""{"missing":null}""");

		var result = JsonFlattener.Flatten(doc);

		result.Should().ContainKey("missing");
		result["missing"].Should().BeNull();
	}

	[Fact]
	public void Flatten_nested_objects_and_arrays()
	{
		using var doc = JsonDocument.Parse("""
			{"servers":[{"name":"a","port":80},{"name":"b","port":81}]}
			""");

		var result = JsonFlattener.Flatten(doc);

		result["servers:0:name"].Should().Be("a");
		result["servers:0:port"].Should().Be("80");
		result["servers:1:name"].Should().Be("b");
		result["servers:1:port"].Should().Be("81");
	}

	[Fact]
	public void Flatten_keys_are_case_insensitive()
	{
		using var doc = JsonDocument.Parse("""{"DB":{"HOST":"localhost"}}""");

		var result = JsonFlattener.Flatten(doc);

		// MEC convention: ordinal-ignore-case key lookups.
		result["db:host"].Should().Be("localhost");
		result["DB:HOST"].Should().Be("localhost");
	}

	[Fact]
	public void Flatten_empty_object_returns_empty_dict()
	{
		using var doc = JsonDocument.Parse("{}");

		var result = JsonFlattener.Flatten(doc);

		result.Should().BeEmpty();
	}
}
