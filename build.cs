#:sdk Cake.Sdk@6.2.0
#:property ManagePackageVersionsCentrally=false
#:property Nullable=disable
#:property TreatWarningsAsErrors=false
#:package Cake.Docker@1.5.0-beta.1
// GitVersion.Tool is installed via .config/dotnet-tools.json (dotnet tool restore runs in build.ps1/build.sh)

var target = Argument("target", "Default");
var configuration = Argument("configuration", "Release");
var dockerImage = Argument("dockerImage", "petbox");
var dockerTagArgument = Argument("dockerTag", string.Empty);
var dockerPushEnabled = Argument("dockerPush", false);
var ghcrRepositoryArgument = Argument("ghcrRepository", string.Empty);
var dockerTagOutputArgument = Argument("dockerTagOutput", string.Empty);
var dockerCacheFrom = Argument("dockerCacheFrom", string.Empty);
var dockerCacheTo = Argument("dockerCacheTo", string.Empty);

var solution = "./PetBox.slnx";
var dockerFile = "./Dockerfile";

// .NET client packages — published to GitHub Packages NuGet feed via `nuget` tag push.
var clientConfigProject = "./src/clients-net/PetBox.Client.Config/PetBox.Client.Config.csproj";

// TS SDK — published to GitHub Packages npm registry via `npm` tag push.
var tsSdkDir = "./src/clients-ts/petbox-client";

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
		// Ensure Playwright browser binaries are installed for the E2E suite.
		// Microsoft.Playwright drops `playwright.ps1` into the test bin dir;
		// invoke it via pwsh. `--with-deps` pulls Linux libs the chromium
		// headless shell needs. No-op fast if already installed.
		var playwrightScript = GetFiles("./tests/PetBox.E2ETests/bin/" + configuration + "/**/playwright.ps1").FirstOrDefault();
		if (playwrightScript != null)
		{
			var args = new ProcessArgumentBuilder()
				.Append(playwrightScript.FullPath)
				.Append("install")
				.Append("chromium");
			if (!IsRunningOnWindows())
				args.Append("--with-deps");
			var exit = StartProcess("pwsh", new ProcessSettings { Arguments = args });
			if (exit != 0)
				throw new Exception($"Playwright install exited with code {exit}");
		}

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

// ─── NuGet packaging + publish (GitHub Packages) ───

Task("Pack")
	.IsDependentOn("Version")
	.Does(() =>
	{
		var buildVersion = gitVersion.FullSemVer;

		DotNetBuild(clientConfigProject, new DotNetBuildSettings
		{
			Configuration = configuration,
			NoRestore = true,
			MSBuildSettings = new DotNetMSBuildSettings()
				.WithProperty("Version", buildVersion)
				.WithProperty("InformationalVersion", $"{buildVersion} ({gitVersion.ShortSha}, {gitVersion.CommitDate})")
				.WithProperty("GitShortSha", gitVersion.ShortSha)
				.WithProperty("GitCommitDate", gitVersion.CommitDate)
		});

		var packSettings = new DotNetPackSettings
		{
			Configuration = configuration,
			OutputDirectory = "./artifacts",
			NoBuild = true,
			NoRestore = true,
			IncludeSource = false,
			IncludeSymbols = true,
			SymbolPackageFormat = "snupkg",
			MSBuildSettings = new DotNetMSBuildSettings()
				.WithProperty("Version", buildVersion)
				.WithProperty("InformationalVersion", $"{buildVersion} ({gitVersion.ShortSha}, {gitVersion.CommitDate})")
		};

		DotNetPack(clientConfigProject, packSettings);
	});

// Publishes to GitHub Packages NuGet feed. Requires GITHUB_TOKEN with packages:write
// and GITHUB_REPOSITORY_OWNER (both set by GitHub Actions automatically). Uses --skip-duplicate
// because the same FullSemVer can land repeatedly on a single tag (re-runs).
Task("NuGetPush")
	.IsDependentOn("Pack")
	.Does(() =>
	{
		var apiKey = EnvironmentVariable("GITHUB_TOKEN");
		var owner = EnvironmentVariable("GITHUB_REPOSITORY_OWNER");

		if (string.IsNullOrWhiteSpace(apiKey))
			throw new CakeException("GITHUB_TOKEN environment variable is not set. GitHub Packages publishing requires GITHUB_TOKEN with packages:write.");
		if (string.IsNullOrWhiteSpace(owner))
			throw new CakeException("GITHUB_REPOSITORY_OWNER environment variable is not set. Set it to your GitHub username/org.");

		var source = $"https://nuget.pkg.github.com/{owner}/index.json";
		var packages = GetFiles("./artifacts/*.nupkg");

		foreach (var package in packages)
		{
			StartProcess("dotnet", new ProcessSettings
			{
				Arguments = $"nuget push \"{package.FullPath}\" --source \"{source}\" --api-key {apiKey} --skip-duplicate"
			});

			Information("Pushed package {0} to {1}", package.GetFilename(), source);
		}
	});

// ─── TS SDK build + publish (GitHub Packages npm) ───

