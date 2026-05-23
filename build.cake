#addin nuget:?package=Cake.Docker&version=5.0.0
#tool dotnet:?package=GitVersion.Tool&version=6.4.0

var target = Argument("target", "Default");
var configuration = Argument("configuration", "Release");
var dockerImage = Argument("dockerImage", "yobabox");
var dockerTagArgument = Argument("dockerTag", string.Empty);
var dockerPushEnabled = Argument("dockerPush", false);
var ghcrRepositoryArgument = Argument("ghcrRepository", string.Empty);
var dockerTagOutputArgument = Argument("dockerTagOutput", string.Empty);
var dockerCacheFrom = Argument("dockerCacheFrom", string.Empty);
var dockerCacheTo = Argument("dockerCacheTo", string.Empty);

var solution = "./YobaBox.slnx";
var dockerFile = "./Dockerfile";

GitVersion gitVersion = null;
var computedDockerTag = "latest";

DotNetBuildSettings CreateVersionedBuildSettings(string buildVersion, string shortSha, string commitDate) =>
	new()
	{
		Configuration = configuration,
		NoRestore = true,
		MSBuildSettings = new DotNetMSBuildSettings()
			.WithProperty("Version", buildVersion)
			.WithProperty("InformationalVersion", $"{buildVersion} ({shortSha}, {commitDate})")
			.WithProperty("GitShortSha", shortSha)
			.WithProperty("GitCommitDate", commitDate)
	};

// ─── Tasks ───

Task("Clean")
	.Does(() => DotNetClean(solution, new DotNetCleanSettings { Configuration = configuration }));

Task("Restore")
	.IsDependentOn("Clean")
	.Does(() => DotNetRestore(solution));

Task("Version")
	.IsDependentOn("Restore")
	.Does(() =>
	{
		gitVersion = GitVersion(new GitVersionSettings
		{
			OutputType = GitVersionOutput.Json,
			NoFetch = true
		});

		Information("GitVersion FullSemVer: {0}", gitVersion.FullSemVer);
		Information("GitVersion ShortSha: {0}", gitVersion.ShortSha);
		Information("GitVersion CommitDate: {0}", gitVersion.CommitDate);
	});

Task("Build")
	.IsDependentOn("Version")
	.Does(() =>
	{
		var buildVersion = gitVersion.FullSemVer;
		DotNetBuild(solution, CreateVersionedBuildSettings(buildVersion, gitVersion.ShortSha, gitVersion.CommitDate));
	});

Task("Test")
	.IsDependentOn("Build")
	.Does(() =>
	{
		var testProjects = GetFiles("./tests/**/*.csproj");
		foreach (var testProj in testProjects)
		{
			DotNetTest(testProj.FullPath, new DotNetTestSettings
			{
				Configuration = configuration,
				NoBuild = true
			});
		}
	});

Task("Docker")
	.IsDependentOn("Test")
	.Does(() =>
	{
		var gitVersionTag = gitVersion.FullSemVer.Replace('+', '-');
		var finalTag = string.IsNullOrWhiteSpace(dockerTagArgument) ? gitVersionTag : dockerTagArgument;
		computedDockerTag = finalTag;
		var imageWithTag = $"{dockerImage}:{finalTag}";

		if (!string.IsNullOrWhiteSpace(dockerTagOutputArgument))
		{
			var outputPath = MakeAbsolute(FilePath.FromString(dockerTagOutputArgument));
			EnsureDirectoryExists(outputPath.GetDirectory());
			System.IO.File.WriteAllText(outputPath.FullPath, finalTag);
		}

		Information("Building Docker image {0}", imageWithTag);

		var cacheFrom = string.IsNullOrWhiteSpace(dockerCacheFrom) ? Array.Empty<string>() : new[] { dockerCacheFrom };
		var cacheTo = string.IsNullOrWhiteSpace(dockerCacheTo) ? Array.Empty<string>() : new[] { dockerCacheTo };

		var buildSettings = new DockerBuildXBuildSettings
		{
			File = dockerFile,
			Tag = new[] { imageWithTag },
			BuildArg = new[]
			{
				$"APP_VERSION={gitVersion.FullSemVer}",
				$"GIT_SHORT_SHA={gitVersion.ShortSha}",
				$"GIT_COMMIT_DATE={gitVersion.CommitDate}"
			},
			CacheFrom = cacheFrom,
			CacheTo = cacheTo,
			Load = true,
		};

		DockerBuildXBuild(buildSettings, ".");
	});

