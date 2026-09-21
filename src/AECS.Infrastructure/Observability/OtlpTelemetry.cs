using OpenTelemetry;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace AECS.Infrastructure.Observability;

/// <summary>
/// Optional OTLP trace export (Fundação §28, M0 "OpenTelemetry básico").
/// Enabled only when OTEL_EXPORTER_OTLP_ENDPOINT is set — the standard
/// OpenTelemetry env contract. Without it, AECS emits Activity spans with no
/// exporter attached (listener-friendly, zero network). The source name is
/// passed in by the CLI so Infrastructure never references Application.
/// </summary>
public static class OtlpTelemetry
{
    public static IDisposable? Configure(string activitySourceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(activitySourceName);
        var endpoint = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT");
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return null;
        }

        var serviceName = Environment.GetEnvironmentVariable("OTEL_SERVICE_NAME");
        if (string.IsNullOrWhiteSpace(serviceName))
        {
            serviceName = "aecs";
        }

        return Sdk.CreateTracerProviderBuilder()
            .SetResourceBuilder(ResourceBuilder.CreateDefault().AddService(serviceName))
            .AddSource(activitySourceName)
            .AddOtlpExporter()
            .Build();
    }
}
