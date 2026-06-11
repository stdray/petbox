using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LinqToDB;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PetBox.Deploy.Contract;
using PetBox.Deploy.Data;

namespace PetBox.Deploy.Services;

// The fleet-wide deploy service. Owns the node registry and per-(service,node) desired
// state in the single DeployDb. Computes ConfigHash and the online/actual-state views.
public sealed class DeployService : IDeployService
{
	// A node is "online" if its last heartbeat is within this window. The agent poll
	// interval is well under this; failover (slice 4) uses a larger staleness threshold.
	private static readonly TimeSpan OnlineWindow = TimeSpan.FromSeconds(120);

	// Resource warning thresholds (server-side so changing them needs no agent redeploy):
	// warn when below the relative floor OR the absolute floor.
	private const double LowMemoryFraction = 0.10;
	private const long LowMemoryFloorMb = 150;
	private const double LowDiskFraction = 0.10;
	private const double LowDiskFloorGb = 2.0;

	static readonly JsonSerializerOptions HostJson = new(JsonSerializerDefaults.Web)
	{
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
	};

	private readonly DeployDb _db;
	private readonly ILogger _logger;

	public DeployService(DeployDb db, ILogger<DeployService>? logger = null)
	{
		_db = db;
		_logger = logger ?? NullLogger<DeployService>.Instance;
	}

	// --- nodes ---

	public async Task<NodeView> UpsertNodeAsync(NodeInput input, CancellationToken ct = default)
	{
		var id = NormalizeId(input.Id);
		var existing = await _db.Nodes.FirstOrDefaultAsync(n => n.Id == id, ct);
		var node = new Node
		{
			Id = id,
			DisplayName = string.IsNullOrWhiteSpace(input.DisplayName) ? id : input.DisplayName.Trim(),
			Tags = NormalizeCsv(input.Tags),
			Ephemeral = input.Ephemeral,
			KeyRef = string.IsNullOrWhiteSpace(input.KeyRef) ? existing?.KeyRef : input.KeyRef!.Trim(),
			LastSeenAt = existing?.LastSeenAt,
			CreatedAt = existing?.CreatedAt ?? DateTime.UtcNow,
		};
		await _db.InsertOrReplaceAsync(node, token: ct);
		var count = await _db.Deployments.CountAsync(d => d.NodeId == id, ct);
		return ToView(node, count);
	}

	public async Task<IReadOnlyList<NodeView>> ListNodesAsync(CancellationToken ct = default)
	{
		var nodes = await _db.Nodes.OrderBy(n => n.Id).ToListAsync(ct);
		var counts = (await _db.Deployments.ToListAsync(ct))
			.GroupBy(d => d.NodeId)
			.ToDictionary(g => g.Key, g => g.Count());
		return nodes.Select(n => ToView(n, counts.GetValueOrDefault(n.Id))).ToList();
	}

	public async Task<NodeView?> GetNodeAsync(string id, CancellationToken ct = default)
	{
		id = NormalizeId(id);
		var node = await _db.Nodes.FirstOrDefaultAsync(n => n.Id == id, ct);
		if (node is null) return null;
		var count = await _db.Deployments.CountAsync(d => d.NodeId == id, ct);
		return ToView(node, count);
	}

	public async Task<bool> DeleteNodeAsync(string id, CancellationToken ct = default)
	{
		id = NormalizeId(id);
		// Cascade: a node's deployments and their reported status go with it.
		await _db.Statuses.Where(s => s.NodeId == id).DeleteAsync(ct);
		await _db.Deployments.Where(d => d.NodeId == id).DeleteAsync(ct);
		var removed = await _db.Nodes.Where(n => n.Id == id).DeleteAsync(ct);
		return removed > 0;
	}

	// --- deployments ---

