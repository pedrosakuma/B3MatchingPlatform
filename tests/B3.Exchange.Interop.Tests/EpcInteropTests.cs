using System.Net;
using B3.EntryPoint.Client;
using B3.EntryPoint.Client.Auth;
using B3.EntryPoint.Client.Models;
using B3.Exchange.Core;
using B3.Exchange.Host;
using Microsoft.Extensions.Logging;

namespace B3.Exchange.Interop.Tests;

/// <summary>
/// Smoke-tests the matching gateway against the real B3.EntryPoint.Client
/// SDK. Each test stands up an <see cref="ExchangeHost"/> on a loopback
/// TCP port with an in-memory UMDF sink and drives one happy-path scenario
/// using the real client's wire emission, asserting the expected
/// <see cref="EntryPointEvent"/>s round-trip back. Issue #243.
/// </summary>
public class EpcInteropTests : IAsyncLifetime
{
    private const long Petr = 900_000_000_001L;
    private const string SessionId = "100";
    private const uint EnteringFirm = 7;

    private sealed class CountingSink : IUmdfPacketSink
    {
        public int Count;
        public void Publish(byte channelNumber, ReadOnlySpan<byte> packet) => Interlocked.Increment(ref Count);
    }

    private static string ResolveRepoFile(string relPath)
    {
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && dir != null; i++)
        {
            var candidate = Path.Combine(dir, relPath);
            if (File.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        throw new FileNotFoundException($"could not locate {relPath} from {AppContext.BaseDirectory}");
    }

    private ExchangeHost _host = null!;
    private IPEndPoint _endpoint = null!;
    private static int s_portSeed = 30_300;

    private readonly List<string> _hostLogs = new();

    private sealed class CapturingProvider : ILoggerProvider
    {
        private readonly List<string> _sink;
        public CapturingProvider(List<string> sink) => _sink = sink;
        public ILogger CreateLogger(string categoryName) => new Capturing(categoryName, _sink);
        public void Dispose() { }
        private sealed class Capturing : ILogger
        {
            private readonly string _cat; private readonly List<string> _sink;
            public Capturing(string cat, List<string> sink) { _cat = cat; _sink = sink; }
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel l) => true;
            public void Log<TState>(LogLevel l, EventId id, TState s, Exception? ex, Func<TState, Exception?, string> fmt)
            { lock (_sink) _sink.Add($"[{l}] {_cat}: {fmt(s, ex)}{(ex != null ? " | " + ex.Message : "")}"); }
        }
    }

    public async Task InitializeAsync()
    {
        var port = Interlocked.Increment(ref s_portSeed);
        var hostCfg = new HostConfig
        {
            Auth = new AuthConfig { RequireFixpHandshake = true, DevMode = true },
            Tcp = new TcpConfig { Listen = "127.0.0.1:0", EnteringFirm = EnteringFirm },
            Firms = new()
            {
                new FirmConfig { Id = "firmA", Name = "Firm A", EnteringFirmCode = EnteringFirm },
            },
            Sessions = new()
            {
                new SessionConfig { SessionId = SessionId, FirmId = "firmA" },
            },
            Channels =
            {
                new ChannelConfig
                {
                    ChannelNumber = 84,
                    IncrementalGroup = "239.255.43.84",
                    IncrementalPort = port,
                    Ttl = 0,
                    InstrumentsFile = ResolveRepoFile("config/instruments-eqt.json"),
                },
            },
        };
        _host = new ExchangeHost(hostCfg,
            loggerFactory: LoggerFactory.Create(b => { b.SetMinimumLevel(LogLevel.Trace); b.AddProvider(new CapturingProvider(_hostLogs)); }),
            packetSinkFactory: _ => new CountingSink());
        await _host.StartAsync();
        _endpoint = _host.TcpEndpoint!;
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private EntryPointClient BuildClient(uint sessionVerId, uint nextSeqNo = 1)
    {
        var json = $"{{\"auth_type\":\"basic\",\"username\":\"{SessionId}\",\"access_key\":\"\"}}";
        var client = new EntryPointClient(new EntryPointClientOptions
        {
            Endpoint = _endpoint,
            Credentials = Credentials.FromUtf8(json),
            SessionId = uint.Parse(SessionId),
            SessionVerId = sessionVerId,
            EnteringFirm = EnteringFirm,
            EnteringTrader = "TRDR",
            ClientAppName = "B3.Exchange.Interop.Tests",
            ClientAppVersion = "1.0.0",
            ClientIP = "127.0.0.1",
            SenderLocation = "TEST",
            DefaultMarketSegmentId = 1,
            Profile = SessionProfile.OrderEntry,
            HandshakeTimeout = TimeSpan.FromSeconds(5),
            KeepAliveIntervalMs = 5_000,
            Logger = LoggerFactory.Create(b => { b.SetMinimumLevel(LogLevel.Trace); b.AddProvider(new CapturingProvider(_clientLogs)); }).CreateLogger("epc"),
        });
        client.Terminated += (_, e) => _terminations.Add(e.Code.ToString() + ":" + e.Reason);
        return client;
    }

    private readonly List<string> _terminations = new();
    private readonly List<string> _clientLogs = new();

    private static async Task<List<EntryPointEvent>> CollectAsync(
        EntryPointClient client, Func<List<EntryPointEvent>, bool> stop, TimeSpan timeout, CancellationToken ct)
    {
        var collected = new List<EntryPointEvent>();
        using var deadlineCts = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadlineCts.Token);
        try
        {
            await foreach (var evt in client.Events(linked.Token).ConfigureAwait(false))
            {
                lock (collected) collected.Add(evt);
                if (stop(collected)) break;
            }
        }
        catch (OperationCanceledException) { }
        return collected;
    }

