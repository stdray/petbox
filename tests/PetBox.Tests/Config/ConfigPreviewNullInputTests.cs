using PetBox.Config.Contract;
using PetBox.Web.Pages.Config;

namespace PetBox.Tests.Config;

// Regression for the prod 500 on POST /ui/{ws}/{project}/config/preview:
// an empty Paths textarea binds to null (ConvertEmptyStringToNull defaults to true),
// so PreviewModel.OnPostAsync did `null.Split(...)` → NullReferenceException → 500.
// Clicking "Resolve" with an empty Paths field must yield empty Results, not throw.
public sealed class ConfigPreviewNullInputTests
{
	sealed class StubConfigService : IConfigService
	{
		public Task<IReadOnlyList<Core.Models.ConfigBinding>> GetActiveBindingsAsync(
			string workspaceKey, CancellationToken ct = default)
			=> Task.FromResult<IReadOnlyList<Core.Models.ConfigBinding>>([]);

		public Task<Core.Models.ConfigBinding?> GetBindingAsync(
			string workspaceKey, long id, CancellationToken ct = default)
			=> Task.FromResult<Core.Models.ConfigBinding?>(null);

		public Task<long> CreateBindingAsync(
			string workspaceKey, string path, string tags, string value,
			Core.Models.BindingKind kind, string actor, DateTime at, CancellationToken ct = default)
			=> Task.FromResult(0L);

		public Task<long> UpdateBindingAsync(
			string workspaceKey, long id, string path, string tags, string value,
			Core.Models.BindingKind kind, string actor, DateTime at, CancellationToken ct = default)
			=> Task.FromResult(id);

		public Task<bool> DeleteBindingAsync(
			string workspaceKey, long id, string actor, DateTime at, CancellationToken ct = default)
			=> Task.FromResult(true);

		public Task<string?> RevealSecretAsync(
			string workspaceKey, long id, CancellationToken ct = default)
			=> Task.FromResult<string?>(null);

		public Task<IReadOnlyDictionary<string, string>> ResolveAsync(
			string workspaceKey, IReadOnlyList<string> requestTags, CancellationToken ct = default)
			=> Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>());

		public Task<PetBox.Config.ResolveMatch?> ResolvePathAsync(
			string workspaceKey, string path, IReadOnlyList<string> requestTags, CancellationToken ct = default)
			=> Task.FromResult<PetBox.Config.ResolveMatch?>(null);

		public Task<IReadOnlyList<PetBox.Config.Data.ConfigBindingHistoryEntry>> GetHistoryAsync(
			string workspaceKey, string? pathSubstring = null, CancellationToken ct = default)
			=> Task.FromResult<IReadOnlyList<PetBox.Config.Data.ConfigBindingHistoryEntry>>([]);

		public Task<IReadOnlyList<PetBox.Config.Data.TagVocabularyEntry>> GetTagsAsync(
			string workspaceKey, CancellationToken ct = default)
			=> Task.FromResult<IReadOnlyList<PetBox.Config.Data.TagVocabularyEntry>>([]);

		public Task AddTagAsync(
			string workspaceKey, string tagKey, string? description, CancellationToken ct = default)
			=> Task.CompletedTask;

		public Task RetireTagAsync(
			string workspaceKey, long id, CancellationToken ct = default)
			=> Task.CompletedTask;

		public Task<int> CountBindingsAsync(
			string workspaceKey, string? tagFilter = null, CancellationToken ct = default)
			=> Task.FromResult(0);

		public Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> AggregateUsedTagValuesAsync(
			string workspaceKey, CancellationToken ct = default)
			=> Task.FromResult<IReadOnlyDictionary<string, IReadOnlyList<string>>>(
				new Dictionary<string, IReadOnlyList<string>>());
	}

	static PreviewModel NewModel()
	{
		return new PreviewModel(new StubConfigService()) { WorkspaceKey = "$system" };
	}

	[Fact]
	public async Task OnPost_NullPaths_DoesNotThrow_AndReturnsNoResults()
	{
		var model = NewModel();
		model.TagsInput = "ws:$system";
		model.PathsInput = null!; // mirrors empty-textarea model binding

		await model.OnPostAsync(CancellationToken.None);

		Assert.Empty(model.Results);
	}

	[Fact]
	public async Task OnPost_NullTags_DoesNotThrow()
	{
		var model = NewModel();
		model.TagsInput = null!;
		model.PathsInput = "some.path";

		await model.OnPostAsync(CancellationToken.None);

		// Path has no matching binding in the empty store → single (no match) row, no throw.
		Assert.Single(model.Results);
		Assert.Null(model.Results[0].Value);
	}
}
