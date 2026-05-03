using System.Net;
using B3.Exchange.ScenarioReplay;
using B3.Exchange.SyntheticTrader;

if (args.Length == 1 && (args[0] == "-h" || args[0] == "--help"))
{
    PrintUsage();
    return 0;
}

var opts = CliOptions.Parse(args, out var error);
if (opts == null)
{
    Console.Error.WriteLine($"error: {error}");
    PrintUsage();
    return 2;
}

void Info(string s) => Console.WriteLine($"[{DateTime.UtcNow:HH:mm:ss.fff}Z] {s}");
void Warn(string s) => Console.Error.WriteLine($"[{DateTime.UtcNow:HH:mm:ss.fff}Z] WARN {s}");

IReadOnlyList<ScriptEvent> events;
try { events = ScriptParser.ParseFile(opts.ScriptPath); }
catch (Exception ex)
{
    Warn($"failed to parse script '{opts.ScriptPath}': {ex.Message}");
    return 3;
}
Info($"loaded {events.Count} events from {opts.ScriptPath}");

using var capture = opts.OutPath == null
    ? new CaptureWriter(TextWriter.Null)
    : CaptureWriter.OpenFile(opts.OutPath);

await using var mcast = opts.MulticastGroup != null
    ? MulticastCapture.Start(IPAddress.Parse(opts.MulticastGroup), opts.MulticastPort, capture, logWarn: Warn)
    : null;

using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; shutdown.Cancel(); };

EntryPointClient client;
try
{
    client = await EntryPointClient.ConnectAsync(opts.Host, opts.Port, logDebug: null, logWarn: Warn, shutdown.Token);
}
catch (Exception ex)
{
    Warn($"connect failed: {ex.Message}");
    return 4;
}

await using (client)
{
    var clock = new SystemClock(opts.Speed);
    var runner = new ReplayRunner(client, clock, capture, Info, Warn);
    try
    {
        await runner.RunAsync(events, shutdown.Token);
    }
    catch (OperationCanceledException)
    {
        Warn("cancelled");
        return 1;
    }
    catch (Exception ex)
    {
        Warn($"script aborted: {ex.Message}");
        return 1;
    }

    if (opts.TrailingTimeoutMs > 0)
    {
        Info($"draining trailing ER frames for {opts.TrailingTimeoutMs}ms");
        try { await Task.Delay(opts.TrailingTimeoutMs, shutdown.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
    }
}

Info($"done; submitted={(args.Length > 0 ? "<see capture>" : "0")}");
return 0;

static void PrintUsage()
{
    Console.Error.WriteLine("""
        usage: B3.Exchange.ScenarioReplay --script <path.jsonl> [options]

        required:
          --script <path>             JSONL replay script

        connection:
          --host <ip|hostname>        EntryPoint host (default 127.0.0.1)
          --port <n>                  EntryPoint port (default 9876)

        capture:
          --out <path>                Tape file (JSONL); default: discard
          --multicast <group:port>    UMDF incremental multicast to record

        timing:
          --speed <multiplier>        Replay speed multiplier (default 1.0)
          --timeout-ms <n>            ms to wait for trailing ER frames after
                                      the last script event (default 1000)

        Exit codes: 0 ok, 1 aborted, 2 usage, 3 script-parse, 4 connect.
        """);
}

internal sealed class CliOptions
{
    public string Host { get; private set; } = "127.0.0.1";
    public int Port { get; private set; } = 9876;
    public string ScriptPath { get; private set; } = "";
    public string? OutPath { get; private set; }
    public string? MulticastGroup { get; private set; }
    public int MulticastPort { get; private set; }
    public double Speed { get; private set; } = 1.0;
    public int TrailingTimeoutMs { get; private set; } = 1000;

    public static CliOptions? Parse(string[] args, out string? error)
    {
        var o = new CliOptions();
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--host": o.Host = Need(args, ref i); break;
                case "--port": o.Port = int.Parse(Need(args, ref i)); break;
                case "--script": o.ScriptPath = Need(args, ref i); break;
                case "--out": o.OutPath = Need(args, ref i); break;
                case "--multicast":
                    {
                        var v = Need(args, ref i);
                        var idx = v.LastIndexOf(':');
                        if (idx < 0) { error = $"invalid --multicast '{v}', expected group:port"; return null; }
                        o.MulticastGroup = v.Substring(0, idx);
                        o.MulticastPort = int.Parse(v.Substring(idx + 1));
                        break;
                    }
                case "--speed": o.Speed = double.Parse(Need(args, ref i), System.Globalization.CultureInfo.InvariantCulture); break;
                case "--timeout-ms": o.TrailingTimeoutMs = int.Parse(Need(args, ref i)); break;
                default:
                    error = $"unknown argument '{args[i]}'";
                    return null;
            }
        }
        if (string.IsNullOrEmpty(o.ScriptPath)) { error = "--script is required"; return null; }
        if (o.Speed <= 0) { error = "--speed must be > 0"; return null; }
        if (!File.Exists(o.ScriptPath)) { error = $"script not found: {o.ScriptPath}"; return null; }
        error = null;
        return o;
    }

    private static string Need(string[] args, ref int i)
    {
        if (i + 1 >= args.Length) throw new ArgumentException($"missing value after '{args[i]}'");
        i++;
        return args[i];
    }
}
