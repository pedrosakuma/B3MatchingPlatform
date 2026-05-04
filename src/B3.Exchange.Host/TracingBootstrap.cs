using B3.Exchange.Core;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace B3.Exchange.Host;

/// <summary>
/// Issue #175: optional OpenTelemetry tracing bootstrap. Wires a
/// <see cref="TracerProvider"/> with the OTLP exporter when
/// <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> is set in the environment, and is a
/// no-op otherwise. Returning <c>null</c> from <see cref="TryBuild"/>
/// keeps the unconfigured path zero-cost — no exporter pipeline, no
/// background flush, no extra threads.
///
/// All other OTLP knobs (protocol, headers, service name, etc.) follow
/// the standard <c>OTEL_*</c> environment variables, which the SDK reads
/// directly. Documented in <c>docs/OPS.md</c>.
/// </summary>
public static class TracingBootstrap
{
    public const string EndpointEnv = "OTEL_EXPORTER_OTLP_ENDPOINT";
    public const string ServiceNameEnv = "OTEL_SERVICE_NAME";
    public const string DefaultServiceName = "b3-exchange";

    public static TracerProvider? TryBuild(ILogger logger)
    {
        var endpoint = Environment.GetEnvironmentVariable(EndpointEnv);
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            logger.LogInformation(
                "tracing disabled: set {Env} to a valid OTLP endpoint to enable OpenTelemetry export",
                EndpointEnv);
            return null;
        }

        var serviceName = Environment.GetEnvironmentVariable(ServiceNameEnv) ?? DefaultServiceName;

        try
        {
            var provider = Sdk.CreateTracerProviderBuilder()
                .AddSource(ExchangeTelemetry.SourceName)
                .SetResourceBuilder(ResourceBuilder.CreateDefault().AddService(serviceName))
                .AddOtlpExporter()
                .Build();

            logger.LogInformation(
                "tracing enabled: source={Source} service={Service} endpoint={Endpoint}",
                ExchangeTelemetry.SourceName, serviceName, endpoint);
            return provider;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "tracing bootstrap failed; continuing without OpenTelemetry export");
            return null;
        }
    }
}