	public async Task<DeploymentView> UpsertDeploymentAsync(DeploymentInput input, CancellationToken ct = default)
	{
		var service = NormalizeId(input.Service);
		var nodeId = NormalizeId(input.NodeId);
		var project = (input.Project ?? string.Empty).Trim();   // project keys are case-sensitive
		if (service.Length == 0 || nodeId.Length == 0 || project.Length == 0)
			throw new ArgumentException("Service, Project and NodeId are required.");

		// One deployment per (Service, NodeId). Reject a colliding row owned by another Id.
		var collision = await _db.Deployments
			.FirstOrDefaultAsync(d => d.Service == service && d.NodeId == nodeId, ct);
		var id = NormalizeId(input.Id ?? string.Empty);
		if (id.Length == 0) id = Guid.NewGuid().ToString("n");
		if (collision is not null && collision.Id != id)
			throw new InvalidOperationException(
				$"Service '{service}' already has a deployment on node '{nodeId}' (id {collision.Id}). One copy per node.");

		var imageDigest = input.ImageDigest.Trim();
		var configTags = NormalizeCsv(input.ConfigTags);
		var runSpecJson = RunSpecJson.ToCanonicalJson(input.RunSpec);   // validates; throws ArgumentException on bad fields
		var deployment = new Deployment
		{
			Id = id,
			Service = service,
			Project = project,
			NodeId = nodeId,
			ImageDigest = imageDigest,
			DesiredState = input.DesiredState,
			Relocatable = input.Relocatable,
			RequiredTags = NormalizeCsv(input.RequiredTags),
			ConfigTags = configTags,
			RunSpec = runSpecJson,
			ConfigHash = ComputeConfigHash(imageDigest, configTags, input.DesiredState, project, runSpecJson),
			UpdatedAt = DateTime.UtcNow,
		};
		await _db.InsertOrReplaceAsync(deployment, token: ct);
		var status = await _db.Statuses.FirstOrDefaultAsync(s => s.NodeId == nodeId && s.Service == service, ct);
		return ToView(deployment, status);
	}

	public async Task<IReadOnlyList<DeploymentView>> ListDeploymentsAsync(string? nodeId = null, string? service = null, CancellationToken ct = default)
	{
		var q = _db.Deployments.AsQueryable();
		if (!string.IsNullOrWhiteSpace(nodeId)) { var n = NormalizeId(nodeId); q = q.Where(d => d.NodeId == n); }
		if (!string.IsNullOrWhiteSpace(service)) { var s = NormalizeId(service); q = q.Where(d => d.Service == s); }
		var deployments = await q.OrderBy(d => d.Service).ThenBy(d => d.NodeId).ToListAsync(ct);
		var statuses = (await _db.Statuses.ToListAsync(ct))
			.ToDictionary(s => (s.NodeId, s.Service));
		return deployments
			.Select(d => ToView(d, statuses.GetValueOrDefault((d.NodeId, d.Service))))
			.ToList();
	}

	public async Task<DeploymentView?> GetDeploymentAsync(string id, CancellationToken ct = default)
	{
		id = NormalizeId(id);
		var d = await _db.Deployments.FirstOrDefaultAsync(x => x.Id == id, ct);
		if (d is null) return null;
		var status = await _db.Statuses.FirstOrDefaultAsync(s => s.NodeId == d.NodeId && s.Service == d.Service, ct);
		return ToView(d, status);
	}

	public async Task<bool> DeleteDeploymentAsync(string id, CancellationToken ct = default)
	{
		id = NormalizeId(id);
		var d = await _db.Deployments.FirstOrDefaultAsync(x => x.Id == id, ct);
		if (d is null) return false;
		await _db.Statuses.Where(s => s.NodeId == d.NodeId && s.Service == d.Service).DeleteAsync(ct);
		await _db.Deployments.Where(x => x.Id == id).DeleteAsync(ct);
		return true;
	}

	// --- agent contract (pull) ---

	public async Task<PollResponse> PollAsync(string nodeId, CancellationToken ct = default)
	{
		nodeId = NormalizeId(nodeId);
		await TouchNodeAsync(nodeId, ct);
		var deployments = await _db.Deployments.Where(d => d.NodeId == nodeId).OrderBy(d => d.Service).ToListAsync(ct);
		var items = deployments
			.Select(d => new PollItem(d.Service, d.Project, d.ImageDigest, d.DesiredState, d.ConfigTags, d.ConfigHash,
				RunSpecJson.Parse(d.RunSpec)))
			.ToList();
		return new PollResponse(nodeId, items);
	}

