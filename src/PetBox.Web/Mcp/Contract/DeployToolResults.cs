using PetBox.Deploy.Contract;

namespace PetBox.Web.Mcp.Contract;

// Typed structured-output records for the deploy.* MCP tools. Reuse the service views
// (NodeView/DeploymentView) so the MCP schema matches the REST/UI shapes exactly.
public sealed record DeployNodesResult(IReadOnlyList<NodeView> Nodes);
public sealed record DeployNodeResult(NodeView Node, string? Key);
public sealed record DeployDeploymentsResult(IReadOnlyList<DeploymentView> Deployments);
public sealed record DeployDeploymentResult(DeploymentView Deployment);
public sealed record DeployDeletedResult(bool Deleted, string Id);
