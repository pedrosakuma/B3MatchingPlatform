using System.Diagnostics;

namespace B3.Exchange.Matching.Tests;

/// <summary>
/// Issue #169: the matching engine documents a single-thread invariant —
/// every public mutation/read entry point and every sink callback must
/// run on the bound owner thread. These tests verify the DEBUG-only
/// assertion fires on cross-thread access and that the eager
/// <see cref="MatchingEngine.BindToDispatchThread"/> path is honoured.
/// </summary>
public class SingleThreadInvariantTests
{
    public SingleThreadInvariantTests()
    {
        // Make Debug.Assert throw instead of popping a UI / silently
        // logging, so we can observe the assertion in tests.
        if (!Trace.Listeners.OfType<ThrowOnAssertListener>().Any())
        {
            Trace.Listeners.Clear();
            Trace.Listeners.Add(new ThrowOnAssertListener());
        }
    }

    [Fact]
    public void Bind_then_call_on_same_thread_succeeds()
    {
        var engine = TestFactory.NewEngine(out _);
        engine.BindToDispatchThread(Thread.CurrentThread);
        // Should not throw.
        _ = engine.AllocateNextRptSeq();
    }

    [Fact]
    public void Lazy_latch_allows_unbound_first_call_and_pins_thread()
    {
        var engine = TestFactory.NewEngine(out _);
        // First call (no explicit binding) latches current thread.
        _ = engine.AllocateNextRptSeq();
        // Second call from another thread must trigger the assert.
        Exception? captured = null;
        var t = new Thread(() =>
        {
            try { _ = engine.AllocateNextRptSeq(); }
            catch (Exception ex) { captured = ex; }
        });
        t.Start();
        t.Join();

#if DEBUG
        Assert.NotNull(captured);
        Assert.IsType<DebugAssertException>(captured);
#else
        Assert.Null(captured);
#endif
    }

    [Fact]
    public void Bind_then_call_from_other_thread_asserts_in_debug()
    {
        var engine = TestFactory.NewEngine(out _);
        engine.BindToDispatchThread(Thread.CurrentThread);

        Exception? captured = null;
        var t = new Thread(() =>
        {
            try { _ = engine.AllocateNextRptSeq(); }
            catch (Exception ex) { captured = ex; }
        });
        t.Start();
        t.Join();

#if DEBUG
        Assert.NotNull(captured);
        Assert.IsType<DebugAssertException>(captured);
#else
        Assert.Null(captured);
#endif
    }

    [Fact]
    public void Rebinding_to_a_different_thread_asserts_in_debug()
    {
        var engine = TestFactory.NewEngine(out _);
        engine.BindToDispatchThread(Thread.CurrentThread);

        Exception? captured = null;
        var t = new Thread(() =>
        {
            try { engine.BindToDispatchThread(Thread.CurrentThread); }
            catch (Exception ex) { captured = ex; }
        });
        t.Start();
        t.Join();

#if DEBUG
        Assert.NotNull(captured);
        Assert.IsType<DebugAssertException>(captured);
#else
        Assert.Null(captured);
#endif
    }

    private sealed class ThrowOnAssertListener : TraceListener
    {
        public override void Fail(string? message)
            => throw new DebugAssertException(message ?? "assert failed");

        public override void Fail(string? message, string? detailMessage)
            => throw new DebugAssertException(($"{message} {detailMessage}").Trim());

        public override void Write(string? message) { }
        public override void WriteLine(string? message) { }
    }

    public sealed class DebugAssertException : Exception
    {
        public DebugAssertException(string message) : base(message) { }
    }
}
