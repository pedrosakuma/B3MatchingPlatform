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

    private OrderedStreamWriteResult(byte value)
    {
        _value = value;
    }

    public bool IsCommitted => _value != 0;

    public bool IsTransportEnqueued => _value == 2;

    public static OrderedStreamWriteResult NotCommitted => default;

    public static OrderedStreamWriteResult Committed =>
        new(1);

    public static OrderedStreamWriteResult CommittedAndEnqueued =>
        new(2);

    public static OrderedStreamWriteResult FromTransportAdmission(bool enqueued) =>
        enqueued ? CommittedAndEnqueued : Committed;
}