    [Fact(Skip = "Blocked by #248 — gateway ER schema version mismatch (EPC SDK reads V5/V6 layout, gateway emits V3)")]
    public async Task SimpleNewOrder_Submit_Cancel_RoundTrips()
    {
        var sessionVerId = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await using var client = BuildClient(sessionVerId);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await client.ConnectAsync(cts.Token);

        var events = new List<EntryPointEvent>();
        var pump = Task.Run(async () =>
        {
            try
            {
                await foreach (var evt in client.Events(cts.Token))
                {
                    lock (events) events.Add(evt);
                }
            }
            catch (OperationCanceledException) { }
        });

        var clOrd = await client.SubmitSimpleAsync(new SimpleNewOrderRequest
        {
            ClOrdID = new ClOrdID(1),
            SecurityId = (ulong)Petr,
            Side = Side.Buy,
            OrderType = SimpleOrderType.Limit,
            TimeInForce = SimpleTimeInForce.Day,
            OrderQty = 100,
            Price = 32.0m,
        }, cts.Token);

        await WaitFor(events, l => l.OfType<OrderAccepted>().Any(), cts.Token);

        await client.CancelAsync(new CancelOrderRequest
        {
            ClOrdID = new ClOrdID(2),
            OrigClOrdID = clOrd,
            SecurityId = (ulong)Petr,
            Side = Side.Buy,
        }, cts.Token);

        await WaitFor(events, l => l.OfType<OrderCancelled>().Any(), cts.Token);

        cts.Cancel();
        await pump;

        Assert.Contains(events, e => e is OrderAccepted oa && oa.ClOrdID.Value == 1);
        Assert.Contains(events, e => e is OrderCancelled oc && oc.OrigClOrdID?.Value == 1);
    }

    [Fact(Skip = "Blocked by #248 — gateway ER schema version mismatch (EPC SDK reads V5/V6 layout, gateway emits V3)")]
    public async Task NewOrderSingleV6_Submit_Replace_Cancel_RoundTrips()
    {
        // Drives template 102 (NewOrderSingle V6) + template 104
        // (OrderCancelReplaceRequest V6) — the SDK 0.8.0+ wire shape that
        // produced #236 / #239 / #241. Asserts the round-trip works
        // end-to-end against the matching gateway.
        var sessionVerId = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await using var client = BuildClient(sessionVerId);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await client.ConnectAsync(cts.Token);

        var events = new List<EntryPointEvent>();
        var pump = Task.Run(async () =>
        {
            try
            {
                await foreach (var evt in client.Events(cts.Token))
                {
                    lock (events) events.Add(evt);
                }
            }
            catch (OperationCanceledException) { }
        });

        var clOrd = await client.SubmitAsync(new NewOrderRequest
        {
            ClOrdID = new ClOrdID(10),
            SecurityId = (ulong)Petr,
            Side = Side.Buy,
            OrderType = OrderType.Limit,
            TimeInForce = TimeInForce.Day,
            OrderQty = 200,
            Price = 31.50m,
        }, cts.Token);

        await WaitFor(events, l => l.OfType<OrderAccepted>().Any(e => e.ClOrdID.Value == 10), cts.Token);

        var replaced = await client.ReplaceAsync(new ReplaceOrderRequest
        {
            ClOrdID = new ClOrdID(11),
            OrigClOrdID = clOrd,
            SecurityId = (ulong)Petr,
            Side = Side.Buy,
            OrderType = OrderType.Limit,
            TimeInForce = TimeInForce.Day,
            OrderQty = 150,
            Price = 31.40m,
        }, cts.Token);

        await WaitFor(events, l => l.OfType<OrderModified>().Any(), cts.Token);

        await client.CancelAsync(new CancelOrderRequest
        {
            ClOrdID = new ClOrdID(12),
            OrigClOrdID = replaced,
            SecurityId = (ulong)Petr,
            Side = Side.Buy,
        }, cts.Token);

        await WaitFor(events, l => l.OfType<OrderCancelled>().Any(), cts.Token);

        cts.Cancel();
        await pump;

        Assert.Contains(events, e => e is OrderAccepted oa && oa.ClOrdID.Value == 10);
        Assert.Contains(events, e => e is OrderModified om && om.ClOrdID.Value == 11);
        Assert.Contains(events, e => e is OrderCancelled);
    }

