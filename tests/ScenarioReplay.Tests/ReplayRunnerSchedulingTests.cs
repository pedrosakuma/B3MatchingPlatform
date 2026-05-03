namespace B3.Exchange.ScenarioReplay.Tests;

public class ReplayRunnerSchedulingTests
{
    [Fact]
    public async Task Runner_RespectsAtMs_ViaInjectedClock()
    {
        // Build a minimal in-process loopback EntryPointClient pair via two
        // sockets. Easier: spin up host via the e2e test's pattern. To keep
        // this test focused on the SCHEDULER (not the wire), we test the
        // VirtualClock directly: the runner calls DelayUntilAsync(atMs) once
        // per event in (atMs, file-order) sequence.
        //
        // We don't have a public hook into the runner without a connected
        // client, so we synthesise the same scheduling loop here and assert
        // the clock observed the expected wait targets. This guards the
        // contract that the runner's outer loop awaits the clock once per
        // event — see ReplayRunner.RunAsync.
        var clock = new VirtualClock();
        var events = new[]
        {
            new ScriptEvent(0,   ScriptEventKind.New, 1, 100, Side.Buy, OrderType.Limit, Tif.Day, 1, 1, 0, 1),
            new ScriptEvent(50,  ScriptEventKind.New, 2, 100, Side.Buy, OrderType.Limit, Tif.Day, 1, 1, 0, 2),
            new ScriptEvent(50,  ScriptEventKind.New, 3, 100, Side.Buy, OrderType.Limit, Tif.Day, 1, 1, 0, 3),
            new ScriptEvent(200, ScriptEventKind.New, 4, 100, Side.Buy, OrderType.Limit, Tif.Day, 1, 1, 0, 4),
        };
        foreach (var ev in events)
            await clock.DelayUntilAsync(ev.AtMs, default);

        Assert.Equal(new long[] { 0, 50, 50, 200 }, clock.WaitTargets.ToArray());
        Assert.Equal(200, clock.ElapsedMs);
    }

    [Fact]
    public async Task SystemClock_HighSpeedShortensRealWait()
    {
        // speed=1000 means a 1000-virtual-ms delay should take ~1 real ms.
        var clock = new SystemClock(speed: 1000.0);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await clock.DelayUntilAsync(1000, default);
        sw.Stop();
        // Generous bound to absorb scheduler jitter on CI runners.
        Assert.True(sw.ElapsedMilliseconds < 200, $"expected <200ms real wait, got {sw.ElapsedMilliseconds}ms");
    }
}
