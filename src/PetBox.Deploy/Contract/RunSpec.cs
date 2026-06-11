using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace PetBox.Deploy.Contract;

// Container healthcheck (docker --health-*). Cmd is required when the block is present;
// Interval/Timeout use docker duration syntax ("30s", "1m"); Retries is the failure count.
public sealed record HealthcheckSpec(string Cmd, string? Interval = null, string? Timeout = null, int? Retries = null);

// Container resource limits. Memory uses docker byte syntax ("256m", "1g"); Cpus is a
// fractional CPU count (--cpus).
public sealed record ResourcesSpec(string? Memory = null, double? Cpus = null);

// Marks a deployment as a SITE: a web app served behind the node's reverse proxy.
// Domain is the public hostname; Port is the loopback port the proxy forwards to
// (defaulted at normalize time from the host port of the first ports entry). The node
// agent keeps the host reverse-proxy (Caddy) config in line with this route.
public sealed record SiteSpec(string Domain, int? Port = null);

// Declarative run-spec of a container — the compose-subset a deployment carries beyond
// image+env (env stays config-resolved). The agent maps it 1:1 to docker run flags.
// Structurally allowlisted: there is deliberately NO privileged/cap-add/extra-args escape;
// dangerous capabilities are added later behind policy, not via a string backdoor.
public sealed record RunSpec(
	IReadOnlyList<string>? Ports = null,        // "127.0.0.1:8080:8080", "5000:5000[/udp]"
	IReadOnlyList<string>? Volumes = null,      // "/host/path:/container/path[:ro|rw]"
	string? Restart = null,                     // no|on-failure|unless-stopped|always (null = agent default)
	HealthcheckSpec? Healthcheck = null,
	ResourcesSpec? Resources = null,
	string? Network = null,                     // bridge|host|none|<network-name>
	IReadOnlyList<string>? Command = null,      // CMD override, appended after the image
	IReadOnlyDictionary<string, string>? Labels = null, // extra labels; "petbox.*" is reserved
	SiteSpec? Site = null);                     // present = this deployment is a site behind the host reverse-proxy

// Normalization, validation and the canonical JSON form of a RunSpec. The canonical JSON
// is what gets persisted in deploy_deployment.RunSpec and hashed into ConfigHash, so it
// must be deterministic: web-cased, nulls omitted, labels sorted, empty spec = "{}".
public static class RunSpecJson
{
	public const string Empty = "{}";

	static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
	{
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
	};

	static readonly string[] RestartPolicies = ["no", "on-failure", "unless-stopped", "always"];
	static readonly Regex PortRx = new(@"^((\d{1,3}\.){3}\d{1,3}:)?\d{1,5}:\d{1,5}(/(tcp|udp))?$", RegexOptions.Compiled);
	static readonly Regex DurationRx = new(@"^\d+(ms|s|m|h)$", RegexOptions.Compiled);
	static readonly Regex MemoryRx = new(@"^\d+[bkmg]?$", RegexOptions.Compiled);
	static readonly Regex NetworkRx = new(@"^[a-z0-9][a-z0-9_.-]*$", RegexOptions.Compiled);
	static readonly Regex DomainRx = new(@"^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?(\.[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?)+$", RegexOptions.Compiled);

	public static RunSpec Parse(string? json) =>
		string.IsNullOrWhiteSpace(json) || json.Trim() == Empty
			? new RunSpec()
			: JsonSerializer.Deserialize<RunSpec>(json, Options) ?? new RunSpec();

	public static string ToCanonicalJson(RunSpec? spec)
	{
		var normalized = Normalize(spec);
		return normalized == new RunSpec() ? Empty : JsonSerializer.Serialize(normalized, Options);
	}

