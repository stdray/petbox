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
// Run `playwright install --with-deps` (apt-installs chromium's system libs). Default
// true for bare dev boxes; CI sets false (hosted runners already have the libs).
var playwrightWithDeps = Argument("playwrightWithDeps", true);

var solution = "./PetBox.slnx";
var dockerFile = "./Dockerfile";

// .NET client packages — published to GitHub Packages NuGet feed via `nuget` tag push.
var clientCoreProject = "./src/clients-net/PetBox.Client/PetBox.Client.csproj";
var clientConfigProject = "./src/clients-net/PetBox.Client.Config/PetBox.Client.Config.csproj";
var clientLinq2DbProject = "./src/clients-net/PetBox.Client.Data.Linq2Db/PetBox.Client.Data.Linq2Db.csproj";
// .NET packages published to nuget.org via `nuget` tag push. Config and Linq2Db depend on the
// core (ProjectReference → package dependency), so all ship at the same GitVersion version.
var nugetProjects = new[] { clientCoreProject, clientConfigProject, clientLinq2DbProject };

// TS SDK — published to the public npm registry (npmjs.org) via `npm` tag push.
var tsSdkDir = "./src/clients-ts/petbox-client";

// Agent-wiring kit — published to the public npm registry (npmjs.org) via its OWN `npm-wire`
// tag push (one tag per package channel, like deploy/nuget/npm/pypi). Raw plain-TS package
// (native type-stripping): no install/build, only a GitVersion version-stamp before publish.
var tsWireDir = "./src/clients-ts/petbox-wire";

// Python SDK — published to public PyPI via `pypi` tag push.
var pyClientDir = "./src/clients-py/petbox-client";

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

void RunUv(string args, string workingDir)
{
	var exit = StartProcess("uv", new ProcessSettings { Arguments = args, WorkingDirectory = workingDir });
	if (exit != 0)
		throw new CakeException($"uv {args} failed with exit code {exit}");
}

// The bun twin of RunUv. StartProcess returns the exit code and DISCARDING it makes the task
// report success no matter what the tool said — the TsSdk* targets each called StartProcess bare,
// so `bun run typecheck` could print TS2322 and the build still exited 0 (same defect FormatVerify
// had, fixed in c68dc71). Every bun invocation goes through here so a gate cannot silently pass.
void RunBun(string args, string workingDir)
{
	var exit = StartProcess("bun", new ProcessSettings { Arguments = args, WorkingDirectory = workingDir });
	if (exit != 0)
		throw new CakeException($"bun {args} failed with exit code {exit}");
}

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

// `Test` IS the .NET gate — the single definition of "the code is OK", and the same thing
// locally and in CI. Every check is a precondition of it, so a green local Test cannot be a red
// CI run. The split it replaces (a `Validate` target = FormatVerify + Test, run only by CI)
// meant the dev loop ran a strictly weaker gate than the pipeline: the sandbox-write-gate branch
// was green on `Test` locally and failed the deploy tag run on whitespace it never saw. A new
// check goes here, once, and every caller — dev loop, CI, Verify — picks it up.
Task("Test")
	.IsDependentOn("Build")
	.IsDependentOn("FormatVerify")
	.Does(() =>
	{
		// Ensure Playwright browser binaries are installed for the E2E suite.
		// Microsoft.Playwright drops `playwright.ps1` into the test bin dir;
		// invoke it via pwsh. `--with-deps` pulls Linux libs the chromium headless
		// shell needs via apt — slow (an apt-get update every run) and redundant on
		// hosted GitHub runners, which already ship Chrome's shared libs. CI passes
		// --playwrightWithDeps=false and caches the browser binaries instead; the
		// default stays true so a bare Linux dev box still gets its libs.
		var playwrightScript = GetFiles("./tests/PetBox.E2ETests/bin/" + configuration + "/**/playwright.ps1").FirstOrDefault();
		if (playwrightScript != null)
		{
			var args = new ProcessArgumentBuilder()
				.Append(playwrightScript.FullPath)
				.Append("install")
				.Append("chromium");
			if (!IsRunningOnWindows() && playwrightWithDeps)
				args.Append("--with-deps");
			var exit = StartProcess("pwsh", new ProcessSettings { Arguments = args });
			if (exit != 0)
				throw new Exception($"Playwright install exited with code {exit}");
		}

		DotNetTest(solution, new DotNetTestSettings
		{
			Configuration = configuration,
			NoBuild = true,
			// `research`-category tests are excluded from the default/CI run
			// (they may hit the network, e.g. DuckDB `INSTALL fts`); run them
			// explicitly with `dotnet test --filter Category=Research`.
			Filter = "Category!=Research"
		});
	});

