using System.Collections.Generic;
using PetBox.Config;

namespace PetBox.Tests.Web;

public sealed class ConfigTemplatesTests
{
	static readonly Dictionary<string, string> Sample = new()
	{
		["db.host"] = "h1",
		["db.port"] = "5432",
		["feature-x"] = "true",
	};

	[Fact]
	public void Flat_BuildsNestedTree()
	{
		var shaped = (Dictionary<string, object>)ConfigTemplates.Shape(Sample, "flat");
		var db = (Dictionary<string, object>)shaped["db"];
		db["host"].Should().Be("h1");
		db["port"].Should().Be("5432");
		shaped["feature-x"].Should().Be("true");
	}

	[Fact]
	public void Default_IsFlat()
	{
		var shaped = ConfigTemplates.Shape(Sample, template: null);
		shaped.Should().BeOfType<Dictionary<string, object>>();
	}

	[Fact]
	public void Dotnet_JoinsWithColon()
	{
		var shaped = (Dictionary<string, string>)ConfigTemplates.Shape(Sample, "dotnet");
		shaped["db:host"].Should().Be("h1");
		shaped["db:port"].Should().Be("5432");
	}

	[Fact]
	public void EnvVar_UpperSnake()
	{
		var shaped = (Dictionary<string, string>)ConfigTemplates.Shape(Sample, "envvar");
		shaped["DB_HOST"].Should().Be("h1");
		shaped["FEATURE_X"].Should().Be("true");
	}

	[Fact]
	public void EnvVarDeep_DoubleUnderscore()
	{
		var shaped = (Dictionary<string, string>)ConfigTemplates.Shape(Sample, "envvar-deep");
		shaped["DB__HOST"].Should().Be("h1");
		shaped["DB__PORT"].Should().Be("5432");
	}

	[Fact]
	public void Dotenv_EmitsKeyValueLines_UpperSnake_Sorted_RawValues()
	{
		var body = ConfigTemplates.Dotenv(Sample);
		// Sorted by original path (db.host, db.port, feature-x), UPPER_SNAKE keys, raw unquoted values.
		body.Should().Be("DB_HOST=h1\nDB_PORT=5432\nFEATURE_X=true\n");
	}
}
