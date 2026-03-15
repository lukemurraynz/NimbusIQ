using Atlas.ControlPlane.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Globalization;

namespace Atlas.ControlPlane.Application.Services;

public class ScoreSimulationService
{
    private readonly AtlasDbContext _db;
    private readonly ILogger<ScoreSimulationService> _logger;

    private static readonly string[] Categories = ["Architecture", "FinOps", "Reliability", "Sustainability"];

    public ScoreSimulationService(AtlasDbContext db, ILogger<ScoreSimulationService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<SimulationResult> SimulateAsync(
        Guid serviceGroupId,
        List<HypotheticalChange> changes,
        CancellationToken ct = default)
    {
        var allSnapshots = await _db.ScoreSnapshots
            .Where(s => s.ServiceGroupId == serviceGroupId)
            .OrderByDescending(s => s.RecordedAt)
            .ToListAsync(ct);

        var latestScores = allSnapshots
            .GroupBy(s => s.Category)
            .ToDictionary(g => g.Key, g => g.First());

        var currentScores = new Dictionary<string, double>();
        var projectedScores = new Dictionary<string, double>();
        var deltas = new Dictionary<string, double>();

        foreach (var category in Categories)
        {
            var current = latestScores.TryGetValue(category, out var snap) ? snap.Score : 50.0;
            currentScores[category] = current;

            var categoryImpact = changes
                .Where(c => string.Equals(c.Category, category, StringComparison.OrdinalIgnoreCase))
                .Sum(c => c.EstimatedImpact ?? 0);

            var projected = Math.Clamp(current + categoryImpact, 0, 100);
            projectedScores[category] = Math.Round(projected, 1);
            deltas[category] = Math.Round(projected - current, 1);
        }

        var costDeltas = await BuildCostDeltasAsync(serviceGroupId, changes, ct);
        var riskDeltas = BuildRiskDeltas(changes, deltas);

        var totalDelta = deltas.Values.Sum();
        var reasoning = totalDelta switch
        {
            > 10 => "Significant improvement expected across multiple dimensions.",
            > 0 => "Moderate positive impact expected from proposed changes.",
            0 => "No measurable impact expected from proposed changes.",
            > -10 => "Minor negative impact — review tradeoffs before proceeding.",
            _ => "Significant negative impact — consider alternative approaches."
        };

        return new SimulationResult
        {
            CurrentScores = currentScores,
            ProjectedScores = projectedScores,
            Deltas = deltas,
            CostDeltas = costDeltas,
            RiskDeltas = riskDeltas,
            Confidence = CalculateConfidence(changes, latestScores.Count),
            Reasoning = reasoning
        };
    }

    /// <summary>
    /// Estimates cost impact by cross-referencing hypothetical changes with
    /// existing recommendations that have EstimatedImpact JSON containing cost data.
    /// </summary>
    private async Task<Dictionary<string, CostDelta>> BuildCostDeltasAsync(
        Guid serviceGroupId,
        List<HypotheticalChange> changes,
        CancellationToken ct)
    {
        var costDeltas = new Dictionary<string, CostDelta>();

        var recs = await _db.Recommendations
            .Where(r => r.ServiceGroupId == serviceGroupId
                        && r.Status == "pending"
                        && r.EstimatedImpact != null)
            .Select(r => new { r.Category, r.EstimatedImpact, r.ChangeContext })
            .ToListAsync(ct);

        foreach (var category in Categories)
        {
            var relevantChanges = changes
                .Where(c => string.Equals(c.Category, category, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (relevantChanges.Count == 0) continue;

            double monthlySavings = 0;
            double implementationCost = 0;

            foreach (var rec in recs.Where(r => string.Equals(r.Category, category, StringComparison.OrdinalIgnoreCase)))
            {
                if (!TryParseJson(rec.EstimatedImpact, out var impact)) continue;

                if (impact.TryGetProperty("monthlySavings", out var savings) && TryReadDouble(savings, out var savingsVal))
                    monthlySavings += savingsVal;

                if (impact.TryGetProperty("implementationCost", out var cost) && TryReadDouble(cost, out var costVal))
                    implementationCost += costVal;
            }

            // Scale by the proportion of changes targeting this category
            var scale = relevantChanges.Count / (double)Math.Max(recs.Count(r =>
                string.Equals(r.Category, category, StringComparison.OrdinalIgnoreCase)), 1);
            scale = Math.Min(scale, 1.0);

            costDeltas[category] = new CostDelta
            {
                EstimatedMonthlySavings = Math.Round(monthlySavings * scale, 2),
                EstimatedImplementationCost = Math.Round(implementationCost * scale, 2),
                NetAnnualImpact = Math.Round((monthlySavings * scale * 12) - (implementationCost * scale), 2)
            };
        }

        return costDeltas;
    }

    private static Dictionary<string, RiskDelta> BuildRiskDeltas(
        List<HypotheticalChange> changes,
        Dictionary<string, double> scoreDeltas)
    {
        var riskDeltas = new Dictionary<string, RiskDelta>();

        foreach (var category in Categories)
        {
            var categoryChanges = changes
                .Where(c => string.Equals(c.Category, category, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (categoryChanges.Count == 0) continue;

            scoreDeltas.TryGetValue(category, out var delta);

            var riskLevel = delta switch
            {
                > 5 => "reduced",
                > 0 => "slightly_reduced",
                0 => "unchanged",
                > -5 => "slightly_increased",
                _ => "increased"
            };

            riskDeltas[category] = new RiskDelta
            {
                RiskLevel = riskLevel,
                ScoreDelta = delta,
                ChangeCount = categoryChanges.Count,
                MitigationNeeded = delta < -3
            };
        }

        return riskDeltas;
    }

    private static double CalculateConfidence(List<HypotheticalChange> changes, int categoryCount)
    {
        // Higher confidence when we have more baseline data and fewer speculative changes
        var baseConfidence = categoryCount >= 4 ? 0.75 : 0.6;
        var changePenalty = Math.Min(changes.Count * 0.03, 0.25);
        var impactCoverage = changes.Count(c => c.EstimatedImpact.HasValue) / (double)Math.Max(changes.Count, 1);

        return Math.Round(Math.Clamp(baseConfidence - changePenalty + (impactCoverage * 0.15), 0.3, 0.95), 2);
    }

    private static bool TryParseJson(string? json, out JsonElement element)
    {
        element = default;
        if (string.IsNullOrWhiteSpace(json)) return false;
        try
        {
            using var doc = JsonDocument.Parse(json);
            element = doc.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryReadDouble(JsonElement element, out double value)
    {
        value = default;

        if (element.ValueKind == JsonValueKind.Number)
        {
            return element.TryGetDouble(out value);
        }

        if (element.ValueKind == JsonValueKind.String)
        {
            var raw = element.GetString();
            if (string.IsNullOrWhiteSpace(raw))
            {
                return false;
            }

            return double.TryParse(raw, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out value);
        }

        return false;
    }
}

public class HypotheticalChange
{
    public required string ChangeType { get; set; }
    public required string Category { get; set; }
    public required string Description { get; set; }
    public double? EstimatedImpact { get; set; }
}

public class SimulationResult
{
    public Dictionary<string, double> CurrentScores { get; set; } = [];
    public Dictionary<string, double> ProjectedScores { get; set; } = [];
    public Dictionary<string, double> Deltas { get; set; } = [];
    public Dictionary<string, CostDelta> CostDeltas { get; set; } = [];
    public Dictionary<string, RiskDelta> RiskDeltas { get; set; } = [];
    public double Confidence { get; set; }
    public string Reasoning { get; set; } = "";
}

public class CostDelta
{
    public double EstimatedMonthlySavings { get; set; }
    public double EstimatedImplementationCost { get; set; }
    public double NetAnnualImpact { get; set; }
}

public class RiskDelta
{
    public string RiskLevel { get; set; } = "unchanged";
    public double ScoreDelta { get; set; }
    public int ChangeCount { get; set; }
    public bool MitigationNeeded { get; set; }
}