	public async Task ApplyHeartbeatAsync(string nodeId, HeartbeatReport report, CancellationToken ct = default)
	{
		nodeId = NormalizeId(nodeId);
		await TouchNodeAsync(nodeId, ct);
		// Capabilities are agent-detected host facts; only a capability-aware agent (non-null)
		// overwrites them, so a legacy agent's heartbeat doesn't erase what a newer one found.
		if (report.Capabilities is not null)
			await _db.Nodes.Where(n => n.Id == nodeId)
				.Set(n => n.Capabilities, NormalizeCsv(string.Join(",", report.Capabilities)))
				.UpdateAsync(ct);

		// Host report: store the snapshot and log warning TRANSITIONS (appeared/cleared) —
		// the server has the previous state + thresholds, the agent stays a dumb reporter.
		// The log line is the history (self-log), the node row holds only the latest.
		if (report.Host is not null)
		{
			var prevJson = await _db.Nodes.Where(n => n.Id == nodeId).Select(n => n.HostReport).FirstOrDefaultAsync(ct);
			var prev = ComputeWarnings(ParseHostReport(prevJson));
			var next = ComputeWarnings(report.Host);
			if (_logger.IsEnabled(LogLevel.Warning))
				foreach (var w in next.Except(prev, StringComparer.Ordinal))
					_logger.LogWarning("deploy node {NodeId} host warning: {Warning}", nodeId, w);
			if (_logger.IsEnabled(LogLevel.Information))
				foreach (var w in prev.Except(next, StringComparer.Ordinal))
					_logger.LogInformation("deploy node {NodeId} host warning cleared: {Warning}", nodeId, w);
			await _db.Nodes.Where(n => n.Id == nodeId)
				.Set(n => n.HostReport, JsonSerializer.Serialize(report.Host, HostJson))
				.UpdateAsync(ct);
		}
		var now = DateTime.UtcNow;
		var reported = new HashSet<string>(StringComparer.Ordinal);
		foreach (var a in report.Actual)
		{
			var service = NormalizeId(a.Service);
			if (service.Length == 0) continue;
			reported.Add(service);
			await _db.InsertOrReplaceAsync(new DeploymentStatus
			{
				NodeId = nodeId,
				Service = service,
				ActualState = a.State,
				ContainerId = a.ContainerId,
				ImageDigest = a.ImageDigest,
				Healthy = a.Healthy,
				Error = string.IsNullOrWhiteSpace(a.Error) ? null : a.Error.Trim(),
				ReportedAt = now,
			}, token: ct);
		}

		// A heartbeat is a FULL snapshot of the node's managed containers. Any status row
		// for this node that's NOT in the report = the container is gone → mark Missing.
		// Without this a stopped/removed deployment shows stale Running/healthy forever in
		// deploy.list and the UI.
		await _db.Statuses
			.Where(s => s.NodeId == nodeId && !reported.Contains(s.Service) && s.ActualState != ActualState.Missing)
			.Set(s => s.ActualState, ActualState.Missing)
			.Set(s => s.Healthy, false)
			.Set(s => s.Error, (string?)null)
			.Set(s => s.ReportedAt, now)
			.UpdateAsync(ct);
	}

	// --- failover ---

	public async Task<IReadOnlyList<RescheduleAction>> RescheduleStaleAsync(TimeSpan staleness, CancellationToken ct = default)
	{
		var now = DateTime.UtcNow;
		var nodes = await _db.Nodes.ToListAsync(ct);
		var staleNodeIds = nodes.Where(n => n.LastSeenAt is { } s && now - s >= staleness).Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
		if (staleNodeIds.Count == 0) return [];

		var online = nodes.Where(n => n.LastSeenAt is { } s && now - s < OnlineWindow && !staleNodeIds.Contains(n.Id)).ToList();

		// All current placements, so we preserve one-copy-per-node as we relocate.
		var occupancy = (await _db.Deployments.ToListAsync(ct))
			.Select(d => (d.Service, d.NodeId)).ToHashSet();

		var candidates = (await _db.Deployments.Where(d => d.Relocatable).ToListAsync(ct))
			.Where(d => staleNodeIds.Contains(d.NodeId))
			.ToList();

		var actions = new List<RescheduleAction>();
		foreach (var d in candidates)
		{
			var required = ParseSet(d.RequiredTags);
			var target = online.FirstOrDefault(n =>
				n.Id != d.NodeId
				&& required.IsSubsetOf(ParseSet(n.Tags))
				&& !occupancy.Contains((d.Service, n.Id)));

			if (target is null)
			{
				actions.Add(new RescheduleAction(d.Id, d.Service, d.NodeId, null, false, "no online node covers required tags"));
				continue;
			}

			occupancy.Remove((d.Service, d.NodeId));
			occupancy.Add((d.Service, target.Id));
			await _db.Deployments.Where(x => x.Id == d.Id)
				.Set(x => x.NodeId, target.Id).Set(x => x.UpdatedAt, now).UpdateAsync(ct);
			actions.Add(new RescheduleAction(d.Id, d.Service, d.NodeId, target.Id, true, "relocated"));
		}
		return actions;
	}

