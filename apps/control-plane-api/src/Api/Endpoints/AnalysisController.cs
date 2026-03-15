using Atlas.ControlPlane.Api.Middleware;
using Atlas.ControlPlane.Domain.Entities;
using Atlas.ControlPlane.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using System.Text.Json;

namespace Atlas.ControlPlane.Api.Endpoints;

[ApiController]
[Route("api/v1/analysis")]
[Authorize(Policy = "AnalysisRead")]
public class AnalysisController : ControllerBase
{
    private readonly AtlasDbContext _context;
    private readonly ILogger<AnalysisController> _logger;

    public AnalysisController(AtlasDbContext context, ILogger<AnalysisController> logger)
    {
        _context = context;
        _logger = logger;
    }

    [HttpPost("start")]
    [Authorize(Policy = "AnalysisWrite")]
    public async Task<ActionResult<AnalysisRunDto>> StartAnalysis(
        [FromBody] StartAnalysisRequest request,
        CancellationToken cancellationToken = default)
    {
        var serviceGroupExists = await _context.ServiceGroups
            .AnyAsync(sg => sg.Id == request.ServiceGroupId, cancellationToken);

        if (!serviceGroupExists)
        {
            return this.ProblemNotFound("ServiceGroupNotFound", $"Service group {request.ServiceGroupId} not found");
        }

        var analysisRun = new AnalysisRun
        {
            Id = Guid.NewGuid(),
            ServiceGroupId = request.ServiceGroupId,
            CorrelationId = Guid.NewGuid(),
            Status = AnalysisRunStatus.Queued,
            TriggeredBy =
                User.FindFirstValue("preferred_username") ??
                User.FindFirstValue("oid") ??
                User.Identity?.Name ??
                "unknown",
            CreatedAt = DateTime.UtcNow
        };

        _context.AnalysisRuns.Add(analysisRun);
        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Started analysis run {AnalysisRunId} for service group {ServiceGroupId} with correlation {CorrelationId}",
            analysisRun.Id, request.ServiceGroupId, analysisRun.CorrelationId);

        // BackgroundAnalysisService polls the database for "queued" runs every 5 s
        // and will process this run automatically — no additional queuing step required.

        // LRO: 202 Accepted with operation-location pointing to the status monitor endpoint.
        var statusUrl = Url.Action(nameof(GetAnalysisStatus), new { id = analysisRun.Id });
        Response.Headers["operation-location"] = statusUrl;
        Response.Headers["Retry-After"] = "5";

