using System.Text.Json;
using Atlas.ControlPlane.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Atlas.ControlPlane.Application.Services;

/// <summary>
/// Generates executive narratives using AI Foundry when available,
/// with deterministic rule-based fallback.
/// </summary>
public class ExecutiveNarrativeService
{
    private readonly AtlasDbContext _db;
    private readonly AIChatService? _aiChatService;
    private readonly ILogger<ExecutiveNarrativeService> _logger;

    public ExecutiveNarrativeService(
        AtlasDbContext db,
        ILogger<ExecutiveNarrativeService> logger,
        AIChatService? aiChatService = null)
    {
        _db = db;
        _aiChatService = aiChatService;
        _logger = logger;
    }

    public async Task<ExecutiveNarrativeResult> GenerateAsync(
        Guid serviceGroupId,
        CancellationToken cancellationToken = default)
    {
        var allSnapshots = await _db.ScoreSnapshots
            .Where(s => s.ServiceGroupId == serviceGroupId)
            .OrderByDescending(s => s.RecordedAt)
            .ToListAsync(cancellationToken);

        var latestScores = allSnapshots
            .GroupBy(s => s.Category)
            .Select(g => g.First())
            .ToList();

        var pendingRecs = await _db.Recommendations
            .Where(r => r.ServiceGroupId == serviceGroupId && r.Status == "pending")
            .CountAsync(cancellationToken);

        var criticalRecs = await _db.Recommendations
            .Where(r => r.ServiceGroupId == serviceGroupId
                        && r.Status == "pending"
                        && (r.Priority == "critical" || r.Priority == "high"))
            .CountAsync(cancellationToken);

        var topRecs = await _db.Recommendations
            .Where(r => r.ServiceGroupId == serviceGroupId && r.Status == "pending")
            .OrderByDescending(r => r.Priority == "critical" ? 4 : r.Priority == "high" ? 3 : r.Priority == "medium" ? 2 : 1)
            .Take(5)
            .Select(r => new { r.Title, r.Category, r.Priority, r.ConfidenceSource })
            .ToListAsync(cancellationToken);

        var latestDrift = await _db.DriftSnapshots
            .Where(d => d.ServiceGroupId == serviceGroupId)
            .OrderByDescending(d => d.SnapshotTime)
            .FirstOrDefaultAsync(cancellationToken);

        var highlights = BuildHighlights(latestScores);
        var avgScore = latestScores.Count > 0 ? latestScores.Average(s => s.Score) : 0;
        var aiConfigured = _aiChatService?.IsAIAvailable == true;
        var aiRuntimeFallback = false;
        string confidenceSource = "rule_engine";

        // AI-powered narrative when Azure AI Foundry is available
        if (aiConfigured)
        {
            try
            {
                var prompt = BuildNarrativePrompt(latestScores, pendingRecs, criticalRecs, topRecs, latestDrift, avgScore);
                var context = new InfrastructureContext
                {
                    ServiceGroupCount = 1,
                    ServiceGroupNames = [serviceGroupId.ToString()],
                    RecentRunCount = 1,
                    CompletedRunCount = 1,
                    PendingRunCount = 0,
                    Findings = highlights.Select(h => new FindingSummary(h.Message, h.Category)).ToList(),
                    DetailedDataJson = JsonSerializer.Serialize(new
                    {
                        scores = latestScores.Select(s => new { s.Category, s.Score, s.Confidence }),
                        pendingRecommendations = pendingRecs,
                        criticalRecommendations = criticalRecs,
                        driftScore = latestDrift?.DriftScore,
                        topRecommendations = topRecs
                    })
                };

                var aiResponse = await _aiChatService!.GenerateResponseAsync(prompt, context, cancellationToken);
                if (aiResponse is null)
                {
                    aiRuntimeFallback = true;
                    confidenceSource = "ai_foundry_fallback";
                    _logger.LogWarning("AI narrative response was null for service group {ServiceGroupId}; using fallback", serviceGroupId);
                }
                else
                {
                    var confidenceSourceValue = aiResponse.ConfidenceSource ?? string.Empty;

                    if (confidenceSourceValue.StartsWith("ai_foundry", StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogInformation("AI narrative generated for service group {ServiceGroupId}", serviceGroupId);
                        return new ExecutiveNarrativeResult
                        {
                            Summary = aiResponse.Text,
                            Highlights = highlights,
                            GeneratedAt = DateTime.UtcNow,
                            ConfidenceSource = "ai_foundry"
                        };
                    }

                    if (!string.IsNullOrWhiteSpace(aiResponse.Text))
                    {
                        var fallbackSource = confidenceSourceValue.Equals("structured_summary_fallback", StringComparison.OrdinalIgnoreCase)
                            ? "ai_foundry_fallback"
                            : "rule_engine";

                        _logger.LogWarning(
                            "Executive narrative fell back to structured summary for service group {ServiceGroupId}. Source: {Source}",
                            serviceGroupId,
                            confidenceSourceValue);

                        return new ExecutiveNarrativeResult
                        {
                            Summary = aiResponse.Text,
                            Highlights = highlights,
                            GeneratedAt = DateTime.UtcNow,
                            ConfidenceSource = fallbackSource
                        };
                    }

                    aiRuntimeFallback = true;
                    confidenceSource = "ai_foundry_fallback";
                }
            }
            catch (Exception ex)
            {
                aiRuntimeFallback = true;
                confidenceSource = "ai_foundry_error";
                _logger.LogWarning(ex, "AI narrative generation failed for {ServiceGroupId}; using rule-based fallback", serviceGroupId);
            }
        }

        // Deterministic fallback — provides basic metrics only (no cross-resource correlation or risk narrative)
        var summaryParts = new List<string>();
        summaryParts.Add($"Overall posture score: {avgScore:F0}/100 across {latestScores.Count} pillars.");
        if (criticalRecs > 0)
            summaryParts.Add($"{criticalRecs} high-priority recommendation(s) require attention.");
        if (pendingRecs > 0)
            summaryParts.Add($"{pendingRecs} total pending recommendation(s).");
        if (latestDrift != null)
            summaryParts.Add($"Latest drift score: {latestDrift.DriftScore:F1} with {latestDrift.TotalViolations} violation(s).");
        if (aiRuntimeFallback)
        {
            summaryParts.Add("AI-enhanced narrative was unavailable for this run, so a deterministic summary is shown.");
        }
        else if (!aiConfigured)
        {
            summaryParts.Add("Enable Azure AI Foundry for cross-pillar correlation, root-cause analysis, and prioritised action plans.");
        }

        return new ExecutiveNarrativeResult
        {
            Summary = string.Join(" ", summaryParts),
            Highlights = highlights,
            GeneratedAt = DateTime.UtcNow,
            ConfidenceSource = confidenceSource
        };
    }

    private static string BuildNarrativePrompt(
        List<Domain.Entities.ScoreSnapshot> scores,
        int pendingRecs,
        int criticalRecs,
        object topRecs,
        Domain.Entities.DriftSnapshot? drift,
        double avgScore)
    {
        var scoreLines = string.Join("\n", scores.Select(s => $"- {s.Category}: {s.Score}/100 (confidence: {s.Confidence:P0})"));
        var driftScoreText = drift?.DriftScore.ToString("F1") ?? "N/A";
        var driftViolations = drift?.TotalViolations ?? 0;
        var topRecsJson = JsonSerializer.Serialize(topRecs);

        return $"""
            You are NimbusIQ, an Azure infrastructure governance AI. Generate a concise executive narrative
            (3–5 sentences) summarizing the current infrastructure posture for a cloud architect audience.
            Scope: summarize only the provided NimbusIQ metrics and findings. Do not provide guidance outside this solution's domain.

            ## Current Scores
            {scoreLines}
            Overall average: {avgScore:F0}/100

            ## Recommendations
            - {pendingRecs} pending recommendations ({criticalRecs} critical/high)
            - Top recommendations: {topRecsJson}

            ## Drift
            - Drift score: {driftScoreText}
            - Total violations: {driftViolations}

            Requirements:
            1. Start with the overall posture assessment (healthy/at-risk/critical)
            2. Highlight the most impactful finding
            3. Call out any urgent action items
            4. Keep tone professional, concise, and actionable
            5. Do NOT use markdown formatting — plain text only
                6. Use only provided evidence. If uncertainty exists, state uncertainty explicitly
                7. Do not include secrets, credentials, access tokens, raw tenant identifiers, or internal endpoint details
                8. Do not claim provider-backed execution; narrative output is analysis-only
            """;
    }

    private static List<NarrativeHighlight> BuildHighlights(List<Domain.Entities.ScoreSnapshot> scores)
    {
        var highlights = new List<NarrativeHighlight>();

        foreach (var score in scores)
        {
            string trend;
            string severity;
            string message;

            if (score.DeltaFromPrevious != null)
            {
                try
                {
                    var delta = JsonSerializer.Deserialize<DeltaInfo>(score.DeltaFromPrevious);
                    if (delta?.delta > 5) { trend = "improving"; severity = "success"; message = $"{score.Category} improved by {delta.delta} points."; }
                    else if (delta?.delta < -5) { trend = "declining"; severity = "warning"; message = $"{score.Category} declined by {Math.Abs(delta.delta)} points — review required."; }
                    else { trend = "stable"; severity = "info"; message = $"{score.Category} is stable at {score.Score}/100."; }
                }
                catch
                {
                    trend = "stable"; severity = "info"; message = $"{score.Category} is at {score.Score}/100.";
                }
            }
            else
            {
                trend = "baseline"; severity = "info"; message = $"{score.Category} baseline established at {score.Score}/100.";
            }

            highlights.Add(new NarrativeHighlight { Category = score.Category, Trend = trend, Message = message, Severity = severity });
        }

        return highlights;
    }

    private sealed class DeltaInfo
    {
        public int previousScore { get; set; }
        public int delta { get; set; }
    }
}

public class ExecutiveNarrativeResult
{
    public string Summary { get; set; } = "";
    public List<NarrativeHighlight> Highlights { get; set; } = [];
    public DateTime GeneratedAt { get; set; }
    public string ConfidenceSource { get; set; } = "rule_engine";
}

public class NarrativeHighlight
{
    public string Category { get; set; } = "";
    public string Trend { get; set; } = "";
    public string Message { get; set; } = "";
    public string Severity { get; set; } = "";
}
