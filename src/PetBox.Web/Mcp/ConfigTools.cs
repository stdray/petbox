using System.ComponentModel;
using LinqToDB;
using LinqToDB.Async;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol.Server;
using PetBox.Config;
using PetBox.Config.Data;
using PetBox.Core.Auth;
using PetBox.Core.Models;
using PetBox.Web.Mcp.Contract;

namespace PetBox.Web.Mcp;

// Typed per-type config-binding tools (mcp-typing wave). Replaces the generic
// entity.* config_binding surface: flat, typed params give the MCP client a real
// JSON schema (type per field), so the object can't be silently stringified the
// way an untyped JsonElement param was. Provisioning ops — admin:provision scope,
// no per-project claim. Secrets are stored encrypted, never as plaintext Value.
// Tools throw on a failed Assert*/validation; McpErrorEnvelopeFilter renders the {error} body.
[McpServerToolType]
public static class ConfigTools
{
	[McpServerTool(Name = "config_binding_upsert", Title = "Upsert a config binding", UseStructuredContent = true, OutputSchemaType = typeof(ConfigBindingUpsertResult))]
	[Description("""
		PUT by (path, tagset): upserts a config binding in a workspace's config store; if an
		ACTIVE binding with the same path and the same normalized tag SET (order/case/whitespace
		of the CSV don't matter) already exists, it is superseded — soft-closed in the same
		transaction and reported in the result's `superseded` ids. No silent duplicates: the
		same (path, tagset) can never be active twice. A different tagset at the same path is a
		normal specificity variant and coexists. Requires admin:provision.
		  • tags must include 'ws:{workspaceKey}'.
		  • kind: 'Plain' (default) or 'Secret' — a Secret stores `value` encrypted
		    (needs PETBOX_MASTER_KEY); the plaintext never lands in the Value column.
		""")]
	public static async Task<ConfigBindingUpsertResult> BindingUpsertAsync(
		IHttpContextAccessor http, IConfigDbFactory configFactory, ISecretEncryptor secrets,
		[Description("Workspace key the binding belongs to.")] string workspaceKey,
		[Description("Dotted config path, e.g. 'app/connectionString'.")] string path,
		[Description("Comma-separated tags; must include 'ws:{workspaceKey}'.")] string tags,
		[Description("The value. For kind=Secret it is stored encrypted.")] string? value = null,
		[Description("'Plain' (default) or 'Secret'.")] string? kind = null,
		CancellationToken ct = default)
	{
		ModuleMcp.AssertScope(http, ApiKeyScopes.AdminProvision);
		if (string.IsNullOrWhiteSpace(workspaceKey)) throw new ArgumentException("workspaceKey is required");
		if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("path is required");
		if (string.IsNullOrWhiteSpace(tags)) throw new ArgumentException("tags is required");
		if (!tags.Contains($"ws:{workspaceKey}", StringComparison.OrdinalIgnoreCase))
			throw new ArgumentException($"Tags must include 'ws:{workspaceKey}'");

		var bindingKind = ParseKind(kind);
		var plaintext = value ?? string.Empty;
		var storedValue = plaintext;
		string? cipher = null, iv = null, authTag = null;
		if (bindingKind == BindingKind.Secret)
		{
			if (!secrets.IsAvailable)
				throw new InvalidOperationException("Secret bindings require PETBOX_MASTER_KEY to be configured.");
			var bundle = secrets.Encrypt(plaintext);
			(cipher, iv, authTag) = (bundle.Ciphertext, bundle.Iv, bundle.AuthTag);
			storedValue = string.Empty;
		}

		var now = DateTime.UtcNow;
		var configDb = configFactory.GetConfigDb(workspaceKey);

		// PUT by (path, tagset): an active twin — same path (resolver equality: ignore-case) and
		// same normalized tag SET — would be a silent duplicate that the resolve pipeline later
		// rejects as ambiguous. Supersede it instead: soft-close every twin in the SAME
		// transaction that inserts the replacement, and report the closed ids. A different
		// tagset at the same path is a specificity variant and is left alone.
		var newTagset = Tagset(tags);
		var superseded = (await configDb.Bindings
				.Where(b => !b.IsDeleted)
				.Select(b => new { b.Id, b.Path, b.Tags })
				.ToListAsync(ct))
			.Where(b => string.Equals(b.Path, path, StringComparison.OrdinalIgnoreCase) && Tagset(b.Tags).SetEquals(newTagset))
			.Select(b => b.Id)
			.ToList();

		long id;
		using (var tx = await configDb.BeginTransactionAsync(ct))
		{
			if (superseded.Count > 0)
				await configDb.Bindings
					.Where(b => superseded.Contains(b.Id) && !b.IsDeleted)
					.Set(b => b.IsDeleted, true)
					.Set(b => b.DeletedAt, (DateTime?)now)
					.Set(b => b.UpdatedAt, now)
					.UpdateAsync(ct);
#pragma warning disable CA2016
			id = Convert.ToInt64(await configDb.InsertWithIdentityAsync(new ConfigBinding
			{
				Path = path,
				Value = storedValue,
				Tags = tags,
				Kind = bindingKind,
				Ciphertext = cipher,
				Iv = iv,
				AuthTag = authTag,
				Version = 1,
				ContentHash = BindingContentHash.Compute(path, tags, bindingKind, storedValue, cipher),
				CreatedAt = now,
				UpdatedAt = now,
			}));
#pragma warning restore CA2016
			await tx.CommitAsync(ct);
		}
		return new ConfigBindingUpsertResult(id, path, tags, bindingKind.ToString(), superseded);
	}

