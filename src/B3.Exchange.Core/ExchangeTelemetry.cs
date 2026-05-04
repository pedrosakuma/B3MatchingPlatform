using System.Diagnostics;

namespace B3.Exchange.Core;

/// <summary>
/// Shared <see cref="ActivitySource"/> for B3 exchange spans (issue #175).
///
/// Instrumentation policy:
/// <list type="bullet">
///   <item><c>gateway.decode</c> — root span per inbound FIXP frame after
///   header parsing; opened on the gateway IO thread.</item>
///   <item><c>dispatch.enqueue</c> — child of <c>gateway.decode</c>; covers
///   the bounded-channel write so contention / queue-full appears in the
///   trace.</item>
///   <item><c>engine.process</c> — opened on the dispatch loop thread when
///   <c>ProcessOne</c> picks the work item up; parented to the enqueue
///   span via the <see cref="ActivityContext"/> captured at enqueue time
///   (the dispatcher crosses thread boundaries, so propagation must be
///   explicit).</item>
///   <item><c>outbound.emit</c> — child of <c>engine.process</c>; brackets
///   the UMDF packet flush + multicast publish.</item>
/// </list>
///
/// All spans share the same <see cref="ActivitySource"/> name so a single
/// <c>AddSource("B3.Exchange")</c> in the host's <c>TracerProvider</c>
/// picks the entire pipeline up. The exporter (OTLP / Console / none) is
/// wired by the host based on environment, so library code stays free of
/// SDK references.
/// </summary>
public static class ExchangeTelemetry
{
    /// <summary>
    /// <see cref="ActivitySource"/> name. Must match the value passed to
    /// <c>AddSource(...)</c> on the consumer-side <c>TracerProvider</c>.
    /// </summary>
    public const string SourceName = "B3.Exchange";

    /// <summary>Singleton <see cref="ActivitySource"/>.</summary>
    public static readonly ActivitySource Source = new(SourceName, "1.0.0");

    public const string SpanGatewayDecode = "gateway.decode";
    public const string SpanDispatchEnqueue = "dispatch.enqueue";
    public const string SpanEngineProcess = "engine.process";
    public const string SpanOutboundEmit = "outbound.emit";

    // Standard tag keys used across spans. Kept as constants so dashboards
    // and tests share a single source of truth.
    public const string TagChannel = "b3.channel";
    public const string TagSession = "b3.session_id";
    public const string TagFirm = "b3.firm";
    public const string TagWorkKind = "b3.work_kind";
    public const string TagTemplate = "b3.template_id";
    public const string TagClOrdId = "b3.cl_ord_id";
    public const string TagSecurityId = "b3.security_id";
    public const string TagBytes = "b3.packet_bytes";
}
