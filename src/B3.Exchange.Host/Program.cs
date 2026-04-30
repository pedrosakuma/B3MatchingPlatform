using B3.Exchange.Host;

if (args.Length == 0)
{
    Console.Error.WriteLine("usage: B3.Exchange.Host <path-to-exchange-simulator.json>");
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
    Console.Error.WriteLine($"failed to load config '{configPath}': {ex.Message}");
    return 3;
}

void Log(string s) => Console.WriteLine($"[{DateTime.UtcNow:HH:mm:ss.fff}Z] {s}");

await using var host = new ExchangeHost(cfg, Log);
await host.StartAsync().ConfigureAwait(false);

using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; shutdown.Cancel(); };
AppDomain.CurrentDomain.ProcessExit += (_, _) => shutdown.Cancel();

Log("exchange running; Ctrl+C to stop");
try { await Task.Delay(Timeout.Infinite, shutdown.Token).ConfigureAwait(false); }
catch (OperationCanceledException) { }

Log("shutting down");
return 0;
