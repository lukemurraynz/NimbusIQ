using Atlas.ControlPlane.Api.Middleware;
using Atlas.ControlPlane.Application.Recommendations;
using Atlas.ControlPlane.Application.Services;
using Atlas.ControlPlane.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;
using System.Globalization;
using Microsoft.Extensions.Logging;

namespace Atlas.ControlPlane.Api.Endpoints;

[ApiController]
[Route("api/v1/recommendations")]
public class RecommendationsController : ControllerBase
{
    private readonly AtlasDbContext _db;
    private readonly DecisionService _decisionService;
    private readonly ScoreSimulationService _scoreSimulation;
    private readonly AvmIacExampleService _avmIacExampleService;
    private readonly ILogger<RecommendationsController> _logger;

    public RecommendationsController(
        AtlasDbContext db,
        DecisionService decisionService,
        ScoreSimulationService scoreSimulation,
        AvmIacExampleService avmIacExampleService,
        ILogger<RecommendationsController> logger)
    {
        _db = db;
        _decisionService = decisionService;
        _scoreSimulation = scoreSimulation;
        _avmIacExampleService = avmIacExampleService;
        _logger = logger;
    }

    /// <summary>
    /// List recommendations with filters
    /// </summary>
    [HttpGet]
    [Authorize(Policy = "RecommendationRead")]
    public async Task<IActionResult> ListRecommendations(
        [FromQuery] string? status = null,
        [FromQuery] Guid? analysisRunId = null,
        [FromQuery] Guid? serviceGroupId = null,
        [FromQuery] string? orderBy = null,
        [FromQuery] string? source = null,
        [FromQuery] string? trustLevel = null,
        [FromQuery] string? category = null,
        [FromQuery] string? confidenceBand = null,
        [FromQuery] string? queueBand = null,
        [FromQuery] string? freshnessBand = null,
        [FromQuery] string? search = null,
        [FromQuery] int limit = 50,
        [FromQuery] int offset = 0,
        CancellationToken cancellationToken = default)
    {
        var query = _db.Recommendations
            .AsNoTracking();

        if (!string.IsNullOrEmpty(status))
        {
            var statusFilters = BuildStatusFilters(status);
            // DB status values are stored as already-normalized strings; calling Normalize() on a
            // DB column is not translatable by EF Core and causes a runtime exception.
            query = query.Where(r => statusFilters.Contains(r.Status));
        }

        if (analysisRunId.HasValue)
        {
            query = query.Where(r => r.AnalysisRunId == analysisRunId.Value);
        }
        if (serviceGroupId.HasValue)
        {
            query = query.Where(r => r.ServiceGroupId == serviceGroupId.Value);
        }

        if (!string.IsNullOrWhiteSpace(category))
        {
            var normalizedCategory = category.Trim().ToLowerInvariant();
            query = query.Where(r => r.Category.ToLower() == normalizedCategory);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLowerInvariant();
            query = query.Where(r =>
                r.Title.ToLower().Contains(term) ||
                r.RecommendationType.ToLower().Contains(term) ||
                r.ResourceId.ToLower().Contains(term) ||
                r.ActionType.ToLower().Contains(term) ||
                (r.ServiceGroup != null && r.ServiceGroup.Name.ToLower().Contains(term)));
        }

        // Push source filter to DB by matching on the raw column values used by NormalizeRecommendationSource.
        var normalizedSource = source?.Trim().ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(normalizedSource))
        {
            query = normalizedSource switch
            {
                "psrule" => query.Where(r =>
                    r.TriggerReason != null && (r.TriggerReason.ToLower().Contains("psrule") || r.TriggerReason.ToLower().Contains("ps_rule"))),
                "quick_review" => query.Where(r =>
                    r.TriggerReason != null && r.TriggerReason.ToLower().Contains("quick") && r.TriggerReason.ToLower().Contains("review")),
                "advisor" => query.Where(r =>
                    r.TriggerReason != null && r.TriggerReason.ToLower().Contains("advisor")),
                "drift" => query.Where(r =>
                    r.TriggerReason != null && r.TriggerReason.ToLower().Contains("drift")),
                "ai_synthesis" => query.Where(r =>
                    r.ConfidenceSource != null && r.ConfidenceSource.ToLower().Contains("ai")),
                _ => query // unclassified: cannot be pushed to DB cleanly; handled post-query
            };
        }

