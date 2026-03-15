using Atlas.ControlPlane.Api.Middleware;
using Atlas.ControlPlane.Domain.Entities;
using Atlas.ControlPlane.Infrastructure.Data;
using Atlas.ControlPlane.Infrastructure.Azure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;

namespace Atlas.ControlPlane.Api.Endpoints;

/// <summary>
/// Drift detection and snapshot management endpoints with Activity Log integration
/// </summary>
[ApiController]
[Route("api/v1/drift")]
public class DriftController : ControllerBase
{
    private readonly AtlasDbContext _db;
    private readonly ActivityLogClient? _activityLogClient;
    private readonly ILogger<DriftController> _logger;

    public DriftController(
        AtlasDbContext db,
        ILogger<DriftController> logger,
        ActivityLogClient? activityLogClient = null)
    {
        _db = db;
        _logger = logger;
        _activityLogClient = activityLogClient;
    }

    /// <summary>
    /// Get drift snapshots for a service group with time-based filtering
    /// </summary>
    [HttpGet("snapshots/{serviceGroupId}")]
    [ProducesResponseType(typeof(List<DriftSnapshotDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<List<DriftSnapshotDto>>> GetSnapshotsAsync(
        [FromRoute, Required] Guid serviceGroupId,
        [FromQuery] DateTime? startDate = null,
        [FromQuery] DateTime? endDate = null,
        [FromQuery] int limit = 30,
        [FromQuery(Name = "api-version")] string? apiVersion = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(apiVersion))
            return this.ProblemBadRequest("MissingApiVersionParameter", "api-version query parameter is required");

        var query = _db.DriftSnapshots
            .Where(s => s.ServiceGroupId == serviceGroupId);

        if (startDate.HasValue)
            query = query.Where(s => s.SnapshotTime >= startDate.Value);
        if (endDate.HasValue)
            query = query.Where(s => s.SnapshotTime <= endDate.Value);

        var snapshots = await query
            .OrderByDescending(s => s.SnapshotTime)
            .Take(limit)
            .ToListAsync(ct);

        var auditEvents = await GetCandidateAuditEvents(serviceGroupId, snapshots, apiVersion, ct);
        var snapshotDtos = snapshots
            .Select(s => ToDto(s, InferCause(s, serviceGroupId, auditEvents)))
            .ToList();

        return Ok(snapshotDtos);
    }

    /// <summary>
    /// Get drift trend analysis for a service group over a time period
    /// </summary>
    [HttpGet("trends/{serviceGroupId}")]
    [ProducesResponseType(typeof(DriftTrendAnalysisDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<DriftTrendAnalysisDto>> GetTrendAnalysisAsync(
        [FromRoute, Required] Guid serviceGroupId,
        [FromQuery] int days = 30,
        [FromQuery(Name = "api-version")] string? apiVersion = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(apiVersion))
            return this.ProblemBadRequest("MissingApiVersionParameter", "api-version query parameter is required");

        var since = DateTime.UtcNow.AddDays(-days);
        var snapshots = await _db.DriftSnapshots
            .Where(s => s.ServiceGroupId == serviceGroupId && s.SnapshotTime >= since)
            .OrderBy(s => s.SnapshotTime)
            .ToListAsync(ct);

        var auditEvents = await GetCandidateAuditEvents(serviceGroupId, snapshots, apiVersion, ct);

        if (snapshots.Count == 0)
        {
            return Ok(new DriftTrendAnalysisDto
            {
                ServiceGroupId = serviceGroupId,
                PeriodDays = days,
                Snapshots = new List<DriftSnapshotDto>(),
                TrendDirection = "stable",
                AverageScore = 0,
                ScoreChange = 0
            });
        }

        var avgScore = snapshots.Average(s => (double)s.DriftScore);
        var firstScore = (double)snapshots.First().DriftScore;
        var lastScore = (double)snapshots.Last().DriftScore;
        var scoreChange = lastScore - firstScore;

        var trendDirection = scoreChange switch
        {
            < -5 => "improving",
            > 5 => "degrading",
            _ => "stable"
        };

        return Ok(new DriftTrendAnalysisDto
        {
            ServiceGroupId = serviceGroupId,
            PeriodDays = days,
            Snapshots = snapshots
                .Select(s => ToDto(s, InferCause(s, serviceGroupId, auditEvents)))
                .ToList(),
            TrendDirection = trendDirection,
            AverageScore = (decimal)Math.Round(avgScore, 2),
            ScoreChange = (decimal)Math.Round(scoreChange, 2)
        });
    }

    /// <summary>
    /// Get real-time drift status (latest snapshot)
    /// </summary>
    [HttpGet("status/{serviceGroupId}")]
    [ProducesResponseType(typeof(DriftSnapshotDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<DriftSnapshotDto>> GetCurrentStatusAsync(
        [FromRoute, Required] Guid serviceGroupId,
        [FromQuery(Name = "api-version")] string? apiVersion = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(apiVersion))
            return this.ProblemBadRequest("MissingApiVersionParameter", "api-version query parameter is required");

        var latest = await _db.DriftSnapshots
            .Where(s => s.ServiceGroupId == serviceGroupId)
            .OrderByDescending(s => s.SnapshotTime)
            .FirstOrDefaultAsync(ct);

        if (latest is null)
        {
            return this.ProblemNotFound("DriftSnapshotNotFound", $"No drift snapshots found for service group {serviceGroupId}");
        }

        var auditEvents = await GetCandidateAuditEvents(serviceGroupId, [latest], apiVersion, ct);
        return Ok(ToDto(latest, InferCause(latest, serviceGroupId, auditEvents)));
    }

    /// <summary>
    /// Store a new drift snapshot (typically called by agent orchestrator)
    /// </summary>
    [HttpPost("snapshots")]
    [ProducesResponseType(typeof(DriftSnapshotDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<DriftSnapshotDto>> CreateSnapshotAsync(
        [FromBody, Required] CreateDriftSnapshotRequest request,
        [FromQuery(Name = "api-version")] string? apiVersion = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(apiVersion))
            return this.ProblemBadRequest("MissingApiVersionParameter", "api-version query parameter is required");

        var snapshot = new DriftSnapshot
        {
            Id = Guid.NewGuid(),
            ServiceGroupId = request.ServiceGroupId,
            SnapshotTime = DateTime.UtcNow,
            TotalViolations = request.TotalViolations,
            CriticalViolations = request.CriticalViolations,
            HighViolations = request.HighViolations,
            MediumViolations = request.MediumViolations,
            LowViolations = request.LowViolations,
            DriftScore = request.DriftScore,
            CategoryBreakdown = request.CategoryBreakdown,
            TrendAnalysis = request.TrendAnalysis,
            CreatedAt = DateTime.UtcNow
        };

        _db.DriftSnapshots.Add(snapshot);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Created drift snapshot {SnapshotId} for service group {ServiceGroupId}: score={DriftScore:F2}, violations={TotalViolations}",
            snapshot.Id, snapshot.ServiceGroupId, snapshot.DriftScore, snapshot.TotalViolations);

        var location = Url.Action(
            nameof(GetCurrentStatusAsync),
            new { serviceGroupId = snapshot.ServiceGroupId, apiVersion })
            ?? $"/api/v1/drift/status/{snapshot.ServiceGroupId}?api-version={apiVersion}";

        var auditEvents = await GetCandidateAuditEvents(snapshot.ServiceGroupId, [snapshot], apiVersion, ct);
        return Created(location, ToDto(snapshot, InferCause(snapshot, snapshot.ServiceGroupId, auditEvents)));
    }

    /// <summary>
    /// Queries Azure Activity Log for events correlated to drift snapshots.
    /// Activity Log provides authoritative control plane operation history.
    /// </summary>
    private async Task<List<ActivityLogCandidate>> GetActivityLogCandidatesAsync(
        Guid serviceGroupId,
        List<DriftSnapshot> snapshots,
        CancellationToken ct)
    {
        if (_activityLogClient is null || snapshots.Count == 0)
        {
            return [];
        }

        try
        {
            // Query Activity Log with ±2 hour window around snapshots
            var minSnapshot = snapshots.Min(s => s.SnapshotTime).AddHours(-2);
            var maxSnapshot = snapshots.Max(s => s.SnapshotTime).AddHours(2);

            // Get service group with scopes to find associated resources
            var serviceGroup = await _db.ServiceGroups
                .AsNoTracking()
                .Include(sg => sg.Scopes)
                .FirstOrDefaultAsync(sg => sg.Id == serviceGroupId, ct);

            if (serviceGroup is null || serviceGroup.Scopes.Count == 0)
            {
                _logger.LogWarning(
                    "Cannot query Activity Log for service group {ServiceGroupId}: no scopes configured",
                    serviceGroupId);
                return [];
            }

            var scope = serviceGroup.Scopes.First();
            if (string.IsNullOrWhiteSpace(scope.SubscriptionId))
            {
                _logger.LogWarning(
                    "Cannot query Activity Log for service group {ServiceGroupId}: subscription ID not available",
                    serviceGroupId);
                return [];
            }

            // Query Activity Log for this subscription
            var activityLogEvents = await _activityLogClient.QueryActivityLogAsync(
                scope.SubscriptionId,
                minSnapshot,
                maxSnapshot,
                resourceId: null, // Query all resources
                resourceGroup: scope.ResourceGroup,
                cancellationToken: ct);

            _logger.LogInformation(
                "Retrieved {EventCount} Activity Log events for service group {ServiceGroupId} ({SubscriptionId})",
                activityLogEvents.Count, serviceGroupId, scope.SubscriptionId);

            // Convert to candidates
            return activityLogEvents
                .Select(evt => new ActivityLogCandidate
                {
                    Timestamp = evt.EventTimestamp,
                    ActorId = evt.CallerIdentity,
                    ActorType = evt.CallerIdentityType ?? "Unknown",
                    EventType = evt.OperationName,
                    ResourceId = evt.ResourceId,
                    CorrelationId = evt.CorrelationId,
                    Status = evt.Status,
                    Category = evt.EventCategory,
                    CauseType = ActivityLogClient.CategorizeActivityLogEvent(evt),
                    IsAuthoritative = true
                })
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to query Activity Log for service group {ServiceGroupId}",
                serviceGroupId);
            return [];
        }
    }

    private async Task<List<AuditCandidate>> GetCandidateAuditEvents(
        Guid serviceGroupId,
        List<DriftSnapshot> snapshots,
        string? apiVersion,
        CancellationToken ct)
    {
        _ = apiVersion;
        if (snapshots.Count == 0)
        {
            return [];
        }

        var minSnapshot = snapshots.Min(s => s.SnapshotTime).AddHours(-2);
        var maxSnapshot = snapshots.Max(s => s.SnapshotTime).AddHours(2);
        var sgId = serviceGroupId.ToString();

        // Query internal audit events (NimbusIQ operations)
        var internalAudit = await _db.AuditEvents
            .AsNoTracking()
            .Where(a => a.Timestamp >= minSnapshot && a.Timestamp <= maxSnapshot)
            .Where(a =>
                (a.EntityType == "serviceGroup" || a.EntityType == "ServiceGroup") && a.EntityId == sgId ||
                a.EventPayload != null && a.EventPayload.Contains(sgId))
            .OrderByDescending(a => a.Timestamp)
            .Take(500)
            .Select(a => new AuditCandidate
            {
                Timestamp = a.Timestamp,
                ActorType = a.ActorType,
                ActorId = a.ActorId,
                EventType = a.EventType ?? a.EventName,
                EntityId = a.EntityId,
                EventPayload = a.EventPayload,
                CorrelationId = a.CorrelationId,
                IsAuthoritative = false // Internal audit is less authoritative than Activity Log
            })
            .ToListAsync(ct);

        // Query Activity Log (Azure control plane operations) - more authoritative
        var activityLogCandidates = await GetActivityLogCandidatesAsync(
            serviceGroupId,
            snapshots,
            ct);

        // Convert Activity Log candidates to AuditCandidate format
        var activityLogAudit = activityLogCandidates.Select(alc => new AuditCandidate
        {
            Timestamp = alc.Timestamp,
            ActorType = alc.ActorType,
            ActorId = alc.ActorId,
            EventType = alc.EventType,
            EntityId = alc.ResourceId,
            EventPayload = null,
            CorrelationId = Guid.TryParse(alc.CorrelationId, out var corrId) ? corrId : Guid.Empty,
            IsAuthoritative = true,
            CauseType = alc.CauseType // Pre-categorized by Activity Log client
        }).ToList();

        // Combine and prioritize Activity Log events
        var combined = activityLogAudit.Concat(internalAudit)
            .OrderByDescending(a => a.IsAuthoritative)
            .ThenByDescending(a => a.Timestamp)
            .Take(500)
            .ToList();

        _logger.LogInformation(
            "Candidateevents for drift correlation: {ActivityLogCount} from Activity Log, {InternalCount} from internal audit",
            activityLogAudit.Count, internalAudit.Count);

        return combined;
    }

    private static DriftCauseDto? InferCause(
        DriftSnapshot snapshot,
        Guid serviceGroupId,
        List<AuditCandidate> candidates)
    {
        if (candidates.Count == 0)
        {
            return null;
        }

        var sgId = serviceGroupId.ToString();

        // Prioritize authoritative Activity Log events
        var nearby = candidates
            .Select(c => new
            {
                Event = c,
                DistanceMinutes = Math.Abs((snapshot.SnapshotTime - c.Timestamp).TotalMinutes)
            })
            .Where(x => x.DistanceMinutes <= 120) // ±2 hour window
            .OrderBy(x => x.Event.IsAuthoritative ? 0 : 1) // Activity Log first
            .ThenBy(x => x.DistanceMinutes)
            .ThenByDescending(x => string.Equals(x.Event.EntityId, sgId, StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault();

        if (nearby is null)
        {
            return null;
        }

        var eventType = nearby.Event.EventType?.ToLowerInvariant() ?? string.Empty;
        var actorType = nearby.Event.ActorType?.ToLowerInvariant() ?? string.Empty;

        // Use Activity Log pre-categorization if available (more accurate)
        var causeType = nearby.Event.CauseType;
        if (string.IsNullOrWhiteSpace(causeType))
        {
            // Fallback to pattern-based inference for internal audit events
            causeType = eventType.Contains("deploy") || eventType.Contains("publish") ||
                        eventType.Contains("pipeline") || eventType.Contains("gitops")
                ? "PipelineDeployment"
                : eventType.Contains("policy") || eventType.Contains("defender") || eventType.Contains("psrule")
                    ? "PolicyEffect"
                    : eventType.Contains("autoscale") || eventType.Contains("scale")
                        ? "PlatformScaling"
                        : actorType == "user"
                            ? "ManualChange"
                            : "UnknownChange";
        }

        // Enhanced confidence scoring
        // Activity Log events get higher base confidence (they're authoritative)
        var baseConfidence = nearby.Event.IsAuthoritative ? 0.85m : 0.6m;

        // Time proximity bonus
        baseConfidence += nearby.DistanceMinutes switch
        {
            <= 5 => 0.10m,   // Very close in time: high confidence
            <= 15 => 0.05m,  // Close in time: medium confidence
            _ => 0m
        };

        // Entity match bonus
        if (string.Equals(nearby.Event.EntityId, sgId, StringComparison.OrdinalIgnoreCase))
        {
            baseConfidence += 0.05m;
        }

        // Cap at 0.95 (never 100% certain without human confirmation)
        if (baseConfidence > 0.95m)
        {
            baseConfidence = 0.95m;
        }

        return new DriftCauseDto
        {
            CauseType = causeType,
            Actor = nearby.Event.ActorId,
            Source = nearby.Event.EventType,
            EventTime = nearby.Event.Timestamp,
            Confidence = baseConfidence,
            SourceEventId = nearby.Event.CorrelationId.ToString(),
            IsAuthoritative = nearby.Event.IsAuthoritative
        };
    }

    private static DriftSnapshotDto ToDto(DriftSnapshot s, DriftCauseDto? cause) => new()
    {
        Id = s.Id,
        ServiceGroupId = s.ServiceGroupId,
        SnapshotTime = s.SnapshotTime,
        TotalViolations = s.TotalViolations,
        CriticalViolations = s.CriticalViolations,
        HighViolations = s.HighViolations,
        MediumViolations = s.MediumViolations,
        LowViolations = s.LowViolations,
        DriftScore = s.DriftScore,
        CategoryBreakdown = s.CategoryBreakdown,
        TrendAnalysis = s.TrendAnalysis,
        CreatedAt = s.CreatedAt,
        CauseType = cause?.CauseType,
        CauseActor = cause?.Actor,
        CauseSource = cause?.Source,
        CauseEventTime = cause?.EventTime,
        CauseConfidence = cause?.Confidence,
        CauseEventId = cause?.SourceEventId,
        CauseIsAuthoritative = cause?.IsAuthoritative
    };

    /// <summary>
    /// Get violation counts grouped by DriftCategory for a service group.
    /// Powers the 5-type drift intelligence dashboard.
    /// </summary>
    [HttpGet("categories/{serviceGroupId}")]
    [ProducesResponseType(typeof(List<DriftCategorySummaryDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<List<DriftCategorySummaryDto>>> GetDriftCategoriesAsync(
        [FromRoute, Required] Guid serviceGroupId,
        [FromQuery(Name = "api-version")] string? apiVersion = null)
    {
        if (string.IsNullOrWhiteSpace(apiVersion))
            return this.ProblemBadRequest("MissingApiVersionParameter", "api-version query parameter is required");

        var categories = await _db.BestPracticeViolations
            .Where(v => v.ServiceGroupId == serviceGroupId && v.Status == "active")
            .GroupBy(v => v.DriftCategory ?? "ConfigurationDrift")
            .Select(g => new DriftCategorySummaryDto
            {
                Category = g.Key,
                ViolationCount = g.Count(),
                CriticalCount = g.Count(v => v.Severity == "critical"),
                HighCount = g.Count(v => v.Severity == "high"),
            })
            .ToListAsync();

        // Ensure all 5 types are present
        var allTypes = new[] { "ConfigurationDrift", "CostDrift", "ComplianceDrift", "PerformanceDrift", "SecurityDrift" };
        var existing = categories.ToDictionary(c => c.Category);
        foreach (var t in allTypes)
        {
            if (!existing.ContainsKey(t))
                categories.Add(new DriftCategorySummaryDto { Category = t });
        }

        return Ok(categories.OrderBy(c => c.Category).ToList());
    }
}

internal sealed record AuditCandidate
{
    public DateTime Timestamp { get; init; }
    public string? ActorType { get; init; }
    public string? ActorId { get; init; }
    public string? EventType { get; init; }
    public string? EntityId { get; init; }
    public string? EventPayload { get; init; }
    public Guid CorrelationId { get; init; }
    public bool IsAuthoritative { get; init; } // True for Activity Log, false for internal audit
    public string? CauseType { get; init; } // Pre-categorized for Activity Log events
}

internal sealed record ActivityLogCandidate
{
    public DateTime Timestamp { get; init; }
    public string ActorId { get; init; } = string.Empty;
    public string ActorType { get; init; } = string.Empty;
    public string EventType { get; init; } = string.Empty;
    public string ResourceId { get; init; } = string.Empty;
    public string? CorrelationId { get; init; }
    public string Status { get; init; } = string.Empty;
    public string Category { get; init; } = string.Empty;
    public string CauseType { get; init; } = string.Empty;
    public bool IsAuthoritative { get; init; } = true;
}

internal sealed record DriftCauseDto
{
    public string CauseType { get; init; } = string.Empty;
    public string? Actor { get; init; }
    public string? Source { get; init; }
    public DateTime EventTime { get; init; }
    public decimal Confidence { get; init; }
    public string SourceEventId { get; init; } = string.Empty;
    public bool IsAuthoritative { get; init; } // True if from Activity Log
}

// DTOs
public record DriftSnapshotDto
{
    public Guid Id { get; init; }
    public Guid ServiceGroupId { get; init; }
    public DateTime SnapshotTime { get; init; }
    public int TotalViolations { get; init; }
    public int CriticalViolations { get; init; }
    public int HighViolations { get; init; }
    public int MediumViolations { get; init; }
    public int LowViolations { get; init; }
    public decimal DriftScore { get; init; }
    public string? CategoryBreakdown { get; init; }
    public string? TrendAnalysis { get; init; }
    public DateTime CreatedAt { get; init; }
    public string? CauseType { get; init; }
    public string? CauseActor { get; init; }
    public string? CauseSource { get; init; }
    public DateTime? CauseEventTime { get; init; }
    public decimal? CauseConfidence { get; init; }
    public string? CauseEventId { get; init; }
    public bool? CauseIsAuthoritative { get; init; } // True if from Azure Activity Log
}

public record CreateDriftSnapshotRequest
{
    [Required]
    public Guid ServiceGroupId { get; init; }

    public int TotalViolations { get; init; }
    public int CriticalViolations { get; init; }
    public int HighViolations { get; init; }
    public int MediumViolations { get; init; }
    public int LowViolations { get; init; }

    [Range(0, 100)]
    public decimal DriftScore { get; init; }

    public string? CategoryBreakdown { get; init; }
    public string? TrendAnalysis { get; init; }
}

public record DriftTrendAnalysisDto
{
    public Guid ServiceGroupId { get; init; }
    public int PeriodDays { get; init; }
    public List<DriftSnapshotDto> Snapshots { get; init; } = new();
    public string TrendDirection { get; init; } = "stable";
    public decimal AverageScore { get; init; }
    public decimal ScoreChange { get; init; }
    public Dictionary<string, int> CategoryTrends { get; init; } = new();
}

public record DriftCategorySummaryDto
{
    public string Category { get; init; } = "";
    public int ViolationCount { get; init; }
    public int CriticalCount { get; init; }
    public int HighCount { get; init; }
}
