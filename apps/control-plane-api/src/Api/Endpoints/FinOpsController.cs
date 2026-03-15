using Atlas.ControlPlane.Api.Middleware;
using Atlas.ControlPlane.Domain.Entities;
using Atlas.ControlPlane.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace Atlas.ControlPlane.Api.Endpoints;

/// <summary>
/// Surfaces FinOps intelligence: orphaned resource detection, cost waste analysis, and Log Analytics efficiency.
/// Data is written by the agent orchestrator as agent_messages with MessageType = 'finops.orphanDetection'.
/// </summary>
[ApiController]
[Route("api/v1/finops")]
[Authorize(Policy = "AnalysisRead")]
public class FinOpsController : ControllerBase
{
    private readonly AtlasDbContext _db;
    private readonly ILogger<FinOpsController> _logger;
    private static readonly JsonSerializerOptions JsonReadOptions = new(JsonSerializerDefaults.Web);

    public FinOpsController(AtlasDbContext db, ILogger<FinOpsController> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// Get orphaned resource detection results for a service group from its latest (or specified) analysis run.
    /// Returns each orphaned resource with its estimated monthly cost and a CLI deletion command.
    /// </summary>
    /// <param name="serviceGroupId">The service group to query orphan results for.</param>
    /// <param name="analysisRunId">Optional: target a specific run. Defaults to the latest completed run.</param>
    /// <param name="minMonthlyCost">Filter: only return resources above this monthly cost threshold.</param>
    /// <param name="resourceType">Filter by resource type (e.g. "Microsoft.Compute/disks").</param>
    /// <param name="cancellationToken"></param>
    [HttpGet("orphaned-resources")]
    [ProducesResponseType(typeof(OrphanedResourcesResponse), 200)]
    [ProducesResponseType(404)]
    public async Task<IActionResult> GetOrphanedResources(
        [FromQuery] Guid serviceGroupId,
        [FromQuery] Guid? analysisRunId = null,
        [FromQuery] decimal minMonthlyCost = 0,
        [FromQuery] string? resourceType = null,
        CancellationToken cancellationToken = default)
    {
        var runId = analysisRunId;

        if (runId is null)
        {
            // Resolve most-recent completed run for the service group
            var latestRun = await _db.AnalysisRuns
                .AsNoTracking()
                .Where(r => r.ServiceGroupId == serviceGroupId && r.Status == AnalysisRunStatus.Completed)
                .OrderByDescending(r => r.CompletedAt)
                .Select(r => r.Id)
                .FirstOrDefaultAsync(cancellationToken);

            if (latestRun == Guid.Empty)
            {
                return this.ProblemNotFound(
                    "NoCompletedAnalysisRun",
                    $"No completed analysis run found for service group {serviceGroupId}. Trigger an analysis first.");
            }

            runId = latestRun;
        }

        var orphanMessage = await _db.AgentMessages
            .AsNoTracking()
            .Where(m => m.AnalysisRunId == runId && m.MessageType == "finops.orphanDetection")
            .OrderByDescending(m => m.CreatedAt)
            .Select(m => new { m.Payload, m.CreatedAt })
            .FirstOrDefaultAsync(cancellationToken);

        if (orphanMessage?.Payload is null)
        {
            return this.ProblemNotFound(
                "OrphanDetectionNotRun",
                $"No orphan detection results found for analysis run {runId}. The FinOps agent may not have run orphan detection.");
        }

        OrphanPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<OrphanPayload>(orphanMessage.Payload, JsonReadOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse orphan detection payload for run {RunId}", runId);
            return StatusCode(500, new { error = "Failed to parse orphan detection results" });
        }

        if (payload is null) return Ok(new OrphanedResourcesResponse { AnalysisRunId = runId.Value });

        var resources = (payload.Resources ?? Enumerable.Empty<OrphanResourceItem>())
            .Where(r => r.EstimatedMonthlyCost >= minMonthlyCost)
            .Where(r => resourceType is null || r.ResourceType?.Equals(resourceType, StringComparison.OrdinalIgnoreCase) == true)
            .OrderByDescending(r => r.EstimatedMonthlyCost)
            .ToList();

        var byType = payload.ByResourceType ?? new Dictionary<string, int>();

        return Ok(new OrphanedResourcesResponse
        {
            ServiceGroupId = serviceGroupId,
            AnalysisRunId = runId.Value,
            DetectedAt = orphanMessage.CreatedAt,
            TotalOrphanedCount = payload.OrphanedResourceCount,
            TotalEstimatedMonthlyCost = payload.TotalEstimatedMonthlyCost,
            ByResourceType = byType,
            Resources = resources
        });
    }

    // ─── DTOs ──────────────────────────────────────────────────────────────────

    private sealed class OrphanPayload
    {
        public int OrphanedResourceCount { get; set; }
        public decimal TotalEstimatedMonthlyCost { get; set; }
        public Dictionary<string, int>? ByResourceType { get; set; }
        public IEnumerable<OrphanResourceItem>? Resources { get; set; }
    }
}

public sealed class OrphanedResourcesResponse
{
    public Guid ServiceGroupId { get; set; }
    public Guid AnalysisRunId { get; set; }
    public DateTime DetectedAt { get; set; }
    public int TotalOrphanedCount { get; set; }
    public decimal TotalEstimatedMonthlyCost { get; set; }
    public Dictionary<string, int> ByResourceType { get; set; } = new();

    /// <summary>Individual orphaned resources, ordered by estimated monthly cost descending.</summary>
    public IEnumerable<OrphanResourceItem> Resources { get; set; } = Enumerable.Empty<OrphanResourceItem>();
}

public sealed class OrphanResourceItem
{
    public string? ResourceId { get; set; }
    public string? ResourceType { get; set; }
    public string? ResourceName { get; set; }
    public string? ResourceGroup { get; set; }
    public string? Location { get; set; }
    public decimal EstimatedMonthlyCost { get; set; }
    public string? OrphanReason { get; set; }
    public string? DeletionCommand { get; set; }
    public string? PowerShellCommand { get; set; }
}
