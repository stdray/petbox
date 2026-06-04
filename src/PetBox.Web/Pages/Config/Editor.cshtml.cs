using LinqToDB;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PetBox.Config;
using PetBox.Config.Data;
using PetBox.Core.Auth;
using PetBox.Core.Models;
using BindingContentHash = PetBox.Config.BindingContentHash;

namespace PetBox.Web.Pages.Config;

[Authorize]
public sealed class EditorModel : PageModel
{
	readonly IConfigDbFactory _configFactory;
	readonly ISecretEncryptor _encryptor;

	public EditorModel(IConfigDbFactory configFactory, ISecretEncryptor encryptor)
	{
		_configFactory = configFactory;
		_encryptor = encryptor;
	}

	[BindProperty(SupportsGet = true)]
	public string? WorkspaceKey { get; set; }

	[BindProperty(SupportsGet = true)]
	public long? BindingId { get; set; }

	public string EffectiveWorkspaceKey { get; private set; } = "$system";
	public bool IsNew => BindingId is null or <= 0;

	[BindProperty]
	public string Path { get; set; } = string.Empty;

	[BindProperty]
	public string Value { get; set; } = string.Empty;

	[BindProperty]
	public string TagsText { get; set; } = string.Empty;

	[BindProperty]
	public BindingKind Kind { get; set; } = BindingKind.Plain;

	public string? ErrorMessage { get; set; }
	public string? ConflictMessage { get; set; }
	public bool SecretsAvailable => _encryptor.IsAvailable;

