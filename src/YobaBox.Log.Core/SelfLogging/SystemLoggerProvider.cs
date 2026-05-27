using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using YobaBox.Log.Core.Models;

namespace YobaBox.Log.Core.SelfLogging;

public sealed class SystemLoggerProvider : ILoggerProvider
{
	readonly SystemLoggerOptions _options;
	readonly TimeProvider _time;
	readonly Channel<LogEntryCandidate> _channel;
	readonly ConcurrentDictionary<string, SystemLogger> _loggers = new(StringComparer.Ordinal);

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
		_loggers.GetOrAdd(categoryName, name => new SystemLogger(name, _options, _channel.Writer, _time));

	public void Dispose() => _channel.Writer.TryComplete();
}
