using System.Net;
using B3.Exchange.ScenarioReplay;
using B3.Exchange.SyntheticTrader;
using B3.Exchange.SyntheticTrader.Fixp;

if (args.Length == 1 && (args[0] == "-h" || args[0] == "--help"))
{
    PrintUsage();
    return 0;
}

if (args.Length >= 1 && args[0] == "diff")
{
    return RunDiff(args.AsSpan(1).ToArray());
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

// Resolve the session list. If --session was used we open one client per
// declared session and require all events to address a known one (or use
// the first declared session as the default for back-compat with scripts
// that omit the field). Without --session we fall back to a single
// implicit "default" session over the legacy raw frame protocol.
var sessions = new Dictionary<string, EntryPointClient>();
string defaultSession;
try
{
    if (opts.Sessions.Count == 0)
    {
        var legacy = await EntryPointClient.ConnectAsync(opts.Host, opts.Port, logDebug: null, logWarn: Warn, shutdown.Token);
        sessions["default"] = legacy;
        defaultSession = "default";
        Info($"connected legacy session to {opts.Host}:{opts.Port}");
    }
    else
    {
        foreach (var s in opts.Sessions)
        {
            var fixp = new FixpClientOptions
            {
                SessionId = s.SessionId,
                EnteringFirm = s.EnteringFirm,
                AccessKey = opts.AccessKeys.TryGetValue(s.Name, out var k) ? k : "",
                CancelOnDisconnect = false,
                RetransmitOnGap = true,
            };
            var client = await EntryPointClient.ConnectAsync(
                s.Host, s.Port, fixp, logDebug: null, logWarn: Warn, shutdown.Token);
            sessions[s.Name] = client;
            Info($"connected session '{s.Name}' (sessionId={s.SessionId} firm={s.EnteringFirm}) to {s.Host}:{s.Port}");
        }
        defaultSession = opts.Sessions[0].Name;
    }
}
catch (Exception ex)
{
    Warn($"connect failed: {ex.Message}");
    foreach (var c in sessions.Values)
    {
        try { await c.DisposeAsync(); } catch { }
    }
    return 4;
}

try
{
    var clock = new SystemClock(opts.Speed);
    var runner = new ReplayRunner(sessions, defaultSession, clock, capture, Info, Warn);
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
finally
{
    foreach (var c in sessions.Values)
    {
        try { await c.DisposeAsync(); } catch { }
    }
}

Info("done");
return 0;

static void PrintUsage()
{
    Console.Error.WriteLine("""
        usage: B3.Exchange.ScenarioReplay --script <path.jsonl> [options]

        required:
          --script <path>             JSONL replay script

        single-session connection (legacy, no FIXP handshake):
          --host <ip|hostname>        EntryPoint host (default 127.0.0.1)
          --port <n>                  EntryPoint port (default 9876)

        multi-session connection (FIXP handshake per session):
          --session <spec>            Repeatable. Spec format:
                                          name=sessionId:firm[@host:port]
                                      e.g. firmA=100:1@127.0.0.1:9876
                                      The first --session is the default
                                      target for events that omit "session".
          --access-key <name>=<key>   Repeatable. Access key for a named
                                      session; default is empty (works in
                                      auth.devMode=true hosts).

        capture:
          --out <path>                Tape file (JSONL); default: discard
          --multicast <group:port>    UMDF incremental multicast to record

        timing:
          --speed <multiplier>        Replay speed multiplier (default 1.0)
          --timeout-ms <n>            ms to wait for trailing ER frames after
                                      the last script event (default 1000)

        Exit codes: 0 ok, 1 aborted, 2 usage, 3 script-parse, 4 connect.

        diff subcommand:
          ScenarioReplay diff --baseline <path> --candidate <path>
                              [--ignore <field,field,...>] [--out <path>]

          Compares two replay tapes (JSONL) for regression testing.
          Default ignored fields: t, sendingTime. --ignore is additive
          (e.g. --ignore orderId tolerates engine-allocated IDs).
          Diff exit codes: 0 equivalent, 1 diverged, 2 malformed.
        """);
}

static int RunDiff(string[] args)
{
    string? baseline = null, candidate = null, outPath = null;
    var ignore = new HashSet<string>(StringComparer.Ordinal) { "t", "sendingTime" };
    for (int i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--baseline": baseline = NeedArg(args, ref i); break;
            case "--candidate": candidate = NeedArg(args, ref i); break;
            case "--out": outPath = NeedArg(args, ref i); break;
            case "--ignore":
                {
                    var v = NeedArg(args, ref i);
                    foreach (var f in v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        ignore.Add(f);
                    break;
                }
            case "-h":
            case "--help":
                Console.Error.WriteLine("usage: ScenarioReplay diff --baseline <path> --candidate <path> [--ignore f1,f2] [--out path]");
                return 2;
            default:
                Console.Error.WriteLine($"diff: unknown argument '{args[i]}'");
                return 2;
        }
    }
    if (string.IsNullOrEmpty(baseline) || string.IsNullOrEmpty(candidate))
    {
        Console.Error.WriteLine("diff: --baseline and --candidate are required");
        return 2;
    }
    if (!File.Exists(baseline)) { Console.Error.WriteLine($"diff: baseline not found: {baseline}"); return 2; }
    if (!File.Exists(candidate)) { Console.Error.WriteLine($"diff: candidate not found: {candidate}"); return 2; }

    List<TapeDiff.NormalisedRecord> b, c;
    try
    {
        b = TapeDiff.Load(baseline, ignore);
        c = TapeDiff.Load(candidate, ignore);
    }
    catch (FormatException fx)
    {
        Console.Error.WriteLine($"diff: malformed tape ({fx.Message})");
        return 2;
    }

    var result = TapeDiff.Compare(b, c);
    using TextWriter sink = outPath == null
        ? Console.Out
        : new StreamWriter(outPath) { NewLine = "\n" };
    TapeDiff.WriteReport(sink, result, baseline, candidate, ignore);
    sink.Flush();
    return result.IsEquivalent ? 0 : 1;

    static string NeedArg(string[] args, ref int i)
    {
        if (i + 1 >= args.Length) throw new ArgumentException($"missing value after '{args[i]}'");
        i++;
        return args[i];
    }
}

internal sealed class SessionSpec
{
    public string Name { get; init; } = "";
    public string SessionId { get; init; } = "";
    public uint EnteringFirm { get; init; }
    public string Host { get; init; } = "127.0.0.1";
    public int Port { get; init; } = 9876;

    /// <summary>
    /// Parses <c>name=sessionId:firm[@host:port]</c>.
    /// </summary>
    public static SessionSpec Parse(string raw, string defaultHost, int defaultPort)
    {
        var eq = raw.IndexOf('=');
        if (eq <= 0 || eq == raw.Length - 1)
            throw new ArgumentException($"invalid --session '{raw}': expected name=sessionId:firm[@host:port]");
        var name = raw.Substring(0, eq);
        var rhs = raw.Substring(eq + 1);
        string host = defaultHost;
        int port = defaultPort;
        var at = rhs.IndexOf('@');
        if (at >= 0)
        {
            var hp = rhs.Substring(at + 1);
            rhs = rhs.Substring(0, at);
            var colon = hp.LastIndexOf(':');
            if (colon < 0) throw new ArgumentException($"invalid --session '{raw}': host segment must be host:port");
            host = hp.Substring(0, colon);
            if (!int.TryParse(hp.AsSpan(colon + 1), out port))
                throw new ArgumentException($"invalid --session '{raw}': port is not an integer");
        }
        var sc = rhs.IndexOf(':');
        if (sc <= 0 || sc == rhs.Length - 1)
            throw new ArgumentException($"invalid --session '{raw}': credentials must be sessionId:firm");
        var sid = rhs.Substring(0, sc);
        if (!uint.TryParse(rhs.AsSpan(sc + 1), out var firm) || firm == 0)
            throw new ArgumentException($"invalid --session '{raw}': firm must be a positive uint32");
        if (!uint.TryParse(sid, out var sidNum) || sidNum == 0)
            throw new ArgumentException($"invalid --session '{raw}': sessionId must be a positive uint32 decimal string");
        return new SessionSpec { Name = name, SessionId = sid, EnteringFirm = firm, Host = host, Port = port };
    }
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
    public List<SessionSpec> Sessions { get; } = new();
    public Dictionary<string, string> AccessKeys { get; } = new(StringComparer.Ordinal);

    public static CliOptions? Parse(string[] args, out string? error)
    {
        var o = new CliOptions();
        var rawSessions = new List<string>();
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
                case "--session": rawSessions.Add(Need(args, ref i)); break;
                case "--access-key":
                    {
                        var v = Need(args, ref i);
                        var idx = v.IndexOf('=');
                        if (idx <= 0) { error = $"invalid --access-key '{v}', expected name=key"; return null; }
                        o.AccessKeys[v.Substring(0, idx)] = v.Substring(idx + 1);
                        break;
                    }
                default:
                    error = $"unknown argument '{args[i]}'";
                    return null;
            }
        }
        if (string.IsNullOrEmpty(o.ScriptPath)) { error = "--script is required"; return null; }
        if (o.Speed <= 0) { error = "--speed must be > 0"; return null; }
        if (!File.Exists(o.ScriptPath)) { error = $"script not found: {o.ScriptPath}"; return null; }
        try
        {
            foreach (var raw in rawSessions)
                o.Sessions.Add(SessionSpec.Parse(raw, o.Host, o.Port));
        }
        catch (ArgumentException ex)
        {
            error = ex.Message;
            return null;
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in o.Sessions)
        {
            if (!seen.Add(s.Name))
            {
                error = $"duplicate --session name '{s.Name}'";
                return null;
            }
        }
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