	// Trim/lowercase/drop-empties and validate every field; throws ArgumentException with a
	// field-specific message on bad input. Returns a spec whose empty collections are null,
	// so an effectively-empty spec equals `new RunSpec()` and canonicalizes to "{}".
	public static RunSpec Normalize(RunSpec? spec)
	{
		if (spec is null) return new RunSpec();

		var ports = CleanList(spec.Ports);
		if (ports is not null)
			foreach (var p in ports)
				if (!PortRx.IsMatch(p))
					throw new ArgumentException($"Bad port mapping '{p}' — expected '[ip:]host:container[/tcp|udp]'.");

		var volumes = CleanList(spec.Volumes, lower: false);   // host paths are case-sensitive
		if (volumes is not null)
			foreach (var v in volumes)
			{
				var parts = v.Split(':');
				var ok = parts.Length is 2 or 3
					&& parts[0].StartsWith('/') && parts[1].StartsWith('/')
					&& (parts.Length == 2 || parts[2] is "ro" or "rw");
				if (!ok)
					throw new ArgumentException($"Bad volume '{v}' — expected '/host/path:/container/path[:ro|rw]' (bind mounts only).");
			}

		var restart = NullIfEmpty(spec.Restart?.Trim().ToLowerInvariant());
		if (restart is not null && !RestartPolicies.Contains(restart))
			throw new ArgumentException($"Bad restart policy '{restart}' — expected one of: {string.Join("|", RestartPolicies)}.");

		HealthcheckSpec? health = null;
		if (spec.Healthcheck is { } hc)
		{
			var cmd = (hc.Cmd ?? string.Empty).Trim();
			if (cmd.Length == 0) throw new ArgumentException("Healthcheck requires a cmd.");
			var interval = NullIfEmpty(hc.Interval?.Trim().ToLowerInvariant());
			var timeout = NullIfEmpty(hc.Timeout?.Trim().ToLowerInvariant());
			if (interval is not null && !DurationRx.IsMatch(interval))
				throw new ArgumentException($"Bad healthcheck interval '{interval}' — expected a docker duration like '30s'.");
			if (timeout is not null && !DurationRx.IsMatch(timeout))
				throw new ArgumentException($"Bad healthcheck timeout '{timeout}' — expected a docker duration like '5s'.");
			if (hc.Retries is { } r && r is < 1 or > 100)
				throw new ArgumentException("Healthcheck retries must be 1..100.");
			health = new HealthcheckSpec(cmd, interval, timeout, hc.Retries);
		}

		ResourcesSpec? resources = null;
		if (spec.Resources is { } res)
		{
			var memory = NullIfEmpty(res.Memory?.Trim().ToLowerInvariant());
			if (memory is not null && !MemoryRx.IsMatch(memory))
				throw new ArgumentException($"Bad memory limit '{memory}' — expected docker byte syntax like '256m'.");
			if (res.Cpus is { } cpus && (cpus <= 0 || cpus > 64 || double.IsNaN(cpus)))
				throw new ArgumentException("Cpus must be > 0 and <= 64.");
			if (memory is not null || res.Cpus is not null)
				resources = new ResourcesSpec(memory, res.Cpus);
		}

		var network = NullIfEmpty(spec.Network?.Trim().ToLowerInvariant());
		if (network is not null && !NetworkRx.IsMatch(network))
			throw new ArgumentException($"Bad network '{network}'.");

		var command = CleanList(spec.Command, lower: false);

		SiteSpec? site = null;
		if (spec.Site is { } s)
		{
			var domain = (s.Domain ?? string.Empty).Trim().ToLowerInvariant();
			if (!DomainRx.IsMatch(domain))
				throw new ArgumentException($"Bad site domain '{domain}' — expected a hostname like 'app.example.com'.");
			// Port is resolved HERE (not at the agent) so the stored canonical spec — and the
			// ConfigHash — always carry the effective route.
			var port = s.Port ?? HostPortOf(ports?.FirstOrDefault());
			if (port is null)
				throw new ArgumentException("A site needs a port: set site port explicitly or publish at least one ports entry.");
			if (port is < 1 or > 65535)
				throw new ArgumentException($"Bad site port {port}.");
			site = new SiteSpec(domain, port);
		}

		SortedDictionary<string, string>? labels = null;
		if (spec.Labels is { Count: > 0 })
		{
			labels = new SortedDictionary<string, string>(StringComparer.Ordinal);
			foreach (var (rawKey, rawValue) in spec.Labels)
			{
				var key = (rawKey ?? string.Empty).Trim();
				if (key.Length == 0) continue;
				if (key.StartsWith("petbox.", StringComparison.OrdinalIgnoreCase))
					throw new ArgumentException($"Label '{key}' is reserved — 'petbox.*' labels are control-plane managed.");
				labels[key] = (rawValue ?? string.Empty).Trim();
			}
			if (labels.Count == 0) labels = null;
		}

		return new RunSpec(ports, volumes, restart, health, resources, network, command, labels, site);
	}

	// The host port of a "[ip:]host:container[/tcp|udp]" mapping, or null.
	static int? HostPortOf(string? portMapping)
	{
		if (portMapping is null) return null;
		var parts = portMapping.Split('/')[0].Split(':');
		return parts.Length >= 2 && int.TryParse(parts[^2], out var p) ? p : null;
	}

	static List<string>? CleanList(IReadOnlyList<string>? items, bool lower = true)
	{
		if (items is null) return null;
		var cleaned = items
			.Select(i => (i ?? string.Empty).Trim())
			.Where(i => i.Length > 0)
			.Select(i => lower ? i.ToLowerInvariant() : i)
			.ToList();
		return cleaned.Count == 0 ? null : cleaned;
	}

	static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;
}
