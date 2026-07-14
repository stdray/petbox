namespace PetBox.Tests.Infra;

public sealed class CaddyJsonTests
{
	static string FindCaddyJson()
	{
		var dir = AppContext.BaseDirectory;
		while (!string.IsNullOrEmpty(dir))
		{
			var candidate = Path.Combine(dir, "infra", "caddy.json");
			if (File.Exists(candidate))
				return candidate;
			dir = Path.GetDirectoryName(dir);
		}
		throw new FileNotFoundException("infra/caddy.json not found walking up from test bin");
	}

	[Fact]
	public void Config_Has_FlushInterval_Minus_One_For_SSE()
	{
		var text = File.ReadAllText(FindCaddyJson());
		text.Should().Contain("\"flush_interval\": -1",
			"SSE live-tail relies on Caddy not buffering the reverse-proxy response body");
	}

	[Fact]
	public void Config_Reverse_Proxies_To_Port_8083()
	{
		var text = File.ReadAllText(FindCaddyJson());
		text.Should().Contain("localhost:8083",
			"petbox's loopback port is 8083 (shared-host convention: yobaconf=8081, yobalog=8082, petbox=8083)");
	}

	[Fact]
	public void Config_Host_Block_Is_PetBox_Domain()
	{
		var text = File.ReadAllText(FindCaddyJson());
		text.Should().Contain("petbox.3po.su",
			"config targets petbox's production host");
	}

	[Fact]
	public void Config_Serves_503_With_RetryAfter_On_Backend_Down()
	{
		var text = File.ReadAllText(FindCaddyJson());
		text.Should().Contain("\"status_code\": 503",
			"deploy downtime must surface as a 503 + Retry-After stub, not a raw 502/504");
		text.Should().Contain("\"Retry-After\": [\"60\"]",
			"retry hint is calibrated from measured deploy downtime (median 37s, p90 ~55s, max 114s)");
	}
}
