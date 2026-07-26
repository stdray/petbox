using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PetBox.Config;
using PetBox.Core.Auth;
using PetBox.Core.Models;

namespace PetBox.Web.Pages.Config;

[Authorize(Policy = "WorkspaceAdmin")]
// {workspaceKey}, not {projectKey} — read Config/Index.cshtml.cs before changing this. This page is
// mapped by TWO templates (Program.cs AddPageRoute), one workspace-scoped and one project-scoped, and
// a PageModel declares once for both: naming `projectKey` would resolve to nothing on the
// workspace-only template and 403 it. A project-claimed key still reaches this page, because
// ITenantAuthorizer knows a project claim authorizes its own workspace.
[TenantFrom(TenantSource.Route, "workspaceKey", tenant: TenantKind.Workspace)]
public sealed class EditorModel : PageModel
{
	readonly IConfigDirectory _config;
	readonly ISecretEncryptor _encryptor;

	public EditorModel(IConfigDirectory config, ISecretEncryptor encryptor)
	{
		_config = config;
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
			var binding = await _config.GetBindingAsync(EffectiveWorkspaceKey, id, ct);
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

		// Encryption stays HERE: ISecretEncryptor is a crypto concern, not a database one, so the
		// config door receives an already-sealed bundle rather than the plaintext.
		string? cipher = null;
		string? iv = null;
		string? authTag = null;
		if (Kind == BindingKind.Secret)
		{
			var bundle = _encryptor.Encrypt(Value);
			cipher = bundle.Ciphertext;
			iv = bundle.Iv;
			authTag = bundle.AuthTag;
		}

		var draft = new ConfigBindingDraft(
			BindingId, Path, canonicalTags, Kind, Value, cipher, iv, authTag);

		var result = await _config.SaveBindingAsync(
			EffectiveWorkspaceKey, draft, User.Identity?.Name ?? "system", ct);

		if (result.NotFound)
		{
			ErrorMessage = $"Binding #{BindingId} not found.";
			return Page();
		}

		if (result.DuplicateOfId is { } duplicateId)
		{
			ConflictMessage =
				$"Binding #{duplicateId} has the same Path and Tags. Saved as a duplicate — older wins by Id.";
			return Page();
		}

		// Direct path via Routes (not RedirectToPage): the config pages carry duplicate
		// conventional routes that make page-name URL generation fail here.
		return LocalRedirect(Routes.SharedConfig(EffectiveWorkspaceKey));
	}

	// THE TENANT THE PEP JUDGED, and nothing else. Both route templates of this page carry
	// {workspaceKey}, so it is always bound — and [TenantFrom(Route, "workspaceKey", …)] on the class
	// refuses the request when it is not, which is what finally makes that guarantee enforced rather
	// than assumed.
	//
	// The old body fell back to the ActiveWorkspace CLAIM and then to a hard-coded "$system". That
	// fallback was unreachable through routing, but it was also the one way this page could read and
	// WRITE config for a workspace TenantEnforcementMiddleware never saw — the target the decision point
	// judged and the target the handler acts on have to be the same string, so the fallback is deleted
	// rather than left as a comfort. If WorkspaceKey were ever empty here, the request would already
	// have been refused above.
	string ResolveWorkspace() => WorkspaceKey!;

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
