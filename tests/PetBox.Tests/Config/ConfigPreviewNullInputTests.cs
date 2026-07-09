using PetBox.Config.Data;
using PetBox.Web.Pages.Config;

namespace PetBox.Tests.Config;

// Regression for the prod 500 on POST /ui/{ws}/{project}/config/preview:
// an empty Paths textarea binds to null (ConvertEmptyStringToNull defaults to true),
// so PreviewModel.OnPost did `null.Split(...)` → NullReferenceException → 500.
// Clicking "Resolve" with an empty Paths field must yield empty Results, not throw.
public sealed class ConfigPreviewNullInputTests
{
	sealed class StubConfigDbFactory(string connectionString) : IConfigDbFactory
	{
		public ConfigDb GetConfigDb(string workspaceKey) => new(ConfigDb.CreateOptions(connectionString));
		public ConfigDb NewConfigDb(string workspaceKey) => new(ConfigDb.CreateOptions(connectionString));
	}

	static PreviewModel NewModel()
	{
		var dir = Path.Combine(Path.GetTempPath(), "petbox-cfg-" + Guid.NewGuid().ToString("N")[..8]);
		Directory.CreateDirectory(dir);
		var cs = $"Data Source={Path.Combine(dir, "cfg.db")}";
		ConfigSchema.Ensure(cs);
		// WorkspaceKey set so ResolveWorkspace() never reads User claims (no HttpContext in test).
		return new PreviewModel(new StubConfigDbFactory(cs)) { WorkspaceKey = "$system" };
	}

	[Fact]
	public void OnPost_NullPaths_DoesNotThrow_AndReturnsNoResults()
	{
		var model = NewModel();
		model.TagsInput = "ws:$system";
		model.PathsInput = null!; // mirrors empty-textarea model binding

		model.OnPost();

		Assert.Empty(model.Results);
	}

	[Fact]
	public void OnPost_NullTags_DoesNotThrow()
	{
		var model = NewModel();
		model.TagsInput = null!;
		model.PathsInput = "some.path";

		model.OnPost();

		// Path has no matching binding in the empty store → single (no match) row, no throw.
		Assert.Single(model.Results);
		Assert.Null(model.Results[0].Value);
	}
}
