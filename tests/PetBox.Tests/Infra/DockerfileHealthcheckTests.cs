namespace PetBox.Tests.Infra;

// Guards the fix for prod-container-diagnosability-gaps #2: before this, neither the Dockerfile
// nor deploy/compose.yaml declared a HEALTHCHECK, so `docker ps` STATUS ("Up") was the only
// liveness signal — and it lied: field observation was `docker ps` showing `Up` against an
// empty `docker top` (and, separately, hours of a process that stayed "Up" while its own
// request/log throughput had collapsed). The final image
// (mcr.microsoft.com/dotnet/nightly/runtime-deps:...-chiseled) is self-contained with no shell
// and no curl/wget/dotnet CLI, so the ONLY thing HEALTHCHECK CMD can invoke is the app's own
// apphost in a self-check mode — see Program.RunHealthCheck / the `--healthcheck` arg branch.
public sealed class DockerfileHealthcheckTests
{
	static string FindDockerfile()
	{
		var dir = AppContext.BaseDirectory;
		while (!string.IsNullOrEmpty(dir))
		{
			var candidate = Path.Combine(dir, "Dockerfile");
			if (File.Exists(candidate))
				return candidate;
			dir = Path.GetDirectoryName(dir);
		}
		throw new FileNotFoundException("Dockerfile not found walking up from test bin");
	}

	[Fact]
	public void Final_Stage_Declares_A_Healthcheck_Calling_The_Apphost_Self_Check()
	{
		var text = File.ReadAllText(FindDockerfile());
		text.Should().MatchRegex(@"HEALTHCHECK\s+[\s\S]*?CMD\s+\[""\./PetBox\.Web"",\s*""--healthcheck""\]",
			"the chiseled final image has no shell/curl/wget/dotnet-CLI — the apphost's own " +
			"`--healthcheck` self-check (Program.RunHealthCheck) is the only executable HEALTHCHECK " +
			"CMD can call in this image");
	}
}