	public IActionResult OnGet()
	{
		EffectiveWorkspaceKey = ResolveWorkspace();

		if (BindingId is { } id and > 0)
		{
			var configDb = _configFactory.GetConfigDb(EffectiveWorkspaceKey);
			var binding = configDb.Bindings.FirstOrDefault(b => b.Id == id);
			if (binding is null)
			{
				ErrorMessage = $"Binding #{id} not found.";
				return Page();
			}

			Path = binding.Path;
			Kind = binding.Kind;
			Value = binding.Kind == BindingKind.Plain ? binding.Value : string.Empty;
			TagsText = string.Join('\n', binding.Tags
				.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
		}
		else
		{
			TagsText = $"ws:{EffectiveWorkspaceKey}";
		}

		return Page();
	}

	public async Task<IActionResult> OnPostSaveAsync()
	{
		EffectiveWorkspaceKey = ResolveWorkspace();

		if (string.IsNullOrWhiteSpace(Path))
		{
			ErrorMessage = "Path is required.";
			return Page();
		}

		var (canonicalTags, parseError) = CanonicalizeTags(TagsText);
		if (parseError is not null)
		{
			ErrorMessage = parseError;
			return Page();
		}

		if (!canonicalTags.Contains($"ws:{EffectiveWorkspaceKey}", StringComparison.Ordinal))
		{
			ErrorMessage = $"Tags must include 'ws:{EffectiveWorkspaceKey}' (workspace mandatory).";
			return Page();
		}

		if (Kind == BindingKind.Secret && !_encryptor.IsAvailable)
		{
			ErrorMessage = "Secret bindings require PETBOX_MASTER_KEY to be configured.";
			return Page();
		}

		if (string.IsNullOrWhiteSpace(Value))
		{
			ErrorMessage = "Value is required.";
			return Page();
		}

		var configDb = _configFactory.GetConfigDb(EffectiveWorkspaceKey);
		var now = DateTime.UtcNow;
		var actor = User.Identity?.Name ?? "system";

		string storedValue;
		string? cipher = null;
		string? iv = null;
		string? authTag = null;

		if (Kind == BindingKind.Secret)
		{
			var bundle = _encryptor.Encrypt(Value);
			cipher = bundle.Ciphertext;
			iv = bundle.Iv;
			authTag = bundle.AuthTag;
			storedValue = string.Empty;
		}
		else
		{
			storedValue = Value;
		}

		var newHash = BindingContentHash.Compute(Path, canonicalTags, Kind, storedValue, cipher);

		long savedId = BindingId ?? 0;

		if (BindingId is { } id and > 0)
		{
			var existing = configDb.Bindings.FirstOrDefault(b => b.Id == id);
			if (existing is null)
			{
				ErrorMessage = $"Binding #{id} not found.";
				return Page();
			}

			// Skip Version bump on no-op edits (same content + same tags + same kind).
			var isNoOp = string.Equals(existing.ContentHash, newHash, StringComparison.Ordinal)
				&& !existing.IsDeleted;

			var updated = existing with
			{
				Path = Path,
				Tags = canonicalTags,
				Kind = Kind,
				Value = storedValue,
				Ciphertext = cipher,
				Iv = iv,
				AuthTag = authTag,
				Version = isNoOp ? existing.Version : existing.Version + 1,
				ContentHash = newHash,
				IsDeleted = false,
				DeletedAt = null,
				UpdatedAt = now,
			};
			await configDb.UpdateAsync(updated);

			if (!isNoOp)
			{
				await configDb.InsertAsync(new ConfigBindingHistoryEntry
				{
					BindingId = id,
					Action = existing.IsDeleted ? "Undelete" : "Update",
					Path = Path,
					Tags = canonicalTags,
					Kind = Kind,
					OldValue = existing.Kind == BindingKind.Plain ? existing.Value : "(secret)",
					NewValue = Kind == BindingKind.Plain ? storedValue : "(secret)",
					Actor = actor,
					At = now,
				});
			}
		}
		else
		{
			var newId = await configDb.InsertWithInt64IdentityAsync(new ConfigBinding
			{
				Path = Path,
				Tags = canonicalTags,
				Kind = Kind,
				Value = storedValue,
				Ciphertext = cipher,
				Iv = iv,
				AuthTag = authTag,
				Version = 1,
				ContentHash = newHash,
				CreatedAt = now,
				UpdatedAt = now,
			});

			await configDb.InsertAsync(new ConfigBindingHistoryEntry
			{
				BindingId = newId,
				Action = "Create",
				Path = Path,
				Tags = canonicalTags,
				Kind = Kind,
				OldValue = null,
				NewValue = Kind == BindingKind.Plain ? storedValue : "(secret)",
				Actor = actor,
				At = now,
			});

			savedId = newId;
		}

		// Use the just-persisted id as self — for a NEW binding BindingId is still null,
		// so without this the row would match itself and report a spurious duplicate.
		var conflict = DetectConflict(configDb, Path, canonicalTags, savedId);
		if (conflict is not null)
		{
			ConflictMessage = conflict;
			return Page();
		}

		// Direct path via Routes (not RedirectToPage): the config pages carry duplicate
		// conventional routes that make page-name URL generation fail here.
		return LocalRedirect(Routes.SharedConfig(EffectiveWorkspaceKey));
	}

	string ResolveWorkspace()
	{
		if (!string.IsNullOrEmpty(WorkspaceKey))
			return WorkspaceKey;
		var claimWs = User.FindFirst(PetBoxClaims.ActiveWorkspace)?.Value;
		return string.IsNullOrEmpty(claimWs) ? "$system" : claimWs;
	}

	static (string Canonical, string? Error) CanonicalizeTags(string raw)
	{
		if (string.IsNullOrWhiteSpace(raw))
			return (string.Empty, "Tags are required (at least 'ws:...').");

		var pairs = new List<(string, string)>();
		foreach (var line in raw.Split(['\r', '\n', ','], StringSplitOptions.RemoveEmptyEntries))
		{
			var trimmed = line.Trim();
			if (trimmed.Length == 0) continue;
			// Accept both 'key:value' (canonical) and 'key=value' (legacy) — store as ':'.
			var sep = trimmed.IndexOfAny([':', '=']);
			if (sep <= 0 || sep == trimmed.Length - 1)
				return (string.Empty, $"'{trimmed}' is not a 'key:value' pair.");
			pairs.Add((trimmed[..sep].Trim(), trimmed[(sep + 1)..].Trim()));
		}

		pairs.Sort((a, b) => string.CompareOrdinal(a.Item1, b.Item1));
		return (string.Join(",", pairs.Select(p => $"{p.Item1}:{p.Item2}")), null);
	}

	static string? DetectConflict(ConfigDb db, string path, string tags, long? selfId)
	{
		var sameKey = db.Bindings.Where(b => b.Path == path).ToList();
		foreach (var other in sameKey)
		{
			if (other.Id == selfId) continue;
			if (string.Equals(other.Tags, tags, StringComparison.Ordinal))
				return $"Binding #{other.Id} has the same Path and Tags. Saved as a duplicate — older wins by Id.";
		}
		return null;
	}
}