        // Push confidenceBand filter to DB via range on the Confidence column.
        var normalizedConfidenceBand = confidenceBand?.Trim().ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(normalizedConfidenceBand))
        {
            query = normalizedConfidenceBand switch
            {
                "high" => query.Where(r => r.Confidence >= 0.8m),
                "medium" => query.Where(r => r.Confidence >= 0.6m && r.Confidence < 0.8m),
                "low" => query.Where(r => r.Confidence < 0.6m),
                _ => query
            };
        }

        var recommendations = await query
            .Include(r => r.ServiceGroup)
            .OrderByDescending(r => r.CreatedAt)
            .ToListAsync(cancellationToken);

        var now = DateTime.UtcNow;
        var materialized = recommendations
            .Select(r =>
            {
                var lens = RecommendationLensCalculator.Calculate(r, now);
                var queueScore = RecommendationPriorityQueueService.CalculateRiskWeightedScore(r, now);
                return new
                {
                    r.Id,
                    r.CorrelationId,
                    r.AnalysisRunId,
                    r.ServiceGroupId,
                    ServiceGroupName = r.ServiceGroup?.Name,
                    r.ResourceId,
                    r.Category,
                    r.RecommendationType,
                    r.ActionType,
                    r.Title,
                    r.Summary,
                    r.Description,
                    r.TriggerReason,
                    r.ChangeContext,
                    Source = NormalizeRecommendationSource(r.TriggerReason, r.ConfidenceSource),
                    SourceLabel = ToSourceLabel(NormalizeRecommendationSource(r.TriggerReason, r.ConfidenceSource)),
                    WellArchitectedPillar = MapWellArchitectedPillar(r.Category),
                    r.Status,
                    r.Priority,
                    ConfidenceScore = r.Confidence,
                    r.ConfidenceSource,
                    r.EvidenceReferences,
                    GroundingSource = ExtractGroundingSource(r.ChangeContext),
                    GroundingTimestampUtc = ExtractGroundingTimestamp(r.ChangeContext),
                    r.ApprovalMode,
                    r.RequiredApprovals,
                    r.ReceivedApprovals,
                    r.CreatedAt,
                    r.ValidUntil,
                    lens.RiskScore,
                    lens.TrustScore,
                    lens.TrustLevel,
                    lens.EvidenceCompleteness,
                    lens.FreshnessDays,
                    RiskWeightedScore = queueScore
                };
            })
            .ToList();

        var filtered = materialized;

        // source and confidenceBand were already pushed to the DB query above.
        // The remaining filters require computed values that cannot be translated to SQL.

        var normalizedTrust = trustLevel?.Trim();
        if (!string.IsNullOrWhiteSpace(normalizedTrust))
        {
            filtered = filtered
                .Where(r => string.Equals(r.TrustLevel, normalizedTrust, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        var normalizedQueueBand = queueBand?.Trim().ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(normalizedQueueBand))
        {
            filtered = filtered
                .Where(r => ScoreBand(r.RiskWeightedScore) == normalizedQueueBand)
                .ToList();
        }

        var normalizedFreshnessBand = freshnessBand?.Trim().ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(normalizedFreshnessBand))
        {
            filtered = filtered
                .Where(r => FreshnessBand(r.FreshnessDays) == normalizedFreshnessBand)
                .ToList();
        }

        var sorted = orderBy?.ToLowerInvariant() switch
        {
            "risk" or "riskScore" => filtered.OrderByDescending(r => r.RiskScore).ThenByDescending(r => r.CreatedAt).ToList(),
            "riskweighted" or "risk-weighted" or "queue" => filtered.OrderByDescending(r => r.RiskWeightedScore).ThenByDescending(r => r.CreatedAt).ToList(),
            "confidence" => filtered.OrderByDescending(r => r.ConfidenceScore).ThenByDescending(r => r.CreatedAt).ToList(),
            _ => filtered.OrderByDescending(r => r.CreatedAt).ToList()
        };

        var normalizedOffset = Math.Max(0, offset);
        var normalizedLimit = Math.Clamp(limit, 1, 500);
        var paged = sorted.Skip(normalizedOffset).Take(normalizedLimit).ToList();

        // Build nextLink when there are more items after this page.
        string? nextLink = null;
        if (normalizedOffset + paged.Count < sorted.Count)
        {
            var nextOffset = normalizedOffset + normalizedLimit;
            var qs = new System.Text.StringBuilder($"/api/v1/recommendations?limit={normalizedLimit}&offset={nextOffset}");
            if (!string.IsNullOrWhiteSpace(status)) qs.Append($"&status={Uri.EscapeDataString(status)}");
            if (analysisRunId.HasValue) qs.Append($"&analysisRunId={analysisRunId}");
            if (serviceGroupId.HasValue) qs.Append($"&serviceGroupId={serviceGroupId}");
            if (!string.IsNullOrWhiteSpace(orderBy)) qs.Append($"&orderBy={Uri.EscapeDataString(orderBy)}");
            if (!string.IsNullOrWhiteSpace(source)) qs.Append($"&source={Uri.EscapeDataString(source)}");
            if (!string.IsNullOrWhiteSpace(trustLevel)) qs.Append($"&trustLevel={Uri.EscapeDataString(trustLevel)}");
            if (!string.IsNullOrWhiteSpace(category)) qs.Append($"&category={Uri.EscapeDataString(category)}");
            if (!string.IsNullOrWhiteSpace(confidenceBand)) qs.Append($"&confidenceBand={Uri.EscapeDataString(confidenceBand)}");
            if (!string.IsNullOrWhiteSpace(queueBand)) qs.Append($"&queueBand={Uri.EscapeDataString(queueBand)}");
            if (!string.IsNullOrWhiteSpace(freshnessBand)) qs.Append($"&freshnessBand={Uri.EscapeDataString(freshnessBand)}");
            if (!string.IsNullOrWhiteSpace(search)) qs.Append($"&search={Uri.EscapeDataString(search)}");
            nextLink = qs.ToString();
        }

        return Ok(new { value = paged, nextLink });
    }

    private static List<string> BuildStatusFilters(string statusFilter)
    {
        var filters = statusFilter
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .SelectMany(RecommendationWorkflowStatus.ExpandStatusFilter)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return filters;
    }

    private static string ScoreBand(decimal score) =>
        score >= 0.8m ? "high" : score >= 0.6m ? "medium" : "low";

    private static string ScoreBand(double score) =>
        score >= 0.8 ? "high" : score >= 0.6 ? "medium" : "low";

    private static string FreshnessBand(int freshnessDays) =>
        freshnessDays >= 30 ? "stale" : freshnessDays >= 7 ? "aging" : "fresh";

    /// <summary>
    /// Get recommendation details
    /// </summary>
    [HttpGet("{id}")]
    [Authorize(Policy = "RecommendationRead")]
    public async Task<IActionResult> GetRecommendation(Guid id, CancellationToken cancellationToken = default)
    {
        var recommendation = await _db.Recommendations
            .AsNoTracking()
            .Include(r => r.ServiceGroup)
            .FirstOrDefaultAsync(r => r.Id == id, cancellationToken);

        if (recommendation == null)
        {
            return this.ProblemNotFound("RecommendationNotFound", "Recommendation not found");
        }

        var lens = RecommendationLensCalculator.Calculate(recommendation, DateTime.UtcNow);

        return Ok(new
        {
            recommendation.Id,
            recommendation.CorrelationId,
            recommendation.AnalysisRunId,
            recommendation.ServiceGroupId,
            ServiceGroupName = recommendation.ServiceGroup != null ? recommendation.ServiceGroup.Name : null,
            recommendation.ResourceId,
            recommendation.Category,
            recommendation.RecommendationType,
            recommendation.ActionType,
            recommendation.Title,
            recommendation.Summary,
            recommendation.Description,
            recommendation.Rationale,
            recommendation.TriggerReason,
            recommendation.ChangeContext,
            Source = NormalizeRecommendationSource(recommendation.TriggerReason, recommendation.ConfidenceSource),
            SourceLabel = ToSourceLabel(NormalizeRecommendationSource(recommendation.TriggerReason, recommendation.ConfidenceSource)),
            WellArchitectedPillar = MapWellArchitectedPillar(recommendation.Category),
            recommendation.Status,
            recommendation.Priority,
            ConfidenceScore = recommendation.Confidence,
            recommendation.ConfidenceSource,
            recommendation.Impact,
            recommendation.ProposedChanges,
            recommendation.EstimatedImpact,
            recommendation.TradeoffProfile,
            recommendation.RiskProfile,
            recommendation.ImpactedServices,
            recommendation.EvidenceReferences,
            GroundingProvenance = ExtractGroundingProvenance(recommendation.ChangeContext),
            recommendation.ApprovalMode,
            recommendation.RequiredApprovals,
            recommendation.ReceivedApprovals,
            recommendation.CreatedAt,
            recommendation.ValidUntil,
            recommendation.ExpiresAt,
            lens.RiskScore,
            RiskWeightedScore = RecommendationPriorityQueueService.CalculateRiskWeightedScore(recommendation, DateTime.UtcNow),
            lens.TrustScore,
            lens.TrustLevel,
            lens.EvidenceCompleteness,
            lens.FreshnessDays
        });
    }

    /// <summary>
    /// Returns recommendation lineage checkpoints for explainability drill-down.
    /// </summary>
    [HttpGet("{id}/lineage")]
    [Authorize(Policy = "RecommendationRead")]
    public async Task<IActionResult> GetLineage(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var recommendation = await _db.Recommendations
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == id, cancellationToken);

        if (recommendation == null)
        {
            return this.ProblemNotFound("RecommendationNotFound", "Recommendation not found");
        }

        var steps = BuildLineageSteps(recommendation);

        return Ok(new
        {
            recommendationId = recommendation.Id,
            category = recommendation.Category,
            source = recommendation.ConfidenceSource,
            steps,
            provenance = ExtractGroundingProvenance(recommendation.ChangeContext)
        });
    }

    /// <summary>
    /// Returns derived workflow stage status for guided remediation UX.
    /// </summary>
    [HttpGet("{id}/workflow")]
    [Authorize(Policy = "RecommendationRead")]
    public async Task<IActionResult> GetWorkflowStatus(Guid id, CancellationToken cancellationToken = default)
    {
        var recommendation = await _db.Recommendations
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == id, cancellationToken);

        if (recommendation == null)
        {
            return this.ProblemNotFound("RecommendationNotFound", "Recommendation not found");
        }

        var latestChangeSet = await _db.IacChangeSets
            .AsNoTracking()
            .Where(c => c.RecommendationId == id)
            .OrderByDescending(c => c.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        var hasEvidence = !string.IsNullOrWhiteSpace(recommendation.Rationale) ||
                          !string.IsNullOrWhiteSpace(recommendation.EvidenceReferences);

        var validated = false;
        if (!string.IsNullOrWhiteSpace(latestChangeSet?.ValidationResult))
        {
            try
            {
                using var doc = JsonDocument.Parse(latestChangeSet.ValidationResult);
                if (doc.RootElement.TryGetProperty("passed", out var passedEl) && passedEl.ValueKind == JsonValueKind.True)
                {
                    validated = true;
                }
            }
            catch (JsonException)
            {
                validated = false;
            }
        }

        var status = RecommendationWorkflowStatus.Normalize(recommendation.Status);
        var approvedOrRejected = status is "approved" or "rejected";
        var published = string.Equals(latestChangeSet?.Status, "published", StringComparison.OrdinalIgnoreCase);

        return Ok(new
        {
            recommendationId = recommendation.Id,
            stages = new
            {
                reviewEvidence = hasEvidence,
                simulatePolicy = status is "planned" or "in_progress" or "verified" or "approved" or "rejected",
                generateChangeSet = latestChangeSet is not null,
                validate = validated || string.Equals(latestChangeSet?.Status, "validated", StringComparison.OrdinalIgnoreCase) || published,
                guardrailLint = string.Equals(latestChangeSet?.Status, "validated", StringComparison.OrdinalIgnoreCase) || published,
                approveReject = approvedOrRejected,
                publish = published
            },
            currentStatus = recommendation.Status,
            changeSetId = latestChangeSet?.Id,
            changeSetStatus = latestChangeSet?.Status
        });
    }

    /// <summary>
    /// Returns a risk-weighted queue used for triage ordering.
    /// </summary>
    [HttpGet("priority-queue")]
    [Authorize(Policy = "RecommendationRead")]
    public async Task<IActionResult> GetPriorityQueue(
        [FromQuery] int limit = 25,
        CancellationToken cancellationToken = default)
    {
        var boundedLimit = Math.Clamp(limit, 1, 200);
        var now = DateTime.UtcNow;
        var queueStatusFilters = RecommendationWorkflowStatus.GetQueueStatusDatabaseValues();

        var recommendations = await _db.Recommendations
            .AsNoTracking()
            .Where(r => queueStatusFilters.Contains(r.Status))
            .Include(r => r.ServiceGroup)
            .Take(500)
            .ToListAsync(cancellationToken);

        var queueCandidates = new List<(
            Guid Id,
            string? Title,
            string? Priority,
            string? Status,
            string? Category,
            Guid ServiceGroupId,
            string? ServiceGroupName,
            double RiskWeightedScore,
            string Reason,
            DateTime? ValidUntil,
            DateTime CreatedAt)>(recommendations.Count);
        foreach (var recommendation in recommendations)
        {
            try
            {
                queueCandidates.Add((
                    recommendation.Id,
                    recommendation.Title,
                    recommendation.Priority,
                    recommendation.Status,
                    recommendation.Category,
                    recommendation.ServiceGroupId,
                    recommendation.ServiceGroup?.Name,
                    RecommendationPriorityQueueService.CalculateRiskWeightedScore(recommendation, now),
                    RecommendationPriorityQueueService.BuildReason(recommendation, now),
                    recommendation.ValidUntil,
                    recommendation.CreatedAt));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Skipping recommendation {RecommendationId} from priority queue due to scoring error",
                    recommendation.Id);
            }
        }

        var queue = queueCandidates
            .OrderByDescending(x => x.RiskWeightedScore)
            .ThenByDescending(x => x.CreatedAt)
            .Take(boundedLimit)
            .Select(x => new
            {
                x.Id,
                x.Title,
                x.Priority,
                x.Status,
                x.Category,
                x.ServiceGroupId,
                x.ServiceGroupName,
                x.RiskWeightedScore,
                x.Reason,
                x.ValidUntil,
                x.CreatedAt
            })
            .ToList();

        return Ok(new { value = queue });
    }

    /// <summary>
    /// Provides explainability data for confidence and trust indicators.
    /// </summary>
    [HttpGet("{id}/confidence-explainer")]
    [Authorize(Policy = "RecommendationRead")]
    public async Task<IActionResult> GetConfidenceExplainer(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var recommendation = await _db.Recommendations
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == id, cancellationToken);

        if (recommendation == null)
        {
            return this.ProblemNotFound("RecommendationNotFound", "Recommendation not found");
        }

        var lens = RecommendationLensCalculator.Calculate(recommendation, DateTime.UtcNow);
        var evidenceRefs = ParseEvidenceReferences(recommendation.EvidenceReferences);

        var factors = new List<string>
        {
            $"Model confidence: {recommendation.Confidence:P0}",
            $"Evidence completeness: {lens.EvidenceCompleteness:P0}",
            $"Freshness: {lens.FreshnessDays} day(s) since creation"
        };

        var groundingSource = ExtractGroundingSource(recommendation.ChangeContext);
        var groundingTimestamp = ExtractGroundingTimestamp(recommendation.ChangeContext);
        if (!string.IsNullOrWhiteSpace(groundingSource))
        {
            factors.Add($"Grounding source: {groundingSource}");
        }
        if (groundingTimestamp.HasValue)
        {
            factors.Add($"Grounding timestamp (UTC): {groundingTimestamp:O}");
        }

        if (!string.IsNullOrWhiteSpace(recommendation.ConfidenceSource))
        {
            factors.Add($"Confidence source: {recommendation.ConfidenceSource}");
        }

        if (!string.IsNullOrWhiteSpace(recommendation.TriggerReason))
        {
            factors.Add($"Trigger: {recommendation.TriggerReason}");
        }

        return Ok(new
        {
            recommendationId = recommendation.Id,
            confidenceScore = recommendation.Confidence,
            confidenceSource = recommendation.ConfidenceSource ?? "unknown",
            trustScore = lens.TrustScore,
            trustLevel = lens.TrustLevel,
            evidenceCompleteness = lens.EvidenceCompleteness,
            freshnessDays = lens.FreshnessDays,
            evidenceReferences = evidenceRefs,
            groundingSource,
            groundingTimestampUtc = groundingTimestamp,
            factors,
            summary = BuildConfidenceSummary(recommendation.ConfidenceSource, lens.TrustLevel, lens.EvidenceCompleteness, lens.FreshnessDays)
        });
    }

    /// <summary>
    /// Generates Foundry-agent-backed Bicep and Terraform remediation examples grounded in AVM modules.
    /// </summary>
    [HttpGet("{id}/iac-examples")]
    [Authorize(Policy = "RecommendationRead")]
    public async Task<IActionResult> GetIacExamples(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var recommendation = await _db.Recommendations
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == id, cancellationToken);

        if (recommendation == null)
        {
            return this.ProblemNotFound("RecommendationNotFound", "Recommendation not found");
        }

        var result = await _avmIacExampleService.BuildExamplesAsync(recommendation, cancellationToken);
        return Ok(result);
    }

    /// <summary>
    /// Simulates policy and score impact before approval.
    /// </summary>
    [HttpPost("{id}/policy-impact-simulation")]
    [Authorize(Policy = "RecommendationRead")]
    public async Task<IActionResult> SimulatePolicyImpact(
        Guid id,
        [FromBody] PolicyImpactSimulationRequest? request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var recommendation = await _db.Recommendations
                .AsNoTracking()
                .FirstOrDefaultAsync(r => r.Id == id, cancellationToken);

            if (recommendation == null)
            {
                return this.ProblemNotFound("RecommendationNotFound", "Recommendation not found");
            }

            // Validate required fields for simulation
            if (string.IsNullOrWhiteSpace(recommendation.ActionType))
            {
                return this.ProblemBadRequest("InvalidRecommendation", "Recommendation ActionType is required for policy simulation");
            }

            if (string.IsNullOrWhiteSpace(recommendation.Category))
            {
                return this.ProblemBadRequest("InvalidRecommendation", "Recommendation Category is required for policy simulation");
            }

            if (string.IsNullOrWhiteSpace(recommendation.Title))
            {
                return this.ProblemBadRequest("InvalidRecommendation", "Recommendation Title is required for policy simulation");
            }

            var impact = request?.EstimatedImpactOverride;
            if (!impact.HasValue)
            {
                impact = TryGetImpactHint(recommendation.EstimatedImpact) ?? 3.0;
            }

            var hypothetical = new List<HypotheticalChange>
            {
                new()
                {
                    ChangeType = recommendation.ActionType,
                    Category = recommendation.Category,
                    Description = recommendation.Title,
                    EstimatedImpact = impact
                }
            };

            var simulation = await _scoreSimulation.SimulateAsync(
                recommendation.ServiceGroupId,
                hypothetical,
                cancellationToken);

            var policyThreshold = Math.Clamp(request?.PolicyThreshold ?? 60.0, 0.0, 100.0);
            var belowThreshold = simulation.ProjectedScores
                .Where(kv => kv.Value < policyThreshold)
                .Select(kv => kv.Key)
                .ToList();
            var hasRiskMitigation = simulation.RiskDeltas.Values.Any(v => v.MitigationNeeded);

            var policyDecision = belowThreshold.Count == 0 && !hasRiskMitigation
                ? "safe_to_approve"
                : "review_required";

            return Ok(new PolicyImpactSimulationResponse
            {
                RecommendationId = recommendation.Id,
                PolicyThreshold = policyThreshold,
                PolicyDecision = policyDecision,
                Reasons = BuildPolicyReasons(belowThreshold, hasRiskMitigation),
                Simulation = simulation
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error simulating policy impact for recommendation {RecommendationId}", id);
            return StatusCode(StatusCodes.Status500InternalServerError, new
            {
                type = "https://api.example.com/errors/policy-simulation-error",
                title = "Policy Simulation Error",
                status = 500,
                detail = "Failed to simulate policy impact. " + ex.Message,
                traceId = HttpContext.TraceIdentifier
            });
        }
    }

    /// <summary>
    /// Approve recommendation
    /// </summary>
    [HttpPost("{id}/approve")]
    [Authorize(Policy = "RecommendationApprove")]
    public async Task<IActionResult> ApproveRecommendation(
        Guid id,
        [FromBody] ApproveRequest request,
        CancellationToken cancellationToken = default)
    {
        var existing = await _db.Recommendations
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == id, cancellationToken);

        if (existing == null)
        {
            return this.ProblemNotFound("RecommendationNotFound", "Recommendation not found");
        }

        if (!string.IsNullOrWhiteSpace(request.ApprovalIntentHash))
        {
            var expected = ComputeApprovalIntentHash(existing, request.Comments);
            if (!string.Equals(expected, request.ApprovalIntentHash, StringComparison.OrdinalIgnoreCase))
            {
                return this.ProblemBadRequest(
                    "ApprovalIntentMismatch",
                    "Approval intent no longer matches the latest recommendation state. Refresh details and review again.");
            }
        }

        var actor =
            User.FindFirstValue("preferred_username") ??
            User.FindFirstValue("oid") ??
            User.Identity?.Name ??
            "unknown";

        var result = await _decisionService.ApproveRecommendationAsync(id, actor, request.Comments, cancellationToken);

        if (!result.IsSuccess)
        {
            return this.ProblemBadRequest("DecisionFailed", result.ErrorMessage ?? "Decision failed");
        }

        return Ok(new
        {
            result.Recommendation!.Id,
            result.Recommendation.Status,
            result.Recommendation.ReceivedApprovals,
            result.Recommendation.RequiredApprovals,
            result.IsIdempotent
        });
    }

    /// <summary>
    /// Reject recommendation
    /// </summary>
    [HttpPost("{id}/reject")]
    [Authorize(Policy = "RecommendationApprove")]
    public async Task<IActionResult> RejectRecommendation(
        Guid id,
        [FromBody] RejectRequest request,
        CancellationToken cancellationToken = default)
    {
        var actor =
            User.FindFirstValue("preferred_username") ??
            User.FindFirstValue("oid") ??
            User.Identity?.Name ??
            "unknown";

        var result = await _decisionService.RejectRecommendationAsync(id, actor, request.Reason, cancellationToken);

        if (!result.IsSuccess)
        {
            return this.ProblemBadRequest("DecisionFailed", result.ErrorMessage ?? "Decision failed");
        }

        return Ok(new
        {
            result.Recommendation!.Id,
            result.Recommendation.Status,
            result.Recommendation.RejectionReason,
            result.IsIdempotent
        });
    }

    /// <summary>
    /// Update recommendation workflow status (planned, in_progress, verified, etc.).
    /// </summary>
    [HttpPost("{id}/status")]
    [Authorize(Policy = "RecommendationApprove")]
    public async Task<IActionResult> UpdateRecommendationStatus(
        Guid id,
        [FromBody] UpdateStatusRequest request,
        CancellationToken cancellationToken = default)
    {
        var normalized = RecommendationWorkflowStatus.Normalize(request.Status);

        if (!RecommendationWorkflowStatus.IsValid(normalized))
        {
            return this.ProblemBadRequest(
                "InvalidStatus",
                $"Status '{request.Status}' is not supported.");
        }

        var recommendation = await _db.Recommendations
            .FirstOrDefaultAsync(r => r.Id == id, cancellationToken);

        if (recommendation == null)
        {
            return this.ProblemNotFound("RecommendationNotFound", "Recommendation not found");
        }

        recommendation.Status = normalized;
        recommendation.UpdatedAt = DateTime.UtcNow;

        if (!string.IsNullOrWhiteSpace(request.Comments))
        {
            recommendation.ApprovalComments = request.Comments;
        }

        await _db.SaveChangesAsync(cancellationToken);

        return Ok(new
        {
            recommendation.Id,
            recommendation.Status,
            recommendation.UpdatedAt
        });
    }

    /// <summary>
    /// Create an execution task payload from a recommendation for external work tracking.
    /// </summary>
    [HttpPost("{id}/tasks")]
    [Authorize(Policy = "RecommendationRead")]
    public async Task<IActionResult> CreateExecutionTask(
        Guid id,
        [FromBody] CreateTaskRequest request,
        CancellationToken cancellationToken = default)
    {
        var recommendation = await _db.Recommendations
            .Include(r => r.ServiceGroup)
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == id, cancellationToken);

        if (recommendation == null)
        {
            return this.ProblemNotFound("RecommendationNotFound", "Recommendation not found");
        }

        var now = DateTime.UtcNow;
        var taskId = $"task-{Guid.NewGuid():N}"[..17];
        var provider = string.IsNullOrWhiteSpace(request.Provider) ? "generic" : request.Provider.Trim();
        var dueDate = request.DueDate?.ToUniversalTime().Date ??
            (recommendation.Priority.ToLowerInvariant() switch
            {
                "critical" => now.Date.AddDays(3),
                "high" => now.Date.AddDays(7),
                "medium" => now.Date.AddDays(14),
                _ => now.Date.AddDays(30)
            });
        var actor =
            User.FindFirstValue("preferred_username") ??
            User.FindFirstValue("oid") ??
            User.Identity?.Name ??
            "unknown";

        var title = string.IsNullOrWhiteSpace(request.Title)
            ? $"[{recommendation.Priority.ToUpperInvariant()}] {recommendation.Title}"
            : request.Title.Trim();

        var payload = new
        {
            recommendationId = recommendation.Id,
            serviceGroupId = recommendation.ServiceGroupId,
            serviceGroupName = recommendation.ServiceGroup?.Name,
            title,
            assignee = request.Assignee,
            dueDate = dueDate.ToString("yyyy-MM-dd"),
            priority = recommendation.Priority,
            status = "planned",
            category = recommendation.Category,
            actionType = recommendation.ActionType,
            resourceId = recommendation.ResourceId,
            rationale = recommendation.Rationale,
            estimatedImpact = recommendation.EstimatedImpact,
            notes = request.Notes
        };

        _db.AuditEvents.Add(new Domain.Entities.AuditEvent
        {
            Id = Guid.NewGuid(),
            CorrelationId = recommendation.CorrelationId,
            ActorType = "user",
            ActorId = actor,
            EventName = "recommendation.task.created",
            EventType = "recommendation.task.created",
            EntityType = "recommendation",
            EntityId = recommendation.Id.ToString(),
            UserId = actor,
            EventPayload = JsonSerializer.Serialize(payload),
            CreatedAt = now,
            Timestamp = now
        });
        await _db.SaveChangesAsync(cancellationToken);

        return Ok(new
        {
            taskId,
            provider,
            status = "created",
            title,
            dueDate = dueDate.ToString("yyyy-MM-dd"),
            payload
        });
    }

    public record ApproveRequest(string? Comments, string? ApprovalIntentHash);
    public record RejectRequest(string Reason);
    public record UpdateStatusRequest(string Status, string? Comments);
    public record CreateTaskRequest(
        string? Provider,
        string? Title,
        string? Assignee,
        DateTime? DueDate,
        string? Notes);

    /// <summary>
    /// Streams an AI-style narrative summary of all pending recommendations via SSE.
    /// Uses the AG-UI protocol so the frontend can reuse the same streaming infrastructure.
    /// </summary>
    [HttpPost("summary")]
    [Authorize(Policy = "RecommendationRead")]
    public async Task StreamSummary(CancellationToken cancellationToken)
    {
        Response.ContentType = "text/event-stream";
        Response.Headers.Append("Cache-Control", "no-cache");
        Response.Headers.Append("Connection", "keep-alive");
        Response.Headers.Append("X-Accel-Buffering", "no");

        var runId = Guid.NewGuid().ToString();
        var threadId = Guid.NewGuid().ToString();

        try
        {
            await WriteSseEvent(new { type = "RUN_STARTED", threadId, runId }, cancellationToken);

            var recs = await _db.Recommendations
                .AsNoTracking()
                .Include(r => r.ServiceGroup)
                .OrderByDescending(r => r.CreatedAt)
                .Take(100)
                .ToListAsync(cancellationToken);

            var messageId = Guid.NewGuid().ToString();
            await WriteSseEvent(new { type = "TEXT_MESSAGE_START", messageId, role = "assistant" }, cancellationToken);

            var text = BuildSummaryNarrative(recs);

            foreach (var chunk in SseChunk(text, 8))
            {
                if (cancellationToken.IsCancellationRequested) break;
                await WriteSseEvent(new { type = "TEXT_MESSAGE_CONTENT", messageId, delta = chunk }, cancellationToken);
                await Task.Delay(25, cancellationToken);
            }

            await WriteSseEvent(new { type = "TEXT_MESSAGE_END", messageId }, cancellationToken);
            await WriteSseEvent(new { type = "RUN_FINISHED", threadId, runId }, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("SSE stream cancelled by client (expected when user navigates away)");
        }
        catch (Exception ex)
        {
            try
            {
                await WriteSseEvent(new { type = "RUN_ERROR", message = ex.Message }, cancellationToken);
            }
            catch (Exception sseEx)
            {
                _logger.LogWarning(sseEx, "Failed to send SSE error event after exception");
            }
        }
    }

    private static readonly JsonSerializerOptions SseJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private async Task WriteSseEvent(object payload, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(payload, SseJsonOptions);
        await Response.WriteAsync($"data: {json}\n\n", Encoding.UTF8, ct);
        await Response.Body.FlushAsync(ct);
    }

    private static IEnumerable<string> SseChunk(string text, int size)
    {
        for (var i = 0; i < text.Length; i += size)
            yield return text[i..Math.Min(i + size, text.Length)];
    }

    private static string BuildSummaryNarrative(List<Domain.Entities.Recommendation> recs)
    {
        if (recs.Count == 0)
            return "No recommendations have been generated yet. Run an analysis on your service groups to get actionable governance insights.";

        var pending = recs.Where(r => RecommendationWorkflowStatus.IsQueueCandidate(r.Status)).ToList();
        var approved = recs.Count(r => RecommendationWorkflowStatus.Normalize(r.Status) == RecommendationWorkflowStatus.Approved);
        var rejected = recs.Count(r => RecommendationWorkflowStatus.Normalize(r.Status) == RecommendationWorkflowStatus.Rejected);

        var byCategory = pending.GroupBy(r => r.Category).OrderByDescending(g => g.Count()).ToList();
        var byPriority = pending.GroupBy(r => r.Priority).ToDictionary(g => g.Key, g => g.Count());
        var bySg = pending.GroupBy(r => r.ServiceGroup?.Name ?? "Unknown").OrderByDescending(g => g.Count()).ToList();

        var sb = new StringBuilder();
        sb.AppendLine("Recommendations Overview");
        sb.AppendLine();
        sb.AppendLine(
            $"Atlas generated {recs.Count} recommendation{(recs.Count == 1 ? "" : "s")} across your service groups.");
        sb.AppendLine(
            $"Status: {pending.Count} pending review, {approved} approved, {rejected} rejected.");

        if (pending.Count > 0)
        {
            var critical = byPriority.GetValueOrDefault("critical", 0);
            var high = byPriority.GetValueOrDefault("high", 0);
            if (critical > 0 || high > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Priority Attention Required");
                if (critical > 0)
                    sb.AppendLine($"- {critical} critical-priority recommendation{(critical == 1 ? "" : "s")} need immediate attention");
                if (high > 0)
                    sb.AppendLine($"- {high} high-priority recommendation{(high == 1 ? "" : "s")} should be addressed soon");
            }

            sb.AppendLine();
            sb.AppendLine("By Category");
            foreach (var group in byCategory)
                sb.AppendLine($"- {group.Key}: {group.Count()} pending");

            if (bySg.Count > 1)
            {
                sb.AppendLine();
                sb.AppendLine("By Service Group");
                foreach (var group in bySg.Take(5))
                    sb.AppendLine($"- {group.Key}: {group.Count()} pending");
            }

            sb.AppendLine();
            sb.AppendLine("Top Recommendations");
            foreach (var rec in pending.OrderByDescending(r => r.Confidence).Take(5))
            {
                var name = ExtractResourceName(rec.ResourceId);
                sb.AppendLine(
                    $"- [{rec.Priority.ToUpperInvariant()}] {rec.Title} - {name} ({rec.Confidence:P0} confidence)");
            }
        }

        return sb.ToString();
    }

    private static string NormalizeRecommendationSource(string? triggerReason, string? confidenceSource)
    {
        var trigger = triggerReason?.ToLowerInvariant() ?? string.Empty;
        var confidence = confidenceSource?.ToLowerInvariant() ?? string.Empty;

        if (trigger.Contains("psrule") || trigger.Contains("ps_rule"))
            return "psrule";
        if (trigger.Contains("quick") && trigger.Contains("review"))
            return "quick_review";
        if (trigger.Contains("advisor"))
            return "advisor";
        if (trigger.Contains("drift"))
            return "drift";
        if (confidence.Contains("ai"))
            return "ai_synthesis";

        return "unclassified";
    }

    private static string ToSourceLabel(string normalizedSource) =>
        normalizedSource switch
        {
            "advisor" => "Advisor",
            "quick_review" => "Quick Review",
            "psrule" => "PSRule",
            "drift" => "Drift",
            "ai_synthesis" => "AI Synthesis",
            _ => "Unclassified"
        };

    private static string MapWellArchitectedPillar(string? category) =>
        category?.ToLowerInvariant() switch
        {
            "architecture" => "Operational Excellence",
            "reliability" => "Reliability",
            "finops" => "Cost Optimization",
            "sustainability" => "Sustainability",
            "security" => "Security",
            _ => "Cross-Pillar"
        };

    private static string ExtractResourceName(string armId)
    {
        var segments = armId.Split('/');
        return segments.Length > 1 ? segments[^1] : armId;
    }

    private static List<string> ParseEvidenceReferences(string? evidenceReferences)
    {
        if (string.IsNullOrWhiteSpace(evidenceReferences))
        {
            return [];
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<List<string>>(evidenceReferences);
            if (parsed is { Count: > 0 })
            {
                return parsed;
            }
        }
        catch (JsonException)
        {
            // Fallback to plain text split when not JSON.
        }

        return evidenceReferences
            .Split(['\n', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string BuildConfidenceSummary(string? source, string trustLevel, decimal evidenceCompleteness, int freshnessDays)
    {
        var origin = string.IsNullOrWhiteSpace(source) ? "unspecified source" : source;
        return $"Confidence is sourced from {origin}, trust is currently {trustLevel}, evidence completeness is {evidenceCompleteness:P0}, and signal freshness is {freshnessDays} day(s).";
    }

    private static object? ExtractGroundingProvenance(string? changeContext)
    {
        if (string.IsNullOrWhiteSpace(changeContext))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(changeContext);
            if (doc.RootElement.TryGetProperty("grounding", out var grounding))
            {
                return JsonSerializer.Deserialize<object>(grounding.GetRawText());
            }
        }
        catch
        {
            return null;
        }

        return null;
    }

    private static string? ExtractGroundingSource(string? changeContext)
    {
        if (string.IsNullOrWhiteSpace(changeContext))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(changeContext);
            if (doc.RootElement.TryGetProperty("grounding", out var grounding) &&
                grounding.TryGetProperty("groundingSource", out var source))
            {
                return source.GetString();
            }
        }
        catch
        {
            return null;
        }

        return null;
    }

    private static DateTime? ExtractGroundingTimestamp(string? changeContext)
    {
        if (string.IsNullOrWhiteSpace(changeContext))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(changeContext);
            if (doc.RootElement.TryGetProperty("grounding", out var grounding) &&
                grounding.TryGetProperty("groundingTimestampUtc", out var timestamp) &&
                DateTime.TryParse(timestamp.GetString(), out var parsed))
            {
                return parsed.ToUniversalTime();
            }
        }
        catch
        {
            return null;
        }

        return null;
    }

    private static IReadOnlyList<object> BuildLineageSteps(Domain.Entities.Recommendation recommendation)
    {
        var steps = new List<object>
        {
            new
            {
                step = "discovery",
                status = "completed",
                source = recommendation.TriggerReason,
                timestampUtc = recommendation.CreatedAt
            },
            new
            {
                step = "evidence",
                status = string.IsNullOrWhiteSpace(recommendation.EvidenceReferences) ? "partial" : "completed",
                source = recommendation.ConfidenceSource,
                timestampUtc = recommendation.UpdatedAt
            },
            new
            {
                step = "grounding",
                status = ExtractGroundingProvenance(recommendation.ChangeContext) is null ? "seeded" : "completed",
                source = ExtractGroundingProvenance(recommendation.ChangeContext) is null ? "seeded_rule" : "learn_mcp",
                timestampUtc = recommendation.UpdatedAt
            },
            new
            {
                step = "final_recommendation",
                status = "completed",
                source = recommendation.ConfidenceSource,
                timestampUtc = recommendation.UpdatedAt
            }
        };

        return steps;
    }

    private static double? TryGetImpactHint(string? estimatedImpactJson)
    {
        if (string.IsNullOrWhiteSpace(estimatedImpactJson))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(estimatedImpactJson);
            var root = doc.RootElement;

            if (root.TryGetProperty("scoreImprovement", out var scoreImprovement) &&
                TryReadDouble(scoreImprovement, out var scoreValue))
            {
                return scoreValue;
            }

            if (root.TryGetProperty("impactScore", out var impactScore) &&
                TryReadDouble(impactScore, out var impactValue))
            {
                return impactValue;
            }
        }
        catch (JsonException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }

        return null;
    }

    private static bool TryReadDouble(JsonElement value, out double parsed)
    {
        parsed = default;

        if (value.ValueKind == JsonValueKind.Number)
        {
            return value.TryGetDouble(out parsed);
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            var raw = value.GetString();
            if (string.IsNullOrWhiteSpace(raw))
            {
                return false;
            }

            return double.TryParse(raw, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out parsed);
        }

        return false;
    }

    private static List<string> BuildPolicyReasons(List<string> belowThreshold, bool hasRiskMitigation)
    {
        var reasons = new List<string>();
        if (belowThreshold.Count > 0)
        {
            reasons.Add($"Projected pillar scores below policy threshold: {string.Join(", ", belowThreshold)}");
        }

        if (hasRiskMitigation)
        {
            reasons.Add("One or more simulated changes require explicit mitigation steps.");
        }

        if (reasons.Count == 0)
        {
            reasons.Add("All projected scores pass threshold and no mitigation flags were triggered.");
        }

        return reasons;
    }

    public sealed record PolicyImpactSimulationRequest(double? EstimatedImpactOverride, double? PolicyThreshold);

    public class PolicyImpactSimulationResponse
    {
        public Guid RecommendationId { get; set; }
        public double PolicyThreshold { get; set; }
        public string PolicyDecision { get; set; } = "review_required";
        public List<string> Reasons { get; set; } = [];
        public Atlas.ControlPlane.Application.Services.SimulationResult Simulation { get; set; } = null!;
    }

    private static string ComputeApprovalIntentHash(Domain.Entities.Recommendation recommendation, string? comments)
    {
        var canonical = string.Join("|",
            recommendation.Id,
            RecommendationWorkflowStatus.Normalize(recommendation.Status),
            comments?.Trim() ?? string.Empty);

        var bytes = Encoding.UTF8.GetBytes(canonical);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
