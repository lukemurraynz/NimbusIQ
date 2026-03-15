using Atlas.ControlPlane.Api.Middleware;
using Atlas.ControlPlane.Domain.Entities;
using Atlas.ControlPlane.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Atlas.ControlPlane.Api.Endpoints;

[ApiController]
[Route("api/v1/timeline")]
[Authorize(Policy = "AnalysisRead")]
public class TimelineController : ControllerBase
{
    private readonly AtlasDbContext _db;

    public TimelineController(AtlasDbContext db)
    {
        _db = db;
    }

    [HttpGet]
    public async Task<IActionResult> GetTimeline(
        [FromQuery] string serviceGroupId = "default",
        [FromQuery] int days = 30,
        [FromQuery(Name = "api-version")] string? apiVersion = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(apiVersion))
            return this.ProblemBadRequest("MissingApiVersionParameter", "api-version query parameter is required");

        var resolvedServiceGroupId = await ResolveServiceGroupIdAsync(serviceGroupId, cancellationToken);
        if (resolvedServiceGroupId == null)
        {
            return Ok(new { historicalEvents = Array.Empty<object>(), projectedEvents = Array.Empty<object>() });
        }

        var end = DateTime.UtcNow;
        var start = end.AddDays(-Math.Max(1, Math.Min(days, 365)));

        // Read rich timeline events written by AnalysisOrchestrationService
        var rawTimelineEvents = await _db.TimelineEvents
            .AsNoTracking()
            .Where(e => e.ServiceGroupId == resolvedServiceGroupId
                     && e.EventTime >= start && e.EventTime <= end)
            .OrderByDescending(e => e.EventTime)
            .Take(100)
            .Select(e => new { e.Id, e.EventType, e.EventCategory, e.EventTime, e.ScoreImpact, e.DeltaSummary, e.EventPayload })
            .ToListAsync(cancellationToken);

        // Also include approval decisions as governance events
        var decisionEvents = await _db.ApprovalDecisions
            .AsNoTracking()
            .Where(d => d.Recommendation.ServiceGroupId == resolvedServiceGroupId
                     && d.DecidedAt >= start && d.DecidedAt <= end)
            .OrderByDescending(d => d.DecidedAt)
            .Take(50)
            .Select(d => new
            {
                d.Id,
                d.Decision,
                d.DecidedAt,
                RecommendationTitle = d.Recommendation.Title,
                RecommendationPriority = d.Recommendation.Priority,
            })
            .ToListAsync(cancellationToken);

        // Merge raw timeline events + decision events into a unified list
        var mergedEvents = new List<TimelineEventRecord>();

        foreach (var e in rawTimelineEvents)
        {
            string? description = null;
            string? impact = null;
            object details = new { };
            if (e.EventPayload is not null)
            {
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(e.EventPayload);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("description", out var desc)) description = desc.GetString();
                    if (root.TryGetProperty("impact", out var imp)) impact = imp.GetString();
                    details = System.Text.Json.JsonSerializer.Deserialize<object>(e.EventPayload) ?? new { };
                }
                catch { /* payload is best-effort */ }
            }
            mergedEvents.Add(new TimelineEventRecord(
                e.Id, e.EventType, e.EventCategory, e.EventTime,
                description, impact, e.ScoreImpact, e.DeltaSummary, details));
        }

        foreach (var d in decisionEvents)
        {
            mergedEvents.Add(new TimelineEventRecord(
                d.Id,
                $"recommendation_{d.Decision}",
                "governance",
                d.DecidedAt,
                $"{char.ToUpper(d.Decision[0])}{d.Decision[1..]}: {d.RecommendationTitle}",
                d.RecommendationPriority,
                null,
                null,
                new { decision = d.Decision, title = d.RecommendationTitle }));
        }

        var historicalEvents = mergedEvents
            .OrderByDescending(e => e.Timestamp)
            .Take(100)
            .Select(e => new
            {
                id = e.Id,
                eventType = e.EventType,
                eventCategory = e.EventCategory,
                timestamp = e.Timestamp,
                description = e.Description,
                impact = e.Impact,
                scoreImpact = e.ScoreImpact,
                deltaSummary = e.DeltaSummary,
                details = e.Details,
            });

        var projectedEvents = await _db.Recommendations
            .AsNoTracking()
            .Where(r => r.ServiceGroupId == resolvedServiceGroupId && r.Status == "pending")
            .OrderByDescending(r => r.CreatedAt)
            .Take(25)
            .Select(r => new
            {
                eventType = "approval_due",
                projectedDate = r.ValidUntil ?? end.AddDays(7),
                description = $"Approval required: {r.Title}",
                confidence = r.Confidence,
                impact = r.Priority,
                rationale = "Pending human decision before any production-impacting action can proceed."
            })
            .ToListAsync(cancellationToken);

        return Ok(new { historicalEvents, projectedEvents });
    }

    private async Task<Guid?> ResolveServiceGroupIdAsync(string serviceGroupId, CancellationToken cancellationToken)
    {
        if (string.Equals(serviceGroupId, "default", StringComparison.OrdinalIgnoreCase))
        {
            // Prefer the service group with the most recent completed/partial analysis run
            // so that "default" always resolves to a service group with actual data.
            var mostRecentAnalysedId = await _db.AnalysisRuns
                .AsNoTracking()
                .Where(r => r.Status == AnalysisRunStatus.Completed || r.Status == AnalysisRunStatus.Partial)
                .OrderByDescending(r => r.CompletedAt)
                .Select(r => (Guid?)r.ServiceGroupId)
                .FirstOrDefaultAsync(cancellationToken);

            if (mostRecentAnalysedId.HasValue)
                return mostRecentAnalysedId;

            // Fallback: first service group by creation date (tie-broken by Id for determinism)
            return await _db.ServiceGroups
                .AsNoTracking()
                .OrderBy(sg => sg.CreatedAt).ThenBy(sg => sg.Id)
                .Select(sg => (Guid?)sg.Id)
                .FirstOrDefaultAsync(cancellationToken);
        }

        return Guid.TryParse(serviceGroupId, out var id) ? id : null;
    }

    private record TimelineEventRecord(
        Guid Id,
        string EventType,
        string? EventCategory,
        DateTime Timestamp,
        string? Description,
        string? Impact,
        double? ScoreImpact,
        string? DeltaSummary,
        object Details);
}
