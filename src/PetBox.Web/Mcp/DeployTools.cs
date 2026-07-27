using System.ComponentModel;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol.Server;
using PetBox.Core.Auth;
using PetBox.Core.Features;
using PetBox.Deploy.Contract;
using PetBox.Deploy.Data;
using PetBox.Web.Auth;
using PetBox.Web.Mcp.Contract;

namespace PetBox.Web.Mcp;

// Typed MCP surface for the deploy control-plane. Gated by Feature.Deploy. Fleet-wide (no
// per-project claim); reads need deploy:read, writes deploy:write. Mirrors the REST/UI
// operations: node registry + the desired-state grid. Node-agents do NOT use these (they
// use /agent/*). Tools throw on a failed Assert*; McpErrorEnvelopeFilter renders the {error} body.
// TENANT DECLARATION (spec authz-scope-declaration): `fleet-wide` — "операция над парком узлов, не
// над арендатором". Declared on the TYPE, for all nine verbs: a node is addressed by its id and a
// deployment by ITS id, and neither id belongs to a tenant. Eight of the nine have no tenant slot
// at all, which is why the cross-tenant probe reports them as not addressable rather than as denied.
//
// deploy_upsert is the ninth and the one worth being explicit about: it DOES take a `projectKey`,
// and that parameter is NOT an access boundary — it selects which project's config bindings resolve
// into the container's env. Spec authz-scope-declaration: "Параметр, выглядящий границей доступа, но
// ею не являющийся, объявляется данными там же, где объявлен сам параметр" — its [Description]
// already says so to every MCP client, and this exemption is the machine-readable half. A
// [TenantFrom(Argument, "projectKey")] here would be a LIE in the other direction: it would start
// refusing the fleet operator whose key names one project, which is every deploy caller today.
//
// The exemption is not an endorsement. Work `deploy-tools-fleet-wide-undocumented` is about exactly
// this reach (a deploy:write key operates any tenant's deployment); declaring the class turns that
// from undocumented into counted — acceptance criterion 5.
[McpServerToolType]
[TenantExempt(TenantExemption.FleetWide,
	"nodes and deployments are addressed by fleet id, not by tenant; deploy_upsert's projectKey is env-resolution input, not a boundary")]
public static class DeployTools
{
	// node-key scopes minted by node_upsert (same as the REST enroll path): poll + heartbeat
	// + ship container logs. No config:read — env is resolved server-side at poll.
	const string NodeKeyScopes = ApiKeyScopes.AgentPoll + "," + ApiKeyScopes.AgentHeartbeat + "," + ApiKeyScopes.LogsIngest;

