using Atlas.ControlPlane.Domain.Entities;
using Atlas.ControlPlane.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Atlas.ControlPlane.Application.Audit;

/// <summary>
/// T057: Evidence/audit queries for release attestation and promotion decisions
/// </summary>
public class AuditQueryService
{
    private readonly AtlasDbContext _context;
    private readonly ILogger<AuditQueryService> _logger;

    public AuditQueryService(
        AtlasDbContext context,
        ILogger<AuditQueryService> logger)
    {
        _context = context;
        _logger = logger;
    }

    /// <summary>
    /// Query audit events for a specific entity
    /// </summary>
    public async Task<List<AuditEvent>> GetEntityAuditTrailAsync(
        string entityType,
        string entityId,
        CancellationToken cancellationToken = default)
    {
        return await _context.AuditEvents
            .Where(e => e.EntityType == entityType && e.EntityId == entityId)
            .OrderByDescending(e => e.Timestamp)
            .AsNoTracking()
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Query release attestations for a release
    /// </summary>
    public async Task<ReleaseAttestationReport> GetReleaseAttestationReportAsync(
        string releaseId,
        CancellationToken cancellationToken = default)
    {
        var attestations = await _context.ReleaseAttestations
            .Where(a => a.ReleaseId == releaseId)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var report = new ReleaseAttestationReport
        {
            ReleaseId = releaseId,
            TotalComponents = attestations.Count,
            PassedComponents = attestations.Count(a => a.ValidationPassed),
            FailedComponents = attestations.Count(a => !a.ValidationPassed),
            MocksDetected = attestations.Any(a => a.MockDetectionResult == "detected"),
            Attestations = attestations
        };

        _logger.LogInformation(
            "Release attestation report for {ReleaseId}: {Passed}/{Total} components passed",
            releaseId,
            report.PassedComponents,
            report.TotalComponents);

        return report;
    }

    /// <summary>
    /// Query recommendation approval history
    /// </summary>
    public async Task<List<ApprovalDecisionSummary>> GetRecommendationApprovalHistoryAsync(
        Guid recommendationId,
        CancellationToken cancellationToken = default)
    {
        var auditEvents = await _context.AuditEvents
            .Where(e => e.EntityType == "recommendation" &&
                       e.EntityId == recommendationId.ToString() &&
                       (e.EventType == "recommendation_approved" || e.EventType == "recommendation_rejected"))
            .OrderBy(e => e.Timestamp)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        return auditEvents.Select(e => new ApprovalDecisionSummary
        {
            RecommendationId = recommendationId,
            DecisionType = e.EventType?.Replace("recommendation_", "") ?? "unknown",
            DecisionBy = e.UserId ?? e.ActorId,
            DecisionAt = e.Timestamp,
            Comments = ExtractCommentsFromPayload(e.EventPayload)
        }).ToList();
    }

    /// <summary>
    /// Query compliance audit events
    /// </summary>
    public async Task<ComplianceAuditReport> GetComplianceAuditReportAsync(
        DateTime startDate,
        DateTime endDate,
        CancellationToken cancellationToken = default)
    {
        var auditEvents = await _context.AuditEvents
            .Where(e => e.Timestamp >= startDate && e.Timestamp <= endDate)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var report = new ComplianceAuditReport
        {
            StartDate = startDate,
            EndDate = endDate,
            TotalEvents = auditEvents.Count,
            EventsByType = auditEvents
                .GroupBy(e => e.EventType ?? "unknown")
                .ToDictionary(g => g.Key, g => g.Count()),
            CriticalEvents = auditEvents
                .Where(e => (e.EventType ?? string.Empty).Contains("failed", StringComparison.OrdinalIgnoreCase)
                    || (e.EventType ?? string.Empty).Contains("rejected", StringComparison.OrdinalIgnoreCase))
                .ToList()
        };

        _logger.LogInformation(
            "Compliance audit report: {TotalEvents} events from {StartDate} to {EndDate}",
            report.TotalEvents,
            startDate,
            endDate);

        return report;
    }

    private string ExtractCommentsFromPayload(string? payload)
    {
        if (string.IsNullOrEmpty(payload))
        {
            return string.Empty;
        }

        try
        {
            var doc = System.Text.Json.JsonDocument.Parse(payload);
            if (doc.RootElement.TryGetProperty("comments", out var commentsElement))
            {
                return commentsElement.GetString() ?? string.Empty;
            }
            if (doc.RootElement.TryGetProperty("reason", out var reasonElement))
            {
                return reasonElement.GetString() ?? string.Empty;
            }
        }
        catch
        {
            // Fallback to empty if JSON parsing fails
        }

        return string.Empty;
    }
}

public class ReleaseAttestationReport
{
    public string ReleaseId { get; set; } = string.Empty;
    public int TotalComponents { get; set; }
    public int PassedComponents { get; set; }
    public int FailedComponents { get; set; }
    public bool MocksDetected { get; set; }
    public List<ReleaseAttestation> Attestations { get; set; } = new();
}

// Query DTOs (avoid conflict with domain entities)
public class ApprovalDecisionSummary
{
    public Guid RecommendationId { get; set; }
    public string DecisionType { get; set; } = string.Empty;
    public string DecisionBy { get; set; } = string.Empty;
    public DateTime DecisionAt { get; set; }
    public string Comments { get; set; } = string.Empty;
}

public class ComplianceAuditReport
{
    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }
    public int TotalEvents { get; set; }
    public Dictionary<string, int> EventsByType { get; set; } = new();
    public List<AuditEvent> CriticalEvents { get; set; } = new();
}
