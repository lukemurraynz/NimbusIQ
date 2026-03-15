using System.Diagnostics;
using Atlas.ControlPlane.Domain.Entities;
using Atlas.ControlPlane.Infrastructure.Data;

namespace Atlas.ControlPlane.Api.Middleware;

/// <summary>
/// Captures all mutating HTTP requests (POST, PUT, PATCH, DELETE) as audit events
/// for enterprise compliance and traceability.
/// </summary>
public class AuditLogMiddleware(RequestDelegate next)
{
    private static readonly HashSet<string> MutatingMethods =
        new(StringComparer.OrdinalIgnoreCase) { "POST", "PUT", "PATCH", "DELETE" };

    private static readonly string[] ExcludedPrefixes =
    [
        "/health/",
        "/api/v1/chat/agent"
    ];

    public async Task InvokeAsync(HttpContext context)
    {
        if (!MutatingMethods.Contains(context.Request.Method))
        {
            await next(context);
            return;
        }

        var path = context.Request.Path.Value ?? string.Empty;

        if (ExcludedPrefixes.Any(ep => path.StartsWith(ep, StringComparison.OrdinalIgnoreCase)))
        {
            await next(context);
            return;
        }

        var startTime = DateTime.UtcNow;

        await next(context);

        try
        {
            var userId = context.User?.Identity?.Name ?? context.User?.FindFirst("oid")?.Value ?? "anonymous";
            var correlationId = context.Response.Headers[CorrelationIdMiddleware.HeaderName].FirstOrDefault()
                                ?? Guid.NewGuid().ToString();

            using var scope = context.RequestServices.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AtlasDbContext>();

            var auditEvent = new AuditEvent
            {
                Id = Guid.NewGuid(),
                CorrelationId = Guid.TryParse(correlationId, out var cid) ? cid : Guid.NewGuid(),
                ActorType = context.User?.Identity?.IsAuthenticated == true ? "user" : "system",
                ActorId = userId,
                EventName = $"http_{context.Request.Method.ToLowerInvariant()}",
                EventPayload = System.Text.Json.JsonSerializer.Serialize(new
                {
                    method = context.Request.Method,
                    path,
                    statusCode = context.Response.StatusCode,
                    durationMs = (DateTime.UtcNow - startTime).TotalMilliseconds
                }),
                TraceId = Activity.Current?.TraceId.ToString(),
                CreatedAt = DateTime.UtcNow,
                EventType = $"http_{context.Request.Method.ToLowerInvariant()}",
                EntityType = ExtractEntityType(path),
                EntityId = ExtractEntityId(path),
                UserId = userId,
                Timestamp = DateTime.UtcNow
            };

            db.AuditEvents.Add(auditEvent);
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            // Audit logging must never break request processing
            var logger = context.RequestServices.GetRequiredService<ILoggerFactory>()
                .CreateLogger<AuditLogMiddleware>();
            logger.LogWarning(
                ex,
                "Failed to write audit log for {Method} {Path}",
                context.Request.Method,
                Atlas.ControlPlane.Infrastructure.Logging.LoggerExtensions.SanitizeUrl(
                    $"{context.Request.Path}{context.Request.QueryString}"));
        }
    }

    // Extracts entity type from API path segments (e.g., /api/v1/service-groups → service-groups)
    private static string? ExtractEntityType(string path)
    {
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        // Pattern: /api/v1/{entityType}/{id}/... → return entityType
        if (segments.Length >= 3 && segments[0] == "api")
            return segments[2];
        return segments.Length >= 1 ? segments[^1] : null;
    }

    private static string? ExtractEntityId(string path)
    {
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        // Pattern: /api/v1/{entityType}/{id} → return id
        if (segments.Length >= 4 && segments[0] == "api" && Guid.TryParse(segments[3], out _))
            return segments[3];
        return null;
    }
}
