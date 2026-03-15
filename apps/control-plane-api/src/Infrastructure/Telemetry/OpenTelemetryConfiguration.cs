using OpenTelemetry;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using OpenTelemetry.Metrics;
using OpenTelemetry.Logs;

namespace Atlas.ControlPlane.Infrastructure.Telemetry;

/// <summary>
/// Configures OpenTelemetry for the control-plane API with tracing, metrics, and logging.
/// </summary>
public static class OpenTelemetryConfiguration
{
    public const string ServiceName = "atlas-control-plane-api";
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
                ["deployment.environment"] = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "unknown",
                ["service.namespace"] = "atlas"
            });
    }

    public static void ConfigureTracing(TracerProviderBuilder builder, string? otlpEndpoint)
    {
        builder
            .SetResourceBuilder(CreateResourceBuilder())
            .AddAspNetCoreInstrumentation(options =>
            {
                options.RecordException = true;
                options.Filter = context =>
                {
                    // Don't trace health check endpoints
                    var path = context.Request.Path.ToString();
                    return !path.StartsWith("/health", StringComparison.OrdinalIgnoreCase);
                };
            })
            .AddHttpClientInstrumentation(options =>
            {
                options.RecordException = true;
            })
            .AddEntityFrameworkCoreInstrumentation()
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
            // Development: console exporter
            builder.AddConsoleExporter();
        }
    }

    public static void ConfigureMetrics(MeterProviderBuilder builder, string? otlpEndpoint)
    {
        builder
            .SetResourceBuilder(CreateResourceBuilder())
            .AddAspNetCoreInstrumentation()
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
