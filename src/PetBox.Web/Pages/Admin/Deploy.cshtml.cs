using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PetBox.Core.Features;
using PetBox.Deploy.Contract;
using PetBox.Deploy.Data;

namespace PetBox.Web.Pages.Admin;

// Sysadmin UI for the deploy control-plane: the fleet's nodes and the desired-state grid.
// Register/remove nodes, create deployments, start/stop/move/remove them. The node-agents
// reconcile to whatever this page sets. Reaches Deploy only through IDeployService.
[Authorize(Policy = "SysAdmin")]
public sealed class DeployModel : PageModel
{
	readonly IDeployService _svc;
	readonly FeatureFlags _features;

	public DeployModel(IDeployService svc, FeatureFlags features)
	{
		_svc = svc;
		_features = features;
	}

	public IReadOnlyList<NodeView> Nodes { get; private set; } = [];
	public IReadOnlyList<DeploymentView> Deployments { get; private set; } = [];
	public string? ErrorMessage { get; set; }

	public async Task<IActionResult> OnGetAsync()
	{
		if (!_features.IsEnabled(Feature.Deploy)) return NotFound();
		Nodes = await _svc.ListNodesAsync();
		Deployments = await _svc.ListDeploymentsAsync();
		return Page();
	}

	public async Task<IActionResult> OnPostNewNodeAsync(string id, string? displayName, string? tags, bool ephemeral)
	{
		if (string.IsNullOrWhiteSpace(id))
			return await Fail("Node id is required.");
		await _svc.UpsertNodeAsync(new NodeInput(id, displayName ?? id, tags ?? "", ephemeral));
		return RedirectToPage();
	}

	public async Task<IActionResult> OnPostDeleteNodeAsync(string id)
	{
		await _svc.DeleteNodeAsync(id);
		return RedirectToPage();
	}

	public async Task<IActionResult> OnPostNewDeploymentAsync(
		string service, string project, string nodeId, string imageDigest, bool relocatable, string? requiredTags, string? configTags,
		string? ports, string? volumes, string? restart, string? memory, double? cpus, string? network)
	{
		if (string.IsNullOrWhiteSpace(service) || string.IsNullOrWhiteSpace(project) || string.IsNullOrWhiteSpace(nodeId) || string.IsNullOrWhiteSpace(imageDigest))
			return await Fail("Service, project, node and image are required.");
		try
		{
			// Empty form fields bind to null (ConvertEmptyStringToNull) — coalesce everything.
			var runSpec = new RunSpec(
				Ports: SplitCsv(ports), Volumes: SplitCsv(volumes), Restart: restart,
				Resources: string.IsNullOrWhiteSpace(memory) && cpus is null ? null : new ResourcesSpec(memory, cpus),
				Network: network);
			await _svc.UpsertDeploymentAsync(new DeploymentInput(
				null, service, project, nodeId, imageDigest, DesiredState.Running, relocatable, requiredTags ?? "", configTags ?? "",
				runSpec));
		}
		catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
		{
			return await Fail(ex.Message);
		}
		return RedirectToPage();
	}

	static string[]? SplitCsv(string? csv) =>
		string.IsNullOrWhiteSpace(csv) ? null : csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

	public async Task<IActionResult> OnPostSetStateAsync(string id, DesiredState desired)
	{
		var d = await _svc.GetDeploymentAsync(id);
		if (d is not null) await _svc.UpsertDeploymentAsync(ToInput(d) with { DesiredState = desired });
		return RedirectToPage();
	}

	public async Task<IActionResult> OnPostMoveAsync(string id, string toNodeId)
	{
		var d = await _svc.GetDeploymentAsync(id);
		if (d is not null && !string.IsNullOrWhiteSpace(toNodeId))
		{
			try { await _svc.UpsertDeploymentAsync(ToInput(d) with { NodeId = toNodeId }); }
			catch (Exception ex) when (ex is InvalidOperationException or ArgumentException) { return await Fail(ex.Message); }
		}
		return RedirectToPage();
	}

	public async Task<IActionResult> OnPostDeleteDeploymentAsync(string id)
	{
		await _svc.DeleteDeploymentAsync(id);
		return RedirectToPage();
	}

	// Carries RunSpec through, so start/stop/move never wipe a deployment's run-spec.
	static DeploymentInput ToInput(DeploymentView d) => new(
		d.Id, d.Service, d.Project, d.NodeId, d.ImageDigest, d.DesiredState, d.Relocatable, d.RequiredTags, d.ConfigTags, d.RunSpec);

	async Task<IActionResult> Fail(string message)
	{
		ErrorMessage = message;
		await OnGetAsync();
		return Page();
	}
}
