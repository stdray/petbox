using PetBox.Config.Data;
using PetBox.Core.Models;

namespace PetBox.Config.Contract;

public interface IConfigService
{
	// -- Bindings ---------------------------------------------------------------

	// Active (non-deleted) bindings in a workspace, ordered by path.
	Task<IReadOnlyList<ConfigBinding>> GetActiveBindingsAsync(
		string workspaceKey, CancellationToken ct = default);

	// One binding by id. Returns null when not found or soft-deleted.
	Task<ConfigBinding?> GetBindingAsync(
		string workspaceKey, long id, CancellationToken ct = default);

	// Create a new binding (insert row + write history). Returns the new id.
	Task<long> CreateBindingAsync(
		string workspaceKey,
		string path,
		string tags,
		string value,
		BindingKind kind,
		string actor,
		DateTime at,
		CancellationToken ct = default);

	// Update an existing binding in-place (version bump + history), or
	// re-activate a soft-deleted row. Returns the binding's id on success.
	Task<long> UpdateBindingAsync(
		string workspaceKey,
		long id,
		string path,
		string tags,
		string value,
		BindingKind kind,
		string actor,
		DateTime at,
		CancellationToken ct = default);

	// Soft-delete a binding by id (writes history, returns true when a row
	// was deleted). Idempotent — already-deleted returns false.
	Task<bool> DeleteBindingAsync(
		string workspaceKey, long id, string actor, DateTime at, CancellationToken ct = default);

	// Decrypt a secret binding's value. Returns null when the binding is not
	// a secret, the encryptor is unavailable, or the row is not found.
	Task<string?> RevealSecretAsync(
		string workspaceKey, long id, CancellationToken ct = default);

	// -- Resolve ----------------------------------------------------------------

	// Resolve all active bindings against a tag-vector. Returns path → value.
	Task<IReadOnlyDictionary<string, string>> ResolveAsync(
		string workspaceKey, IReadOnlyList<string> requestTags, CancellationToken ct = default);

	// Detailed resolve for a single path. Returns the match (or null when
	// there is no binding for that path).
	Task<ResolveMatch?> ResolvePathAsync(
		string workspaceKey, string path, IReadOnlyList<string> requestTags,
		CancellationToken ct = default);

	// -- History ----------------------------------------------------------------

	// History entries for a workspace, newest-first, optionally filtered by
	// a substring match on Path. Capped at 500 rows.
	Task<IReadOnlyList<ConfigBindingHistoryEntry>> GetHistoryAsync(
		string workspaceKey, string? pathSubstring = null, CancellationToken ct = default);

	// -- Tags -------------------------------------------------------------------

	// Declared tag vocabulary, ordered by tag key.
	Task<IReadOnlyList<TagVocabularyEntry>> GetTagsAsync(
		string workspaceKey, CancellationToken ct = default);

	// Declare a new tag key (idempotent — silently skips duplicates).
	Task AddTagAsync(
		string workspaceKey, string tagKey, string? description, CancellationToken ct = default);

	// Remove a tag from the vocabulary by row id.
	Task RetireTagAsync(
		string workspaceKey, long id, CancellationToken ct = default);

	// -- Counts -----------------------------------------------------------------

	// Number of active bindings, optionally filtered to those whose Tags
	// contain `tagFilter` (exact match on one comma-separated token).
	Task<int> CountBindingsAsync(
		string workspaceKey, string? tagFilter = null, CancellationToken ct = default);

	// All distinct tag namespace → value pairs seen across active bindings.
	Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> AggregateUsedTagValuesAsync(
		string workspaceKey, CancellationToken ct = default);
}
