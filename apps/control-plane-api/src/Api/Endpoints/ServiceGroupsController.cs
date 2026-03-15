using System.Diagnostics;
using System.Collections.Concurrent;
using Atlas.ControlPlane.Api.Middleware;
using Atlas.ControlPlane.Application.Recommendations;
using Atlas.ControlPlane.Domain.Entities;
using Atlas.ControlPlane.Infrastructure.Azure;
using Atlas.ControlPlane.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;

namespace Atlas.ControlPlane.Api.Endpoints;

[ApiController]
[Route("api/v1/service-groups")]
[Authorize(Policy = "AnalysisRead")]
public class ServiceGroupsController : ControllerBase
{
    private readonly AtlasDbContext _context;
    private readonly ILogger<ServiceGroupsController> _logger;
    private readonly AzureResourceGraphClient? _resourceGraphClient;
    private readonly bool _allowSubscriptionFallbackByDefault;
    private readonly IServiceScopeFactory _scopeFactory;
    private static readonly ConcurrentDictionary<Guid, DiscoveryOperationState> DiscoveryOperations = new();

    public ServiceGroupsController(
        AtlasDbContext context,
        ILogger<ServiceGroupsController> logger,
        IConfiguration configuration,
        IServiceScopeFactory scopeFactory,
        AzureResourceGraphClient? resourceGraphClient = null)
    {
        _context = context;
        _logger = logger;
        _scopeFactory = scopeFactory;
        _resourceGraphClient = resourceGraphClient;
        _allowSubscriptionFallbackByDefault =
            configuration.GetValue<bool>("ServiceGroupDiscovery:AllowSubscriptionFallback")
            || string.Equals(
                Environment.GetEnvironmentVariable("NIMBUSIQ_DISCOVERY_ALLOW_SUBSCRIPTION_FALLBACK"),
                "true",
                StringComparison.OrdinalIgnoreCase);
    }

    private sealed class DiscoveryOperationState
    {
        public required Guid OperationId { get; init; }
        public required Guid CorrelationId { get; init; }
        public required DateTime CreatedAt { get; init; }
        public DateTime? StartedAt { get; set; }
        public DateTime? CompletedAt { get; set; }
        public string Status { get; set; } = "queued";
        public DiscoverServiceGroupsResponse? Result { get; set; }
        public string? ErrorCode { get; set; }
        public string? ErrorMessage { get; set; }
    }