	private static HashSet<string> ParseSet(string csv) =>
		csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.Ordinal);

	// LastSeenAt is the liveness signal failover watches; both poll and heartbeat
	// are outbound agent contacts, so both bump it.
	private Task<int> TouchNodeAsync(string nodeId, CancellationToken ct) =>
		_db.Nodes.Where(n => n.Id == nodeId).Set(n => n.LastSeenAt, (DateTime?)DateTime.UtcNow).UpdateAsync(ct);

	// --- helpers ---

	private static NodeView ToView(Node n, int deployments)
	{
		var host = ParseHostReport(n.HostReport);
		var warnings = ComputeWarnings(host);
		return new(
			n.Id, n.DisplayName, n.Tags, n.Ephemeral, n.KeyRef, n.LastSeenAt,
			Online: n.LastSeenAt is { } seen && DateTime.UtcNow - seen < OnlineWindow,
			Deployments: deployments,
			CreatedAt: n.CreatedAt,
			Capabilities: n.Capabilities,
			Host: host,
			Warnings: warnings.Count == 0 ? null : warnings);
	}

	private static HostReport? ParseHostReport(string? json) =>
		string.IsNullOrWhiteSpace(json) || json.Trim() == "{}"
			? null
			: JsonSerializer.Deserialize<HostReport>(json, HostJson);

	// The warning set a host snapshot implies. Deterministic strings: the transition log
	// and the UI badges show the same text, and set-diffing detects appear/clear.
	public static IReadOnlyList<string> ComputeWarnings(HostReport? host)
	{
		if (host is null) return [];
		var warnings = new List<string>();
		if (host.Security?.RootLoginEnabled == true)
			warnings.Add("root SSH login is not disabled");
		if (host.Security?.PasswordAuthEnabled == true)
			warnings.Add("password SSH auth is allowed");
		if (host.Memory is { TotalMb: > 0, AvailableMb: { } availMb }
			&& (availMb < host.Memory.TotalMb * LowMemoryFraction || availMb < LowMemoryFloorMb))
			warnings.Add($"low memory: {availMb} MB available of {host.Memory.TotalMb} MB");
		if (host.Disk is { TotalGb: > 0, FreeGb: { } freeGb }
			&& (freeGb < host.Disk.TotalGb * LowDiskFraction || freeGb < LowDiskFloorGb))
			warnings.Add($"low disk: {freeGb} GB free of {host.Disk.TotalGb} GB");
		return warnings;
	}

	private static DeploymentView ToView(Deployment d, DeploymentStatus? s) => new(
		d.Id, d.Service, d.Project, d.NodeId, d.ImageDigest, d.DesiredState, d.Relocatable,
		d.RequiredTags, d.ConfigTags, d.ConfigHash, d.UpdatedAt,
		ActualState: s?.ActualState, Healthy: s?.Healthy, ReportedAt: s?.ReportedAt,
		RunSpec: RunSpecJson.Parse(d.RunSpec),
		Error: s?.Error);

	public static string ComputeConfigHash(string imageDigest, string configTags, DesiredState desired, string project, string runSpecJson = RunSpecJson.Empty)
	{
		var payload = $"{imageDigest}\0{configTags}\0{(int)desired}\0{project}\0{runSpecJson}";
		var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
		return Convert.ToHexString(bytes).ToLowerInvariant();
	}

	// Lowercase-trim slug normalization (ids/services/tags are case-insensitive).
	private static string NormalizeId(string? s) => (s ?? string.Empty).Trim().ToLowerInvariant();

	// Trim + dedupe a CSV tag vector, preserving order, lowercased.
	private static string NormalizeCsv(string? csv)
	{
		if (string.IsNullOrWhiteSpace(csv)) return string.Empty;
		var seen = new HashSet<string>();
		var parts = new List<string>();
		foreach (var raw in csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
		{
			var t = raw.ToLowerInvariant();
			if (seen.Add(t)) parts.Add(t);
		}
		return string.Join(",", parts);
	}
}