Task("TsSdkInstall")
	.Does(() => StartProcess("bun", new ProcessSettings { Arguments = "install --frozen-lockfile", WorkingDirectory = tsSdkDir }));

Task("TsSdkTypecheck")
	.IsDependentOn("TsSdkInstall")
	.Does(() => StartProcess("bun", new ProcessSettings { Arguments = "run typecheck", WorkingDirectory = tsSdkDir }));

Task("TsSdkLint")
	.IsDependentOn("TsSdkInstall")
	.Does(() => StartProcess("bun", new ProcessSettings { Arguments = "run lint", WorkingDirectory = tsSdkDir }));

Task("TsSdkTest")
	.IsDependentOn("TsSdkInstall")
	.Does(() => StartProcess("bun", new ProcessSettings { Arguments = "test", WorkingDirectory = tsSdkDir }));

Task("TsSdkBuild")
	.IsDependentOn("TsSdkInstall")
	.Does(() => StartProcess("bun", new ProcessSettings { Arguments = "run build", WorkingDirectory = tsSdkDir }));

// Stamp package.json version from GitVersion, then verify the build output.
// GitVersion runs standalone (only needs git, not .NET) so we avoid the full
// Clean→Restore→Version chain which requires the entire solution to build.
Task("TsSdkPack")
	.IsDependentOn("TsSdkBuild")
	.Does(() =>
	{
		var gv = GitVersion(new GitVersionSettings { OutputType = GitVersionOutput.Json, NoFetch = true });
		// npm semver forbids '+', so replace the GitVersion build-metadata separator.
		var npmVersion = gv.FullSemVer.Replace('+', '-');
		Information("Stamping TS SDK version: {0} (GitVersion: {1})", npmVersion, gv.FullSemVer);
		StartProcess("npm", new ProcessSettings
		{
			Arguments = $"version {npmVersion} --no-git-tag-version --allow-same-version",
			WorkingDirectory = tsSdkDir,
		});
	});

// Publishes to GitHub Packages npm registry. Requires GITHUB_TOKEN with packages:write.
// Writes .npmrc with the registry + auth token, then `npm publish`. Token is the same
// as for NuGet — GitHub Packages uses one token for both registries.
Task("NpmPublish")
	.IsDependentOn("TsSdkPack")
	.Does(() =>
	{
		var token = EnvironmentVariable("GITHUB_TOKEN");
		var owner = EnvironmentVariable("GITHUB_REPOSITORY_OWNER");

		if (string.IsNullOrWhiteSpace(token))
			throw new CakeException("GITHUB_TOKEN environment variable is not set.");
		if (string.IsNullOrWhiteSpace(owner))
			throw new CakeException("GITHUB_REPOSITORY_OWNER environment variable is not set.");

		var absDir = MakeAbsolute(Directory(tsSdkDir)).FullPath;
		var npmrc = System.IO.Path.Combine(absDir, ".npmrc");
		// Scope-bound registry: @stdray packages → npm.pkg.github.com. Token used
		// for authentication. Per-package scope mapping is needed because the
		// default registry (npmjs.org) is also queried for unscoped deps.
		System.IO.File.WriteAllText(npmrc,
			$"@{owner.ToLowerInvariant()}:registry=https://npm.pkg.github.com\n" +
			$"//npm.pkg.github.com/:_authToken={token}\n");
		try
		{
			StartProcess("npm", new ProcessSettings
			{
				Arguments = "publish",
				WorkingDirectory = tsSdkDir,
			});
			Information("Published TS SDK to GitHub Packages (@{0}/petbox-client)", owner.ToLowerInvariant());
		}
		finally
		{
			if (System.IO.File.Exists(npmrc))
				System.IO.File.Delete(npmrc);
		}
	});

// ─── Dev loop ───

Task("Dev")
	.Description("Run bun + dotnet watchers side by side. Ctrl+C to stop both.")
	.Does(() =>
	{
		var webDir = "./src/PetBox.Web";

		Information("Installing frontend deps (bun install)...");
		var bunInstallExit = StartProcess("bun", new ProcessSettings
		{
			Arguments = "install",
			WorkingDirectory = webDir,
		});
		if (bunInstallExit != 0)
			throw new CakeException($"bun install failed with exit code {bunInstallExit}");

		Information("Starting bun run dev (ts + css watchers)...");
		var bunProc = StartAndReturnProcess("bun", new ProcessSettings
		{
			Arguments = "run dev",
			WorkingDirectory = webDir,
		});

		try
		{
			Information("Starting dotnet watch (Ctrl+C to stop both)...");
			StartProcess("dotnet", new ProcessSettings
			{
				Arguments = "watch run",
				WorkingDirectory = webDir,
			});
		}
		finally
		{
			Information("Stopping bun watcher...");
			try { bunProc.Kill(); } catch (Exception ex) { Warning("Failed to stop bun: {0}", ex.Message); }
			bunProc.Dispose();
		}
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
	.IsDependentOn("Test")
	.IsDependentOn("TsSdkLint")
	.IsDependentOn("TsSdkTypecheck")
	.IsDependentOn("TsSdkTest");

Task("CI")
	.IsDependentOn("Verify");

Task("Default")
	.IsDependentOn("DockerPush");

RunTarget(target);