    [HttpGet]
    public async Task<IActionResult> ListServiceGroups(
        [FromQuery] string? subscriptionId = null,
        [FromQuery] bool includeSubscriptionBacked = false,
        CancellationToken cancellationToken = default)
    {
        var query = _context.ServiceGroups.AsNoTracking();

        if (!includeSubscriptionBacked)
        {
            // Hide synthetic subscription-backed groups by default; these are legacy fallback artifacts.
            query = query.Where(sg => !sg.ExternalKey.StartsWith("/subscriptions/"));
        }

        if (!string.IsNullOrEmpty(subscriptionId))
        {
            query = query.Where(sg => _context.ServiceGroupScopes
                .Any(sgs => sgs.ServiceGroupId == sg.Id && sgs.SubscriptionId == subscriptionId));
        }

        var serviceGroups = await query
            .OrderBy(sg => sg.Name)
            .Select(sg => new ServiceGroupDto
            {
                Id = sg.Id,
                Name = sg.Name,
                Description = sg.Description,
                CreatedAt = sg.CreatedAt,
                ScopeCount = _context.ServiceGroupScopes.Count(sgs => sgs.ServiceGroupId == sg.Id)
            })
            .ToListAsync(cancellationToken);

        return Ok(new { value = serviceGroups });
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<ServiceGroupDetailDto>> GetServiceGroup(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var serviceGroup = await _context.ServiceGroups
            .AsNoTracking()
            .Where(sg => sg.Id == id)
            .Select(sg => new ServiceGroupDetailDto
            {
                Id = sg.Id,
                Name = sg.Name,
                Description = sg.Description,
                CreatedAt = sg.CreatedAt,
                Scopes = sg.Scopes.Select(s => new ServiceGroupScopeDto
                {
                    AzureSubscriptionId = s.SubscriptionId,
                    AzureResourceGroupName = s.ResourceGroup,
                    TagFilter = s.ScopeFilter
                }).ToList()
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (serviceGroup == null)
        {
            return this.ProblemNotFound("ServiceGroupNotFound", $"Service group {id} not found");
        }

        return Ok(serviceGroup);
    }

    [HttpGet("{id}/health")]
    public async Task<IActionResult> GetServiceGroupHealth(
        [FromRoute] Guid id,
        CancellationToken cancellationToken = default)
    {
        var serviceGroup = await _context.ServiceGroups
            .AsNoTracking()
            .Where(sg => sg.Id == id)
            .Select(sg => new
            {
                sg.Id,
                sg.Name,
                sg.Description,
                sg.BusinessOwner,
                sg.CreatedAt
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (serviceGroup == null)
        {
            return this.ProblemNotFound("ServiceGroupNotFound", $"Service group {id} not found");
        }

        var now = DateTime.UtcNow;
        var scoreSnapshots = await _context.ScoreSnapshots
            .AsNoTracking()
            .Where(s => s.ServiceGroupId == id)
            .OrderByDescending(s => s.RecordedAt)
            .ToListAsync(cancellationToken);

        var latestScores = scoreSnapshots
            .GroupBy(s => s.Category)
            .Select(g => g.First())
            .ToDictionary(
                s => s.Category,
                s => new
                {
                    s.Score,
                    s.Confidence,
                    s.ResourceCount,
                    s.RecordedAt,
                    s.Dimensions
                });

        var pending = await _context.Recommendations
            .AsNoTracking()
            .Where(r => r.ServiceGroupId == id)
            .ToListAsync(cancellationToken);

        pending = pending
            .Where(r => RecommendationWorkflowStatus.IsQueueCandidate(r.Status))
            .ToList();

        var queue = pending
            .Select(r => new
            {
                r.Id,
                r.Title,
                r.Priority,
                r.Category,
                r.Status,
                r.ResourceId,
                QueueScore = RecommendationPriorityQueueService.CalculateRiskWeightedScore(r, now),
                DueDate = GetSuggestedDueDate(r.Priority, now)
            })
            .OrderByDescending(x => x.QueueScore)
            .ToList();

        var doNow = queue.Where(r => r.Priority is "critical" or "high" || r.QueueScore >= 0.75).Take(5).ToList();
        var thisWeek = queue.Where(r => !doNow.Select(x => x.Id).Contains(r.Id) && (r.QueueScore >= 0.55 || r.Priority == "medium")).Take(5).ToList();
        var backlog = queue.Where(r => !doNow.Select(x => x.Id).Contains(r.Id) && !thisWeek.Select(x => x.Id).Contains(r.Id)).Take(10).ToList();

        var topRisks = queue
            .Where(r => r.Category == "Reliability" || r.Priority is "critical" or "high")
            .Take(5)
            .ToList();

        var topSavings = pending
            .Where(r => r.Category == "FinOps")
            .Select(r => new
            {
                r.Id,
                r.Title,
                r.Priority,
                MonthlySavings = TryReadMonthlySavings(r.EstimatedImpact),
                Resource = ExtractResourceName(r.ResourceId)
            })
            .OrderByDescending(x => x.MonthlySavings)
            .ThenByDescending(x => x.Priority)
            .Take(5)
            .ToList();

        var reliabilityWeakPoints = new List<object>();
        if (latestScores.TryGetValue("Reliability", out var reliability) &&
            !string.IsNullOrWhiteSpace(reliability.Dimensions))
        {
            foreach (var weak in ParseWeakDimensions(reliability.Dimensions))
            {
                reliabilityWeakPoints.Add(new
                {
                    weak.Dimension,
                    weak.Score,
                    Severity = weak.Score < 0.5 ? "high" : "medium"
                });
            }
        }

        var businessImpact = new
        {
            outageRisk = pending.Count(r => r.Category == "Reliability" && (r.Priority == "critical" || r.Priority == "high")),
            complianceExposure = pending.Count(r =>
                (r.TriggerReason ?? string.Empty).Contains("policy", StringComparison.OrdinalIgnoreCase) ||
                (r.TriggerReason ?? string.Empty).Contains("defender", StringComparison.OrdinalIgnoreCase) ||
                (r.TriggerReason ?? string.Empty).Contains("psrule", StringComparison.OrdinalIgnoreCase)),
            monthlyCostOpportunity = Math.Round(
                pending.Where(r => r.Category == "FinOps").Select(r => TryReadMonthlySavings(r.EstimatedImpact)).Sum(),
                2),
            sustainabilityOpportunity = pending.Count(r => r.Category == "Sustainability")
        };

        return Ok(new
        {
            serviceGroup,
            latestScores,
            pendingRecommendationCount = pending.Count,
            priorityInbox = new
            {
                doNow,
                thisWeek,
                backlog
            },
            businessImpact,
            topRisks,
            topSavings,
            reliabilityWeakPoints
        });
    }

    [HttpGet("{id}/agent-scores")]
    public async Task<IActionResult> GetAgentScores(
        [FromRoute] Guid id,
        CancellationToken cancellationToken = default)
    {
        var exists = await _context.ServiceGroups
            .AsNoTracking()
            .AnyAsync(sg => sg.Id == id, cancellationToken);

        if (!exists)
        {
            return this.ProblemNotFound("ServiceGroupNotFound", $"Service group {id} not found");
        }

        var latestSnapshots = await _context.ScoreSnapshots
            .AsNoTracking()
            .Where(s => s.ServiceGroupId == id)
            .OrderByDescending(s => s.RecordedAt)
            .ToListAsync(cancellationToken);

        var scoreByCategory = latestSnapshots
            .GroupBy(s => s.Category)
            .Select(g => g.First())
            .ToDictionary(s => s.Category, StringComparer.OrdinalIgnoreCase);

        var pendingRecs = await _context.Recommendations
            .AsNoTracking()
            .Where(r => r.ServiceGroupId == id)
            .Where(r => RecommendationWorkflowStatus.GetQueueStatusDatabaseValues().Contains(r.Status))
            .OrderByDescending(r => r.CreatedAt)
            .ToListAsync(cancellationToken);

        var violations = await _context.BestPracticeViolations
            .AsNoTracking()
            .Where(v => v.ServiceGroupId == id && v.Status == "active")
            .OrderByDescending(v => v.DetectedAt)
            .Take(100)
            .ToListAsync(cancellationToken);

        object BuildSection(string category)
        {
            scoreByCategory.TryGetValue(category, out var snapshot);

            var findings = pendingRecs
                .Where(r => string.Equals(r.Category, category, StringComparison.OrdinalIgnoreCase))
                .Take(5)
                .Select(r => new
                {
                    category = r.Category,
                    severity = r.Priority,
                    description = r.Description,
                    impact = r.Impact,
                    affectedResources = new[] { r.ResourceId }
                })
                .ToList();

            var recommendations = pendingRecs
                .Where(r => string.Equals(r.Category, category, StringComparison.OrdinalIgnoreCase))
                .Take(5)
                .Select(r => new
                {
                    action = r.Title,
                    priority = r.Priority,
                    rationale = r.Rationale,
                    expectedImpact = r.Impact,
                    estimatedEffort = EstimateEffort(r.Priority)
                })
                .ToList();

            return new
            {
                score = Math.Round((snapshot?.Score ?? 0) / 100.0, 4),
                confidence = Math.Round(snapshot?.Confidence ?? 0, 4),
                findings,
                recommendations
            };
        }

        var technicalDebtFindings = violations
            .Take(10)
            .Select(v => new
            {
                category = v.DriftCategory ?? "ConfigurationDrift",
                severity = v.Severity,
                description = v.ViolationType,
                impact = $"{v.ResourceType}: expected {v.ExpectedState}, found {v.CurrentState}",
                affectedResources = new[] { v.ResourceId }
            })
            .ToList();

        return Ok(new
        {
            architecture = BuildSection("Architecture"),
            finops = BuildSection("FinOps"),
            reliability = BuildSection("Reliability"),
            sustainability = BuildSection("Sustainability"),
            technicalDebt = new
            {
                score = Math.Round(Math.Clamp(1.0 - (technicalDebtFindings.Count / 20.0), 0.0, 1.0), 4),
                confidence = 0.8,
                findings = technicalDebtFindings
            }
        });
    }

    [HttpPost]
    [Authorize(Policy = "Admin")]
    public async Task<ActionResult<ServiceGroupDto>> CreateServiceGroup(
        [FromBody] CreateServiceGroupRequest request,
        CancellationToken cancellationToken = default)
    {
        var serviceGroup = new ServiceGroup
        {
            Id = Guid.NewGuid(),
            ExternalKey = $"sg-{Guid.NewGuid():N}",
            Name = request.Name,
            Description = request.Description,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _context.ServiceGroups.Add(serviceGroup);

        foreach (var scope in request.Scopes)
        {
            _context.ServiceGroupScopes.Add(new ServiceGroupScope
            {
                Id = Guid.NewGuid(),
                ServiceGroupId = serviceGroup.Id,
                SubscriptionId = scope.AzureSubscriptionId,
                ResourceGroup = scope.AzureResourceGroupName,
                ScopeFilter = scope.TagFilter,
                CreatedAt = DateTime.UtcNow
            });
        }

        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Created service group {ServiceGroupId} with name {Name}",
            serviceGroup.Id, serviceGroup.Name);

        return CreatedAtAction(
            nameof(GetServiceGroup),
            new { id = serviceGroup.Id },
            new ServiceGroupDto
            {
                Id = serviceGroup.Id,
                Name = serviceGroup.Name,
                Description = serviceGroup.Description,
                CreatedAt = serviceGroup.CreatedAt
            });
    }

    private static DateTime GetSuggestedDueDate(string priority, DateTime now) =>
        priority.ToLowerInvariant() switch
        {
            "critical" => now.AddDays(3),
            "high" => now.AddDays(7),
            "medium" => now.AddDays(14),
            _ => now.AddDays(30)
        };

    private static string ExtractResourceName(string armId)
    {
        var segments = armId.Split('/');
        return segments.Length > 1 ? segments[^1] : armId;
    }

    private static double TryReadMonthlySavings(string? estimatedImpactJson)
    {
        if (string.IsNullOrWhiteSpace(estimatedImpactJson))
        {
            return 0;
        }

        try
        {
            using var doc = JsonDocument.Parse(estimatedImpactJson);
            var root = doc.RootElement;

            if (root.TryGetProperty("monthlySavings", out var monthlySavings) &&
                monthlySavings.TryGetDouble(out var monthlyValue))
            {
                return monthlyValue;
            }

            if (root.TryGetProperty("costDelta", out var costDelta) &&
                costDelta.TryGetDouble(out var deltaValue))
            {
                return Math.Max(0, -deltaValue);
            }
        }
        catch (JsonException)
        {
            return 0;
        }

        return 0;
    }

    private static IReadOnlyList<(string Dimension, double Score)> ParseWeakDimensions(string dimensionsJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(dimensionsJson);
            var root = doc.RootElement;

            JsonElement dims = root;
            if (root.TryGetProperty("dimensions", out var nested))
            {
                dims = nested;
            }

            return dims.EnumerateObject()
                .Where(p => p.Value.ValueKind == JsonValueKind.Number)
                .Select(p => (Dimension: p.Name, Score: p.Value.GetDouble()))
                .Where(x => x.Score < 0.75)
                .OrderBy(x => x.Score)
                .Take(5)
                .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string EstimateEffort(string priority) =>
        priority.ToLowerInvariant() switch
        {
            "critical" => "high",
            "high" => "medium",
            "medium" => "medium",
            _ => "low"
        };

    [HttpDelete("{id}")]
    [Authorize(Policy = "Admin")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteServiceGroup(
        [FromRoute] Guid id,
        CancellationToken cancellationToken = default)
    {
        var serviceGroup = await _context.ServiceGroups
            .FirstOrDefaultAsync(sg => sg.Id == id, cancellationToken);

        if (serviceGroup == null)
        {
            return this.ProblemNotFound("ServiceGroupNotFound", $"Service group {id} not found");
        }

        await using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);

        // Break hierarchy links first (both table-driven and direct parent pointer).
        _context.Set<ServiceGroupHierarchy>().RemoveRange(
            await _context.Set<ServiceGroupHierarchy>()
                .Where(h => h.ParentServiceGroupId == id || h.ChildServiceGroupId == id)
                .ToListAsync(cancellationToken));

        var directChildren = await _context.ServiceGroups
            .Where(sg => sg.ParentServiceGroupId == id)
            .ToListAsync(cancellationToken);
        foreach (var child in directChildren)
        {
            child.ParentServiceGroupId = null;
            child.UpdatedAt = DateTime.UtcNow;
        }

        // Defensive cleanup for legacy/optional tables that may exist in older DB schemas
        // but are not always mapped in the current DbContext model.
        await _context.Database.ExecuteSqlInterpolatedAsync($@"
DO $$
BEGIN
    IF to_regclass('public.compliance_assessments') IS NOT NULL THEN
        DELETE FROM public.compliance_assessments WHERE service_group_id = {id};
    END IF;

    IF to_regclass('public.cloud_native_maturity_assessments') IS NOT NULL THEN
        DELETE FROM public.cloud_native_maturity_assessments WHERE service_group_id = {id};
    END IF;

    IF to_regclass('public.sustainability_assessments') IS NOT NULL THEN
        DELETE FROM public.sustainability_assessments WHERE service_group_id = {id};
    END IF;
END
$$;", cancellationToken);

        // Service graph artifacts can have restrictive node-edge relationships.
        var nodeIds = await _context.ServiceNodes
            .Where(n => n.ServiceGroupId == id)
            .Select(n => n.Id)
            .ToListAsync(cancellationToken);
        var domainIds = await _context.ServiceDomains
            .Where(d => d.ServiceGroupId == id)
            .Select(d => d.Id)
            .ToListAsync(cancellationToken);

        if (domainIds.Count > 0)
        {
            var domainIdSet = domainIds.ToHashSet();
            _context.ServiceDomainMemberships.RemoveRange(
                await _context.ServiceDomainMemberships
                    .Where(m => domainIdSet.Contains(m.DomainId))
                    .ToListAsync(cancellationToken));
        }

        if (nodeIds.Count > 0)
        {
            var nodeIdSet = nodeIds.ToHashSet();
            _context.ServiceEdges.RemoveRange(
                await _context.ServiceEdges
                    .Where(e => nodeIdSet.Contains(e.SourceNodeId) || nodeIdSet.Contains(e.TargetNodeId) || e.ServiceGroupId == id)
                    .ToListAsync(cancellationToken));

            _context.ServiceDomainMemberships.RemoveRange(
                await _context.ServiceDomainMemberships
                    .Where(m => nodeIdSet.Contains(m.NodeId))
                    .ToListAsync(cancellationToken));
        }

        _context.ServiceNodes.RemoveRange(
            await _context.ServiceNodes.Where(n => n.ServiceGroupId == id).ToListAsync(cancellationToken));
        _context.ServiceDomains.RemoveRange(
            await _context.ServiceDomains.Where(d => d.ServiceGroupId == id).ToListAsync(cancellationToken));
        _context.ServiceGraphSnapshots.RemoveRange(
            await _context.ServiceGraphSnapshots.Where(s => s.ServiceGroupId == id).ToListAsync(cancellationToken));

        // Direct service-group linked rows.
        _context.ScoreSnapshots.RemoveRange(
            await _context.ScoreSnapshots.Where(s => s.ServiceGroupId == id).ToListAsync(cancellationToken));
        _context.ServiceGroupScopes.RemoveRange(
            await _context.ServiceGroupScopes.Where(s => s.ServiceGroupId == id).ToListAsync(cancellationToken));
        _context.TimelineEvents.RemoveRange(
            await _context.TimelineEvents.Where(t => t.ServiceGroupId == id).ToListAsync(cancellationToken));
        _context.DriftSnapshots.RemoveRange(
            await _context.DriftSnapshots.Where(d => d.ServiceGroupId == id).ToListAsync(cancellationToken));
        _context.BestPracticeViolations.RemoveRange(
            await _context.BestPracticeViolations.Where(v => v.ServiceGroupId == id).ToListAsync(cancellationToken));

        // Break circular references before deleting analysis runs/snapshots:
        // AnalysisRun.SnapshotId -> DiscoverySnapshot.Id and DiscoverySnapshot.AnalysisRunId -> AnalysisRun.Id
        var runsWithSnapshot = await _context.AnalysisRuns
            .Where(r => r.ServiceGroupId == id && r.SnapshotId != null)
            .ToListAsync(cancellationToken);
        foreach (var run in runsWithSnapshot)
        {
            run.SnapshotId = null;
        }

        var snapshotsWithRun = await _context.DiscoverySnapshots
            .Where(s => s.ServiceGroupId == id && s.AnalysisRunId != null)
            .ToListAsync(cancellationToken);
        foreach (var snapshot in snapshotsWithRun)
        {
            snapshot.AnalysisRunId = null;
        }

        if (runsWithSnapshot.Count > 0 || snapshotsWithRun.Count > 0)
        {
            await _context.SaveChangesAsync(cancellationToken);
        }

        var runIds = await _context.AnalysisRuns
            .Where(r => r.ServiceGroupId == id).Select(r => r.Id).ToListAsync(cancellationToken);

        if (runIds.Count > 0)
        {
            var runIdSet = runIds.ToHashSet();
            _context.AgentMessages.RemoveRange(
                await _context.AgentMessages.Where(m => runIdSet.Contains(m.AnalysisRunId)).ToListAsync(cancellationToken));

            var recIds = await _context.Recommendations
                .Where(r => runIdSet.Contains(r.AnalysisRunId)).Select(r => r.Id).ToListAsync(cancellationToken);

            if (recIds.Count > 0)
            {
                var recIdSet = recIds.ToHashSet();
                _context.ApprovalDecisions.RemoveRange(
                    await _context.ApprovalDecisions.Where(a => recIdSet.Contains(a.RecommendationId)).ToListAsync(cancellationToken));
                _context.IacChangeSets.RemoveRange(
                    await _context.IacChangeSets.Where(c => recIdSet.Contains(c.RecommendationId)).ToListAsync(cancellationToken));
            }

            _context.Recommendations.RemoveRange(
                await _context.Recommendations.Where(r => runIdSet.Contains(r.AnalysisRunId)).ToListAsync(cancellationToken));
            _context.AnalysisRuns.RemoveRange(
                await _context.AnalysisRuns.Where(r => r.ServiceGroupId == id).ToListAsync(cancellationToken));
        }

        var snapIds = await _context.DiscoverySnapshots
            .Where(s => s.ServiceGroupId == id).Select(s => s.Id).ToListAsync(cancellationToken);

        if (snapIds.Count > 0)
        {
            var snapIdSet = snapIds.ToHashSet();
            _context.DiscoveredResources.RemoveRange(
                await _context.DiscoveredResources.Where(r => snapIdSet.Contains(r.SnapshotId)).ToListAsync(cancellationToken));
            _context.DiscoverySnapshots.RemoveRange(
                await _context.DiscoverySnapshots.Where(s => s.ServiceGroupId == id).ToListAsync(cancellationToken));
        }

        _context.ServiceGroups.Remove(serviceGroup);
        await _context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        _logger.LogInformation("Deleted service group {ServiceGroupId}", id);

        return NoContent();
    }

    /// <summary>
    /// Starts Azure Service Group discovery as an asynchronous long-running operation.
    /// </summary>
    [HttpPost("discover/operations")]
    [Authorize(Policy = "ServiceGroupDiscovery")]
    [ProducesResponseType(typeof(DiscoverServiceGroupsAcceptedResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public IActionResult StartDiscoverServiceGroupsOperation(
        [FromQuery] bool? allowSubscriptionFallback = null)
    {
        if (_resourceGraphClient == null)
        {
            return this.ProblemServiceUnavailable(
                "ResourceGraphUnavailable",
                "Azure Resource Graph client is not configured. Check managed identity configuration.");
        }

        Guid correlationId;
        if (Request.Headers.TryGetValue(CorrelationIdMiddleware.HeaderName, out var headerValues) &&
            Guid.TryParse(headerValues.ToString(), out var parsedCorrelationId))
        {
            correlationId = parsedCorrelationId;
        }
        else
        {
            correlationId = Guid.NewGuid();
        }

        var operationId = Guid.NewGuid();
        var state = new DiscoveryOperationState
        {
            OperationId = operationId,
            CorrelationId = correlationId,
            CreatedAt = DateTime.UtcNow,
            Status = "queued"
        };

        DiscoveryOperations[operationId] = state;

        _ = Task.Run(async () =>
        {
            state.Status = "running";
            state.StartedAt = DateTime.UtcNow;

            try
            {
                using var scope = _scopeFactory.CreateScope();
                var response = await ExecuteDiscoveryInScopeAsync(
                    scope.ServiceProvider,
                    allowSubscriptionFallback,
                    CancellationToken.None);

                state.Result = response;
                state.Status = "succeeded";
                state.CompletedAt = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Service group discovery operation {OperationId} failed [correlation={CorrelationId}]",
                    operationId,
                    correlationId);

                state.Status = "failed";
                state.ErrorCode = "ResourceGraphQueryFailed";
                state.ErrorMessage = ex.Message;
                state.CompletedAt = DateTime.UtcNow;
            }
        });

        var operationLocation =
            $"{Request.Scheme}://{Request.Host}/api/v1/service-groups/discover/operations/{operationId}";

        Response.Headers.Append("operation-location", operationLocation);
        Response.Headers.Append("X-Correlation-Id", correlationId.ToString());
        Response.Headers.Append("Retry-After", "3");

        return Accepted(new DiscoverServiceGroupsAcceptedResponse
        {
            OperationId = operationId,
            CorrelationId = correlationId,
            Status = state.Status,
            CreatedAt = state.CreatedAt,
            OperationLocation = operationLocation
        });
    }

    /// <summary>
    /// Gets status for an asynchronous Azure Service Group discovery operation.
    /// </summary>
    [HttpGet("discover/operations/{operationId}")]
    [Authorize(Policy = "ServiceGroupDiscovery")]
    [ProducesResponseType(typeof(DiscoverServiceGroupsOperationStatusResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public IActionResult GetDiscoverServiceGroupsOperationStatus(
        [FromRoute] Guid operationId)
    {
        if (!DiscoveryOperations.TryGetValue(operationId, out var state))
        {
            return this.ProblemNotFound(
                "DiscoveryOperationNotFound",
                $"Discovery operation {operationId} was not found");
        }

        Response.Headers.Append("X-Correlation-Id", state.CorrelationId.ToString());
        if (state.Status is "queued" or "running")
        {
            Response.Headers.Append("Retry-After", "3");
        }

        return Ok(new DiscoverServiceGroupsOperationStatusResponse
        {
            OperationId = state.OperationId,
            CorrelationId = state.CorrelationId,
            Status = state.Status,
            CreatedAt = state.CreatedAt,
            StartedAt = state.StartedAt,
            CompletedAt = state.CompletedAt,
            ErrorCode = state.ErrorCode,
            ErrorMessage = state.ErrorMessage,
            Result = state.Result
        });
    }

    private static async Task<DiscoverServiceGroupsResponse> ExecuteDiscoveryInScopeAsync(
        IServiceProvider serviceProvider,
        bool? allowSubscriptionFallback,
        CancellationToken cancellationToken)
    {
        var context = serviceProvider.GetRequiredService<AtlasDbContext>();
        var logger = serviceProvider.GetRequiredService<ILogger<ServiceGroupsController>>();
        var configuration = serviceProvider.GetRequiredService<IConfiguration>();
        var resourceGraphClient = serviceProvider.GetService<AzureResourceGraphClient>();

        if (resourceGraphClient == null)
        {
            throw new InvalidOperationException(
                "Azure Resource Graph client is not configured. Check managed identity configuration.");
        }

        var controller = new ServiceGroupsController(
            context,
            logger,
            configuration,
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            resourceGraphClient);

        var result = await controller.DiscoverServiceGroups(allowSubscriptionFallback, cancellationToken);

        if (result is OkObjectResult ok && ok.Value is DiscoverServiceGroupsResponse payload)
        {
            return payload;
        }

        if (result is ObjectResult obj && obj.Value is ProblemDetails problem)
        {
            throw new InvalidOperationException(problem.Detail ?? problem.Title ?? "Discovery failed.");
        }

        throw new InvalidOperationException("Discovery operation returned an unexpected response.");
    }

    /// <summary>
    /// Discovers Azure Service Groups (microsoft.management/servicegroups) from Azure Resource
    /// Graph and upserts them into the database, preserving the parent-child hierarchy.
    ///
    /// Uses the system-assigned managed identity, which must be assigned the Service Group Reader
    /// role on the root service group (/providers/Microsoft.Management/serviceGroups/{tenantId}).
    ///
    /// Each Azure Service Group becomes an Atlas service group with:
    ///   ExternalKey = ARM resource ID of the Azure Service Group
    ///   Name        = displayName property (or resource name if displayName is unset)
    ///   Parent      = resolved from properties.parent.resourceId
    ///
    /// See: https://learn.microsoft.com/azure/governance/service-groups/overview
    /// </summary>
    [HttpPost("discover")]
    [Authorize(Policy = "ServiceGroupDiscovery")]
    [ProducesResponseType(typeof(DiscoverServiceGroupsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> DiscoverServiceGroups(
        [FromQuery] bool? allowSubscriptionFallback = null,
        CancellationToken cancellationToken = default)
    {
        if (_resourceGraphClient == null)
        {
            return this.ProblemServiceUnavailable(
                "ResourceGraphUnavailable",
                "Azure Resource Graph client is not configured. Check managed identity configuration.");
        }

        IReadOnlyList<DiscoveredAzureServiceGroup> discovered;
        bool isSubscriptionFallback = false;
        var fallbackEnabled = allowSubscriptionFallback ?? _allowSubscriptionFallbackByDefault;
        try
        {
            discovered = await _resourceGraphClient.DiscoverAzureServiceGroupsAsync(cancellationToken);

            if (discovered.Count == 0 && fallbackEnabled)
            {
                // Optional fallback for tenants where Service Group preview is not enabled.
                _logger.LogInformation(
                    "No Azure Service Groups found (feature may not be registered). " +
                    "Falling back to subscription-based discovery.");
                discovered = await _resourceGraphClient.DiscoverSubscriptionsAsServiceGroupsAsync(cancellationToken);
                isSubscriptionFallback = true;
            }
            else if (discovered.Count == 0)
            {
                _logger.LogInformation(
                    "No Azure Service Groups found. Subscription fallback is disabled; returning zero discovered groups.");
                return Ok(new DiscoverServiceGroupsResponse
                {
                    Value = [],
                    Discovered = 0,
                    Created = 0,
                    Updated = 0
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Azure Service Group discovery failed");
            return this.ProblemServiceUnavailable(
                "ResourceGraphQueryFailed",
                "Failed to query Azure Resource Graph for Service Groups. " +
                "Ensure the system-assigned managed identity has 'Service Group Reader' role " +
                "on the root service group (/providers/Microsoft.Management/serviceGroups/{tenantId}).");
        }

        // When real Azure Service Groups are discovered, remove stale subscription-backed SGs
        // that were created by the fallback path in earlier discovery runs.
        if (!isSubscriptionFallback && discovered.Count > 0)
        {
            var staleSgs = await _context.ServiceGroups
                .Where(sg => sg.ExternalKey.StartsWith("/subscriptions/"))
                .ToListAsync(cancellationToken);

            if (staleSgs.Count > 0)
            {
                var staleIds = staleSgs.Select(sg => sg.Id).ToHashSet();

                // Cascade-delete related entities that reference the stale SGs
                _context.ServiceGroupScopes.RemoveRange(
                    await _context.ServiceGroupScopes.Where(s => staleIds.Contains(s.ServiceGroupId)).ToListAsync(cancellationToken));
                _context.TimelineEvents.RemoveRange(
                    await _context.TimelineEvents.Where(t => staleIds.Contains(t.ServiceGroupId)).ToListAsync(cancellationToken));
                _context.DriftSnapshots.RemoveRange(
                    await _context.DriftSnapshots.Where(d => staleIds.Contains(d.ServiceGroupId)).ToListAsync(cancellationToken));
                _context.BestPracticeViolations.RemoveRange(
                    await _context.BestPracticeViolations.Where(v => staleIds.Contains(v.ServiceGroupId)).ToListAsync(cancellationToken));

                // Break AnalysisRun → DiscoverySnapshot circular FK before cascade delete
                var staleRuns = await _context.AnalysisRuns
                    .Where(r => staleIds.Contains(r.ServiceGroupId) && r.SnapshotId != null)
                    .ToListAsync(cancellationToken);
                if (staleRuns.Count > 0)
                {
                    foreach (var run in staleRuns) run.SnapshotId = null;
                    await _context.SaveChangesAsync(cancellationToken);
                }

                var staleRunIds = await _context.AnalysisRuns
                    .Where(r => staleIds.Contains(r.ServiceGroupId))
                    .Select(r => r.Id)
                    .ToListAsync(cancellationToken);

                if (staleRunIds.Count > 0)
                {
                    var staleRunIdSet = staleRunIds.ToHashSet();
                    _context.AgentMessages.RemoveRange(
                        await _context.AgentMessages.Where(m => staleRunIdSet.Contains(m.AnalysisRunId)).ToListAsync(cancellationToken));

                    var staleRecIds = await _context.Recommendations
                        .Where(r => staleRunIdSet.Contains(r.AnalysisRunId))
                        .Select(r => r.Id)
                        .ToListAsync(cancellationToken);

                    if (staleRecIds.Count > 0)
                    {
                        var staleRecIdSet = staleRecIds.ToHashSet();
                        _context.ApprovalDecisions.RemoveRange(
                            await _context.ApprovalDecisions.Where(a => staleRecIdSet.Contains(a.RecommendationId)).ToListAsync(cancellationToken));
                        _context.IacChangeSets.RemoveRange(
                            await _context.IacChangeSets.Where(c => staleRecIdSet.Contains(c.RecommendationId)).ToListAsync(cancellationToken));
                    }

                    _context.Recommendations.RemoveRange(
                        await _context.Recommendations.Where(r => staleRunIdSet.Contains(r.AnalysisRunId)).ToListAsync(cancellationToken));
                    _context.AnalysisRuns.RemoveRange(
                        await _context.AnalysisRuns.Where(r => staleIds.Contains(r.ServiceGroupId)).ToListAsync(cancellationToken));
                }

                var staleSnapIds = await _context.DiscoverySnapshots
                    .Where(s => staleIds.Contains(s.ServiceGroupId))
                    .Select(s => s.Id)
                    .ToListAsync(cancellationToken);

                if (staleSnapIds.Count > 0)
                {
                    var staleSnapIdSet = staleSnapIds.ToHashSet();
                    _context.DiscoveredResources.RemoveRange(
                        await _context.DiscoveredResources.Where(r => staleSnapIdSet.Contains(r.SnapshotId)).ToListAsync(cancellationToken));
                    _context.DiscoverySnapshots.RemoveRange(
                        await _context.DiscoverySnapshots.Where(s => staleIds.Contains(s.ServiceGroupId)).ToListAsync(cancellationToken));
                }

                _context.ServiceGroups.RemoveRange(staleSgs);
                await _context.SaveChangesAsync(cancellationToken);

                _logger.LogInformation(
                    "Removed {Count} stale subscription-backed service groups after discovering real Azure Service Groups",
                    staleSgs.Count);
            }
        }

        // Build a lookup of ARM ID → existing Atlas service group (single DB round-trip)
        var externalKeys = discovered
            .Select(sg => sg.ArmId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var existing = await _context.ServiceGroups
            .Where(sg => externalKeys.Contains(sg.ExternalKey))
            .ToDictionaryAsync(sg => sg.ExternalKey, StringComparer.OrdinalIgnoreCase, cancellationToken);

        int created = 0;
        // createdIds tracks rows inserted in this run so we don't double-count their initial
        // parent-link assignment (second pass) as an update.
        var createdIds = new HashSet<Guid>();
        var updatedIds = new HashSet<Guid>();
        var now = DateTime.UtcNow;

        // First pass: create or update the Atlas service groups without resolving parent links.
        // We need all rows in the DB before we can wire up ParentServiceGroupId.
        var armIdToAtlasId = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);

        foreach (var sg in discovered)
        {
            var effectiveName = !string.IsNullOrEmpty(sg.DisplayName) ? sg.DisplayName : sg.Name;
            var description = isSubscriptionFallback
                ? $"Azure Subscription '{effectiveName}' (id: {sg.Name})"
                : $"Azure Service Group '{effectiveName}' (id: {sg.Name})";

            if (existing.TryGetValue(sg.ArmId, out var serviceGroup))
            {
                bool changed = false;
                if (serviceGroup.Name != effectiveName)
                {
                    serviceGroup.Name = effectiveName;
                    changed = true;
                }
                if (serviceGroup.Description != description)
                {
                    serviceGroup.Description = description;
                    changed = true;
                }
                if (changed)
                {
                    serviceGroup.UpdatedAt = now;
                    updatedIds.Add(serviceGroup.Id);
                }
                armIdToAtlasId[sg.ArmId] = serviceGroup.Id;
            }
            else
            {
                serviceGroup = new ServiceGroup
                {
                    Id = Guid.NewGuid(),
                    ExternalKey = sg.ArmId,
                    Name = effectiveName,
                    Description = description,
                    CreatedAt = now,
                    UpdatedAt = now
                };
                _context.ServiceGroups.Add(serviceGroup);
                existing[sg.ArmId] = serviceGroup;
                armIdToAtlasId[sg.ArmId] = serviceGroup.Id;
                createdIds.Add(serviceGroup.Id);
                created++;
            }
        }

        // Save so all rows have persisted IDs before we resolve parent links
        await _context.SaveChangesAsync(cancellationToken);

        // Second pass: wire up parent-child hierarchy.
        // Track parent-link changes in the same updatedIds set so each row counts at most once.
        // Newly created rows get their initial parent-link wired here, but that is part of
        // creation — not counted as an update to avoid double-reporting in the response.
        foreach (var sg in discovered)
        {
            if (string.IsNullOrEmpty(sg.ParentArmId))
                continue;

            // Skip if the parent is the root service group (whose ID is the tenant ID) —
            // root is not imported as an Atlas service group, so its ARM ID won't be in the map.
            if (!armIdToAtlasId.TryGetValue(sg.ParentArmId, out var parentAtlasId))
                continue;

            var serviceGroup = existing[sg.ArmId];
            if (serviceGroup.ParentServiceGroupId != parentAtlasId)
            {
                serviceGroup.ParentServiceGroupId = parentAtlasId;
                serviceGroup.UpdatedAt = now;
                // Only count as updated for pre-existing groups; newly created ones have their
                // parent wired as part of their initial setup, not as a subsequent change.
                if (!createdIds.Contains(serviceGroup.Id))
                {
                    updatedIds.Add(serviceGroup.Id);
                }
            }
        }

        int updated = updatedIds.Count;

        await _context.SaveChangesAsync(cancellationToken);

        // For subscription fallback: ensure each newly created service group has a scope
        // pointing to the subscription it represents.
        if (isSubscriptionFallback && createdIds.Count > 0)
        {
            var existingScopeGroupIds = await _context.ServiceGroupScopes
                .Where(s => createdIds.Contains(s.ServiceGroupId))
                .Select(s => s.ServiceGroupId)
                .Distinct()
                .ToListAsync(cancellationToken);

            var groupIdsNeedingScopes = createdIds.Except(existingScopeGroupIds).ToHashSet();

            foreach (var sg in discovered)
            {
                if (!armIdToAtlasId.TryGetValue(sg.ArmId, out var atlasId))
                    continue;
                if (!groupIdsNeedingScopes.Contains(atlasId))
                    continue;

                // sg.Name is the subscription ID for fallback-discovered groups
                _context.ServiceGroupScopes.Add(new ServiceGroupScope
                {
                    Id = Guid.NewGuid(),
                    ServiceGroupId = atlasId,
                    SubscriptionId = sg.Name,
                    ResourceGroup = null,
                    ScopeFilter = null,
                    CreatedAt = now
                });
            }

            await _context.SaveChangesAsync(cancellationToken);
        }

        // For real Azure Service Groups: auto-populate scopes from RelationshipResources membership.
        // Each SG member subscription becomes a ServiceGroupScope entry for its parent SG.
        if (!isSubscriptionFallback && discovered.Count > 0)
        {
            try
            {
                var sgArmIds = armIdToAtlasId.Keys.ToList();
                var membershipMap = await _resourceGraphClient.DiscoverServiceGroupSubscriptionMembersAsync(
                    sgArmIds, cancellationToken);

                if (membershipMap.Count > 0)
                {
                    // Remove existing auto-populated scopes for these SGs to avoid duplicates on re-discovery
                    var allSgIds = armIdToAtlasId.Values.ToHashSet();
                    _context.ServiceGroupScopes.RemoveRange(
                        await _context.ServiceGroupScopes
                            .Where(s => allSgIds.Contains(s.ServiceGroupId))
                            .ToListAsync(cancellationToken));

                    int scopesCreated = 0;
                    foreach (var (sgArmId, subscriptionIds) in membershipMap)
                    {
                        if (!armIdToAtlasId.TryGetValue(sgArmId, out var atlasId))
                            continue;

                        foreach (var subId in subscriptionIds)
                        {
                            _context.ServiceGroupScopes.Add(new ServiceGroupScope
                            {
                                Id = Guid.NewGuid(),
                                ServiceGroupId = atlasId,
                                SubscriptionId = subId,
                                ResourceGroup = null,
                                ScopeFilter = null,
                                CreatedAt = now
                            });
                            scopesCreated++;
                        }
                    }

                    if (scopesCreated > 0)
                    {
                        await _context.SaveChangesAsync(cancellationToken);
                        _logger.LogInformation(
                            "Auto-populated {ScopeCount} subscription scope(s) across {GroupCount} service group(s) from membership data",
                            scopesCreated, membershipMap.Count);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Failed to auto-populate service group scopes from membership data. " +
                    "Service groups will have scopeCount=0 until manually configured or next discovery.");
            }
        }

        _logger.LogInformation(
            "Azure Service Group discovery complete. Discovered={Discovered}, Created={Created}, Updated={Updated}",
            discovered.Count, created, updated);

        // Return the discovered service groups.
        // Azure Service Groups don't use subscription/resource-group scopes — membership is
        // managed via Microsoft.Relationship/ServiceGroupMember on each resource — so freshly
        // discovered groups will have ScopeCount=0. Groups that already existed in the DB and
        // had manual scopes added will show those counts here.
        // Use a HashSet for efficient Contains checks in EF Core IN-clause generation
        var resultIds = armIdToAtlasId.Values.ToHashSet();

        // Resolve ScopeCount via a single grouped query to avoid N+1
        var scopeCounts = await _context.ServiceGroupScopes
            .Where(s => resultIds.Contains(s.ServiceGroupId))
            .GroupBy(s => s.ServiceGroupId)
            .Select(g => new { ServiceGroupId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.ServiceGroupId, x => x.Count, cancellationToken);

        var serviceGroupRows = await _context.ServiceGroups
            .AsNoTracking()
            .Where(sg => resultIds.Contains(sg.Id))
            .OrderBy(sg => sg.Name)
            .Select(sg => new { sg.Id, sg.Name, sg.Description, sg.CreatedAt })
            .ToListAsync(cancellationToken);

        var response = serviceGroupRows
            .Select(sg => new ServiceGroupDto
            {
                Id = sg.Id,
                Name = sg.Name,
                Description = sg.Description,
                CreatedAt = sg.CreatedAt,
                ScopeCount = scopeCounts.TryGetValue(sg.Id, out var c) ? c : 0
            })
            .ToList();

        return Ok(new DiscoverServiceGroupsResponse
        {
            Value = response,
            Discovered = discovered.Count,
            Created = created,
            Updated = updated
        });
    }

    /// <summary>
    /// Start analysis for a service group (T022 - Analysis Endpoints with LRO)
    /// </summary>
    [HttpPost("{id}/analysis")]
    [Authorize(Policy = "Admin")]
    [ProducesResponseType(typeof(AnalysisStartedResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> StartAnalysis(
        [FromRoute] Guid id,
        [FromQuery(Name = "api-version")] string? apiVersion,
        CancellationToken cancellationToken = default)
    {
        // Validate api-version parameter (required)
        if (string.IsNullOrWhiteSpace(apiVersion))
        {
            return this.ProblemBadRequest("MissingApiVersionParameter", "api-version query parameter is required");
        }

        // Validate service group exists
        var serviceGroup = await _context.ServiceGroups
            .FirstOrDefaultAsync(sg => sg.Id == id, cancellationToken);

        if (serviceGroup == null)
        {
            return this.ProblemNotFound("ServiceGroupNotFound", $"Service group {id} does not exist");
        }

        // Get correlation ID from request header (set/expected by CorrelationIdMiddleware)
        Guid correlationId;
        if (Request.Headers.TryGetValue(CorrelationIdMiddleware.HeaderName, out var headerValues) &&
            Guid.TryParse(headerValues.ToString(), out var parsedCorrelationId))
        {
            correlationId = parsedCorrelationId;
        }
        else
        {
            correlationId = Guid.NewGuid();
        }

        // Create AnalysisRun with queued status
        var analysisRun = new AnalysisRun
        {
            Id = Guid.NewGuid(),
            ServiceGroupId = id,
            CorrelationId = correlationId,
            Status = AnalysisRunStatus.Queued, // Status transitions: queued → running → completed/partial/failed
            TriggeredBy = User.Identity?.Name ?? "system",
            CreatedAt = DateTime.UtcNow
        };

        _context.AnalysisRuns.Add(analysisRun);
        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Started analysis run {AnalysisRunId} for service group {ServiceGroupId} with correlation {CorrelationId}",
            analysisRun.Id, id, correlationId);

        // Analysis run is persisted with status "queued"; BackgroundAnalysisService will
        // pick it up within the next poll cycle (default 5 s) and execute the full workflow.
        // No explicit trigger is needed because the background service continuously polls
        // the analysis_runs table for queued entries.

        // LRO pattern: Return 202 Accepted with operation-location header
        var operationLocation = $"{Request.Scheme}://{Request.Host}/api/v1/service-groups/{id}/analysis/{analysisRun.Id}?api-version={apiVersion}";

        Response.Headers.Append("operation-location", operationLocation);
        Response.Headers.Append("X-Correlation-Id", correlationId.ToString());
        Response.Headers.Append("Retry-After", "5"); // Poll every 5 seconds

        return Accepted(new AnalysisStartedResponse
        {
            RunId = analysisRun.Id,
            CorrelationId = correlationId,
            Status = analysisRun.Status,
            CreatedAt = analysisRun.CreatedAt
        });
    }

    /// <summary>
    /// Get analysis run status (T022 - LRO status monitoring)
    /// </summary>
    [HttpGet("{id}/analysis/{runId}")]
    [ProducesResponseType(typeof(AnalysisStatusResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetAnalysisStatus(
        [FromRoute] Guid id,
        [FromRoute] Guid runId,
        [FromQuery(Name = "api-version")] string? apiVersion,
        CancellationToken cancellationToken = default)
    {
        // Validate api-version parameter
        if (string.IsNullOrWhiteSpace(apiVersion))
        {
            return this.ProblemBadRequest("MissingApiVersionParameter", "api-version query parameter is required");
        }

        var analysisRun = await _context.AnalysisRuns
            .AsNoTracking()
            .FirstOrDefaultAsync(ar => ar.Id == runId && ar.ServiceGroupId == id, cancellationToken);

        if (analysisRun == null)
        {
            return this.ProblemNotFound("AnalysisRunNotFound", $"Analysis run {runId} not found for service group {id}");
        }

        var response = new AnalysisStatusResponse
        {
            RunId = analysisRun.Id,
            ServiceGroupId = analysisRun.ServiceGroupId,
            Status = analysisRun.Status,
            CorrelationId = analysisRun.CorrelationId,
            TriggeredBy = analysisRun.TriggeredBy,
            CreatedAt = analysisRun.CreatedAt,
            StartedAt = analysisRun.StartedAt,
            CompletedAt = analysisRun.CompletedAt
        };

        // LRO pattern: Include Retry-After header for non-terminal states
        if (!AnalysisRunStatus.IsTerminal(analysisRun.Status))
        {
            Response.Headers.Append("Retry-After", "5"); // Poll every 5 seconds for non-terminal states
        }

        Response.Headers.Append("X-Correlation-Id", analysisRun.CorrelationId.ToString());

        return Ok(response);
    }

    /// <summary>
    /// Get analysis scores for a completed run (T022)
    /// </summary>
    [HttpGet("{id}/analysis/{runId}/scores")]
    [ProducesResponseType(typeof(AnalysisScoresResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(AnalysisScoresResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetAnalysisScores(
        [FromRoute] Guid id,
        [FromRoute] Guid runId,
        [FromQuery(Name = "api-version")] string? apiVersion,
        CancellationToken cancellationToken = default)
    {
        // Validate api-version parameter
        if (string.IsNullOrWhiteSpace(apiVersion))
        {
            return this.ProblemBadRequest("MissingApiVersionParameter", "api-version query parameter is required");
        }

        var analysisRun = await _context.AnalysisRuns
            .AsNoTracking()
            .Include(ar => ar.Messages)
            .FirstOrDefaultAsync(ar => ar.Id == runId && ar.ServiceGroupId == id, cancellationToken);

        if (analysisRun == null)
        {
            return this.ProblemNotFound("AnalysisRunNotFound", $"Analysis run {runId} not found for service group {id}");
        }

        // Check if analysis is complete; return 202 with Retry-After for non-terminal states
        var terminalStates = new[] { AnalysisRunStatus.Completed, AnalysisRunStatus.Partial };
        if (!terminalStates.Contains(analysisRun.Status.ToLowerInvariant()))
        {
            Response.Headers.Append("Retry-After", "5");
            Response.Headers.Append("X-Correlation-Id", analysisRun.CorrelationId.ToString());

            var pendingResponse = new AnalysisScoresResponse
            {
                RunId = analysisRun.Id,
                ServiceGroupId = analysisRun.ServiceGroupId,
                Status = analysisRun.Status,
                CompletedAt = analysisRun.CompletedAt,
                Scores = new List<ScoreDetail>()
            };

            return Accepted(pendingResponse);
        }

        // Extract scores from agent messages (T025 will populate these)
        var scoreMessages = analysisRun.Messages
            .Where(m => m.MessageType == "score" || m.MessageType == "assessment")
            .OrderByDescending(m => m.CreatedAt)
            .ToList();

        var response = new AnalysisScoresResponse
        {
            RunId = analysisRun.Id,
            ServiceGroupId = analysisRun.ServiceGroupId,
            Status = analysisRun.Status,
            CompletedAt = analysisRun.CompletedAt,
            Scores = scoreMessages.Select(m => new ScoreDetail
            {
                Category = ExtractCategory(m.Payload) ?? m.AgentName,
                Score = ExtractScore(m.Payload),
                Confidence = ExtractConfidence(m.Payload),
                Dimensions = ExtractDimensions(m.Payload),
                ResourceCount = ExtractResourceCount(m.Payload),
                CreatedAt = m.CreatedAt
            }).ToList()
        };

        Response.Headers.Append("X-Correlation-Id", analysisRun.CorrelationId.ToString());

        return Ok(response);
    }

    private static string? ExtractCategory(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload)) return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(payload);
            if (doc.RootElement.TryGetProperty("category", out var cat))
                return cat.GetString();
        }
        catch { /* malformed JSON */ }
        return null;
    }

    private static int ExtractScore(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload)) return 0;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(payload);
            var root = doc.RootElement;

            // New format: direct "score" field (0-100)
            if (root.TryGetProperty("score", out var scoreProp) && scoreProp.ValueKind == System.Text.Json.JsonValueKind.Number)
                return scoreProp.GetInt32();

            // Legacy format: average of dimension scores (0.0-1.0) × 100
            var dims = new[] { "completeness", "cost_efficiency", "availability", "security" };
            var values = dims
                .Where(d => root.TryGetProperty(d, out _))
                .Select(d => root.GetProperty(d).GetDouble())
                .ToList();
            if (values.Count > 0)
                return (int)Math.Round(values.Average() * 100);
        }
        catch (System.Text.Json.JsonException) { /* malformed JSON — return 0 */ }
        catch (InvalidOperationException) { /* unexpected element kind — return 0 */ }
        catch (FormatException) { /* numeric conversion failed — return 0 */ }
        return 0;
    }

    private static double ExtractConfidence(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload)) return 0.0;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(payload);
            var root = doc.RootElement;

            // New format: direct "normalized" field (0.0-1.0)
            if (root.TryGetProperty("normalized", out var normProp) && normProp.ValueKind == System.Text.Json.JsonValueKind.Number)
                return Math.Round(normProp.GetDouble(), 4);

            // Legacy format: average of dimension scores as a confidence proxy
            var dims = new[] { "completeness", "cost_efficiency", "availability", "security" };
            var values = dims
                .Where(d => root.TryGetProperty(d, out _))
                .Select(d => root.GetProperty(d).GetDouble())
                .ToList();
            return values.Count > 0 ? Math.Round(values.Average(), 4) : 0.0;
        }
        catch (System.Text.Json.JsonException) { /* malformed JSON — return 0 */ }
        catch (InvalidOperationException) { /* unexpected element kind — return 0 */ }
        catch (FormatException) { /* numeric conversion failed — return 0 */ }
        return 0.0;
    }

    private static Dictionary<string, double> ExtractDimensions(string? payload)
    {
        var result = new Dictionary<string, double>();
        if (string.IsNullOrWhiteSpace(payload)) return result;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(payload);
            var root = doc.RootElement;
            foreach (var dim in new[] { "completeness", "cost_efficiency", "availability", "security" })
            {
                if (root.TryGetProperty(dim, out var el))
                    result[dim] = Math.Round(el.GetDouble(), 4);
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // Intentional: malformed JSON — return empty result as fallback
        }
        catch (InvalidOperationException)
        {
            // Intentional: unexpected element kind — return empty result as fallback
        }
        return result;
    }

    private static int ExtractResourceCount(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload)) return 0;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(payload);
            if (doc.RootElement.TryGetProperty("resource_count", out var el))
                return el.GetInt32();
        }
        catch (System.Text.Json.JsonException)
        {
            // Intentional: malformed JSON — return 0 as fallback
        }
        catch (InvalidOperationException)
        {
            // Intentional: unexpected element kind — return 0 as fallback
        }
        return 0;
    }

    /// <summary>
    /// Get best-practice violations for a service group
    /// </summary>
    [HttpGet("{id}/violations")]
    [ProducesResponseType(typeof(List<ViolationDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<List<ViolationDto>>> GetViolationsAsync(
        [FromRoute] Guid id,
        [FromQuery] string? status = "active",
        [FromQuery] string? severity = null,
        [FromQuery] int limit = 50,
        [FromQuery(Name = "api-version")] string? apiVersion = null)
    {
        if (string.IsNullOrWhiteSpace(apiVersion))
            return this.ProblemBadRequest("MissingApiVersionParameter", "api-version query parameter is required");

        var serviceGroup = await _context.ServiceGroups
            .AsNoTracking()
            .FirstOrDefaultAsync(sg => sg.Id == id);

        if (serviceGroup is null)
        {
            return this.ProblemNotFound("ServiceGroupNotFound", $"Service group {id} not found");
        }

        var query = _context.BestPracticeViolations
            .Include(v => v.Rule)
            .Where(v => v.ServiceGroupId == id);

        if (!string.IsNullOrWhiteSpace(status))
            query = query.Where(v => v.Status == status);

        if (!string.IsNullOrWhiteSpace(severity))
            query = query.Where(v => v.Severity == severity);

        var violations = await query
            .OrderByDescending(v => v.DetectedAt)
            .Take(limit)
            .Select(v => new ViolationDto
            {
                Id = v.Id,
                RuleId = v.RuleId,
                RuleName = v.Rule != null ? v.Rule.Name : string.Empty,
                Category = v.Rule != null ? v.Rule.Category : string.Empty,
                DriftCategory = v.DriftCategory ?? "ConfigurationDrift",
                ResourceId = v.ResourceId,
                ResourceType = v.ResourceType,
                ViolationType = v.ViolationType,
                Severity = v.Severity,
                CurrentState = v.CurrentState,
                ExpectedState = v.ExpectedState,
                Status = v.Status,
                DetectedAt = v.DetectedAt
            })
            .ToListAsync();

        return Ok(violations);
    }
}

public record ServiceGroupDto
{
    public Guid Id { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public int ScopeCount { get; init; }
    public DateTime CreatedAt { get; init; }
}

public record ServiceGroupDetailDto
{
    public Guid Id { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public DateTime CreatedAt { get; init; }
    public List<ServiceGroupScopeDto> Scopes { get; init; } = new();
}

public record ServiceGroupScopeDto
{
    public required string AzureSubscriptionId { get; init; }
    public string? AzureResourceGroupName { get; init; }
    public string? TagFilter { get; init; }
}

public record CreateServiceGroupRequest
{
    public required string Name { get; init; }
    public string? Description { get; init; }
    public List<ServiceGroupScopeRequest> Scopes { get; init; } = new();
}

public record ServiceGroupScopeRequest
{
    public required string AzureSubscriptionId { get; init; }
    public string? AzureResourceGroupName { get; init; }
    public string? TagFilter { get; init; }
}

public record DiscoverServiceGroupsResponse
{
    /// <summary>The Atlas service groups created or updated by this discovery run.</summary>
    public List<ServiceGroupDto> Value { get; init; } = new();
    /// <summary>Total Azure Service Groups discovered from Azure Resource Graph.</summary>
    public int Discovered { get; init; }
    /// <summary>Number of new Atlas service groups created in this run.</summary>
    public int Created { get; init; }
    /// <summary>Number of existing Atlas service groups updated in this run.</summary>
    public int Updated { get; init; }
}

public record DiscoverServiceGroupsAcceptedResponse
{
    public Guid OperationId { get; init; }
    public Guid CorrelationId { get; init; }
    public required string Status { get; init; }
    public required string OperationLocation { get; init; }
    public DateTime CreatedAt { get; init; }
}

public record DiscoverServiceGroupsOperationStatusResponse
{
    public Guid OperationId { get; init; }
    public Guid CorrelationId { get; init; }
    public required string Status { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime? StartedAt { get; init; }
    public DateTime? CompletedAt { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public DiscoverServiceGroupsResponse? Result { get; init; }
}

// T022 DTOs for Analysis Endpoints
public record AnalysisStartedResponse
{
    public Guid RunId { get; init; }
    public Guid CorrelationId { get; init; }
    public required string Status { get; init; }
    public DateTime CreatedAt { get; init; }
}

public record AnalysisStatusResponse
{
    public Guid RunId { get; init; }
    public Guid ServiceGroupId { get; init; }
    public required string Status { get; init; }
    public Guid CorrelationId { get; init; }
    public required string TriggeredBy { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime? StartedAt { get; init; }
    public DateTime? CompletedAt { get; init; }
}

public record AnalysisScoresResponse
{
    public Guid RunId { get; init; }
    public Guid ServiceGroupId { get; init; }
    public required string Status { get; init; }
    public DateTime? CompletedAt { get; init; }
    public List<ScoreDetail> Scores { get; init; } = new();
}

public record ScoreDetail
{
    public required string Category { get; init; }
    public int Score { get; init; }
    public double Confidence { get; init; }
    /// <summary>Individual dimension scores (0.0–1.0). Keys: completeness, cost_efficiency, availability, security.</summary>
    public Dictionary<string, double> Dimensions { get; init; } = new();
    /// <summary>Number of Azure resources that were scored.</summary>
    public int ResourceCount { get; init; }
    public DateTime CreatedAt { get; init; }
}

public record ViolationDto
{
    public Guid Id { get; init; }
    public Guid RuleId { get; init; }
    public required string RuleName { get; init; }
    public required string Category { get; init; }
    public required string DriftCategory { get; init; }
    public required string ResourceId { get; init; }
    public required string ResourceType { get; init; }
    public required string ViolationType { get; init; }
    public required string Severity { get; init; }
    public required string CurrentState { get; init; }
    public required string ExpectedState { get; init; }
    public required string Status { get; init; }
    public DateTime DetectedAt { get; init; }
}
