using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PetBox.Config;
using PetBox.Config.Contract;
using PetBox.Core.Auth;
using PetBox.Core.Models;

namespace PetBox.Web.Pages.Config;

[Authorize(Policy = "WorkspaceAdmin")]
public sealed class EditorModel : PageModel
{
	readonly IConfigService _configService;
	readonly ISecretEncryptor _encryptor;

	public EditorModel(IConfigService configService, ISecretEncryptor encryptor)
	{
		_configService = configService;
		_encryptor = encryptor;
	}

	// authz-bypass-project-create: route-only bind — see Admin/Projects.cshtml.cs for why.
	[FromRoute(Name = "workspaceKey")]
	public string? WorkspaceKey { get; set; }

	// Not workspace-scoping — BindingId only selects a row inside the ALREADY route-locked
	// workspace's configDb (a foreign workspace's id simply won't exist there), so this one is
	// left on the ordinary composite bind (SupportsGet, so both the editor GET prefill and the
	// hidden field on save keep working).
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

	public async Task<IActionResult> OnGetAsync(CancellationToken ct)
	{
		EffectiveWorkspaceKey = ResolveWorkspace();

		if (BindingId is { } id and > 0)
		{
			var binding = await _configService.GetBindingAsync(EffectiveWorkspaceKey, id, ct);
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

	public async Task<IActionResult> OnPostSaveAsync(CancellationToken ct)
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

		var now = DateTime.UtcNow;
		var actor = User.Identity?.Name ?? "system";

		long savedId;

		if (BindingId is { } id and > 0)
		{
			savedId = await _configService.UpdateBindingAsync(
				EffectiveWorkspaceKey, id, Path, canonicalTags, Value, Kind, actor, now, ct);
		}
		else
		{
			savedId = await _configService.CreateBindingAsync(
				EffectiveWorkspaceKey, Path, canonicalTags, Value, Kind, actor, now, ct);
		}

		// Detect conflict: another binding with the same (Path, Tags) that is not this one.
		var allBindings = await _configService.GetActiveBindingsAsync(EffectiveWorkspaceKey, ct);
		foreach (var other in allBindings)
		{
			if (other.Id == savedId) continue;
			if (string.Equals(other.Path, Path, StringComparison.Ordinal)
				&& string.Equals(other.Tags, canonicalTags, StringComparison.Ordinal))
			{
				ConflictMessage = $"Binding #{other.Id} has the same Path and Tags. Saved as a duplicate — older wins by Id.";
				return Page();
			}
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
}
