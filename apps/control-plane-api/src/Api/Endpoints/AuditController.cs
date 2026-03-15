using System.Diagnostics;
using Atlas.ControlPlane.Api.Middleware;
using Atlas.ControlPlane.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Atlas.ControlPlane.Api.Endpoints;

[ApiController]
[Route("api/v1/audit")]
[Authorize(Policy = "AnalysisRead")]
public class AuditController : ControllerBase
{
    private static readonly ActivitySource ActivitySource = new("Atlas.ControlPlane.Audit");
    private readonly AtlasDbContext _db;
    private readonly ILogger<AuditController> _logger;

    public AuditController(AtlasDbContext db, ILogger<AuditController> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// Query audit events with filtering and pagination.
    /// </summary>
    [HttpGet("events")]
    [ProducesResponseType(typeof(AuditEventsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<AuditEventsResponse>> GetAuditEvents(
        [FromQuery(Name = "api-version")] string? apiVersion,
        [FromQuery] string? entityType = null,
        [FromQuery] string? eventType = null,
        [FromQuery] DateTime? startDate = null,
        [FromQuery] DateTime? endDate = null,
        [FromQuery] int maxResults = 50,
        [FromQuery] string? continuationToken = null,
        [FromHeader(Name = "x-continuation-token")] string? continuationTokenHeader = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(apiVersion))
            return this.ProblemBadRequest("MissingApiVersionParameter", "api-version query parameter is required");

        using var activity = ActivitySource.StartActivity("GetAuditEvents");
        activity?.SetTag("entity_type", entityType);
        activity?.SetTag("event_type", eventType);
        activity?.SetTag("max_results", maxResults);

        if (maxResults is < 1 or > 100)
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Invalid maxResults",
                Detail = "maxResults must be between 1 and 100",
                Status = StatusCodes.Status400BadRequest,
                Extensions = { ["errorCode"] = "InvalidMaxResults" }
            });
        }

        if (startDate.HasValue && endDate.HasValue && startDate > endDate)
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Invalid date range",
                Detail = "startDate must be before endDate",
                Status = StatusCodes.Status400BadRequest,
                Extensions = { ["errorCode"] = "InvalidDateRange" }
            });
        }

        var skip = 0;
        var effectiveContinuationToken = !string.IsNullOrEmpty(continuationTokenHeader)
            ? continuationTokenHeader
            : continuationToken;

        if (!string.IsNullOrEmpty(effectiveContinuationToken))
        {
            if (!int.TryParse(effectiveContinuationToken, out skip) || skip < 0)
            {
                return BadRequest(new ProblemDetails
                {
                    Title = "Invalid continuation token",
                    Detail = "Continuation token must be a non-negative integer",
                    Status = StatusCodes.Status400BadRequest,
                    Extensions = { ["errorCode"] = "InvalidContinuationToken" }
                });
            }
        }

        var query = _db.AuditEvents.AsNoTracking();

        if (!string.IsNullOrEmpty(entityType))
            query = query.Where(e => e.EntityType == entityType);

        if (!string.IsNullOrEmpty(eventType))
            query = query.Where(e => e.EventType == eventType);

        if (startDate.HasValue)
            query = query.Where(e => e.Timestamp >= startDate.Value);

        if (endDate.HasValue)
            query = query.Where(e => e.Timestamp <= endDate.Value);

        query = query.OrderByDescending(e => e.Timestamp);

        var total = await query.CountAsync(ct);
        var events = await query.Skip(skip).Take(maxResults).ToListAsync(ct);

        var nextContinuationToken = (skip + events.Count < total)
            ? (skip + events.Count).ToString()
            : null;

        _logger.LogInformation(
            "Retrieved {Count} audit events (total: {Total}, skip: {Skip})",
            events.Count, total, skip);

        activity?.SetTag("events.count", events.Count);
        activity?.SetTag("events.total", total);

        // Build nextLink that preserves all active filter parameters so pagination
        // returns a filtered result set rather than an unfiltered full scan.
        string? nextLink = null;
        if (nextContinuationToken != null)
        {
            var qs = new System.Text.StringBuilder();
            qs.Append($"/api/v1/audit/events?api-version={Uri.EscapeDataString(apiVersion)}&maxResults={maxResults}");
            qs.Append($"&continuationToken={Uri.EscapeDataString(nextContinuationToken)}");
            if (!string.IsNullOrEmpty(entityType))
                qs.Append($"&entityType={Uri.EscapeDataString(entityType)}");
            if (!string.IsNullOrEmpty(eventType))
                qs.Append($"&eventType={Uri.EscapeDataString(eventType)}");
            if (startDate.HasValue)
                qs.Append($"&startDate={Uri.EscapeDataString(startDate.Value.ToString("O"))}");
            if (endDate.HasValue)
                qs.Append($"&endDate={Uri.EscapeDataString(endDate.Value.ToString("O"))}");
            nextLink = qs.ToString();
        }

        return Ok(new AuditEventsResponse
        {
            Value = events.Select(e => new AuditEventDto
            {
                Id = e.Id.ToString(),
                Timestamp = e.Timestamp,
                EntityType = e.EntityType ?? string.Empty,
                EntityId = e.EntityId ?? string.Empty,
                EventType = e.EventType ?? e.EventName,
                Description = e.EventName,
                CorrelationId = e.CorrelationId.ToString(),
                Metadata = e.EventPayload
            }).ToList(),
            NextLink = nextLink,
            ContinuationToken = nextContinuationToken,
            TotalCount = total
        });
    }

    /// <summary>
    /// Query audit events with filtering and pagination (POST-based, secure pagination).
    /// Recommended for complex queries. Pagination token is kept in response body, not exposed in URLs.
    /// </summary>
    [HttpPost("events/query")]
    [ProducesResponseType(typeof(AuditEventsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<AuditEventsResponse>> QueryAuditEvents(
        [FromBody] AuditEventsQueryRequest request,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.ApiVersion))
            return this.ProblemBadRequest("MissingApiVersionParameter", "api-version is required in request body");

        using var activity = ActivitySource.StartActivity("QueryAuditEvents");
        activity?.SetTag("entity_type", request.EntityType);
        activity?.SetTag("event_type", request.EventType);
        activity?.SetTag("max_results", request.MaxResults);

        if (request.MaxResults is < 1 or > 100)
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Invalid maxResults",
                Detail = "maxResults must be between 1 and 100",
                Status = StatusCodes.Status400BadRequest,
                Extensions = { ["errorCode"] = "InvalidMaxResults" }
            });
        }

        if (request.StartDate.HasValue && request.EndDate.HasValue && request.StartDate > request.EndDate)
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Invalid date range",
                Detail = "startDate must be before endDate",
                Status = StatusCodes.Status400BadRequest,
                Extensions = { ["errorCode"] = "InvalidDateRange" }
            });
        }

        var skip = 0;
        if (!string.IsNullOrEmpty(request.ContinuationToken))
        {
            if (!int.TryParse(request.ContinuationToken, out skip) || skip < 0)
            {
                return BadRequest(new ProblemDetails
                {
                    Title = "Invalid continuation token",
                    Detail = "Continuation token must be a non-negative integer",
                    Status = StatusCodes.Status400BadRequest,
                    Extensions = { ["errorCode"] = "InvalidContinuationToken" }
                });
            }
        }

        var query = _db.AuditEvents.AsNoTracking();

        if (!string.IsNullOrEmpty(request.EntityType))
            query = query.Where(e => e.EntityType == request.EntityType);

        if (!string.IsNullOrEmpty(request.EventType))
            query = query.Where(e => e.EventType == request.EventType);

        if (request.StartDate.HasValue)
            query = query.Where(e => e.Timestamp >= request.StartDate.Value);

        if (request.EndDate.HasValue)
            query = query.Where(e => e.Timestamp <= request.EndDate.Value);

        query = query.OrderByDescending(e => e.Timestamp);

        var total = await query.CountAsync(ct);
        var events = await query.Skip(skip).Take(request.MaxResults).ToListAsync(ct);

        var nextContinuationToken = (skip + events.Count < total)
            ? (skip + events.Count).ToString()
            : null;

        _logger.LogInformation(
            "Retrieved {Count} audit events via POST (total: {Total}, skip: {Skip})",
            events.Count, total, skip);

        activity?.SetTag("events.count", events.Count);
        activity?.SetTag("events.total", total);

        return Ok(new AuditEventsResponse
        {
            Value = events.Select(e => new AuditEventDto
            {
                Id = e.Id.ToString(),
                Timestamp = e.Timestamp,
                EntityType = e.EntityType ?? string.Empty,
                EntityId = e.EntityId ?? string.Empty,
                EventType = e.EventType ?? e.EventName,
                Description = e.EventName,
                CorrelationId = e.CorrelationId.ToString(),
                Metadata = e.EventPayload
            }).ToList(),
            // SECURITY: NextLink is null for POST-based pagination.
            // Client passes continuationToken in request body, not URL query params.
            NextLink = null,
            ContinuationToken = nextContinuationToken,
            TotalCount = total
        });
    }

    /// <summary>
    /// Get evidence trail for a specific entity.
    /// </summary>
    [HttpGet("evidence/{entityId}")]
    [ProducesResponseType(typeof(EvidenceTrailResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<EvidenceTrailResponse>> GetEvidenceTrail(
        string entityId,
        [FromQuery(Name = "api-version")] string? apiVersion,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(apiVersion))
            return this.ProblemBadRequest("MissingApiVersionParameter", "api-version query parameter is required");

        using var activity = ActivitySource.StartActivity("GetEvidenceTrail");
        activity?.SetTag("entity_id", entityId);

        var events = await _db.AuditEvents
            .AsNoTracking()
            .Where(e => e.EntityId == entityId)
            .OrderBy(e => e.Timestamp)
            .ToListAsync(ct);

        if (events.Count == 0)
        {
            return NotFound(new ProblemDetails
            {
                Title = "Entity not found",
                Detail = $"No evidence trail found for entity: {entityId}",
                Status = StatusCodes.Status404NotFound,
                Extensions = { ["errorCode"] = "EntityNotFound" }
            });
        }

        _logger.LogInformation("Retrieved evidence trail for {EntityId}: {Count} events", entityId, events.Count);
        activity?.SetTag("events.count", events.Count);

        return Ok(new EvidenceTrailResponse
        {
            EntityId = entityId,
            EntityType = events.First().EntityType ?? string.Empty,
            Events = events.Select(e => new AuditEventDto
            {
                Id = e.Id.ToString(),
                Timestamp = e.Timestamp,
                EntityType = e.EntityType ?? string.Empty,
                EntityId = e.EntityId ?? string.Empty,
                EventType = e.EventType ?? e.EventName,
                Description = e.EventName,
                CorrelationId = e.CorrelationId.ToString(),
                Metadata = e.EventPayload
            }).ToList(),
            TotalEvents = events.Count,
            FirstEvent = events.First().Timestamp,
            LastEvent = events.Last().Timestamp
        });
    }

    /// <summary>
    /// Get timeline events for a service group.
    /// </summary>
    [HttpGet("timeline/{serviceGroupId}")]
    [ProducesResponseType(typeof(TimelineProjectionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TimelineProjectionResponse>> GetServiceGroupTimeline(
        Guid serviceGroupId,
        [FromQuery(Name = "api-version")] string? apiVersion,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(apiVersion))
            return this.ProblemBadRequest("MissingApiVersionParameter", "api-version query parameter is required");

        using var activity = ActivitySource.StartActivity("GetServiceGroupTimeline");
        activity?.SetTag("service_group_id", serviceGroupId);

        var serviceGroup = await _db.ServiceGroups
            .AsNoTracking()
            .FirstOrDefaultAsync(g => g.Id == serviceGroupId, ct);

        if (serviceGroup == null)
        {
            return NotFound(new ProblemDetails
            {
                Title = "Service group not found",
                Detail = $"Service group not found: {serviceGroupId}",
                Status = StatusCodes.Status404NotFound,
                Extensions = { ["errorCode"] = "ServiceGroupNotFound" }
            });
        }

        var events = await _db.TimelineEvents
            .AsNoTracking()
            .Where(e => e.ServiceGroupId == serviceGroupId)
            .OrderBy(e => e.EventTime)
            .ToListAsync(ct);

        var now = DateTime.UtcNow;
        var today = now.Date;

        var historical = events.Where(e => e.EventTime.Date < today).ToList();
        var current = events.Where(e => e.EventTime.Date == today).ToList();
        var projected = events.Where(e => e.EventTime.Date > today).ToList();

        _logger.LogInformation(
            "Retrieved timeline for {ServiceGroupId}: {Historical} historical, {Current} current, {Projected} projected",
            serviceGroupId, historical.Count, current.Count, projected.Count);

        activity?.SetTag("events.historical", historical.Count);
        activity?.SetTag("events.current", current.Count);
        activity?.SetTag("events.projected", projected.Count);

        static AuditEventDto MapTimelineEvent(Domain.Entities.TimelineEvent e) => new()
        {
            Id = e.Id.ToString(),
            Timestamp = e.EventTime,
            EntityType = "ServiceGroup",
            EntityId = e.ServiceGroupId.ToString(),
            EventType = e.EventType,
            Description = e.EventType,
            CorrelationId = null,
            Metadata = e.EventPayload
        };

        return Ok(new TimelineProjectionResponse
        {
            ServiceGroupId = serviceGroupId.ToString(),
            ServiceGroupName = serviceGroup.Name,
            CurrentTimestamp = now,
            Historical = historical.Select(MapTimelineEvent).ToList(),
            Current = current.Select(MapTimelineEvent).ToList(),
            Projected = projected.Select(MapTimelineEvent).ToList(),
            TotalEvents = events.Count
        });
    }
}

public record AuditEventsResponse
{
    public List<AuditEventDto> Value { get; init; } = new();
    public string? NextLink { get; init; }
    /// <summary>
    /// Pagination token for POST-based queries (not exposed in URL for security).
    /// Pass this in the next request body as continuationToken.
    /// </summary>
    public string? ContinuationToken { get; init; }
    public int TotalCount { get; init; }
}

public record AuditEventsQueryRequest
{
    public string ApiVersion { get; init; } = string.Empty;
    public string? EntityType { get; init; }
    public string? EventType { get; init; }
    public DateTime? StartDate { get; init; }
    public DateTime? EndDate { get; init; }
    public int MaxResults { get; init; } = 50;
    public string? ContinuationToken { get; init; }
}

public record AuditEventDto
{
    public string Id { get; init; } = string.Empty;
    public DateTime Timestamp { get; init; }
    public string EntityType { get; init; } = string.Empty;
    public string EntityId { get; init; } = string.Empty;
    public string EventType { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string? CorrelationId { get; init; }
    public string? Metadata { get; init; }
}

public record EvidenceTrailResponse
{
    public string EntityId { get; init; } = string.Empty;
    public string EntityType { get; init; } = string.Empty;
    public List<AuditEventDto> Events { get; init; } = new();
    public int TotalEvents { get; init; }
    public DateTime FirstEvent { get; init; }
    public DateTime LastEvent { get; init; }
}

public record TimelineProjectionResponse
{
    public string ServiceGroupId { get; init; } = string.Empty;
    public string ServiceGroupName { get; init; } = string.Empty;
    public DateTime CurrentTimestamp { get; init; }
    public List<AuditEventDto> Historical { get; init; } = new();
    public List<AuditEventDto> Current { get; init; } = new();
    public List<AuditEventDto> Projected { get; init; } = new();
    public int TotalEvents { get; init; }
}