Task("DockerSmoke")
	.IsDependentOn("Docker")
	.Does(() =>
	{
		var imageWithTag = $"{dockerImage}:{computedDockerTag}";
		var containerName = $"{dockerImage}-smoke-{Guid.NewGuid():N}"[..30];

		Information("Starting smoke-test container {0}", containerName);
		var runExit = StartProcess("docker", new ProcessSettings
		{
			Arguments = $"run -d --name {containerName} -p 8080:8080 {imageWithTag}"
		});
		if (runExit != 0)
			throw new CakeException($"docker run failed with exit code {runExit}");

		try
		{
			var healthy = false;
			for (var i = 1; i <= 30; i++)
			{
				System.Threading.Thread.Sleep(1000);
				var curlExit = StartProcess("curl", new ProcessSettings
				{
					Arguments = IsRunningOnWindows()
						? "-fsS --max-time 2 -o NUL http://127.0.0.1:8080/health"
						: "-fsS --max-time 2 -o /dev/null http://127.0.0.1:8080/health",
					RedirectStandardError = true,
					RedirectStandardOutput = true
				});

				if (curlExit == 0)
				{
					Information("Smoke test passed after {0}s", i);
					healthy = true;
					break;
				}
			}

			if (!healthy)
			{
				StartProcess("docker", $"logs {containerName}");
				throw new CakeException("Container did not respond with 200 on / within 30s");
			}
		}
		finally
		{
			StartProcess("docker", $"stop {containerName}");
			StartProcess("docker", $"rm {containerName}");
		}
	});

Task("DockerPush")
	.IsDependentOn("Test")
	.IsDependentOn("DockerSmoke")
	.WithCriteria(() => dockerPushEnabled)
	.Does(() =>
	{
		if (string.IsNullOrWhiteSpace(computedDockerTag))
			throw new CakeException("Docker tag was not computed.");

		var sourceImage = $"{dockerImage}:{computedDockerTag}";
		var repository = ghcrRepositoryArgument;

		if (string.IsNullOrWhiteSpace(repository))
		{
			var githubRepositoryEnv = EnvironmentVariable("GITHUB_REPOSITORY");
			if (string.IsNullOrWhiteSpace(githubRepositoryEnv))
				throw new CakeException("dockerPush enabled but no ghcrRepository and no GITHUB_REPOSITORY.");
			repository = $"ghcr.io/{githubRepositoryEnv.ToLowerInvariant()}/{dockerImage}";
		}
		else if (!repository.StartsWith("ghcr.io/", StringComparison.OrdinalIgnoreCase))
		{
			repository = $"ghcr.io/{repository}";
		}

		var targetImage = $"{repository}:{computedDockerTag}";
		var ghcrUsername = EnvironmentVariable("GHCR_USERNAME");
		var ghcrToken = EnvironmentVariable("GHCR_TOKEN");

		if (!string.IsNullOrWhiteSpace(ghcrUsername) && !string.IsNullOrWhiteSpace(ghcrToken))
			DockerLogin("ghcr.io", ghcrUsername, ghcrToken);
		else
			Information("GHCR credentials not provided; assuming docker login already performed.");

		Information("Tagging {0} as {1}", sourceImage, targetImage);
		DockerTag(sourceImage, targetImage);
		Information("Pushing {0}", targetImage);
		DockerPush(targetImage);
	});

// ─── Lint / verify ───

Task("FormatVerify")
	.Does(() =>
	{
		StartProcess("dotnet", $"restore {solution}");
		StartProcess("dotnet", $"format {solution} --verify-no-changes --no-restore");
	});

Task("Verify")
	.IsDependentOn("FormatVerify")
	.IsDependentOn("Test");

Task("CI")
	.IsDependentOn("Verify");

Task("Default")
	.IsDependentOn("DockerPush");

RunTarget(target);