        return Accepted(statusUrl, new AnalysisRunDto
        {
            Id = analysisRun.Id,
            ServiceGroupId = analysisRun.ServiceGroupId,
            Status = analysisRun.Status,
            CorrelationId = analysisRun.CorrelationId.ToString(),
            InitiatedAt = analysisRun.CreatedAt
        });
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<AnalysisRunDetailDto>> GetAnalysisStatus(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var analysisRun = await _context.AnalysisRuns
            .AsNoTracking()
            .Include(ar => ar.Snapshot)
            .FirstOrDefaultAsync(ar => ar.Id == id, cancellationToken);

        if (analysisRun == null)
        {
            return this.ProblemNotFound("AnalysisRunNotFound", $"Analysis run {id} not found");
        }

        var resourcesDiscovered = 0;
        if (analysisRun.SnapshotId.HasValue)
        {
            resourcesDiscovered = await _context.DiscoveredResources
                .AsNoTracking()
                .CountAsync(dr => dr.SnapshotId == analysisRun.SnapshotId.Value, cancellationToken);
        }

        var confidence = ConfidenceFromTelemetryHealth(analysisRun.Snapshot?.TelemetryHealth);

        // LRO polling: add Retry-After for non-terminal states so clients know when to re-poll.
        if (!AnalysisRunStatus.IsTerminal(analysisRun.Status))
        {
            Response.Headers["Retry-After"] = "5";
        }

        return Ok(new AnalysisRunDetailDto
        {
            Id = analysisRun.Id,
            ServiceGroupId = analysisRun.ServiceGroupId,
            Status = analysisRun.Status,
            CorrelationId = analysisRun.CorrelationId.ToString(),
            InitiatedAt = analysisRun.CreatedAt,
            InitiatedBy = analysisRun.TriggeredBy,
            CompletedAt = analysisRun.CompletedAt,
            ErrorMessage = null,
            ResourcesDiscovered = resourcesDiscovered,
            ConfidenceScore = confidence.score,
            ConfidenceLevel = confidence.level,
            DegradationFactors = confidence.factors
        });
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<AnalysisRunDto>>> ListAnalysisRuns(
        [FromQuery] Guid? serviceGroupId = null,
        [FromQuery] string? status = null,
        [FromQuery] int limit = 50,
        CancellationToken cancellationToken = default)
    {
        var query = _context.AnalysisRuns.AsNoTracking();

        if (serviceGroupId.HasValue)
        {
            query = query.Where(ar => ar.ServiceGroupId == serviceGroupId.Value);
        }

        if (!string.IsNullOrEmpty(status))
        {
            query = query.Where(ar => ar.Status == status);
        }

        var analysisRuns = await query
            .OrderByDescending(ar => ar.CreatedAt)
            .Take(limit)
            .Select(ar => new AnalysisRunDto
            {
                Id = ar.Id,
                ServiceGroupId = ar.ServiceGroupId,
                Status = ar.Status,
                CorrelationId = ar.CorrelationId.ToString(),
                InitiatedAt = ar.CreatedAt
            })
            .ToListAsync(cancellationToken);

        return Ok(analysisRuns);
    }

    [HttpGet("{id}/messages")]
    public async Task<ActionResult<IEnumerable<AgentMessageDto>>> GetAgentMessages(
        Guid id,
        [FromQuery(Name = "api-version")] string? apiVersion,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 100,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(apiVersion))
            return this.ProblemBadRequest("MissingApiVersionParameter", "api-version query parameter is required");

        var exists = await _context.AnalysisRuns.AsNoTracking()
            .AnyAsync(ar => ar.Id == id, cancellationToken);

        if (!exists)
        {
            return this.ProblemNotFound("AnalysisRunNotFound", $"Analysis run {id} not found");
        }

        const int maxPageSize = 200;
        var skip = Math.Max(0, (page - 1) * pageSize);
        var take = Math.Clamp(pageSize, 1, maxPageSize);

        var query = _context.AgentMessages.AsNoTracking()
            .Where(m => m.AnalysisRunId == id)
            .OrderBy(m => m.CreatedAt);

        var totalCount = await query.CountAsync(cancellationToken);

        var messages = await query
            .Skip(skip)
            .Take(take)
            .Select(m => new AgentMessageDto
            {
                Id = m.Id,
                MessageId = m.MessageId,
                ParentMessageId = m.ParentMessageId,
                AgentName = m.AgentName,
                AgentRole = m.AgentRole,
                MessageType = m.MessageType,
                Payload = m.Payload,
                Confidence = m.Confidence,
                CreatedAt = m.CreatedAt
            })
            .ToListAsync(cancellationToken);

        // Build nextLink preserving api-version and pagination params.
        // Uses a relative path like AuditController so it works without knowing
        // the current scheme/host at compile time.
        string? nextLink = null;
        if ((skip + messages.Count) < totalCount)
        {
            var qs = new System.Text.StringBuilder();
            qs.Append($"/api/v1/analysis/{id}/messages?api-version={Uri.EscapeDataString(apiVersion)}&page={page + 1}&pageSize={take}");
            nextLink = qs.ToString();
        }

        return Ok(new { value = messages, nextLink, totalCount });
    }

    private static (decimal? score, string? level, List<string> factors) ConfidenceFromTelemetryHealth(string? telemetryHealthJson)
    {
        if (string.IsNullOrWhiteSpace(telemetryHealthJson))
        {
            return (null, null, new List<string>());
        }

        try
        {
            using var doc = JsonDocument.Parse(telemetryHealthJson);
            if (!doc.RootElement.TryGetProperty("confidence", out var confidence))
            {
                return (null, null, new List<string>());
            }

            var score = confidence.TryGetProperty("value", out var v) && v.TryGetDecimal(out var d) ? d : (decimal?)null;
            var level = confidence.TryGetProperty("level", out var l) ? l.GetString() : null;

            var factors = new List<string>();
            if (confidence.TryGetProperty("degradationFactors", out var f) && f.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in f.EnumerateArray())
                {
                    var s = item.GetString();
                    if (!string.IsNullOrWhiteSpace(s)) factors.Add(s);
                }
            }

            return (score, level, factors);
        }
        catch
        {
            return (null, null, new List<string>());
        }
    }
}

public record StartAnalysisRequest
{
    public Guid ServiceGroupId { get; init; }
}

public record AnalysisRunDto
{
    public Guid Id { get; init; }
    public Guid ServiceGroupId { get; init; }
    public required string Status { get; init; }
    public required string CorrelationId { get; init; }
    public DateTime InitiatedAt { get; init; }
}

public record AgentMessageDto
{
    public Guid Id { get; init; }
    public Guid MessageId { get; init; }
    public Guid? ParentMessageId { get; init; }
    public required string AgentName { get; init; }
    public required string AgentRole { get; init; }
    public required string MessageType { get; init; }
    public string? Payload { get; init; }
    public decimal? Confidence { get; init; }
    public DateTime CreatedAt { get; init; }
}

public record AnalysisRunDetailDto
{
    public Guid Id { get; init; }
    public Guid ServiceGroupId { get; init; }
    public required string Status { get; init; }
    public required string CorrelationId { get; init; }
    public DateTime InitiatedAt { get; init; }
    public required string InitiatedBy { get; init; }
    public DateTime? CompletedAt { get; init; }
    public string? ErrorMessage { get; init; }
    public int ResourcesDiscovered { get; init; }
    public decimal? ConfidenceScore { get; init; }
    public string? ConfidenceLevel { get; init; }
    public List<string> DegradationFactors { get; init; } = new();
}
