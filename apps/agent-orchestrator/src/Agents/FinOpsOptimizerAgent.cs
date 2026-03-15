using Atlas.AgentOrchestrator.Integrations.Azure;
using Atlas.AgentOrchestrator.Integrations.MCP;
using Atlas.AgentOrchestrator.Integrations.Prompts;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Text.Json;

namespace Atlas.AgentOrchestrator.Agents;

/// <summary>
/// T4.3: FinOps Optimizer Agent - Advanced cost intelligence with AI-powered analysis
/// Real-time cost anomaly detection, predictive modeling, automated optimization recommendations
/// Integrated with Microsoft Agent Framework for context-aware cost optimization.
/// Calls the official Azure MCP <c>cost_query</c> tool (via <see cref="AzureMcpToolClient"/>)
/// for live billing data before falling back to the context supplied by the caller.
/// </summary>
public class FinOpsOptimizerAgent
{
    private readonly ILogger<FinOpsOptimizerAgent> _logger;
    private readonly IAzureAIFoundryClient? _foundryClient;
    private readonly OrphanDetectionService? _orphanDetectionService;
    private readonly AzureMcpToolClient? _mcpToolClient;
    private readonly IPromptProvider? _promptProvider;
    // Configurable lookback window — defaults to 90 days but can be overridden via
    // AzureMcpOptions.CostLookbackDays when the MCP client is supplied.
    private readonly int _costLookbackDays;

    public FinOpsOptimizerAgent(
        ILogger<FinOpsOptimizerAgent> logger,
        IAzureAIFoundryClient? foundryClient = null,
        OrphanDetectionService? orphanDetectionService = null,
        AzureMcpToolClient? mcpToolClient = null,
        int costLookbackDays = 90,
        IPromptProvider? promptProvider = null)
    {
        _logger = logger;
        _foundryClient = foundryClient;
        _orphanDetectionService = orphanDetectionService;
        _mcpToolClient = mcpToolClient;
        _costLookbackDays = costLookbackDays > 0 ? costLookbackDays : 90;
        _promptProvider = promptProvider;
    }

