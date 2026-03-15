using Atlas.ControlPlane.Api.Middleware;
using Atlas.ControlPlane.Application.Services;
using Atlas.ControlPlane.Domain.Entities;
using Atlas.ControlPlane.Infrastructure.Azure;
using Atlas.ControlPlane.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;
using System.Text.Json;

namespace Atlas.ControlPlane.Api.Endpoints;

[ApiController]
[Authorize(Policy = "AnalysisRead")]
[Route("api/v1/scores")]
public class ScoresController : ControllerBase
{
    private readonly AtlasDbContext _db;
    private readonly ScoreSimulationService _simulation;
    private readonly CompletenessAnalyzer _completenessAnalyzer;
    private readonly IImpactFactorInsightService? _insightService;
    private readonly ILogger<ScoresController> _logger;

    public ScoresController(
        AtlasDbContext db,
        ScoreSimulationService simulation,
        CompletenessAnalyzer completenessAnalyzer,
        ILogger<ScoresController> logger,
        IImpactFactorInsightService? insightService = null)
    {
        _db = db;
        _simulation = simulation;
        _completenessAnalyzer = completenessAnalyzer;
        _insightService = insightService;
        _logger = logger;
    }

    /// <summary>
    /// Get score history for a service group with optional category filter.
    /// Returns time-series snapshots suitable for trend charts.
    /// </summary>
    [HttpGet("history/{serviceGroupId}")]
    [ProducesResponseType(typeof(ScoreHistoryResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<ScoreHistoryResponse>> GetHistoryAsync(
        [FromRoute, Required] Guid serviceGroupId,
        [FromQuery] string? category = null,
        [FromQuery] int limit = 50,
        [FromQuery] DateTime? since = null,
        [FromQuery(Name = "api-version")] string? apiVersion = null)
    {
        if (string.IsNullOrWhiteSpace(apiVersion))
            return this.ProblemBadRequest("MissingApiVersionParameter", "api-version query parameter is required");

        var query = _db.ScoreSnapshots
            .Where(s => s.ServiceGroupId == serviceGroupId);

        if (!string.IsNullOrWhiteSpace(category))
            query = query.Where(s => s.Category == category);

        if (since.HasValue)
            query = query.Where(s => s.RecordedAt >= since.Value);

        var snapshots = await query
            .OrderByDescending(s => s.RecordedAt)
            .Take(Math.Clamp(limit, 1, 200))
            .Select(s => new ScorePointDto
            {
                Id = s.Id,
                Category = s.Category,
                Score = s.Score,
                Confidence = s.Confidence,
                Dimensions = s.Dimensions,
                DeltaFromPrevious = s.DeltaFromPrevious,
                ResourceCount = s.ResourceCount,
                RecordedAt = s.RecordedAt,
                AnalysisRunId = s.AnalysisRunId
            })
            .ToListAsync();

        return Ok(new ScoreHistoryResponse { Value = snapshots });
    }

    /// <summary>
    /// Get the latest score snapshot per category for a service group —
    /// used by the explainability blade to show current state + deltas.
    /// </summary>
    [HttpGet("latest/{serviceGroupId}")]
    [ProducesResponseType(typeof(ScoreHistoryResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<ScoreHistoryResponse>> GetLatestAsync(
        [FromRoute, Required] Guid serviceGroupId,
        [FromQuery(Name = "api-version")] string? apiVersion = null)
    {
        if (string.IsNullOrWhiteSpace(apiVersion))
            return this.ProblemBadRequest("MissingApiVersionParameter", "api-version query parameter is required");

        var allSnapshots = await _db.ScoreSnapshots
            .Where(s => s.ServiceGroupId == serviceGroupId)
            .OrderByDescending(s => s.RecordedAt)
            .ToListAsync();

        var latest = allSnapshots
            .GroupBy(s => s.Category)
            .Select(g => g.First())
            .Select(s => new ScorePointDto
            {
                Id = s.Id,
                Category = s.Category,
                Score = s.Score,
                Confidence = s.Confidence,
                Dimensions = s.Dimensions,
                DeltaFromPrevious = s.DeltaFromPrevious,
                ResourceCount = s.ResourceCount,
                RecordedAt = s.RecordedAt,
                AnalysisRunId = s.AnalysisRunId
            })
            .ToList();

        return Ok(new ScoreHistoryResponse { Value = latest });
    }

    /// <summary>
    /// Get score explainability breakdown for a service group by category.
    /// Returns WAF pillar scores, top contributors, and path-to-target actions.
    /// Phase 3.2: Score Explainability
    /// </summary>
    [HttpGet("explainability/{serviceGroupId}")]
    [ProducesResponseType(typeof(ScoreExplainabilityResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ScoreExplainabilityResponse>> GetExplainabilityAsync(
        [FromRoute, Required] Guid serviceGroupId,
        [FromQuery, Required] string category,
        [FromQuery] double? targetScore = null,
        [FromQuery(Name = "api-version")] string? apiVersion = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(apiVersion))
            return this.ProblemBadRequest("MissingApiVersionParameter", "api-version query parameter is required");

        if (string.IsNullOrWhiteSpace(category))
            return this.ProblemBadRequest("MissingCategoryParameter", "category query parameter is required");

        // Get latest score snapshot for this category
        var latestSnapshot = await _db.ScoreSnapshots
            .Where(s => s.ServiceGroupId == serviceGroupId && s.Category == category)
            .OrderByDescending(s => s.RecordedAt)
            .FirstOrDefaultAsync(ct);

        if (latestSnapshot == null)
            return this.ProblemNotFound("ScoreNotFound", $"No score found for service group {serviceGroupId} and category {category}");

        var parsedDimensions = ParseScoreDimensions(latestSnapshot.Dimensions);

        // Parse dimensions JSON to extract WAF pillar scores and contributors
        Dictionary<string, double>? wafPillars = null;
        Dictionary<string, double>? contributingDimensions = null;
        List<ContributorDto>? topContributors = null;

        if (parsedDimensions != null)
        {
            wafPillars = parsedDimensions.WafPillars;
            contributingDimensions = parsedDimensions.Dimensions;

            if (parsedDimensions.TopImpactFactors is { Count: > 0 })
            {
                topContributors = parsedDimensions.TopImpactFactors
                    .Select(f => new ContributorDto
                    {
                        Factor = f.Factor,
                        Description = f.Description,
                        Rationale = f.Rationale,
                        RemediationGuidance = f.RemediationGuidance,
                        RemediationIac = f.RemediationIac,
                        Impact = f.Severity == "Critical" ? -15.0 : f.Severity == "High" ? -10.0 : f.Severity == "Medium" ? -5.0 : -2.0,
                        Severity = f.Severity,
                        Count = f.GetOccurrenceCount(),
                        DriftCategories = f.DriftCategories ?? new List<string>(),
                        RuleId = f.RuleId,
                        Pillar = f.Pillar
                    })
                    .ToList();
            }
        }

        // Default WAF pillars if not in dimensions
        wafPillars ??= category switch
        {
            "Security" => new Dictionary<string, double> { ["security"] = latestSnapshot.Score / 100.0 },
            "Reliability" => new Dictionary<string, double> { ["reliability"] = latestSnapshot.Score / 100.0 },
            "FinOps" => new Dictionary<string, double> { ["costOptimization"] = latestSnapshot.Score / 100.0 },
            "Architecture" => new Dictionary<string, double> { ["performanceEfficiency"] = latestSnapshot.Score / 100.0 },
            "Sustainability" => new Dictionary<string, double> { ["sustainability"] = latestSnapshot.Score / 100.0 },
            _ => new Dictionary<string, double>()
        };

        // Enrich top contributors with Agent Framework insights
        if (_insightService != null && topContributors?.Count > 0)
        {
            try
            {
                // Get violations for this analysis run to pass to agent
                IReadOnlyList<BestPracticeViolation> violations = latestSnapshot.AnalysisRunId.HasValue
                    ? (await _db.BestPracticeViolations
                        .Where(v => v.AnalysisRunId == latestSnapshot.AnalysisRunId.Value)
                        .ToListAsync(ct))
                        .AsReadOnly()
                    : Array.Empty<BestPracticeViolation>();

                var enrichedInsights = await _insightService.GenerateInsightsAsync(
                    category,
                    topContributors
                        .Select(c => new TopImpactFactorInput(
                            c.Factor,
                            c.Count,
                            c.Severity,
                            c.DriftCategories,
                            c.RuleId))
                        .ToList(),
                    new ScoreResult(
                        Completeness: latestSnapshot.Score / 100.0,
                        CostEfficiency: latestSnapshot.Score / 100.0,
                        Availability: latestSnapshot.Score / 100.0,
                        Security: latestSnapshot.Score / 100.0,
                        ResourceCount: 0,
                        IsProductionEnvironment: false,
                        TaggingCoverage: latestSnapshot.Score / 100.0,
                        Utilization: latestSnapshot.Score / 100.0,
                        Resiliency: latestSnapshot.Score / 100.0,
                        ManagedServiceRatio: 0.5,
                        GreenRegionUsage: 0.5),
                    violations,
                    ct);

                // Map agent-generated insights back to contributors
                for (int i = 0; i < topContributors.Count && i < enrichedInsights.Count; i++)
                {
                    topContributors[i].AgentInsight = enrichedInsights[i].ActionableLongDescription;
                    topContributors[i].AgentConfidence = enrichedInsights[i].ConfidenceLevel;
                }
            }
            catch (Exception ex)
            {
                // Log but don't fail the entire response if insight enrichment fails
                // Contributors will still be returned with basic metadata
                _logger.LogWarning(ex, "Agent insight enrichment failed for category {Category}; proceeding with unenriched contributors", category);
            }
        }

        // Calculate path to target
        var target = targetScore ?? 80.0;
        var currentScore = latestSnapshot.Score;
        var gap = target - currentScore;

        var pathToTarget = new List<ActionDto>();
        if (gap > 0)
        {
            pathToTarget = await BuildRecommendationGroundedPathToTargetAsync(
                serviceGroupId,
                latestSnapshot.AnalysisRunId,
                category,
                gap,
                topContributors,
                ct);
        }

        return Ok(new ScoreExplainabilityResponse
        {
            ServiceGroupId = serviceGroupId,
            Category = category,
            AnalysisRunId = latestSnapshot.AnalysisRunId,
            CurrentScore = currentScore,
            TargetScore = target,
            Gap = gap,
            ScoringFormula = GetScoringFormula(category),
            ContributingDimensions = contributingDimensions ?? [],
            WafPillarScores = wafPillars,
            TopContributors = topContributors ?? [],
            PathToTarget = pathToTarget,
            Confidence = latestSnapshot.Confidence,
            RecordedAt = latestSnapshot.RecordedAt
        });
    }

    private async Task<List<ActionDto>> BuildRecommendationGroundedPathToTargetAsync(
        Guid serviceGroupId,
        Guid? analysisRunId,
        string category,
        double gap,
        List<ContributorDto>? contributors,
        CancellationToken ct)
    {
        var query = _db.Recommendations
            .AsNoTracking()
            .Where(r => r.ServiceGroupId == serviceGroupId)
            .Where(r => string.Equals(r.Category, category, StringComparison.OrdinalIgnoreCase));

        if (analysisRunId.HasValue)
        {
            query = query.Where(r => r.AnalysisRunId == analysisRunId.Value);
        }

        var recommendations = await query
            .OrderByDescending(r => r.CreatedAt)
            .ToListAsync(ct);

        var groundedActions = recommendations
            .Select(r => new
            {
                Action = !string.IsNullOrWhiteSpace(r.Title) ? r.Title : r.ProposedChanges,
                EstimatedImpact = ExtractEstimatedImpactPoints(r.EstimatedImpact, category),
                Effort = ExtractEffort(r.EstimatedImpact, r.Priority),
                PriorityRank = PriorityRank(r.Priority),
                Confidence = (double)r.Confidence
            })
            .Where(a => !string.IsNullOrWhiteSpace(a.Action) && a.EstimatedImpact > 0)
            .GroupBy(a => a.Action, StringComparer.OrdinalIgnoreCase)
            .Select(g => g
                .OrderByDescending(a => a.EstimatedImpact)
                .ThenBy(a => a.PriorityRank)
                .ThenByDescending(a => a.Confidence)
                .First())
            .OrderByDescending(a => a.EstimatedImpact)
            .ThenBy(a => a.PriorityRank)
            .ThenByDescending(a => a.Confidence)
            .Take(5)
            .Select((a, index) => new ActionDto
            {
                Priority = index + 1,
                Action = a.Action,
                EstimatedImpact = a.EstimatedImpact,
                Effort = a.Effort
            })
            .ToList();

        if (groundedActions.Count > 0)
        {
            return groundedActions;
        }

        return GeneratePathToTargetActions(category, gap, contributors);
    }

    private static List<ActionDto> GeneratePathToTargetActions(string category, double gap, List<ContributorDto>? contributors)
    {
        var actions = new List<ActionDto>();

        // Generate category-specific actions based on gap size
        if (gap <= 0) return actions;

        if (gap <= 10)
        {
            actions.Add(new ActionDto
            {
                Priority = 1,
                Action = $"Address {contributors?.FirstOrDefault()?.Factor ?? "top critical"} violations",
                EstimatedImpact = Math.Min(gap, 8.0),
                Effort = "Low"
            });
        }
        else if (gap <= 25)
        {
            actions.Add(new ActionDto
            {
                Priority = 1,
                Action = $"Remediate all critical and high-severity {category.ToLower()} findings",
                EstimatedImpact = 15.0,
                Effort = "Medium"
            });
            actions.Add(new ActionDto
            {
                Priority = 2,
                Action = $"Implement baseline {category.ToLower()} monitoring and alerting",
                EstimatedImpact = 8.0,
                Effort = "Medium"
            });
        }
        else
        {
            actions.Add(new ActionDto
            {
                Priority = 1,
                Action = $"Conduct comprehensive {category.ToLower()} assessment and remediation",
                EstimatedImpact = 20.0,
                Effort = "High"
            });
            actions.Add(new ActionDto
            {
                Priority = 2,
                Action = $"Establish {category.ToLower()} governance policies and automation",
                EstimatedImpact = 15.0,
                Effort = "High"
            });
            actions.Add(new ActionDto
            {
                Priority = 3,
                Action = $"Deploy continuous compliance monitoring for {category.ToLower()}",
                EstimatedImpact = 10.0,
                Effort = "Medium"
            });
        }

        return actions;
    }

    private static ScoreDimensions? ParseScoreDimensions(string? rawDimensions)
    {
        if (string.IsNullOrWhiteSpace(rawDimensions))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<ScoreDimensions>(rawDimensions, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string GetScoringFormula(string category) =>
        category switch
        {
            "Architecture" => "Score = 100 × (0.45 × completeness + 0.35 × availability + 0.20 × security)",
            "FinOps" => "Score = 100 × (0.50 × cost_efficiency + 0.30 × tagging_coverage + 0.20 × resource_utilization)",
            "Reliability" => "Score = 100 × (0.55 × availability + 0.25 × resiliency + 0.20 × security)",
            "Sustainability" => "Score = 100 × (0.50 × resource_utilization + 0.30 × carbon_signal + 0.20 × cost_efficiency)",
            "Security" => "Score = 100 × security_posture (identity, network isolation, data protection)",
            _ => "Score = 100 × weighted component dimensions"
        };

    private static double ExtractEstimatedImpactPoints(string? estimatedImpactJson, string category)
    {
        if (string.IsNullOrWhiteSpace(estimatedImpactJson))
        {
            return 0;
        }

        try
        {
            using var doc = JsonDocument.Parse(estimatedImpactJson);
            var root = doc.RootElement;

            var candidates = category switch
            {
                "Architecture" => new[]
                {
                    TryReadDouble(root, "scoreImprovement"),
                    TryReadDouble(root, "availabilityDelta"),
                    TryReadDouble(root, "securityDelta"),
                    TryReadDouble(root, "performanceDelta")
                },
                "FinOps" => new[]
                {
                    TryReadDouble(root, "scoreImprovement"),
                    TryReadDouble(root, "costDelta")
                },
                "Reliability" => new[]
                {
                    TryReadDouble(root, "scoreImprovement"),
                    TryReadDouble(root, "availabilityDelta"),
                    TryReadDouble(root, "securityDelta")
                },
                "Sustainability" => new[]
                {
                    TryReadDouble(root, "scoreImprovement"),
                    TryReadDouble(root, "carbonDelta"),
                    TryReadDouble(root, "performanceDelta"),
                    TryReadDouble(root, "costDelta")
                },
                _ => new[]
                {
                    TryReadDouble(root, "scoreImprovement"),
                    TryReadDouble(root, "availabilityDelta"),
                    TryReadDouble(root, "securityDelta"),
                    TryReadDouble(root, "costDelta")
                }
            };

            var best = candidates
                .Where(value => value.HasValue && value.Value > 0)
                .Select(value => value!.Value)
                .DefaultIfEmpty(0)
                .Max();

            return Math.Round(best, 1);
        }
        catch (JsonException)
        {
            return 0;
        }
    }

    private static double? TryReadDouble(JsonElement root, string propertyName)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (!string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetDouble(out var number))
            {
                return number;
            }

            if (property.Value.ValueKind == JsonValueKind.String &&
                double.TryParse(property.Value.GetString(), out var parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    private static string ExtractEffort(string? estimatedImpactJson, string priority)
    {
        if (!string.IsNullOrWhiteSpace(estimatedImpactJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(estimatedImpactJson);
                if (TryReadString(doc.RootElement, "implementationCost") is { Length: > 0 } implementationCost)
                {
                    return NormalizeEffort(implementationCost);
                }
            }
            catch (JsonException)
            {
                // Fall back to priority-derived effort.
            }
        }

        return priority.ToLowerInvariant() switch
        {
            "critical" => "High",
            "high" => "Medium",
            "medium" => "Medium",
            _ => "Low"
        };
    }

    private static string? TryReadString(JsonElement root, string propertyName)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (!string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (property.Value.ValueKind == JsonValueKind.String)
            {
                return property.Value.GetString();
            }
        }

        return null;
    }

    private static string NormalizeEffort(string rawEffort) =>
        rawEffort.Trim().ToLowerInvariant() switch
        {
            "low" or "small" => "Low",
            "medium" or "moderate" => "Medium",
            "high" or "large" => "High",
            _ => "Medium"
        };

    private static int PriorityRank(string priority) =>
        priority.ToLowerInvariant() switch
        {
            "critical" => 0,
            "high" => 1,
            "medium" => 2,
            "low" => 3,
            _ => 4
        };

    /// <summary>
    /// Simulate score impact of hypothetical changes.
    /// Uses the latest scores as baseline and applies estimated deltas.
    /// </summary>
    [HttpPost("simulate")]
    [ProducesResponseType(typeof(ScoreSimulationResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<ScoreSimulationResponse>> SimulateAsync(
        [FromBody] ScoreSimulationRequest request,
        [FromQuery(Name = "api-version")] string? apiVersion = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(apiVersion))
            return this.ProblemBadRequest("MissingApiVersionParameter", "api-version query parameter is required");

        if (request.HypotheticalChanges.Count == 0)
            return this.ProblemBadRequest("NoChanges", "At least one hypothetical change is required");

        if (request.HypotheticalChanges.Count > 50)
            return this.ProblemBadRequest("TooManyChanges", "Maximum 50 hypothetical changes per request");

        foreach (var change in request.HypotheticalChanges)
        {
            if (string.IsNullOrWhiteSpace(change.ChangeType))
                return this.ProblemBadRequest("InvalidChange", "ChangeType is required for each hypothetical change");
            if (string.IsNullOrWhiteSpace(change.Category))
                return this.ProblemBadRequest("InvalidChange", "Category is required for each hypothetical change");
            if (change.EstimatedImpact.HasValue)
                change.EstimatedImpact = Math.Clamp(change.EstimatedImpact.Value, -50, 50);
        }

        var changes = request.HypotheticalChanges.Select(c => new HypotheticalChange
        {
            ChangeType = c.ChangeType,
            Category = c.Category,
            Description = c.Description,
            EstimatedImpact = c.EstimatedImpact
        }).ToList();

        SimulationResult result;
        try
        {
            result = await _simulation.SimulateAsync(request.ServiceGroupId, changes, ct);
        }
        catch (Exception ex)
        {
            return this.ProblemServiceUnavailable("SimulationFailed",
                $"Simulation failed for service group {request.ServiceGroupId}: {ex.Message}");
        }

        return Ok(new ScoreSimulationResponse
        {
            CurrentScores = result.CurrentScores,
            ProjectedScores = result.ProjectedScores,
            Deltas = result.Deltas,
            CostDeltas = result.CostDeltas.ToDictionary(kv => kv.Key, kv => new CostDeltaDto
            {
                EstimatedMonthlySavings = kv.Value.EstimatedMonthlySavings,
                EstimatedImplementationCost = kv.Value.EstimatedImplementationCost,
                NetAnnualImpact = kv.Value.NetAnnualImpact
            }),
            RiskDeltas = result.RiskDeltas.ToDictionary(kv => kv.Key, kv => new RiskDeltaDto
            {
                RiskLevel = kv.Value.RiskLevel,
                ScoreDelta = kv.Value.ScoreDelta,
                ChangeCount = kv.Value.ChangeCount,
                MitigationNeeded = kv.Value.MitigationNeeded
            }),
            Confidence = result.Confidence,
            Reasoning = result.Reasoning
        });
    }

    /// <summary>
    /// Analyze metadata completeness gaps for a service group's latest analysis run.
    /// Shows which metadata fields (tags, region, SKU, kind) are missing and by how much,
    /// helping diagnose why completeness scores are low.
    /// </summary>
    [HttpGet("diagnose-completeness/{serviceGroupId}")]
    [ProducesResponseType(typeof(CompletenessAnalysisResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<CompletenessAnalysisResult>> DiagnoseCompletenessAsync(
        [FromRoute, Required] Guid serviceGroupId,
        [FromQuery(Name = "api-version")] string? apiVersion = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(apiVersion))
            return this.ProblemBadRequest("MissingApiVersionParameter", "api-version query parameter is required");

        // Find the latest analysis run for this service group
        var run = await _db.AnalysisRuns
            .Where(r => r.ServiceGroupId == serviceGroupId)
            .OrderByDescending(r => r.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (run == null)
            return this.ProblemNotFound("NoAnalysisRun",
                $"No analysis has been run for service group {serviceGroupId}");

        // Find the snapshot associated with this run
        var snapshot = run.SnapshotId.HasValue
            ? await _db.DiscoverySnapshots
                .Where(s => s.Id == run.SnapshotId)
                .FirstOrDefaultAsync(cancellationToken)
            : null;

        if (snapshot == null)
            return this.ProblemNotFound("NoSnapshot",
                $"No discovery snapshot found for analysis run {run.Id}");

        // Load discovered resources from the snapshot
        var discoveredResources = await _db.DiscoveredResources
            .Where(r => r.SnapshotId == snapshot.Id)
            .ToListAsync(cancellationToken);

        if (discoveredResources.Count == 0)
            return this.ProblemNotFound("NoResources",
                "The discovery snapshot contains no resources");

        // Convert to the format expected by the analyzer
        var resourcesToAnalyze = discoveredResources
            .Select(r => new DiscoveredAzureResource(
                ArmId: r.AzureResourceId,
                Name: r.ResourceName,
                ResourceType: r.ResourceType,
                Location: r.Region ?? "global",
                ResourceGroup: null,
                SubscriptionId: null,
                Sku: r.Sku,
                Tags: r.Metadata,
                Kind: null,
                Properties: null))
            .ToList();

        // Run the analysis
        var analysis = _completenessAnalyzer.Analyze(resourcesToAnalyze);
        return Ok(analysis);
    }
}

public class ScoreHistoryResponse
{
    public List<ScorePointDto> Value { get; set; } = [];
}

public class ScorePointDto
{
    public Guid Id { get; set; }
    public string Category { get; set; } = "";
    public double Score { get; set; }
    public double Confidence { get; set; }
    public string? Dimensions { get; set; }
    public string? DeltaFromPrevious { get; set; }
    public int ResourceCount { get; set; }
    public DateTime RecordedAt { get; set; }
    public Guid? AnalysisRunId { get; set; }
}
public class ScoreSimulationRequest
{
    public Guid ServiceGroupId { get; set; }
    public List<HypotheticalChangeDto> HypotheticalChanges { get; set; } = [];
}

public class HypotheticalChangeDto
{
    public required string ChangeType { get; set; }
    public required string Category { get; set; }
    public required string Description { get; set; }
    public double? EstimatedImpact { get; set; }
}

public class ScoreSimulationResponse
{
    public Dictionary<string, double> CurrentScores { get; set; } = [];
    public Dictionary<string, double> ProjectedScores { get; set; } = [];
    public Dictionary<string, double> Deltas { get; set; } = [];
    public Dictionary<string, CostDeltaDto> CostDeltas { get; set; } = [];
    public Dictionary<string, RiskDeltaDto> RiskDeltas { get; set; } = [];
    public double Confidence { get; set; }
    public string Reasoning { get; set; } = "";
}

public class CostDeltaDto
{
    public double EstimatedMonthlySavings { get; set; }
    public double EstimatedImplementationCost { get; set; }
    public double NetAnnualImpact { get; set; }
}

public class RiskDeltaDto
{
    public string RiskLevel { get; set; } = "unchanged";
    public double ScoreDelta { get; set; }
    public int ChangeCount { get; set; }
    public bool MitigationNeeded { get; set; }
}

// Phase 3.2: Score Explainability DTOs
public class ScoreExplainabilityResponse
{
    public Guid ServiceGroupId { get; set; }
    public string Category { get; set; } = "";
    public Guid? AnalysisRunId { get; set; }
    public double CurrentScore { get; set; }
    public double TargetScore { get; set; }
    public double Gap { get; set; }
    public string ScoringFormula { get; set; } = "";
    public Dictionary<string, double> ContributingDimensions { get; set; } = [];
    public Dictionary<string, double> WafPillarScores { get; set; } = [];
    public List<ContributorDto> TopContributors { get; set; } = [];
    public List<ActionDto> PathToTarget { get; set; } = [];
    public double Confidence { get; set; }
    public DateTime RecordedAt { get; set; }
}

public class ContributorDto
{
    public string Factor { get; set; } = "";
    public string? Description { get; set; }
    public string? Rationale { get; set; }
    public string? RemediationGuidance { get; set; }
    public string? RemediationIac { get; set; }
    public double Impact { get; set; }
    public string Severity { get; set; } = "";
    public int Count { get; set; }
    public List<string> DriftCategories { get; set; } = new();
    public string? RuleId { get; set; }
    public string? Pillar { get; set; }

    /// <summary>
    /// Agent Framework-generated narrative insight explaining why this factor impacts the score.
    /// </summary>
    public string? AgentInsight { get; set; }

    /// <summary>
    /// Confidence level from Agent analysis (e.g., "High", "Medium", "Low").
    /// </summary>
    public string? AgentConfidence { get; set; }
}

public class ActionDto
{
    public int Priority { get; set; }
    public string Action { get; set; } = "";
    public double EstimatedImpact { get; set; }
    public string Effort { get; set; } = "";
}

// Internal model for parsing Dimensions JSON
internal class ScoreDimensions
{
    public Dictionary<string, double>? Dimensions { get; set; }
    public Dictionary<string, double>? WafPillars { get; set; }
    public List<ImpactFactor>? TopImpactFactors { get; set; }
}

internal class ImpactFactor
{
    public string Factor { get; set; } = "";
    public string? Description { get; set; }
    public string? Rationale { get; set; }
    public string? RemediationGuidance { get; set; }
    public string? RemediationIac { get; set; }
    public string Severity { get; set; } = "";
    public int Count { get; set; }
    public int AffectedResources { get; set; }
    public List<string>? DriftCategories { get; set; }
    public string? RuleId { get; set; }
    public string? Pillar { get; set; }

    public int GetOccurrenceCount() => Count > 0 ? Count : AffectedResources;
}
