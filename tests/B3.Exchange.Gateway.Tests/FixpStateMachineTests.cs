using B3.Exchange.Gateway;

namespace B3.Exchange.Gateway.Tests;

/// <summary>
/// Acceptance-matrix coverage for <see cref="FixpStateMachine"/>. One test
/// per (state, event) cell from <c>docs/B3-ENTRYPOINT-ARCHITECTURE.md</c> §5.
/// </summary>
public class FixpStateMachineTests
{
    // ── Idle row ──────────────────────────────────────────────────────────

    [Fact]
    public void Idle_Negotiate_Accepted_TransitionsToNegotiated()
    {
        var t = FixpStateMachine.Apply(FixpState.Idle, FixpEvent.Negotiate);
        Assert.Equal(FixpState.Negotiated, t.NewState);
        Assert.Equal(FixpAction.Accept, t.Action);
    }

    [Fact]
    public void Idle_Establish_NotApplied_StaysIdle()
    {
        var t = FixpStateMachine.Apply(FixpState.Idle, FixpEvent.Establish);
        Assert.Equal(FixpState.Idle, t.NewState);
        Assert.Equal(FixpAction.NotApplied, t.Action);
    }

    [Fact]
    public void Idle_ApplicationMessage_RejectsAndTerminatesUnnegotiated()
    {
        var t = FixpStateMachine.Apply(FixpState.Idle, FixpEvent.ApplicationMessage);
        Assert.Equal(FixpState.Terminated, t.NewState);
        Assert.Equal(FixpAction.RejectAndTerminateUnnegotiated, t.Action);
    }

    [Fact]
    public void Idle_Sequence_DroppedSilently()
    {
        var t = FixpStateMachine.Apply(FixpState.Idle, FixpEvent.Sequence);
        Assert.Equal(FixpState.Idle, t.NewState);
        Assert.Equal(FixpAction.DropSilently, t.Action);
    }

    [Fact]
    public void Idle_Terminate_Accepted_GoesTerminated()
    {
        var t = FixpStateMachine.Apply(FixpState.Idle, FixpEvent.Terminate);
        Assert.Equal(FixpState.Terminated, t.NewState);
        Assert.Equal(FixpAction.Accept, t.Action);
    }

    [Fact]
    public void Idle_RetransmitRequest_DroppedSilently()
    {
        var t = FixpStateMachine.Apply(FixpState.Idle, FixpEvent.RetransmitRequest);
        Assert.Equal(FixpState.Idle, t.NewState);
        Assert.Equal(FixpAction.DropSilently, t.Action);
    }

    [Fact]
    public void Idle_Detach_GoesTerminated()
    {
        var t = FixpStateMachine.Apply(FixpState.Idle, FixpEvent.Detach);
        Assert.Equal(FixpState.Terminated, t.NewState);
        Assert.Equal(FixpAction.Accept, t.Action);
    }

    // ── Negotiated row ────────────────────────────────────────────────────

    [Fact]
    public void Negotiated_Negotiate_RejectsAlreadyNegotiated()
    {
        var t = FixpStateMachine.Apply(FixpState.Negotiated, FixpEvent.Negotiate);
        Assert.Equal(FixpState.Negotiated, t.NewState);
        Assert.Equal(FixpAction.NegotiateRejectAlreadyNegotiated, t.Action);
    }

    [Fact]
    public void Negotiated_Establish_Accepted_TransitionsToEstablished()
    {
        var t = FixpStateMachine.Apply(FixpState.Negotiated, FixpEvent.Establish);
        Assert.Equal(FixpState.Established, t.NewState);
        Assert.Equal(FixpAction.Accept, t.Action);
    }

    [Fact]
    public void Negotiated_ApplicationMessage_RejectsAndTerminatesUnestablished()
    {
        var t = FixpStateMachine.Apply(FixpState.Negotiated, FixpEvent.ApplicationMessage);
        Assert.Equal(FixpState.Terminated, t.NewState);
        Assert.Equal(FixpAction.RejectAndTerminateUnestablished, t.Action);
    }

    [Fact]
    public void Negotiated_Sequence_NotApplied()
    {
        var t = FixpStateMachine.Apply(FixpState.Negotiated, FixpEvent.Sequence);
        Assert.Equal(FixpState.Negotiated, t.NewState);
        Assert.Equal(FixpAction.NotApplied, t.Action);
    }

    [Fact]
    public void Negotiated_Terminate_GoesTerminated()
    {
        var t = FixpStateMachine.Apply(FixpState.Negotiated, FixpEvent.Terminate);
        Assert.Equal(FixpState.Terminated, t.NewState);
        Assert.Equal(FixpAction.Accept, t.Action);
    }

    [Fact]
    public void Negotiated_RetransmitRequest_DroppedSilently()
    {
        var t = FixpStateMachine.Apply(FixpState.Negotiated, FixpEvent.RetransmitRequest);
        Assert.Equal(FixpState.Negotiated, t.NewState);
        Assert.Equal(FixpAction.DropSilently, t.Action);
    }

    [Fact]
    public void Negotiated_Detach_GoesTerminated()
    {
        var t = FixpStateMachine.Apply(FixpState.Negotiated, FixpEvent.Detach);
        Assert.Equal(FixpState.Terminated, t.NewState);
        Assert.Equal(FixpAction.Accept, t.Action);
    }

    // ── Established row ───────────────────────────────────────────────────

    [Fact]
    public void Established_Negotiate_Rejects()
    {
        var t = FixpStateMachine.Apply(FixpState.Established, FixpEvent.Negotiate);
        Assert.Equal(FixpState.Established, t.NewState);
        Assert.Equal(FixpAction.NegotiateReject, t.Action);
    }