	// The binding-identity tag SET: CSV split, trimmed, blanks dropped, ignore-case — the same
	// equality the resolve pipeline applies when it declares two bindings ambiguous.
	static HashSet<string> Tagset(string raw) =>
		new(raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
			StringComparer.OrdinalIgnoreCase);

	[McpServerTool(Name = "config_binding_list", Title = "List config bindings", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(ConfigBindingsListResult))]
	[Description("Lists a workspace's active config bindings (id, path, tags, kind). Requires admin:provision. Secret values are never returned.")]
	public static async Task<ConfigBindingsListResult> BindingListAsync(
		IHttpContextAccessor http, IConfigDbFactory configFactory,
		[Description("Workspace key to list bindings for.")] string workspaceKey,
		CancellationToken ct = default)
	{
		ModuleMcp.AssertScope(http, ApiKeyScopes.AdminProvision);
		if (string.IsNullOrWhiteSpace(workspaceKey)) throw new ArgumentException("workspaceKey is required");
		var configDb = configFactory.GetConfigDb(workspaceKey);
		// Project the enum raw, stringify in memory — linq2db can't translate Enum.ToString().
		var rows = await configDb.Bindings
			.Where(b => !b.IsDeleted)
			.OrderBy(b => b.Path)
			.Select(b => new { b.Id, b.Path, b.Tags, b.Kind })
			.ToListAsync(ct);
		return new ConfigBindingsListResult(rows.Select(b => new ConfigBindingRow(b.Id, b.Path, b.Tags, b.Kind.ToString())).ToList());
	}

	[McpServerTool(Name = "config_binding_delete", Title = "Delete a config binding", Destructive = true, UseStructuredContent = true, OutputSchemaType = typeof(ConfigBindingDeletedResult))]
	[Description("Soft-deletes a config binding by id (the row is kept, marked deleted). Requires admin:provision.")]
	public static async Task<ConfigBindingDeletedResult> BindingDeleteAsync(
		IHttpContextAccessor http, IConfigDbFactory configFactory,
		[Description("Workspace key the binding belongs to.")] string workspaceKey,
		[Description("Binding id (from config_binding_list).")] long id,
		CancellationToken ct = default)
	{
		ModuleMcp.AssertScope(http, ApiKeyScopes.AdminProvision);
		if (string.IsNullOrWhiteSpace(workspaceKey)) throw new ArgumentException("workspaceKey is required");
		var configDb = configFactory.GetConfigDb(workspaceKey);
		var now = DateTime.UtcNow;
		var updated = await configDb.Bindings
			.Where(b => b.Id == id && !b.IsDeleted)
			.Set(b => b.IsDeleted, true)
			.Set(b => b.DeletedAt, (DateTime?)now)
			.Set(b => b.UpdatedAt, now)
			.UpdateAsync(ct);
		if (updated == 0) throw new InvalidOperationException("Binding not found");
		return new ConfigBindingDeletedResult(true, id);
	}

	static BindingKind ParseKind(string? raw)
	{
		if (string.IsNullOrWhiteSpace(raw)) return BindingKind.Plain;
		if (Enum.TryParse<BindingKind>(raw, ignoreCase: true, out var k)) return k;
		throw new ArgumentException($"Unknown kind '{raw}'. Known: {string.Join(", ", Enum.GetNames<BindingKind>())}");
	}
}