// The image is built from the Dockerfile, which restores+publishes INSIDE the
// container — it needs neither the host Build nor Test output, only the git version
// for the tag/build-args. So it depends on Version, not Test: in CI the image build
// runs as its own job in PARALLEL with the test job, and `deploy` gates on both.
Task("Docker")
	.IsDependentOn("Version")
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

// ─── NuGet packaging + publish (public nuget.org) ───

Task("Pack")
	.IsDependentOn("Version")
	.Does(() =>
	{
		var buildVersion = gitVersion.FullSemVer;

		foreach (var project in nugetProjects)
		{
			// Restore here (not NoRestore) — the `nuget` tag job has no separate restore step,
			// and the core project may be cold on a clean CI checkout.
			DotNetBuild(project, new DotNetBuildSettings
			{
				Configuration = configuration,
				MSBuildSettings = new DotNetMSBuildSettings()
					.WithProperty("Version", buildVersion)
					.WithProperty("InformationalVersion", $"{buildVersion} ({gitVersion.ShortSha}, {gitVersion.CommitDate})")
					.WithProperty("GitShortSha", gitVersion.ShortSha)
					.WithProperty("GitCommitDate", gitVersion.CommitDate)
			});

			DotNetPack(project, new DotNetPackSettings
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
			});
		}
	});