    [Fact(Skip = "Blocked by #248 — gateway ER schema version mismatch (EPC SDK reads V5/V6 layout, gateway emits V3)")]
    public async Task SimpleModify_Submit_Modify_RoundTrips()
    {
        var sessionVerId = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await using var client = BuildClient(sessionVerId);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await client.ConnectAsync(cts.Token);

        var events = new List<EntryPointEvent>();
        var pump = Task.Run(async () =>
        {
            try
            {
                await foreach (var evt in client.Events(cts.Token))
                {
                    lock (events) events.Add(evt);
                }
            }
            catch (OperationCanceledException) { }
        });

        var clOrd = await client.SubmitSimpleAsync(new SimpleNewOrderRequest
        {
            ClOrdID = new ClOrdID(20),
            SecurityId = (ulong)Petr,
            Side = Side.Buy,
            OrderType = SimpleOrderType.Limit,
            TimeInForce = SimpleTimeInForce.Day,
            OrderQty = 100,
            Price = 32.0m,
        }, cts.Token);

        await WaitFor(events, l => l.OfType<OrderAccepted>().Any(), cts.Token);

        await client.ReplaceSimpleAsync(new SimpleModifyRequest
        {
            ClOrdID = new ClOrdID(21),
            OrigClOrdID = clOrd,
            SecurityId = (ulong)Petr,
            Side = Side.Buy,
            OrderType = SimpleOrderType.Limit,
            TimeInForce = SimpleTimeInForce.Day,
            OrderQty = 80,
            Price = 31.95m,
        }, cts.Token);

        await WaitFor(events, l => l.OfType<OrderModified>().Any(), cts.Token);

        cts.Cancel();
        await pump;

        Assert.Contains(events, e => e is OrderModified om && om.ClOrdID.Value == 21);
    }

    [Fact(Skip = "Blocked by #248 — gateway ER schema version mismatch (EPC SDK reads V5/V6 layout, gateway emits V3)")]
    public async Task MassAction_CancelAllOrdersForSession_AffectsRestingOrders()
    {
        var sessionVerId = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await using var client = BuildClient(sessionVerId);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await client.ConnectAsync(cts.Token);

        var events = new List<EntryPointEvent>();
        var pump = Task.Run(async () =>
        {
            try
            {
                await foreach (var evt in client.Events(cts.Token))
                {
                    lock (events) events.Add(evt);
                }
            }
            catch (OperationCanceledException) { }
        });

        for (ulong i = 30; i < 33; i++)
        {
            await client.SubmitSimpleAsync(new SimpleNewOrderRequest
            {
                ClOrdID = new ClOrdID(i),
                SecurityId = (ulong)Petr,
                Side = Side.Buy,
                OrderType = SimpleOrderType.Limit,
                TimeInForce = SimpleTimeInForce.Day,
                OrderQty = 50,
                Price = 31.00m + (decimal)(i - 30) * 0.01m,
            }, cts.Token);
        }

        await WaitFor(events, l => l.OfType<OrderAccepted>().Count() >= 3, cts.Token);

        var report = await client.MassActionAsync(new MassActionRequest
        {
            ClOrdID = new ClOrdID(40),
            ActionType = MassActionType.CancelOrders,
            Scope = MassActionScope.AllOrdersForATradingSession,
            MarketSegment = "1",
        }, cts.Token);

        Assert.Equal(MassActionResponse.Accepted, report.Response);

        cts.Cancel();
        await pump;
    }

