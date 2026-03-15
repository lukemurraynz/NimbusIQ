using Atlas.ControlPlane.Api.Middleware;
using Atlas.ControlPlane.Application.Services;
using Atlas.ControlPlane.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Atlas.ControlPlane.Api.Endpoints;

[ApiController]
[Route("api/v1/governance")]
public class GovernanceController : ControllerBase
{
    private readonly AtlasDbContext _db;
    private readonly GovernanceAgentReasoningService _reasoningService;
    private readonly ILogger<GovernanceController> _logger;

    public GovernanceController(
        AtlasDbContext db,
        GovernanceAgentReasoningService reasoningService,
        ILogger<GovernanceController> logger)
    {
        _db = db;
        _reasoningService = reasoningService;
        _logger = logger;
    }

    /// <summary>
    /// Negotiate governance conflicts among competing recommendations.
    /// Analyzes the specified recommendations for policy/pillar trade-offs
    /// and proposes adjusted recommendations that balance competing concerns.
    /// </summary>
    [HttpPost("negotiate")]
    [Authorize(Policy = "RecommendationRead")]
    public async Task<IActionResult> Negotiate(
        [FromBody] GovernanceNegotiationRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.ConflictIds is not { Count: 2 })
            return this.ProblemBadRequest("InvalidConflictIds", "Exactly two conflict IDs are required for governance comparison.");

