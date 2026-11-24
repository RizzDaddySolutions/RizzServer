using RizzServer.Core.Api;
using System.Collections.Concurrent;
using RizzServer.Server;

namespace RizzServer.Service;

/// <summary>Keeps the last events in memory for GET /api/v1/log.</summary>
public sealed class LogBuffer : ILoggerProvider
{
    private const int Capacity = 1000;
    private readonly ConcurrentQueue<LogLine> _lines = new();

    public IReadOnlyList<LogLine> Snapshot() => _lines.ToArray();

    public ILogger CreateLogger(string categoryName) => new BufferLogger(this, categoryName);

    public void Dispose() { }

    private void Add(LogLine line)
    {
        _lines.Enqueue(line);
        while (_lines.Count > Capacity && _lines.TryDequeue(out _)) { }
    }

    private sealed class BufferLogger(LogBuffer owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        // Per-request chatter from ASP.NET would push the real events out of the buffer.
        public bool IsEnabled(LogLevel logLevel) =>
            logLevel >= (category.StartsWith("Microsoft.", StringComparison.Ordinal) ? LogLevel.Warning : LogLevel.Information);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var message = formatter(state, exception);
            if (exception is not null) message += " — " + exception.Message;
            owner.Add(new LogLine(DateTimeOffset.UtcNow, logLevel.ToString(), category, message));
        }
    }
}

/// <summary>Starts the runtime with the process and stops it on shutdown.</summary>
public sealed class GatewayHostedService(GatewayRuntime runtime, ServiceSettings settings, RedundancyManager redundancy, ILogger<GatewayHostedService> log) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (redundancy.Enabled)
        {
            // A redundant node always starts as standby and lets the heartbeat decide who is active.
            try { await redundancy.StartAsync(); }
            catch (Exception ex) { log.LogError("Runtime did not start: {Error}. The API is still available.", ex.Message); }
            return;
        }

        if (!settings.AutoStart)
        {
            log.LogInformation("AutoStart is off; start the runtime with POST /api/v1/runtime/start");
            return;
        }

        try { await runtime.StartAsync(); }
        catch (Exception ex)
        {
            // Keep the API up so the problem can be fixed remotely.
            log.LogError("Runtime did not start: {Error}. The API is still available.", ex.Message);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => runtime.StopAsync();
}