    [Fact(Skip = "Blocked by #248 — gateway ER schema version mismatch (EPC SDK reads V5/V6 layout, gateway emits V3)")]
    public async Task Reconnect_WithNonZeroNextSeqNo_DoesNotTriggerNotApplied()
    {
        // Regression for #239(b): client reconnecting with NextSeqNo > 1
        // must not produce a "NotApplied" / inbound-gap-at-reconnect on
        // the server side. We submit a single order on the first
        // connection, then disconnect and re-establish with NextSeqNo
        // bumped to (last + 1) and submit another order.
        var sessionVerId = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // First connection: submit one order, capture how many outbound
        // app-frames the SDK sent so we can advance the seqno on the
        // re-connect (the SDK manages this internally via its
        // SessionStateStore, but recreating the client without a shared
        // store forces us to use ReconnectAsync, which resumes from the
        // SDK's last known sequence).
        await using (var client = BuildClient(sessionVerId))
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await client.ConnectAsync(cts.Token);

            var events = new List<EntryPointEvent>();
            var pump = Task.Run(async () =>
            {
                try
                {
                    await foreach (var evt in client.Events(cts.Token))
                    {
                        lock (events) events.Add(evt);
                    }
                }
                catch (OperationCanceledException) { }
            });

            await client.SubmitSimpleAsync(new SimpleNewOrderRequest
            {
                ClOrdID = new ClOrdID(50),
                SecurityId = (ulong)Petr,
                Side = Side.Buy,
                OrderType = SimpleOrderType.Limit,
                TimeInForce = SimpleTimeInForce.Day,
                OrderQty = 100,
                Price = 30.0m,
            }, cts.Token);

            await WaitFor(events, l => l.OfType<OrderAccepted>().Any(), cts.Token);

            // Reconnect on the same EntryPointClient instance: the SDK
            // re-establishes with the next outbound seq it has been
            // tracking (must be > 1 since we already sent one frame).
            await client.ReconnectAsync(0u, cts.Token);

            await client.SubmitSimpleAsync(new SimpleNewOrderRequest
            {
                ClOrdID = new ClOrdID(51),
                SecurityId = (ulong)Petr,
                Side = Side.Buy,
                OrderType = SimpleOrderType.Limit,
                TimeInForce = SimpleTimeInForce.Day,
                OrderQty = 100,
                Price = 30.01m,
            }, cts.Token);

            await WaitFor(events, l => l.OfType<OrderAccepted>().Count(e => e.ClOrdID.Value == 51) > 0, cts.Token);

            cts.Cancel();
            await pump;

            Assert.Contains(events, e => e is OrderAccepted oa && oa.ClOrdID.Value == 50);
            Assert.Contains(events, e => e is OrderAccepted oa && oa.ClOrdID.Value == 51);
        }
    }

    [Fact]
    public async Task Handshake_Succeeds_NoTermination()
    {
        // Smoke: real EPC SDK 0.14.0 negotiates + establishes a FIXP
        // session against our gateway and exits cleanly. This exercises
        // the full handshake (Negotiate / Establish / EndOfDay-style
        // teardown) end-to-end, but does NOT submit any business
        // messages, so it is unaffected by the ExecutionReport schema
        // mismatch tracked by #248.
        var sessionVerId = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await using var client = BuildClient(sessionVerId);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await client.ConnectAsync(cts.Token);

        // Drain any inbound frames briefly to surface a Terminated event
        // if one were to occur during/after handshake.
        var pump = Task.Run(async () =>
        {
            try
            {
                await foreach (var _ in client.Events(cts.Token))
                {
                }
            }
            catch (OperationCanceledException) { }
        });

        await Task.Delay(200, cts.Token);
        cts.Cancel();
        await pump;

        Assert.Empty(_terminations);
    }

    private async Task WaitFor(
        List<EntryPointEvent> events, Func<List<EntryPointEvent>, bool> predicate, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            bool ok;
            lock (events) ok = predicate(events);
            if (ok) return;
            await Task.Delay(20, ct).ConfigureAwait(false);
        }
        lock (events)
            throw new TimeoutException(
                "predicate not satisfied. captured: " +
                string.Join(", ", events.Select(e => e.GetType().Name)) +
                "\nterminations: " + string.Join(", ", _terminations) +
                "\n" + DumpHostLogs());
    }

    private string DumpHostLogs()
    {
        lock (_hostLogs) lock (_clientLogs) return "host logs:\n  " + string.Join("\n  ", _hostLogs) + "\nclient logs:\n  " + string.Join("\n  ", _clientLogs);
    }
}
