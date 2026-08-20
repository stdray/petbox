using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PetBox.Log.Core.Models;

namespace PetBox.Log.Core.SelfLogging;

// ISupportExternalScope: the standard MEL hook a logging provider implements to receive the
// host's ambient BeginScope chain. LoggerFactory detects this interface and calls
// SetScopeProvider automatically for every registered ILoggerProvider — no extra Program.cs
// wiring needed. This is what lets a BackgroundService's `logger.BeginScope(new Dictionary
// { ["Invoker"] = "background:X" })` (BackgroundInvokerScope) reach SystemLogger.Log and land
// in the emitted record's properties.
public sealed class SystemLoggerProvider : ILoggerProvider, ISupportExternalScope
{
	readonly SystemLoggerOptions _options;
	readonly TimeProvider _time;
	readonly Channel<LogEntryCandidate> _channel;
	readonly ConcurrentDictionary<string, SystemLogger> _loggers = new(StringComparer.Ordinal);
	IExternalScopeProvider? _scopeProvider;

	public SystemLoggerProvider(IOptions<SystemLoggerOptions> options, TimeProvider? time = null)
	{
		_options = options.Value;
		_time = time ?? TimeProvider.System;
		_channel = Channel.CreateBounded<LogEntryCandidate>(new BoundedChannelOptions(_options.QueueCapacity)
		{
			FullMode = BoundedChannelFullMode.DropWrite,
			SingleReader = true,
			SingleWriter = false,
		});
	}

	internal ChannelReader<LogEntryCandidate> Reader => _channel.Reader;
	internal SystemLoggerOptions Options => _options;

	public ILogger CreateLogger(string categoryName) =>
		_loggers.GetOrAdd(categoryName, name => new SystemLogger(name, _options, _channel.Writer, _time, _scopeProvider));

	// LoggerFactory calls this once at startup (before most loggers are typically requested),
	// but don't rely on that ordering: propagate to any SystemLogger this provider already
	// cached, so a category resolved earlier still picks up scopes going forward.
	public void SetScopeProvider(IExternalScopeProvider scopeProvider)
	{
		_scopeProvider = scopeProvider;
		foreach (var logger in _loggers.Values)
			logger.SetScopeProvider(scopeProvider);
	}

	public void Dispose() => _channel.Writer.TryComplete();
}
