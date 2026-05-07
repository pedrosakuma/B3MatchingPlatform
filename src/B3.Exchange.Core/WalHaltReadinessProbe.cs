namespace B3.Exchange.Core;

/// <summary>
/// Issue #286: readiness probe that reports NOT_READY as soon as
/// any of the channels passed at construction time has been
/// WAL-halted (<see cref="ChannelDispatcher.IsWalHealthy"/>
/// returned <c>false</c>). The host registers one of these per
/// HTTP server when at least one channel runs with
/// <see cref="WalAppendFailurePolicy.Halt"/>.
///
/// <para>The probe is read-only — the underlying flag is set
/// inside the dispatch loop on the first failed
/// <c>WAL.Append</c>. There is no path back to ready short of a
/// host restart, so a load balancer that observes the probe
/// flipping to NOT_READY drains the host while the operator
/// resolves the storage fault.</para>
/// </summary>
public sealed class WalHaltReadinessProbe : IReadinessProbe
{
    private readonly IReadOnlyList<ChannelDispatcher> _dispatchers;

    public WalHaltReadinessProbe(IReadOnlyList<ChannelDispatcher> dispatchers, string name = "wal-halt")
    {
        _dispatchers = dispatchers ?? throw new ArgumentNullException(nameof(dispatchers));
        Name = name;
    }

    public string Name { get; }

    public bool IsReady
    {
        get
        {
            for (int i = 0; i < _dispatchers.Count; i++)
            {
                if (!_dispatchers[i].IsWalHealthy) return false;
            }
            return true;
        }
    }
}
