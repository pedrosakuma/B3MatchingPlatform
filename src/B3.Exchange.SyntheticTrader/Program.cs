using B3.Exchange.SyntheticTrader;

if (args.Length == 0)
{
    Console.Error.WriteLine("usage: B3.Exchange.SyntheticTrader <path-to-synthetic-trader.json>");
    return 2;
}

SyntheticTraderConfig cfg;
try
{
    cfg = SyntheticTraderConfigLoader.Load(args[0]);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"failed to load config '{args[0]}': {ex.Message}");
    return 3;
}

void Info(string s) => Console.WriteLine($"[{DateTime.UtcNow:HH:mm:ss.fff}Z] {s}");
void Warn(string s) => Console.Error.WriteLine($"[{DateTime.UtcNow:HH:mm:ss.fff}Z] WARN {s}");
void Debug(string s)
{
    if (Environment.GetEnvironmentVariable("SYNTH_TRADER_DEBUG") == "1")
        Console.WriteLine($"[{DateTime.UtcNow:HH:mm:ss.fff}Z] DEBUG {s}");
}

using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; shutdown.Cancel(); };
AppDomain.CurrentDomain.ProcessExit += (_, _) => shutdown.Cancel();

Info($"connecting to {cfg.Host.Host}:{cfg.Host.Port}");
EntryPointClient client;
try
{
    if (cfg.Fixp != null)
    {
        var fixpOptions = new B3.Exchange.SyntheticTrader.Fixp.FixpClientOptions
        {
            SessionId = cfg.Fixp.SessionId,
            EnteringFirm = cfg.Firm,
            AccessKey = cfg.Fixp.AccessKey,
            KeepAliveIntervalMillis = cfg.Fixp.KeepAliveIntervalMs,
            CancelOnDisconnect = cfg.Fixp.CancelOnDisconnect,
            RetransmitOnGap = cfg.Fixp.RetransmitOnGap,
            ClientAppName = cfg.Fixp.ClientAppName,
            ClientAppVersion = cfg.Fixp.ClientAppVersion,
        };
        Info($"FIXP handshake sessionId={cfg.Fixp.SessionId} firm={cfg.Firm} keepAlive={cfg.Fixp.KeepAliveIntervalMs}ms cod={cfg.Fixp.CancelOnDisconnect}");
        client = await EntryPointClient.ConnectAsync(cfg.Host.Host, cfg.Host.Port, fixpOptions, Debug, Warn, shutdown.Token);
        Info("FIXP session established");
    }
    else
    {
        client = await EntryPointClient.ConnectAsync(cfg.Host.Host, cfg.Host.Port, Debug, Warn, shutdown.Token);
    }
}
catch (Exception ex)
{
    Warn($"connect failed: {ex.Message}");
    return 4;
}

var instruments = cfg.Instruments.Select(ic =>
    (cfg: ic, strategies: (IReadOnlyList<IStrategy>)ic.Strategies
        .Select(s => SyntheticTraderConfigLoader.BuildStrategy(s, cfg.TickIntervalMs)).ToList()));

var seed = cfg.Seed != 0 ? cfg.Seed : Environment.TickCount;
var rng = new Random(seed);

await using var runner = new SyntheticTraderRunner(client, instruments, rng,
    TimeSpan.FromMilliseconds(Math.Max(1, cfg.TickIntervalMs)),
    Info, Warn, shutdown.Token);
runner.Start();

Info($"synthetic trader running; seed={seed}; instruments={cfg.Instruments.Count}; Ctrl+C to stop");
try { await Task.Delay(Timeout.Infinite, shutdown.Token).ConfigureAwait(false); }
catch (OperationCanceledException) { }

Info($"shutting down (ticks={runner.TicksRun} sent={runner.OrdersSent} trades={runner.TradesObserved})");
await client.DisposeAsync().ConfigureAwait(false);
return 0;
