using OpenTelemetry;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using OpenTelemetry.Metrics;

namespace Atlas.AgentOrchestrator.Orchestration.Telemetry;

/// <summary>
/// Configures OpenTelemetry for the agent orchestrator with tracing and metrics.
/// </summary>
public static class OpenTelemetryConfiguration
{
    public const string ServiceName = "nimbusiq-agent-orchestrator";
    public const string ServiceVersion = "1.0.0";

    public static ResourceBuilder CreateResourceBuilder()
    {
        return ResourceBuilder.CreateDefault()
            .AddService(
                serviceName: ServiceName,
                serviceVersion: ServiceVersion,
                serviceInstanceId: Environment.MachineName)
            .AddAttributes(new Dictionary<string, object>
            {
                ["deployment.environment"] = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? "unknown",
                ["service.namespace"] = "nimbusiq"
            });
    }

    public static void ConfigureTracing(TracerProviderBuilder builder, string? otlpEndpoint)
    {
        builder
            .SetResourceBuilder(CreateResourceBuilder())
            .AddHttpClientInstrumentation(options =>
            {
                options.RecordException = true;
            })
            .AddSource("Atlas.*"); // Capture custom activity sources

        if (!string.IsNullOrEmpty(otlpEndpoint))
        {
            builder.AddOtlpExporter(options =>
            {
                options.Endpoint = new Uri(otlpEndpoint);
            });
        }
        else
        {
            builder.AddConsoleExporter();
        }
    }

    public static void ConfigureMetrics(MeterProviderBuilder builder, string? otlpEndpoint)
    {
        builder
            .SetResourceBuilder(CreateResourceBuilder())
            .AddHttpClientInstrumentation()
            .AddRuntimeInstrumentation()
            .AddMeter("Atlas.*"); // Capture custom meters

        if (!string.IsNullOrEmpty(otlpEndpoint))
        {
            builder.AddOtlpExporter(options =>
            {
                options.Endpoint = new Uri(otlpEndpoint);
            });
        }
        else
        {
            builder.AddConsoleExporter();
        }
    }
}
