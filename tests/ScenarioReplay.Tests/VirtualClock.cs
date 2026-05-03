namespace B3.Exchange.ScenarioReplay.Tests;

internal sealed class VirtualClock : IClock
{
    public long ElapsedMs { get; set; }
    public List<long> WaitTargets { get; } = new();

    public Task DelayUntilAsync(long targetMs, CancellationToken ct)
    {
        WaitTargets.Add(targetMs);
        if (targetMs > ElapsedMs) ElapsedMs = targetMs;
        return Task.CompletedTask;
    }
}
