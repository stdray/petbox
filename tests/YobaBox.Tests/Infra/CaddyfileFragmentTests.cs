namespace YobaBox.Tests.Infra;

public sealed class CaddyfileFragmentTests
{
	static string FindFragment()
	{
		var dir = AppContext.BaseDirectory;
		while (!string.IsNullOrEmpty(dir))
		{
			var candidate = Path.Combine(dir, "infra", "Caddyfile.fragment");
			if (File.Exists(candidate))
				return candidate;
			dir = Path.GetDirectoryName(dir);
		}
		throw new FileNotFoundException("infra/Caddyfile.fragment not found walking up from test bin");
	}

	[Fact]
	public void Fragment_Has_FlushInterval_Minus_One_For_SSE()
	{
		var text = File.ReadAllText(FindFragment());
		text.Should().Contain("flush_interval -1",
			"SSE live-tail relies on Caddy not buffering the reverse-proxy response body");
	}

	[Fact]
	public void Fragment_Reverse_Proxies_To_Port_8080()
	{
		var text = File.ReadAllText(FindFragment());
		text.Should().Contain("127.0.0.1:8080",
			"yobabox's loopback port is 8080");
	}

	[Fact]
	public void Fragment_Host_Block_Is_YobaBox_Domain()
	{
		var text = File.ReadAllText(FindFragment());
		text.Should().Contain("yobabox.3po.su",
			"fragment targets yobabox's production host");
	}
}
