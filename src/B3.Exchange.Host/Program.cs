using B3.Exchange.Host;
using Microsoft.Extensions.Logging;

using var loggerFactory = LoggerFactory.Create(builder =>
{
    var format = Environment.GetEnvironmentVariable("LOG_FORMAT");
    if (string.Equals(format, "json", StringComparison.OrdinalIgnoreCase))
    {
        builder.AddJsonConsole(o =>
        {
            o.IncludeScopes = true;
            o.UseUtcTimestamp = true;
            o.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";
        });
    }
    else
    {
        builder.AddSimpleConsole(o =>
        {
            o.IncludeScopes = true;
            o.UseUtcTimestamp = true;
            o.TimestampFormat = "HH:mm:ss.fff ";
            o.SingleLine = true;
        });
    }

    var levelEnv = Environment.GetEnvironmentVariable("LOG_LEVEL");
    if (!string.IsNullOrEmpty(levelEnv) && Enum.TryParse<LogLevel>(levelEnv, ignoreCase: true, out var lvl))
        builder.SetMinimumLevel(lvl);
    else
        builder.SetMinimumLevel(LogLevel.Information);
});

var bootLogger = loggerFactory.CreateLogger("B3.Exchange.Host.Program");

if (args.Length == 0)
{
    bootLogger.LogError("usage: B3.Exchange.Host <path-to-exchange-simulator.json>");
    return 2;
}

var configPath = args[0];
HostConfig cfg;
try
{
    cfg = HostConfigLoader.Load(configPath);
}
catch (Exception ex)
{
    bootLogger.LogError(ex, "failed to load config '{ConfigPath}'", configPath);
    return 3;
}

await using var host = new ExchangeHost(cfg, loggerFactory);
await host.StartAsync().ConfigureAwait(false);

using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; shutdown.Cancel(); };
AppDomain.CurrentDomain.ProcessExit += (_, _) => shutdown.Cancel();

bootLogger.LogInformation("exchange running; Ctrl+C to stop");
try { await Task.Delay(Timeout.Infinite, shutdown.Token).ConfigureAwait(false); }
catch (OperationCanceledException) { }

bootLogger.LogInformation("shutting down");
// Bound the graceful shutdown so a stuck phase can't pin the process
// indefinitely. The host's per-phase logging makes any breach visible.
using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
try { await host.StopAsync(stopCts.Token).ConfigureAwait(false); }
catch (Exception ex) { bootLogger.LogError(ex, "graceful shutdown failed"); }
return 0;
