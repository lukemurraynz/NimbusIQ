using System.Diagnostics;

namespace Atlas.ControlPlane.Api.Middleware;

/// <summary>
/// Ensures every request has a correlation ID and echoes it back for end-to-end traceability.
/// Header: X-Correlation-Id
/// </summary>
public class CorrelationIdMiddleware(RequestDelegate next)
{
    public const string HeaderName = "X-Correlation-Id";
    public const string TraceHeaderName = "X-Trace-Id";

    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = context.Request.Headers[HeaderName].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(correlationId))
        {
            correlationId = Guid.NewGuid().ToString();
        }

        context.Response.Headers[HeaderName] = correlationId;

        var traceId = Activity.Current?.TraceId.ToString();
        if (!string.IsNullOrEmpty(traceId))
        {
            context.Response.Headers[TraceHeaderName] = traceId;
        }

        using (context.RequestServices.GetRequiredService<ILoggerFactory>()
                   .CreateLogger("Correlation")
                   .BeginScope(new Dictionary<string, object>
                   {
                       ["correlation_id"] = correlationId,
                       ["trace_id"] = traceId ?? string.Empty
                   }))
        {
            Activity.Current?.SetTag("correlation.id", correlationId);
            await next(context);
        }
    }
}

