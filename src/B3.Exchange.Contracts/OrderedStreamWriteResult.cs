namespace B3.Exchange.Contracts;

/// <summary>
/// Semantic outcome of committing a business frame to a session's ordered
/// FIXP stream. Stream commitment means the assigned sequence and frame are
/// retained for retransmission; live transport admission is reported
/// separately because a Suspended session can commit successfully without
/// having a socket to enqueue onto.
/// </summary>
public readonly record struct OrderedStreamWriteResult
{
    private readonly byte _value;
    private readonly Task<OrderedStreamWriteResult>? _deferredCompletion;

    private OrderedStreamWriteResult(
        byte value,
        Task<OrderedStreamWriteResult>? deferredCompletion = null)
    {
        _value = value;
        _deferredCompletion = deferredCompletion;
    }

    public bool IsCommitted => _value is 1 or 2;

    public bool IsTransportEnqueued => _value == 2;

    /// <summary>
    /// True when the route accepted this write into its bounded deferred FIFO,
    /// but no FIXP sequence number has been allocated yet.
    /// </summary>
    public bool IsDeferred => _value == 3;

    /// <summary>
    /// True when the write either committed immediately or was accepted for
    /// ordered deferred delivery.
    /// </summary>
    public bool IsAccepted => _value != 0;

    /// <summary>
    /// Resolves to the eventual stream-commit result. Immediate writes return
    /// an already-completed task.
    /// </summary>
    public Task<OrderedStreamWriteResult> Completion =>
        _deferredCompletion ?? Task.FromResult(this);

    public static OrderedStreamWriteResult NotCommitted => default;

    public static OrderedStreamWriteResult Committed =>
        new(1);

    public static OrderedStreamWriteResult CommittedAndEnqueued =>
        new(2);

    public static OrderedStreamWriteResult Deferred(
        Task<OrderedStreamWriteResult> completion)
    {
        ArgumentNullException.ThrowIfNull(completion);
        return new OrderedStreamWriteResult(3, completion);
    }

    public static OrderedStreamWriteResult FromTransportAdmission(bool enqueued) =>
        enqueued ? CommittedAndEnqueued : Committed;
}
