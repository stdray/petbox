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
	[McpServerTool(Name = "config.create_binding", Title = "Create a config binding", UseStructuredContent = true, OutputSchemaType = typeof(ConfigBindingCreatedResult))]
	[Description("""
		Creates a config binding in a workspace's config store. Requires admin:provision.
		  • tags must include 'ws:{workspaceKey}'.
		  • kind: 'Plain' (default) or 'Secret' — a Secret stores `value` encrypted
		    (needs PETBOX_MASTER_KEY); the plaintext never lands in the Value column.
		""")]
	public static async Task<ConfigBindingCreatedResult> CreateBindingAsync(
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
#pragma warning disable CA2016
		var id = Convert.ToInt64(await configDb.InsertWithIdentityAsync(new ConfigBinding
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
		return new ConfigBindingCreatedResult(id, path, tags, bindingKind.ToString());
	}

	[McpServerTool(Name = "config.list_bindings", Title = "List config bindings", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(ConfigBindingsListResult))]
	[Description("Lists a workspace's active config bindings (id, path, tags, kind). Requires admin:provision. Secret values are never returned.")]
	public static async Task<ConfigBindingsListResult> ListBindingsAsync(
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

	[McpServerTool(Name = "config.delete_binding", Title = "Delete a config binding", Destructive = true, UseStructuredContent = true, OutputSchemaType = typeof(ConfigBindingDeletedResult))]
	[Description("Soft-deletes a config binding by id (the row is kept, marked deleted). Requires admin:provision.")]
	public static async Task<ConfigBindingDeletedResult> DeleteBindingAsync(
		IHttpContextAccessor http, IConfigDbFactory configFactory,
		[Description("Workspace key the binding belongs to.")] string workspaceKey,
		[Description("Binding id (from config.list_bindings).")] long id,
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
