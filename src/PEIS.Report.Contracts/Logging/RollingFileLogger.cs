using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace PEIS.Report.Contracts.Logging;

public sealed class RollingFileLoggerOptions
{
    public string LogDirectory { get; set; } = "logs";
    public string FilePrefix { get; set; } = "app";
    public int RetentionDays { get; set; } = 30;
    public LogLevel MinLevel { get; set; } = LogLevel.Information;
}

public sealed class RollingFileLoggerProvider : ILoggerProvider, IAsyncDisposable
{
    private readonly RollingFileLoggerOptions _options;
    private readonly Channel<string> _channel;
    private readonly CancellationTokenSource _cts;
    private readonly Task _writerTask;
    private bool _disposed;

    public RollingFileLoggerProvider(RollingFileLoggerOptions options)
    {
        _options = options ?? new RollingFileLoggerOptions();
        _channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
        _cts = new CancellationTokenSource();
        _writerTask = Task.Run(ProcessLogQueueAsync);
    }

    public ILogger CreateLogger(string categoryName)
    {
        return new RollingFileLogger(categoryName, this, _options);
    }

    internal void Enqueue(string formattedLine)
    {
        if (!_disposed)
        {
            _channel.Writer.TryWrite(formattedLine);
        }
    }

    private async Task ProcessLogQueueAsync()
    {
        var token = _cts.Token;
        var reader = _channel.Reader;

        try
        {
            while (await reader.WaitToReadAsync(token).ConfigureAwait(false))
            {
                while (reader.TryRead(out var logMessage))
                {
                    try
                    {
                        WriteToFile(logMessage);
                    }
                    catch
                    {
                        // Fallback to ignore disk failure to never crash the host
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            while (reader.TryRead(out var logMessage))
            {
                try
                {
                    WriteToFile(logMessage);
                }
                catch
                {
                    // Fallback to ignore disk failure
                }
            }
        }
    }

    private void WriteToFile(string message)
    {
        var dir = Path.IsPathRooted(_options.LogDirectory)
            ? _options.LogDirectory
            : Path.Combine(AppContext.BaseDirectory, _options.LogDirectory);

        if (!Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var today = DateTime.Now.ToString("yyyy-MM-dd");
        var fileName = $"{_options.FilePrefix}-{today}.log";
        var filePath = Path.Combine(dir, fileName);

        using var stream = new FileStream(filePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        using var writer = new StreamWriter(stream, Encoding.UTF8);
        writer.WriteLine(message);
    }

    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _channel.Writer.TryComplete();
        _cts.Cancel();

        try
        {
            await _writerTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _cts.Dispose();
        }
    }
}

internal sealed class RollingFileLogger(string category, RollingFileLoggerProvider provider, RollingFileLoggerOptions options) : ILogger
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= options.MinLevel;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;

        var message = formatter(state, exception);
        if (string.IsNullOrEmpty(message) && exception == null) return;

        var now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
        var levelStr = logLevel switch
        {
            LogLevel.Trace => "TRACE",
            LogLevel.Debug => "DEBUG",
            LogLevel.Information => "INFO ",
            LogLevel.Warning => "WARN ",
            LogLevel.Error => "ERROR",
            LogLevel.Critical => "FATAL",
            _ => "INFO "
        };

        var sb = new StringBuilder();
        sb.Append('[').Append(now).Append("] [").Append(levelStr).Append("] [").Append(category).Append("] ").Append(message);

        if (exception != null)
        {
            sb.AppendLine().Append(exception);
        }

        provider.Enqueue(sb.ToString());
    }
}

public static class RollingFileLoggerExtensions
{
    public static ILoggingBuilder AddRollingFile(this ILoggingBuilder builder, Action<RollingFileLoggerOptions>? configure = null)
    {
        var options = new RollingFileLoggerOptions();
        configure?.Invoke(options);

        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<ILoggerProvider>(new RollingFileLoggerProvider(options)));
        builder.Services.AddSingleton(options);
        builder.Services.AddHostedService<LogArchiveWorker>();
        return builder;
    }
}