        try
        {
            var serviceGroup = await _db.ServiceGroups
                .AsNoTracking()
                .FirstOrDefaultAsync(sg => sg.Id == request.ServiceGroupId, cancellationToken);

            if (serviceGroup is null)
                return NotFound(new { error = "Service group not found." });

            var recommendations = await _db.Recommendations
                .AsNoTracking()
                .Where(r => request.ConflictIds.Contains(r.Id))
                .ToListAsync(cancellationToken);

            if (recommendations.Count == 0)
                return NotFound(new { error = "No matching recommendations found." });

            var orderedRecommendations = request.ConflictIds
                .Select(id => recommendations.FirstOrDefault(r => r.Id == id))
                .Where(r => r is not null)
                .Cast<Domain.Entities.Recommendation>()
                .ToList();

            if (orderedRecommendations.Count != 2)
                return NotFound(new { error = "One or more selected recommendations could not be found." });

            var reasoning = await BuildReasoningAsync(orderedRecommendations, request.Preferences, cancellationToken);

            var compromises = new List<GovernanceCompromiseDto>();
            var pillarGroups = orderedRecommendations
                .GroupBy(r => r.Category)
                .ToDictionary(g => g.Key, g => g.ToList());

            // Detect cross-pillar conflicts (e.g., Security vs Cost, Reliability vs Performance)
            var pillars = pillarGroups.Keys.ToList();
            var conflictPairs = new List<(string PillarA, string PillarB)>();
            for (var i = 0; i < pillars.Count; i++)
            {
                for (var j = i + 1; j < pillars.Count; j++)
                {
                    conflictPairs.Add((pillars[i], pillars[j]));
                }
            }

            foreach (var rec in orderedRecommendations)
            {
                var comparison = reasoning?.Comparisons
                    .FirstOrDefault(c => c.RecommendationId == rec.Id);

                var compromise = NegotiateRecommendation(rec, pillarGroups, request.Preferences, comparison);
                compromises.Add(compromise);
            }

            var hasRealConflicts = conflictPairs.Count > 0;
            var confidence = reasoning?.Confidence
                ?? (hasRealConflicts
                    ? Math.Max(0.5, 1.0 - (conflictPairs.Count * 0.1))
                    : 0.95);

            var reasoningText = reasoning?.Reasoning ?? (hasRealConflicts
                ? $"Detected cross-pillar tensions between {string.Join(", ", conflictPairs.Select(p => $"{p.PillarA} ↔ {p.PillarB}"))}. " +
                  $"Adjusted {compromises.Count(c => c.AdjustedRecommendation != c.OriginalRecommendation)} of {compromises.Count} recommendations to balance trade-offs."
                : $"All {orderedRecommendations.Count} recommendations align on the same pillar(s). Minor adjustments applied based on priority ordering.");

            var result = new GovernanceNegotiationResultDto
            {
                Resolution = reasoning?.Resolution ?? (hasRealConflicts ? "compromise_reached" : "no_conflict"),
                Compromises = compromises,
                Confidence = confidence,
                Reasoning = reasoningText,
                AgentReasoningSource = reasoning?.Source ?? "rule_engine",
            };

            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Governance negotiation failed for service group {ServiceGroupId} with conflict IDs {ConflictIds}",
                request.ServiceGroupId, string.Join(", ", request.ConflictIds));
            return StatusCode(500, new
            {
                type = "https://httpstatuses.com/500",
                title = "Governance negotiation failed",
                status = 500,
                detail = ex.Message,
                errorCode = "GovernanceNegotiationError"
            });
        }
    }

    private async Task<GovernanceAgentReasoningResult?> BuildReasoningAsync(
        List<Domain.Entities.Recommendation> recommendations,
        Dictionary<string, string>? preferences,
        CancellationToken cancellationToken)
    {
        if (recommendations.Count < 2)
        {
            return null;
        }

        preferences ??= new Dictionary<string, string>();
        preferences.TryGetValue("priorityPillar", out var priorityPillar);
        preferences.TryGetValue("naturalLanguageContext", out var naturalLanguageContext);

        return await _reasoningService.AnalyzeAsync(
            recommendations[0],
            recommendations[1],
            priorityPillar,
            naturalLanguageContext,
            cancellationToken);
    }

    private static GovernanceCompromiseDto NegotiateRecommendation(
        Domain.Entities.Recommendation rec,
        Dictionary<string, List<Domain.Entities.Recommendation>> pillarGroups,
        Dictionary<string, string>? preferences,
        GovernanceComparison? comparison)
    {
        var pillar = rec.Category;
        var hasCompetingPillars = pillarGroups.Count > 1;
        var preferredPillar = preferences?.GetValueOrDefault("priorityPillar");

        // If user has a preferred pillar and this rec is not from it, soften the recommendation
        var isDeprioritized = hasCompetingPillars
            && preferredPillar is not null
            && !string.Equals(pillar, preferredPillar, StringComparison.OrdinalIgnoreCase);

        var originalText = rec.Title;
        var adjustedText = isDeprioritized
            ? $"[Deferred] {rec.Title} — deprioritized in favor of {preferredPillar} objectives"
            : rec.Title;

        var tradeoff = comparison?.Tradeoff ?? (isDeprioritized
            ? $"This {pillar} recommendation is deferred to prioritize {preferredPillar}. " +
              $"Monitor {pillar} metrics and revisit if thresholds degrade."
            : hasCompetingPillars
                ? $"This {pillar} recommendation is retained. No trade-off needed."
                : "No conflict detected for this recommendation.");

        return new GovernanceCompromiseDto
        {
            ConflictId = rec.Id.ToString(),
            RecommendationId = rec.Id,
            OriginalRecommendation = originalText,
            AdjustedRecommendation = adjustedText,
            Tradeoff = tradeoff,
            Pillar = comparison?.Pillar ?? pillar,
            ImpactScore = comparison?.ImpactScore,
            Swot = comparison?.Swot is null
                ? null
                : new GovernanceSwotDto
                {
                    Strengths = comparison.Swot.Strengths,
                    Weaknesses = comparison.Swot.Weaknesses,
                    Opportunities = comparison.Swot.Opportunities,
                    Threats = comparison.Swot.Threats,
                }
        };
    }
}

public record GovernanceNegotiationRequest
{
    public Guid ServiceGroupId { get; init; }
    public List<Guid> ConflictIds { get; init; } = [];
    public Dictionary<string, string>? Preferences { get; init; }
}

public record GovernanceNegotiationResultDto
{
    public required string Resolution { get; init; }
    public required List<GovernanceCompromiseDto> Compromises { get; init; }
    public required double Confidence { get; init; }
    public required string Reasoning { get; init; }
    public required string AgentReasoningSource { get; init; }
}

public record GovernanceCompromiseDto
{
    public required string ConflictId { get; init; }
    public required Guid RecommendationId { get; init; }
    public required string OriginalRecommendation { get; init; }
    public required string AdjustedRecommendation { get; init; }
    public required string Tradeoff { get; init; }
    public string? Pillar { get; init; }
    public double? ImpactScore { get; init; }
    public GovernanceSwotDto? Swot { get; init; }
}

public record GovernanceSwotDto
{
    public IReadOnlyList<string> Strengths { get; init; } = [];
    public IReadOnlyList<string> Weaknesses { get; init; } = [];
    public IReadOnlyList<string> Opportunities { get; init; } = [];
    public IReadOnlyList<string> Threats { get; init; } = [];
}