	[McpServerTool(Name = "deploy_node_list", Title = "List fleet nodes", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(DeployNodesResult))]
	[Description("Lists every node in the fleet (id, tags, online, last-seen, deployment count) — ALL nodes across ALL projects, not just yours: there is no `projectKey` parameter and none is accepted as a filter. Requires deploy:read.")]
	public static async Task<DeployNodesResult> NodeListAsync(IHttpContextAccessor http, FeatureFlags features, IDeployService svc, CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Deploy);
		ModuleMcp.AssertScope(http, ApiKeyScopes.DeployRead);
		return new DeployNodesResult(await svc.ListNodesAsync(ct));
	}

	[McpServerTool(Name = "deploy_node_upsert", Title = "Register/update a node", UseStructuredContent = true, OutputSchemaType = typeof(DeployNodeResult))]
	[Description("Registers (new id) or updates (existing id) a node. PATCH on update — an OMITTED displayName/tags/ephemeral keeps the node's current value; it does NOT reset to a default. To CLEAR tags explicitly, pass tags:\"\" (empty CSV); there is no clear sentinel for displayName (omit to keep, or send the value you want) or for ephemeral (omit to keep, or send true/false explicitly — there is no bool 'unset'). On CREATE an omitted displayName defaults to id, omitted tags to \"\", omitted ephemeral to false, same as before. FLEET-WIDE: a node has no owning project — there is no `projectKey`, and a deploy:write key can create/edit ANY node in the fleet, not just ones hosting your project's deployments. With mintKey=true also mints (or rotates) the node-scoped agent key and returns it ONCE. Requires deploy:write.")]
	public static async Task<DeployNodeResult> NodeUpsertAsync(
		IHttpContextAccessor http, FeatureFlags features, IDeployService svc, AgentKeyAdminService keys,
		[Description("Node id (slug), e.g. 'vdsina-1'.")] string id,
		[Description("Display name. Omit on update to keep the current one; on create, omitted defaults to id.")] string? displayName = null,
		[Description("Capability tags CSV, e.g. 'net.x,disk=nvme'. Omit on update to keep the current CSV; pass \"\" to clear it. On create, omitted = \"\".")] string? tags = null,
		[Description("Comes and goes (laptop/WSL2) — failover treats it as relocatable target carefully. Omit on update to keep the current flag; there is no separate clear sentinel, send true/false explicitly to change it. On create, omitted = false.")] bool? ephemeral = null,
		[Description("Mint (or rotate) the node agent key and return it once.")] bool mintKey = false,
		CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Deploy);
		ModuleMcp.AssertScope(http, ApiKeyScopes.DeployWrite);
		if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("id is required");

		var keyRef = $"node:{id.Trim().ToLowerInvariant()}";
		var node = await svc.UpsertNodeAsync(new NodeInput(id, displayName, tags, ephemeral, mintKey ? keyRef : null), ct);

		// ApiKeys is core.db, and core.db is opened in the service layer only: the rotate-and-mint
		// (drop the node's previous key, insert the fresh one) lives in AgentKeyAdminService, not here.
		var key = mintKey ? await keys.MintNodeKeyAsync(keyRef, node.Id, NodeKeyScopes, ct) : null;
		return new DeployNodeResult(node, key);
	}

	[McpServerTool(Name = "deploy_node_delete", Title = "Delete a node", Destructive = true, UseStructuredContent = true, OutputSchemaType = typeof(DeployDeletedResult))]
	[Description("Deletes a node and cascades ALL of its deployments — across every project hosted on it, not just yours. FLEET-WIDE and DESTRUCTIVE: there is no `projectKey` to scope this to one project's deployments; unlike config_binding/memory/tasks, a deploy:write key is not confined to a project here — it reaches every node in the fleet. Requires deploy:write.")]
	public static async Task<DeployDeletedResult> NodeDeleteAsync(
		IHttpContextAccessor http, FeatureFlags features, IDeployService svc,
		[Description("Node id to delete.")] string id,
		CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Deploy);
		ModuleMcp.AssertScope(http, ApiKeyScopes.DeployWrite);
		if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("id is required");
		return new DeployDeletedResult(await svc.DeleteNodeAsync(id, ct), id);
	}

	[McpServerTool(Name = "deploy_list", Title = "List deployments", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(DeployDeploymentsResult))]
	[Description("Lists deployments (desired + last actual state) across the WHOLE fleet, optionally filtered by node and/or service — NOT by project: there is no `projectKey` parameter, and the result spans every project's deployments a deploy:read key can see. Requires deploy:read.")]
	public static async Task<DeployDeploymentsResult> ListAsync(
		IHttpContextAccessor http, FeatureFlags features, IDeployService svc,
		[Description("Filter by node id (the deploy-fleet host, not a plan node).")] string? hostId = null,
		[Description("Filter by service.")] string? service = null,
		CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Deploy);
		ModuleMcp.AssertScope(http, ApiKeyScopes.DeployRead);
		return new DeployDeploymentsResult(await svc.ListDeploymentsAsync(hostId, service, ct));
	}

	[McpServerTool(Name = "deploy_upsert", Title = "Create/update a deployment", UseStructuredContent = true, OutputSchemaType = typeof(DeployDeploymentResult))]
	[Description("Creates (omit id) or updates (existing id) a deployment of a service on a node. One copy per (service, node). PATCH on the run-spec fields below (ports/volumes/restart/healthcheck*/memory/cpus/network/command/labels/domain+sitePort): on update, an OMITTED one keeps the deployment's current value — it does NOT get wiped. To CLEAR one explicitly: pass an empty array for ports/volumes/command/labels, \"\" for restart/network/healthcheckCmd/domain (healthcheckCmd:\"\" drops the whole healthcheck block; domain:\"\" drops the whole site block), \"\" for memory, or 0 for cpus (0 is otherwise never a valid cpu limit — same clear-by-sentinel convention apikey_update uses for expiresInSeconds:0). Setting healthcheckCmd or domain to a non-empty value replaces its WHOLE sub-block (healthcheckInterval/Timeout/Retries, or sitePort) from what's passed in this call, not merged field-by-field with the old block. service/projectKey/hostId/imageDigest/running/relocatable/requiredTags/configTags are NOT patched — every call must resend the full deployment identity/desired-state for those. On CREATE, all of the above default the same way they always have (omit = no value). `projectKey` here is NOT an access boundary the way it is in config_binding/memory/tasks — it is resolution input ONLY (picks which project's config bindings resolve into the container's env); a deploy:write key can create/update a deployment naming ANY projectKey, on ANY node, regardless of the calling key's own project claim. Requires deploy:write.")]
	public static async Task<DeployDeploymentResult> UpsertAsync(
		IHttpContextAccessor http, FeatureFlags features, IDeployService svc,
		[Description("Service name (slug).")] string service,
		// NAMED projectKey, not `project`: every project-routing MCP arg is spelled `projectKey`, and
		// McpProjectDefaultFilter keys the key's-default injection on exactly that name — an odd one
		// out would silently sit outside the coverage.
		[Description("Project whose config resolves into the container's env — resolution input ONLY, not an access boundary: any project name is accepted regardless of the calling key's own project claim.")] string projectKey,
		[Description("Target node id (the deploy-fleet host, not a plan node).")] string hostId,
		[Description("Image reference/digest to run.")] string imageDigest,
		[Description("Existing deployment id to update; omit to create.")] string? id = null,
		[Description("Desired running (true) or stopped (false).")] bool running = true,
		[Description("Auto-relocate on node failure.")] bool relocatable = false,
		[Description("Tags a node must cover to host this (CSV). Always resent in full on update, not patched.")] string? requiredTags = null,
		[Description("Config tag-vector for env resolution (CSV). Always resent in full on update, not patched.")] string? configTags = null,
		[Description("Port publications, '[ip:]host:container[/tcp|udp]' entries, e.g. '127.0.0.1:8080:8080'. Omit on update to keep the current ports; pass [] to clear them.")] string[]? ports = null,
		[Description("Bind mounts, '/host/path:/container/path[:ro|rw]' entries. Omit on update to keep the current mounts; pass [] to clear them.")] string[]? volumes = null,
		[Description("Restart policy: no|on-failure|unless-stopped|always (default unless-stopped at the agent). Omit on update to keep the current policy; pass \"\" to clear it.")] string? restart = null,
		[Description("Container healthcheck command (docker --health-cmd). Omit on update to keep the current healthcheck block untouched; pass \"\" to drop it entirely; a new value REPLACES the whole block (Cmd + Interval/Timeout/Retries below), not merged field-by-field.")] string? healthcheckCmd = null,
		[Description("Healthcheck interval, docker duration like '30s'. Only applied when healthcheckCmd is being set in this same call.")] string? healthcheckInterval = null,
		[Description("Healthcheck timeout, docker duration like '5s'. Only applied when healthcheckCmd is being set in this same call.")] string? healthcheckTimeout = null,
		[Description("Healthcheck retries before unhealthy. Only applied when healthcheckCmd is being set in this same call.")] int? healthcheckRetries = null,
		[Description("Memory limit, docker byte syntax like '256m'. Omit on update to keep the current limit; pass \"\" to clear it.")] string? memory = null,
		[Description("CPU limit, fractional CPUs (docker --cpus). Omit on update to keep the current limit; pass 0 to clear it (0 is never a valid limit otherwise).")] double? cpus = null,
		[Description("Container network: bridge|host|none|<name>. Omit on update to keep the current network; pass \"\" to clear it.")] string? network = null,
		[Description("CMD override, one entry per argument. Omit on update to keep the current override; pass [] to clear it.")] string[]? command = null,
		[Description("Extra container labels, 'key=value' entries ('petbox.*' is reserved). Omit on update to keep the current labels; pass [] to clear them.")] string[]? labels = null,
		[Description("Site domain (e.g. 'app.example.com') — makes this deployment a SITE: the node agent routes the domain to the loopback port via the host reverse-proxy (Caddy). Omit on update to keep the current site block untouched; pass \"\" to drop it entirely; a new domain REPLACES the whole block (domain + sitePort below), not merged field-by-field.")] string? domain = null,
		[Description("Loopback port the reverse-proxy forwards to; default = host port of the first ports entry. Only applied when domain is being set in this same call.")] int? sitePort = null,
		CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Deploy);
		ModuleMcp.AssertScope(http, ApiKeyScopes.DeployWrite);
		// Raw signals only — DeployService.MergeRunSpec (not this method) decides omit-vs-clear
		// against the deployment's previously stored RunSpec. Do NOT collapse "omitted" and
		// "explicit empty/blank" here (e.g. via IsNullOrWhiteSpace) — that's exactly the
		// distinction the merge depends on, and RunSpecJson.Normalize (downstream) already
		// collapses an explicit empty list to null, so it must run AFTER the merge, not before.
		var runSpec = new RunSpec(
			Ports: ports, Volumes: volumes, Restart: restart,
			Healthcheck: healthcheckCmd is null
				? null
				: new HealthcheckSpec(healthcheckCmd.Trim(), healthcheckInterval, healthcheckTimeout, healthcheckRetries),
			Resources: memory is null && cpus is null ? null : new ResourcesSpec(memory, cpus),
			Network: network, Command: command, Labels: ParseLabels(labels),
			Site: domain is null ? null : new SiteSpec(domain.Trim(), sitePort));
		var d = await svc.UpsertDeploymentAsync(new DeploymentInput(
			id, service, projectKey, hostId, imageDigest,
			running ? DesiredState.Running : DesiredState.Stopped, relocatable, requiredTags ?? "", configTags ?? "",
			runSpec), ct);
		return new DeployDeploymentResult(d);
	}

	[McpServerTool(Name = "deploy_start", Title = "Start a deployment", UseStructuredContent = true, OutputSchemaType = typeof(DeployDeploymentResult))]
	[Description("Sets a deployment's desired state to running. FLEET-WIDE: `id` addresses a deployment directly with no project check — a deploy:write key can start ANY deployment on ANY node, not just your project's. Requires deploy:write.")]
	public static Task<DeployDeploymentResult> StartAsync(IHttpContextAccessor http, FeatureFlags features, IDeployService svc,
		[Description("Deployment id.")] string id, CancellationToken ct = default) =>
		SetDesiredAsync(http, features, svc, id, DesiredState.Running, ct);

	[McpServerTool(Name = "deploy_stop", Title = "Stop a deployment", UseStructuredContent = true, OutputSchemaType = typeof(DeployDeploymentResult))]
	[Description("Sets a deployment's desired state to stopped — stops the live container on its node. FLEET-WIDE and effectively DESTRUCTIVE: `id` addresses a deployment directly with no project check — a deploy:write key can stop ANY deployment on ANY node in the fleet, not just one belonging to your project. Requires deploy:write.")]
	public static Task<DeployDeploymentResult> StopAsync(IHttpContextAccessor http, FeatureFlags features, IDeployService svc,
		[Description("Deployment id.")] string id, CancellationToken ct = default) =>
		SetDesiredAsync(http, features, svc, id, DesiredState.Stopped, ct);

	[McpServerTool(Name = "deploy_move", Title = "Move a deployment to another node", UseStructuredContent = true, OutputSchemaType = typeof(DeployDeploymentResult))]
	[Description("Moves a deployment to a different node (the agents reconcile the move). FLEET-WIDE: `id`/`toHostId` address the fleet directly with no project check — a deploy:write key can relocate ANY deployment to ANY node, not just your project's. Requires deploy:write.")]
	public static async Task<DeployDeploymentResult> MoveAsync(
		IHttpContextAccessor http, FeatureFlags features, IDeployService svc,
		[Description("Deployment id.")] string id,
		[Description("Destination node id (the deploy-fleet host, not a plan node).")] string toHostId,
		CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Deploy);
		ModuleMcp.AssertScope(http, ApiKeyScopes.DeployWrite);
		if (string.IsNullOrWhiteSpace(toHostId)) throw new ArgumentException("toHostId is required");
		var d = await svc.GetDeploymentAsync(id, ct) ?? throw new InvalidOperationException("deployment not found");
		return new DeployDeploymentResult(await svc.UpsertDeploymentAsync(ToInput(d) with { NodeId = toHostId }, ct));
	}

	[McpServerTool(Name = "deploy_delete", Title = "Delete a deployment", Destructive = true, UseStructuredContent = true, OutputSchemaType = typeof(DeployDeletedResult))]
	[Description("Deletes a deployment (the owning node's agent then removes the container). FLEET-WIDE and DESTRUCTIVE: `id` addresses a deployment directly with no project check — a deploy:write key can delete ANY deployment on ANY node in the fleet, not just one belonging to your project. Requires deploy:write.")]
	public static async Task<DeployDeletedResult> DeleteAsync(
		IHttpContextAccessor http, FeatureFlags features, IDeployService svc,
		[Description("Deployment id to delete.")] string id,
		CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Deploy);
		ModuleMcp.AssertScope(http, ApiKeyScopes.DeployWrite);
		return new DeployDeletedResult(await svc.DeleteDeploymentAsync(id, ct), id);
	}

	static async Task<DeployDeploymentResult> SetDesiredAsync(IHttpContextAccessor http, FeatureFlags features, IDeployService svc, string id, DesiredState desired, CancellationToken ct)
	{
		ModuleMcp.AssertFeature(features, Feature.Deploy);
		ModuleMcp.AssertScope(http, ApiKeyScopes.DeployWrite);
		var d = await svc.GetDeploymentAsync(id, ct) ?? throw new InvalidOperationException("deployment not found");
		return new DeployDeploymentResult(await svc.UpsertDeploymentAsync(ToInput(d) with { DesiredState = desired }, ct));
	}

	// Carries RunSpec through, so start/stop/move never wipe a deployment's run-spec.
	static DeploymentInput ToInput(DeploymentView d) => new(
		d.Id, d.Service, d.Project, d.NodeId, d.ImageDigest, d.DesiredState, d.Relocatable, d.RequiredTags, d.ConfigTags, d.RunSpec);

	// "key=value" entries → label map; a value-less entry becomes an empty-value label.
	// Omitted (null) vs explicitly-empty is preserved for DeployService.MergeRunSpec: null
	// = not supplied at all (keep the deployment's current labels on update); a non-null
	// entries array — even one that resolves to zero real labels — is an explicit "here is
	// the label set" and returns a non-null (possibly empty, i.e. clearing) map.
	static Dictionary<string, string>? ParseLabels(string[]? entries)
	{
		if (entries is null) return null;
		var labels = new Dictionary<string, string>(StringComparer.Ordinal);
		foreach (var e in entries)
		{
			if (string.IsNullOrWhiteSpace(e)) continue;
			var i = e.IndexOf('=');
			if (i < 0) labels[e.Trim()] = string.Empty;
			else labels[e[..i].Trim()] = e[(i + 1)..].Trim();
		}
		return labels;
	}
}