    /// <summary>
    /// Analyze cost optimization opportunities.
    /// Enriches <paramref name="context"/> with live cost data from the Azure MCP
    /// <c>cost_query</c> tool when <see cref="AzureMcpToolClient"/> is available.
    /// </summary>
    public async Task<FinOpsAnalysisResult> AnalyzeAsync(
        FinOpsContext context,
        CancellationToken cancellationToken = default)
    {
        var activity = Activity.Current;
        activity?.SetTag("finops.serviceGroupId", context.ServiceGroupId);
        activity?.SetTag("finops.currentMonthlyCost", context.CurrentMonthlyCost);

        try
        {
            // Enrich context with live Azure cost data via the official MCP cost_query tool
            await EnrichContextWithMcpCostDataAsync(context, cancellationToken);

            var anomalies = DetectCostAnomalies(context);
            var rightsizingOpportunities = AnalyzeRightsizing(context);
            var commitmentAnalysis = AnalyzeCommitmentOpportunities(context);
            var utilizationAnalysis = AnalyzeUtilization(context);
            var wasteDetection = DetectWaste(context);

            // Enhanced orphan detection using resource-specific KQL patterns
            OrphanDetectionResult? comprehensiveOrphanDetection = null;
            if (_orphanDetectionService != null && context.Scope != null)
            {
                try
                {
                    comprehensiveOrphanDetection = await _orphanDetectionService.DetectAllOrphansAsync(
                        context.Scope,
                        cancellationToken);

                    _logger.LogInformation(
                        "Comprehensive orphan detection: {Count} resources, ${Cost:F2}/month waste",
                        comprehensiveOrphanDetection.TotalOrphans,
                        comprehensiveOrphanDetection.TotalEstimatedMonthlyCost);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Comprehensive orphan detection failed, using tag-based fallback");
                }
            }

            var costForecast = await PredictFutureCostsAsync(context, cancellationToken);
            var aiRecommendations = await GenerateAIRecommendationsAsync(
                context, anomalies, rightsizingOpportunities, cancellationToken);

            var result = new FinOpsAnalysisResult
            {
                ServiceGroupId = context.ServiceGroupId,
                AnalyzedAt = DateTime.UtcNow,
                CurrentMonthlyCost = context.CurrentMonthlyCost,
                PotentialMonthlySavings = CalculateTotalSavings(
                    rightsizingOpportunities,
                    commitmentAnalysis,
                    wasteDetection,
                    comprehensiveOrphanDetection),
                Anomalies = anomalies,
                RightsizingOpportunities = rightsizingOpportunities,
                CommitmentAnalysis = commitmentAnalysis,
                UtilizationAnalysis = utilizationAnalysis,
                WasteDetection = wasteDetection,
                ComprehensiveOrphanDetection = comprehensiveOrphanDetection,
                CostForecast = costForecast,
                AIRecommendations = aiRecommendations
            };

            var savingsPercent = context.CurrentMonthlyCost > 0
                ? (result.PotentialMonthlySavings / context.CurrentMonthlyCost) * 100
                : 0;

            _logger.LogInformation(
                "FinOps analysis complete for {ServiceGroupId}: Current=${CurrentCost:F2}, Savings=${PotentialSavings:F2} ({SavingsPercent:F1}%), {Count} recommendations",
                context.ServiceGroupId,
                context.CurrentMonthlyCost,
                result.PotentialMonthlySavings,
                savingsPercent,
                aiRecommendations.Count);

            activity?.SetTag("finops.potentialSavings", result.PotentialMonthlySavings);
            activity?.SetTag("finops.recommendationCount", aiRecommendations.Count);

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to analyze FinOps for service group {ServiceGroupId}", context.ServiceGroupId);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
    }

    /// <summary>
    /// Detect cost anomalies using statistical analysis
    /// </summary>
    private List<CostAnomaly> DetectCostAnomalies(FinOpsContext context)
    {
        var anomalies = new List<CostAnomaly>();

        if (context.HistoricalCosts.Count < 7)
        {
            _logger.LogInformation("Insufficient historical data for anomaly detection (need 7+ days)");
            return anomalies;
        }

        // Calculate baseline (average of last 30 days excluding last 7)
        var baselineWindow = context.HistoricalCosts
            .OrderByDescending(c => c.Date)
            .Skip(7)
            .Take(30)
            .ToList();

        var baseline = baselineWindow.Average(c => c.Amount);

        var stdDev = CalculateStandardDeviation(
            baselineWindow.Select(c => c.Amount).ToList());

        // Guard against zero/near-zero standard deviation
        if (stdDev <= 0.01)
        {
            _logger.LogInformation("Standard deviation too low for meaningful anomaly detection (stdDev={StdDev})", stdDev);
            return anomalies;
        }

        // Check recent costs for anomalies (3+ standard deviations)
        var recentCosts = context.HistoricalCosts
            .OrderByDescending(c => c.Date)
            .Take(7)
            .ToList();

        foreach (var cost in recentCosts)
        {
            var deviation = Math.Abs(cost.Amount - baseline);
            var zScore = deviation / (decimal)stdDev;

            if (zScore > 3) // Significant anomaly
            {
                anomalies.Add(new CostAnomaly
                {
                    Date = cost.Date,
                    Amount = cost.Amount,
                    Baseline = baseline,
                    Deviation = deviation,
                    ZScore = (double)zScore,
                    Severity = zScore > 5 ? "Critical" : "High",
                    Description = $"Cost spike detected: ${cost.Amount:F2} vs baseline ${baseline:F2} ({(deviation / baseline * 100):F1}% increase)"
                });
            }
        }

        return anomalies;
    }

    /// <summary>
    /// Analyze SKU rightsizing opportunities
    /// </summary>
    private List<RightsizingOpportunity> AnalyzeRightsizing(FinOpsContext context)
    {
        var opportunities = new List<RightsizingOpportunity>();

        foreach (var resource in context.Resources)
        {
            // Check if resource is overprovisioned based on utilization
            if (resource.CpuUtilization < 20 && resource.MemoryUtilization < 30)
            {
                var currentCost = resource.MonthlyCost;
                var potentialSavings = currentCost * 0.5m; // Estimate 50% savings

                opportunities.Add(new RightsizingOpportunity
                {
                    ResourceId = resource.ResourceId,
                    ResourceType = resource.ResourceType,
                    CurrentSku = resource.Sku,
                    RecommendedSku = SuggestSmallerSku(resource.Sku),
                    CurrentMonthlyCost = currentCost,
                    PotentialMonthlySavings = potentialSavings,
                    Confidence = CalculateConfidence(resource),
                    Rationale = $"Low utilization: CPU {resource.CpuUtilization:F1}%, Memory {resource.MemoryUtilization:F1}%"
                });
            }
        }

        return opportunities;
    }

    /// <summary>
    /// Analyze reserved capacity and savings plan opportunities
    /// </summary>
    private CommitmentAnalysis AnalyzeCommitmentOpportunities(FinOpsContext context)
    {
        // Identify resources eligible for reservations
        var eligibleResources = context.Resources
            .Where(r => IsEligibleForReservation(r))
            .ToList();

        // Guard: nothing eligible — return a zero-cost analysis rather than dividing by zero.
        if (eligibleResources.Count == 0)
        {
            return new CommitmentAnalysis
            {
                EligibleResourceCount = 0,
                TotalPayAsYouGoCost = 0m,
                EstimatedReservationCost = 0m,
                PotentialMonthlySavings = 0m,
                RecommendedCommitmentTerm = DetermineOptimalCommitmentTerm(context),
                BreakEvenMonths = 0,
                Confidence = 0.40m
            };
        }

        var totalPayAsYouGoCost = eligibleResources.Sum(r => r.MonthlyCost);
        var estimatedReservationCost = totalPayAsYouGoCost * 0.65m; // ~35% savings
        var potentialSavings = totalPayAsYouGoCost - estimatedReservationCost;

        return new CommitmentAnalysis
        {
            EligibleResourceCount = eligibleResources.Count,
            TotalPayAsYouGoCost = totalPayAsYouGoCost,
            EstimatedReservationCost = estimatedReservationCost,
            PotentialMonthlySavings = potentialSavings,
            RecommendedCommitmentTerm = DetermineOptimalCommitmentTerm(context),
            BreakEvenMonths = CalculateBreakEven(potentialSavings),
            Confidence = eligibleResources.Count > 5 ? 0.85m : 0.60m
        };
    }

    /// <summary>
    /// Analyze resource utilization patterns
    /// </summary>
    private UtilizationAnalysis AnalyzeUtilization(FinOpsContext context)
    {
        var totalResources = context.Resources.Count;
        var underutilized = context.Resources.Count(r => r.CpuUtilization < 20 && r.MemoryUtilization < 30);
        var wellUtilized = context.Resources.Count(r => r.CpuUtilization >= 50 && r.CpuUtilization <= 80);
        var overutilized = context.Resources.Count(r => r.CpuUtilization > 80 || r.MemoryUtilization > 85);

        return new UtilizationAnalysis
        {
            TotalResources = totalResources,
            UnderutilizedCount = underutilized,
            WellUtilizedCount = wellUtilized,
            OverutilizedCount = overutilized,
            AverageCpuUtilization = totalResources > 0
                ? context.Resources.Average(r => r.CpuUtilization) : 0m,
            AverageMemoryUtilization = totalResources > 0
                ? context.Resources.Average(r => r.MemoryUtilization) : 0m,
            UtilizationScore = CalculateUtilizationScore(wellUtilized, totalResources)
        };
    }

    /// <summary>
    /// Detect wasteful spending patterns
    /// </summary>
    private WasteDetection DetectWaste(FinOpsContext context)
    {
        var waste = new WasteDetection();

        // Unused resources (stopped VMs, unattached disks, etc.)
        waste.UnusedResources = context.Resources
            .Where(r => IsUnused(r))
            .Select(r => new WasteItem
            {
                ResourceId = r.ResourceId,
                ResourceType = r.ResourceType,
                MonthlyCost = r.MonthlyCost,
                WasteType = "unused",
                Description = "Resource not actively used"
            })
            .ToList();

        // Orphaned resources
        waste.OrphanedResources = context.Resources
            .Where(r => IsOrphaned(r))
            .Select(r => new WasteItem
            {
                ResourceId = r.ResourceId,
                ResourceType = r.ResourceType,
                MonthlyCost = r.MonthlyCost,
                WasteType = "orphaned",
                Description = "Resource no longer attached to active workload"
            })
            .ToList();

        // Development resources in production SKUs
        waste.OverprovisionedDevResources = context.Resources
            .Where(r => r.Tags.ContainsKey("environment") &&
                       r.Tags["environment"] == "dev" &&
                       r.Sku.Contains("Premium", StringComparison.OrdinalIgnoreCase))
            .Select(r => new WasteItem
            {
                ResourceId = r.ResourceId,
                ResourceType = r.ResourceType,
                MonthlyCost = r.MonthlyCost,
                WasteType = "overprovisioned_dev",
                Description = "Development resource using production-grade SKU"
            })
            .ToList();

        waste.TotalWasteCost = waste.UnusedResources.Sum(w => w.MonthlyCost) +
                               waste.OrphanedResources.Sum(w => w.MonthlyCost) +
                               waste.OverprovisionedDevResources.Sum(w => w.MonthlyCost);

        return waste;
    }

    // -------------------------------------------------------------------------
    // Azure MCP enrichment
    // -------------------------------------------------------------------------

    /// <summary>
    /// Enriches <paramref name="context"/> with live billing data fetched via the
    /// Azure MCP <c>cost_query</c> tool.  Fails gracefully — if the MCP client is
    /// unavailable or the call fails the context is returned unchanged.
    /// </summary>
    private async Task EnrichContextWithMcpCostDataAsync(
        FinOpsContext context,
        CancellationToken cancellationToken)
    {
        if (_mcpToolClient == null)
        {
            _logger.LogDebug("AzureMcpToolClient not configured — skipping MCP cost enrichment");
            return;
        }

        if (string.IsNullOrEmpty(context.SubscriptionId))
        {
            _logger.LogDebug("No SubscriptionId in FinOpsContext — skipping MCP cost enrichment");
            return;
        }

        try
        {
            _logger.LogInformation(
                "Calling Azure MCP cost_query for subscription {SubscriptionId}",
                context.SubscriptionId);

            var endDate = DateTime.UtcNow;
            var startDate = endDate.AddDays(-_costLookbackDays);

            var mcpResult = await _mcpToolClient.QueryCostAsync(
                context.SubscriptionId,
                startDate,
                endDate,
                cancellationToken,
                context.McpContext);

            if (mcpResult.TryGetValue("status", out var status) &&
                status?.ToString() == "success" &&
                mcpResult.TryGetValue("text", out var rawText) &&
                rawText?.ToString() is { Length: > 0 } text)
            {
                // The Azure MCP server returns JSON; attempt to parse total cost
                TryUpdateCurrentCostFromMcpResponse(context, text);

                _logger.LogInformation(
                    "MCP cost_query enrichment applied for subscription {SubscriptionId}",
                    context.SubscriptionId);

                context.McpCostQueryResult = text;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Azure MCP cost_query failed for subscription {SubscriptionId}; continuing with provided context",
                context.SubscriptionId);
        }
    }

    private void TryUpdateCurrentCostFromMcpResponse(FinOpsContext context, string jsonText)
    {
        try
        {
            using var doc = JsonDocument.Parse(jsonText);
            var root = doc.RootElement;

            // Azure Cost Management MCP response typically includes a "totalCost" field
            if (root.TryGetProperty("totalCost", out var totalCostEl) &&
                totalCostEl.TryGetDecimal(out var totalCost) &&
                totalCost > 0)
            {
                context.CurrentMonthlyCost = totalCost;
            }
        }
        catch (JsonException)
        {
            // Non-JSON response or different schema; ignore
        }
    }

    // -------------------------------------------------------------------------
    // Cost prediction
    // -------------------------------------------------------------------------

    /// <summary>
    /// Predict future costs using trend analysis
    /// </summary>
    private async Task<CostForecast> PredictFutureCostsAsync(
        FinOpsContext context,
        CancellationToken cancellationToken)
    {
        if (context.HistoricalCosts.Count < 30)
        {
            return new CostForecast
            {
                NextMonthEstimate = context.CurrentMonthlyCost,
                Confidence = 0.5m,
                Methodology = "Insufficient data for trend analysis"
            };
        }

        // Simple linear regression for cost trend
        var trend = CalculateCostTrend(context.HistoricalCosts);

        var nextMonthEstimate = context.CurrentMonthlyCost + trend;
        var nextQuarterEstimate = nextMonthEstimate * 3;

        return new CostForecast
        {
            NextMonthEstimate = Math.Max(0, nextMonthEstimate),
            NextQuarterEstimate = Math.Max(0, nextQuarterEstimate),
            MonthlyTrend = trend,
            Confidence = 0.75m,
            Methodology = "Linear regression on 30-day historical data"
        };
    }

    /// <summary>
    /// Generate AI-powered recommendations using Agent Framework
    /// </summary>
    private async Task<List<AIRecommendation>> GenerateAIRecommendationsAsync(
        FinOpsContext context,
        List<CostAnomaly> anomalies,
        List<RightsizingOpportunity> rightsizing,
        CancellationToken cancellationToken)
    {
        var recommendations = new List<AIRecommendation>();

        // Generate rule-based recommendations first
        recommendations.AddRange(GenerateRuleBasedRecommendations(context, anomalies, rightsizing));

        // If AI Foundry is available, enhance with GPT-4 insights
        if (_foundryClient != null)
        {
            try
            {
                var prompt = BuildFinOpsPrompt(context, anomalies, rightsizing);
                var aiResponse = await _foundryClient.SendPromptAsync(prompt, cancellationToken);

                recommendations.Add(new AIRecommendation
                {
                    Title = "AI-Generated Cost Optimization Strategy",
                    Description = aiResponse,
                    Category = "Strategic",
                    PotentialSavings = 0, // GPT-4 response may include estimates
                    Confidence = 0.70m,
                    Source = "Microsoft Agent Framework (Azure AI Foundry)"
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to generate AI recommendations, using rule-based only");
            }
        }

        return recommendations;
    }

    private List<AIRecommendation> GenerateRuleBasedRecommendations(
        FinOpsContext context,
        List<CostAnomaly> anomalies,
        List<RightsizingOpportunity> rightsizing)
    {
        var recommendations = new List<AIRecommendation>();

        // High-priority: Cost anomalies
        if (anomalies.Any(a => a.Severity == "Critical"))
        {
            var criticalAnomaly = anomalies.First(a => a.Severity == "Critical");
            recommendations.Add(new AIRecommendation
            {
                Title = "Investigate Critical Cost Spike",
                Description = $"Cost increased by ${criticalAnomaly.Deviation:F2} on {criticalAnomaly.Date:yyyy-MM-dd}. Immediate investigation recommended.",
                Category = "Anomaly",
                PotentialSavings = criticalAnomaly.Deviation * 0.8m, // Assume 80% is preventable
                Confidence = 0.90m,
                Source = "Statistical Anomaly Detection"
            });
        }

        // Medium-priority: Rightsizing
        var topRightsizing = rightsizing
            .OrderByDescending(r => r.PotentialMonthlySavings)
            .Take(3);

        foreach (var opportunity in topRightsizing)
        {
            recommendations.Add(new AIRecommendation
            {
                Title = $"Rightsize {opportunity.ResourceType}",
                Description = $"Change from {opportunity.CurrentSku} to {opportunity.RecommendedSku}. {opportunity.Rationale}",
                Category = "Rightsizing",
                PotentialSavings = opportunity.PotentialMonthlySavings,
                Confidence = opportunity.Confidence,
                Source = "Utilization Analysis"
            });
        }

        return recommendations;
    }

    private string BuildFinOpsPrompt(
        FinOpsContext context,
        List<CostAnomaly> anomalies,
        List<RightsizingOpportunity> rightsizing)
    {
        var topCostDrivers = string.Join("\n", context.Resources
            .OrderByDescending(r => r.MonthlyCost)
            .Take(3)
            .Select(r => $"- {r.ResourceType}: ${r.MonthlyCost:F2}/month"));

        if (_promptProvider is null)
        {
            throw new InvalidOperationException("Prompt provider is required for FinOps optimization prompt rendering.");
        }

        return _promptProvider.Render(
            "finops-optimizer",
            new Dictionary<string, string>
            {
                ["ServiceGroupId"] = context.ServiceGroupId.ToString(),
                ["CurrentMonthlyCost"] = context.CurrentMonthlyCost.ToString("F2"),
                ["ResourceCount"] = context.Resources.Count.ToString(),
                ["AnomalyCount"] = anomalies.Count.ToString(),
                ["RightsizingCount"] = rightsizing.Count.ToString(),
                ["TopCostDrivers"] = topCostDrivers
            });
    }

    // Helper methods
    private decimal CalculateTotalSavings(
        List<RightsizingOpportunity> rightsizing,
        CommitmentAnalysis commitments,
        WasteDetection waste,
        OrphanDetectionResult? orphans = null)
    {
        return rightsizing.Sum(r => r.PotentialMonthlySavings)
            + commitments.PotentialMonthlySavings
            + waste.TotalWasteCost
            + (orphans?.TotalEstimatedMonthlyCost ?? 0m);
    }

    private double CalculateStandardDeviation(List<decimal> values)
    {
        var avg = values.Average();
        var sumOfSquares = values.Sum(v => Math.Pow((double)(v - avg), 2));
        return Math.Sqrt(sumOfSquares / values.Count);
    }

    private string SuggestSmallerSku(string currentSku)
    {
        // Simplified SKU downgrade logic
        if (currentSku.Contains("Standard_D8", StringComparison.OrdinalIgnoreCase))
            return "Standard_D4s_v5";
        if (currentSku.Contains("Standard_D4", StringComparison.OrdinalIgnoreCase))
            return "Standard_D2s_v5";
        if (currentSku.Contains("Premium", StringComparison.OrdinalIgnoreCase))
            return currentSku.Replace("Premium", "Standard");

        return "Smaller SKU (analyze specific type)";
    }

    private decimal CalculateConfidence(FinOpsResourceInfo resource)
    {
        // Higher confidence if utilization is consistently low
        if (resource.CpuUtilization < 10 && resource.MemoryUtilization < 20)
            return 0.95m;
        if (resource.CpuUtilization < 20 && resource.MemoryUtilization < 30)
            return 0.80m;

        return 0.65m;
    }

    private bool IsEligibleForReservation(FinOpsResourceInfo resource)
    {
        // VMs, SQL databases, and certain managed services are eligible
        var eligibleTypes = new[] { "virtualMachines", "sqlDatabases", "managedClusters" };
        return eligibleTypes.Any(t => resource.ResourceType.Contains(t, StringComparison.OrdinalIgnoreCase));
    }

    private string DetermineOptimalCommitmentTerm(FinOpsContext context)
    {
        // If workload is stable for 6+ months, recommend 3-year. Otherwise, 1-year.
        return context.HistoricalCosts.Count > 180 ? "3-year" : "1-year";
    }

    private int CalculateBreakEven(decimal monthlySavings)
    {
        // Simplified: Upfront commitment typically breaks even in 6-12 months
        return monthlySavings > 1000 ? 6 : 12;
    }

    private decimal CalculateUtilizationScore(int wellUtilized, int totalResources)
    {
        return totalResources > 0 ? (decimal)wellUtilized / totalResources * 100 : 0;
    }

    private bool IsUnused(FinOpsResourceInfo resource)
    {
        // Simple heuristic: CPU < 1% for extended period
        return resource.CpuUtilization < 1;
    }

    private bool IsOrphaned(FinOpsResourceInfo resource)
    {
        // Check if resource has owner tag and is disconnected
        return !resource.Tags.ContainsKey("owner") || !resource.Tags.ContainsKey("application");
    }

    private decimal CalculateCostTrend(List<DailyCost> historicalCosts)
    {
        // Simple linear regression
        var n = historicalCosts.Count;
        var x = Enumerable.Range(1, n).ToList();
        var y = historicalCosts.OrderBy(c => c.Date).Select(c => (double)c.Amount).ToList();

        var sumX = x.Sum();
        var sumY = y.Sum();
        var sumXY = x.Zip(y, (xi, yi) => xi * yi).Sum();
        var sumX2 = x.Sum(xi => xi * xi);

        var slope = (n * sumXY - sumX * sumY) / (n * sumX2 - sumX * sumX);

        return (decimal)slope;
    }
}

// DTOs
public class FinOpsContext
{
    public Guid ServiceGroupId { get; set; }
    public decimal CurrentMonthlyCost { get; set; }
    public List<FinOpsResourceInfo> Resources { get; set; } = new();
    public List<DailyCost> HistoricalCosts { get; set; } = new();
    /// <summary>Subscription/resource group scope for comprehensive orphan detection.</summary>
    public ServiceGroupScope? Scope { get; set; }
    /// <summary>
    /// Azure subscription identifier used to call the Azure MCP <c>cost_query</c> tool.
    /// If not set the MCP enrichment step is skipped and the caller-supplied cost is used.
    /// </summary>
    public string? SubscriptionId { get; set; }
    /// <summary>
    /// Raw JSON string returned by the Azure MCP cost_query tool, set after a successful
    /// MCP call. Available for downstream processing and audit logging.
    /// </summary>
    public string? McpCostQueryResult { get; set; }

    /// <summary>
    /// Optional audit/trace context for MCP tool calls.
    /// </summary>
    public ToolCallContext? McpContext { get; set; }
}

public class FinOpsResourceInfo
{
    public required string ResourceId { get; set; }
    public required string ResourceType { get; set; }
    public required string Sku { get; set; }
    public decimal MonthlyCost { get; set; }
    public decimal CpuUtilization { get; set; }
    public decimal MemoryUtilization { get; set; }
    public Dictionary<string, string> Tags { get; set; } = new();
}

public class DailyCost
{
    public DateTime Date { get; set; }
    public decimal Amount { get; set; }
}

public class FinOpsAnalysisResult
{
    public Guid ServiceGroupId { get; set; }
    public DateTime AnalyzedAt { get; set; }
    public decimal CurrentMonthlyCost { get; set; }
    public decimal PotentialMonthlySavings { get; set; }
    public List<CostAnomaly> Anomalies { get; set; } = new();
    public List<RightsizingOpportunity> RightsizingOpportunities { get; set; } = new();
    public CommitmentAnalysis CommitmentAnalysis { get; set; } = new() { RecommendedCommitmentTerm = "None" };
    public UtilizationAnalysis UtilizationAnalysis { get; set; } = new();
    public WasteDetection WasteDetection { get; set; } = new();
    public OrphanDetectionResult? ComprehensiveOrphanDetection { get; set; }
    public CostForecast CostForecast { get; set; } = new() { Methodology = "Linear trend analysis" };
    public List<AIRecommendation> AIRecommendations { get; set; } = new();
}

public class CostAnomaly
{
    public DateTime Date { get; set; }
    public decimal Amount { get; set; }
    public decimal Baseline { get; set; }
    public decimal Deviation { get; set; }
    public double ZScore { get; set; }
    public required string Severity { get; set; }
    public required string Description { get; set; }
}

public class RightsizingOpportunity
{
    public required string ResourceId { get; set; }
    public required string ResourceType { get; set; }
    public required string CurrentSku { get; set; }
    public required string RecommendedSku { get; set; }
    public decimal CurrentMonthlyCost { get; set; }
    public decimal PotentialMonthlySavings { get; set; }
    public decimal Confidence { get; set; }
    public required string Rationale { get; set; }
}

public class CommitmentAnalysis
{
    public int EligibleResourceCount { get; set; }
    public decimal TotalPayAsYouGoCost { get; set; }
    public decimal EstimatedReservationCost { get; set; }
    public decimal PotentialMonthlySavings { get; set; }
    public required string RecommendedCommitmentTerm { get; set; }
    public int BreakEvenMonths { get; set; }
    public decimal Confidence { get; set; }
}

public class UtilizationAnalysis
{
    public int TotalResources { get; set; }
    public int UnderutilizedCount { get; set; }
    public int WellUtilizedCount { get; set; }
    public int OverutilizedCount { get; set; }
    public decimal AverageCpuUtilization { get; set; }
    public decimal AverageMemoryUtilization { get; set; }
    public decimal UtilizationScore { get; set; }
}

public class WasteDetection
{
    public List<WasteItem> UnusedResources { get; set; } = new();
    public List<WasteItem> OrphanedResources { get; set; } = new();
    public List<WasteItem> OverprovisionedDevResources { get; set; } = new();
    public decimal TotalWasteCost { get; set; }
}

public class WasteItem
{
    public required string ResourceId { get; set; }
    public required string ResourceType { get; set; }
    public decimal MonthlyCost { get; set; }
    public required string WasteType { get; set; }
    public required string Description { get; set; }
}

public class CostForecast
{
    public decimal NextMonthEstimate { get; set; }
    public decimal NextQuarterEstimate { get; set; }
    public decimal MonthlyTrend { get; set; }
    public decimal Confidence { get; set; }
    public required string Methodology { get; set; }
}

public class AIRecommendation
{
    public required string Title { get; set; }
    public required string Description { get; set; }
    public required string Category { get; set; }
    public decimal PotentialSavings { get; set; }
    public decimal Confidence { get; set; }
    public required string Source { get; set; }
}