// Publishes to the PUBLIC nuget.org feed (canonical registry for the .NET ecosystem,
// matching the ts→npmjs / py→PyPI posture). Requires NUGET_API_KEY — a nuget.org API key
// with push rights for the PetBox.Client.* packages. --skip-duplicate because the same
// FullSemVer can land repeatedly on a single tag (re-runs).
Task("NuGetPush")
	.IsDependentOn("Pack")
	.Does(() =>
	{
		var apiKey = EnvironmentVariable("NUGET_API_KEY");

		if (string.IsNullOrWhiteSpace(apiKey))
			throw new CakeException("NUGET_API_KEY environment variable is not set. Public nuget.org publishing requires a nuget.org API key with push rights.");

		var source = "https://api.nuget.org/v3/index.json";
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

// ─── TS SDK build + publish (public npmjs) ───

Task("TsSdkInstall")
	.Does(() => RunBun("install --frozen-lockfile", tsSdkDir));

Task("TsSdkTypecheck")
	.IsDependentOn("TsSdkInstall")
	.Does(() => RunBun("run typecheck", tsSdkDir));

Task("TsSdkLint")
	.IsDependentOn("TsSdkInstall")
	.Does(() => RunBun("run lint", tsSdkDir));

Task("TsSdkTest")
	.IsDependentOn("TsSdkInstall")
	.Does(() => RunBun("test", tsSdkDir));

Task("TsSdkBuild")
	.IsDependentOn("TsSdkInstall")
	.Does(() => RunBun("run build", tsSdkDir));

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

// Publishes the TS SDK to the PUBLIC npm registry (registry.npmjs.org). Requires
// NPM_TOKEN — an npmjs automation/publish token with rights to the @stdray-npm
// scope. Writes a temporary .npmrc with the auth token, then `npm publish
// --access public` (scoped packages are restricted by default; this one is public).
Task("NpmPublish")
	.IsDependentOn("TsSdkPack")
	.Does(() =>
	{
		var token = EnvironmentVariable("NPM_TOKEN");
		if (string.IsNullOrWhiteSpace(token))
			throw new CakeException("NPM_TOKEN environment variable is not set. Public npm publishing requires an npmjs token with publish rights to the @stdray-npm scope.");

		var absDir = MakeAbsolute(Directory(tsSdkDir)).FullPath;
		var npmrc = System.IO.Path.Combine(absDir, ".npmrc");
		System.IO.File.WriteAllText(npmrc, $"//registry.npmjs.org/:_authToken={token}\n");
		try
		{
			var exit = StartProcess("npm", new ProcessSettings
			{
				Arguments = "publish --access public",
				WorkingDirectory = tsSdkDir,
			});
			if (exit != 0)
				throw new CakeException($"npm publish failed with exit code {exit}.");
			Information("Published TS SDK to npmjs (@stdray-npm/petbox-client)");
		}
		finally
		{
			if (System.IO.File.Exists(npmrc))
				System.IO.File.Delete(npmrc);
		}
	});

// ─── agent-wiring kit (petbox-wire) — same npm registry, own `npm-wire` tag ───

// Stamp package.json version from GitVersion. The kit is raw plain TS (native type-stripping),
// so there is no install/build/pack — just the version bump, mirroring TsSdkPack. GitVersion runs
// standalone (only needs git) so we skip the full Clean→Restore→Version .NET chain.
Task("TsWirePack")
	.Does(() =>
	{
		var gv = GitVersion(new GitVersionSettings { OutputType = GitVersionOutput.Json, NoFetch = true });
		// npm semver forbids '+', so replace the GitVersion build-metadata separator.
		var npmVersion = gv.FullSemVer.Replace('+', '-');
		Information("Stamping agent-wiring kit version: {0} (GitVersion: {1})", npmVersion, gv.FullSemVer);
		StartProcess("npm", new ProcessSettings
		{
			Arguments = $"version {npmVersion} --no-git-tag-version --allow-same-version",
			WorkingDirectory = tsWireDir,
		});
	});

// Publishes the agent-wiring kit (petbox-wire, UNSCOPED public package) to registry.npmjs.org
// on its own `npm-wire` tag (CI calls `./build.sh --target=NpmWirePublish`). Same NPM_TOKEN +
// temporary-.npmrc mechanism as NpmPublish.
Task("NpmWirePublish")
	.IsDependentOn("TsWirePack")
	.Does(() =>
	{
		var token = EnvironmentVariable("NPM_TOKEN");
		if (string.IsNullOrWhiteSpace(token))
			throw new CakeException("NPM_TOKEN environment variable is not set. Public npm publishing requires an npmjs token with publish rights.");

		var absDir = MakeAbsolute(Directory(tsWireDir)).FullPath;
		var npmrc = System.IO.Path.Combine(absDir, ".npmrc");
		System.IO.File.WriteAllText(npmrc, $"//registry.npmjs.org/:_authToken={token}\n");
		try
		{
			var exit = StartProcess("npm", new ProcessSettings
			{
				Arguments = "publish --access public",
				WorkingDirectory = tsWireDir,
			});
			if (exit != 0)
				throw new CakeException($"npm publish failed with exit code {exit}.");
			Information("Published agent-wiring kit to npmjs (petbox-wire)");
		}
		finally
		{
			if (System.IO.File.Exists(npmrc))
				System.IO.File.Delete(npmrc);
		}
	});

// ─── Python SDK build + publish (PyPI) ───

Task("PyClientInstall")
	.Does(() => RunUv("sync --frozen", pyClientDir));

Task("PyClientLint")
	.IsDependentOn("PyClientInstall")
	.Does(() =>
	{
		RunUv("run ruff check .", pyClientDir);
		RunUv("run ruff format --check .", pyClientDir);
	});

Task("PyClientTypecheck")
	.IsDependentOn("PyClientInstall")
	.Does(() => RunUv("run mypy", pyClientDir));

Task("PyClientTest")
	.IsDependentOn("PyClientInstall")
	.Does(() => RunUv("run pytest", pyClientDir));

Task("PyClientBuild")
	.IsDependentOn("PyClientInstall")
	.Does(() => RunUv("build", pyClientDir));

// Stamp the version from GitVersion (PEP 440), then build sdist + wheel into dist/.
// GitVersion runs standalone (only needs git, not .NET) so we avoid the full
// Clean→Restore→Version chain which requires the entire solution to build.
Task("PyClientPack")
	.IsDependentOn("PyClientBuild")
	.Does(() =>
	{
		var gv = GitVersion(new GitVersionSettings { OutputType = GitVersionOutput.Json, NoFetch = true });
		// Map GitVersion → PEP 440. Tagged releases use MajorMinorPatch; everything else
		// becomes a developmental release (.devN) so PyPI accepts it (no '+' local segment,
		// which PyPI rejects) and it sorts before the matching release.
		var pep440 = string.IsNullOrEmpty(gv.PreReleaseTag)
			? gv.MajorMinorPatch
			: $"{gv.MajorMinorPatch}.dev{gv.CommitsSinceVersionSource ?? 0}";
		Information("Stamping Python SDK version: {0} (GitVersion: {1})", pep440, gv.FullSemVer);

		// Wipe stale artifacts so `uv publish` only sees this build's files.
		CleanDirectory($"{pyClientDir}/dist");
		RunUv($"version --no-sync {pep440}", pyClientDir);
		RunUv("build", pyClientDir);
	});

// Publishes the Python SDK to the PUBLIC PyPI registry. Requires PYPI_TOKEN — a PyPI
// API token (project- or account-scoped) with upload rights. `uv publish` uploads
// everything under dist/; re-runs of the same version are rejected by PyPI, so the
// `pypi` tag should move only when the version actually advances.
Task("PyPiPublish")
	.IsDependentOn("PyClientPack")
	.Does(() =>
	{
		var token = EnvironmentVariable("PYPI_TOKEN");
		if (string.IsNullOrWhiteSpace(token))
			throw new CakeException("PYPI_TOKEN environment variable is not set. Public PyPI publishing requires a PyPI API token with upload rights.");

		RunUv($"publish --token {token}", pyClientDir);
		Information("Published Python SDK to PyPI (petbox-client)");
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

// A gate that cannot fail is not a gate. StartProcess returns the exit code — DISCARDING it
// (as this task used to) made FormatVerify succeed even when `dotnet format` reported errors,
// so it was a no-op even on the rare occasion something invoked it.
//
// Only the `whitespace` sub-command runs here, not the default (style + analyzers + whitespace).
// .editorconfig only declares whitespace rules — charset, eol, indent/tabs, final-newline,
// trim-trailing — it carries no style or analyzer severities. Style and analyzer enforcement
// already lives in Directory.Build.props (AnalysisMode=All + EnforceCodeStyleInBuild=true +
// TreatWarningsAsErrors=true), so the Build task fails on those diagnostics itself. Running
// `dotnet format`'s style/analyzer passes here would just re-run diagnostics Build already
// gates on — for ~50s of the ~80s FormatVerify budget, paid twice for nothing.
//
// Depends on the shared Restore task instead of restoring inline: Cake dedupes tasks across a
// single run, so when Test pulls in both FormatVerify and Build, Restore (and the Clean it
// depends on) executes once and both tasks see the same restored packages — no wasted second
// restore, and Clean runs before either consumer touches the tree.
Task("FormatVerify")
	.IsDependentOn("Restore")
	.Does(() =>
	{
		var formatExit = StartProcess("dotnet", $"format whitespace {solution} --verify-no-changes --no-restore");
		if (formatExit != 0)
			throw new CakeException(
				$"dotnet format whitespace --verify-no-changes failed with exit code {formatExit} — run `dotnet format whitespace` and commit the result");
	});

// `SdkChecks` IS the client-SDK gate — lint + typecheck + test for the TS and Python SDKs we
// publish to npm/PyPI, and nothing from the .NET chain. It exists as its own target so CI can
// run it as a SEPARATE job (bun + uv toolchains, in parallel with the .NET `test` job) without
// dragging in Clean→Restore→Build→Test. Before this, the SDK targets lived only inside `Verify`,
// which CI never called: the SDKs shipped to public registries unchecked by anything.
Task("SdkChecks")
	.IsDependentOn("TsSdkLint")
	.IsDependentOn("TsSdkTypecheck")
	.IsDependentOn("TsSdkTest")
	.IsDependentOn("PyClientLint")
	.IsDependentOn("PyClientTypecheck")
	.IsDependentOn("PyClientTest");

// Everything Test covers, plus the client SDKs (bun + uv toolchains). This is the full
// pre-push sweep — the local equivalent of CI's two jobs (`test` + `sdk`) taken together.
Task("Verify")
	.IsDependentOn("Test")
	.IsDependentOn("SdkChecks");

Task("CI")
	.IsDependentOn("Verify");

Task("Default")
	.IsDependentOn("DockerPush");

RunTarget(target);