    [Fact]
    public void Established_Establish_Rejects()
    {
        var t = FixpStateMachine.Apply(FixpState.Established, FixpEvent.Establish);
        Assert.Equal(FixpState.Established, t.NewState);
        Assert.Equal(FixpAction.EstablishReject, t.Action);
    }

    [Fact]
    public void Established_ApplicationMessage_Accepted()
    {
        var t = FixpStateMachine.Apply(FixpState.Established, FixpEvent.ApplicationMessage);
        Assert.Equal(FixpState.Established, t.NewState);
        Assert.Equal(FixpAction.Accept, t.Action);
    }

    [Fact]
    public void Established_Sequence_Accepted()
    {
        var t = FixpStateMachine.Apply(FixpState.Established, FixpEvent.Sequence);
        Assert.Equal(FixpState.Established, t.NewState);
        Assert.Equal(FixpAction.Accept, t.Action);
    }

    [Fact]
    public void Established_Terminate_GoesTerminated()
    {
        var t = FixpStateMachine.Apply(FixpState.Established, FixpEvent.Terminate);
        Assert.Equal(FixpState.Terminated, t.NewState);
        Assert.Equal(FixpAction.Accept, t.Action);
    }

    [Fact]
    public void Established_RetransmitRequest_TriggersReplay()
    {
        var t = FixpStateMachine.Apply(FixpState.Established, FixpEvent.RetransmitRequest);
        Assert.Equal(FixpState.Established, t.NewState);
        Assert.Equal(FixpAction.Replay, t.Action);
    }

    [Fact]
    public void Established_Detach_GoesSuspended()
    {
        var t = FixpStateMachine.Apply(FixpState.Established, FixpEvent.Detach);
        Assert.Equal(FixpState.Suspended, t.NewState);
        Assert.Equal(FixpAction.Accept, t.Action);
    }

    // ── Suspended row ─────────────────────────────────────────────────────

    [Fact]
    public void Suspended_Negotiate_RejectsAlreadyNegotiated()
    {
        var t = FixpStateMachine.Apply(FixpState.Suspended, FixpEvent.Negotiate);
        Assert.Equal(FixpState.Suspended, t.NewState);
        Assert.Equal(FixpAction.NegotiateRejectAlreadyNegotiated, t.Action);
    }

    [Fact]
    public void Suspended_Establish_ReAttachesToEstablished()
    {
        var t = FixpStateMachine.Apply(FixpState.Suspended, FixpEvent.Establish);
        Assert.Equal(FixpState.Established, t.NewState);
        Assert.Equal(FixpAction.Accept, t.Action);
    }

    [Fact]
    public void Suspended_ApplicationMessage_DroppedSilently()
    {
        var t = FixpStateMachine.Apply(FixpState.Suspended, FixpEvent.ApplicationMessage);
        Assert.Equal(FixpState.Suspended, t.NewState);
        Assert.Equal(FixpAction.DropSilently, t.Action);
    }

    [Fact]
    public void Suspended_Sequence_DroppedSilently()
    {
        var t = FixpStateMachine.Apply(FixpState.Suspended, FixpEvent.Sequence);
        Assert.Equal(FixpState.Suspended, t.NewState);
        Assert.Equal(FixpAction.DropSilently, t.Action);
    }

    [Fact]
    public void Suspended_Terminate_GoesTerminated()
    {
        var t = FixpStateMachine.Apply(FixpState.Suspended, FixpEvent.Terminate);
        Assert.Equal(FixpState.Terminated, t.NewState);
        Assert.Equal(FixpAction.Accept, t.Action);
    }

    [Fact]
    public void Suspended_RetransmitRequest_DeferredToReestablish()
    {
        var t = FixpStateMachine.Apply(FixpState.Suspended, FixpEvent.RetransmitRequest);
        Assert.Equal(FixpState.Suspended, t.NewState);
        Assert.Equal(FixpAction.DeferredToReestablish, t.Action);
    }

    [Fact]
    public void Suspended_Detach_StaysSuspended()
    {
        var t = FixpStateMachine.Apply(FixpState.Suspended, FixpEvent.Detach);
        Assert.Equal(FixpState.Suspended, t.NewState);
        Assert.Equal(FixpAction.DropSilently, t.Action);
    }

    // ── Terminated (terminal) ─────────────────────────────────────────────

    [Theory]
    [InlineData(FixpEvent.Negotiate)]
    [InlineData(FixpEvent.Establish)]
    [InlineData(FixpEvent.ApplicationMessage)]
    [InlineData(FixpEvent.Sequence)]
    [InlineData(FixpEvent.Terminate)]
    [InlineData(FixpEvent.RetransmitRequest)]
    [InlineData(FixpEvent.Detach)]
    public void Terminated_AnyEvent_StaysTerminated(FixpEvent ev)
    {
        var t = FixpStateMachine.Apply(FixpState.Terminated, ev);
        Assert.Equal(FixpState.Terminated, t.NewState);
        Assert.Equal(FixpAction.Terminal, t.Action);
    }

    // ── Matrix completeness guard ─────────────────────────────────────────

    /// <summary>
    /// Sanity check: every (state, event) combination must be handled by
    /// <see cref="FixpStateMachine.Apply"/>. Catches accidental matrix
    /// holes if a future enum value is added without a row update.
    /// </summary>
    [Fact]
    public void EveryStateEventCombination_IsCovered()
    {
        foreach (FixpState s in Enum.GetValues<FixpState>())
            foreach (FixpEvent e in Enum.GetValues<FixpEvent>())
            {
                // Should not throw.
                var t = FixpStateMachine.Apply(s, e);
                _ = t.NewState;
                _ = t.Action;
            }
    }
}
