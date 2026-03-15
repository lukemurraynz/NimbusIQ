using Atlas.AgentOrchestrator.Integrations.Azure;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace Atlas.AgentOrchestrator.Agents;

/// <summary>
/// Sustainability Agent - Evaluates environmental impact and carbon efficiency.
/// Uses Azure AI Foundry (GPT-4) to reason about carbon trade-offs, region migration
/// opportunities, and the most impactful emission-reduction actions.
/// </summary>
public class SustainabilityAgent
{
    private readonly ILogger<SustainabilityAgent> _logger;
    private readonly IAzureAIFoundryClient? _foundryClient;

    public SustainabilityAgent(
        ILogger<SustainabilityAgent> logger,
        IAzureAIFoundryClient? foundryClient = null)
    {
        _logger = logger;
        _foundryClient = foundryClient;
    }

    /// <summary>
    /// Analyze sustainability based on service knowledge graph and carbon data.
    /// When <see cref="AzureAIFoundryClient"/> is configured, appends a GPT-4 generated
    /// narrative that reasons about region migration trade-offs and carbon reduction ROI.
    /// </summary>
    public async Task<AgentAnalysisResult> AnalyzeSustainabilityAsync(
        ServiceGraphContext graphContext,
        SustainabilityContext sustainabilityContext,
        CancellationToken cancellationToken = default)
    {
        var activity = Activity.Current;
        activity?.SetTag("agent.type", "sustainability");
        activity?.SetTag("service.group.id", graphContext.ServiceGroupId);

        var findings = new List<Finding>();
        var recommendations = new List<Recommendation>();
        var evidence = new List<string>();

        // Analyze carbon-aware regions
        var regionScore = AnalyzeCarbonAwareRegions(graphContext, sustainabilityContext, findings, recommendations, evidence);

        // Analyze resource efficiency
        var efficiencyScore = AnalyzeResourceEfficiency(graphContext, sustainabilityContext, findings, recommendations, evidence);

        // Analyze idle resource waste
        var idleScore = AnalyzeIdleResources(sustainabilityContext, findings, recommendations, evidence);

        // Analyze renewable energy usage
        var renewableScore = AnalyzeRenewableEnergy(graphContext, findings, recommendations, evidence);

        // Calculate overall sustainability score (0-100)
        var overallScore = (regionScore + efficiencyScore + idleScore + renewableScore) / 4.0;

        // Calculate confidence based on data availability
        var confidence = CalculateConfidence(sustainabilityContext);

        // Record carbon emissions data in evidence so downstream consumers (e.g. AnalysisRunProcessor) can persist it.
        // Use the real-data flag as the primary signal instead of requiring emissions > 0,
        // because some subscriptions can legitimately have 0 in a measured period.
        if (sustainabilityContext.HasCarbonIntensityData || sustainabilityContext.MonthlyCarbonKg > 0)
        {
            evidence.Add(FormattableString.Invariant($"carbon_monthly_kg:{sustainabilityContext.MonthlyCarbonKg:F4}"));
            var hasRealCarbonData = sustainabilityContext.HasCarbonIntensityData ? "true" : "false";
            evidence.Add($"carbon_has_real_data:{hasRealCarbonData}");
            // Emit per-region breakdown when available from the real Carbon API (format: "carbon_region_kg:{region}:{kg}")
            foreach (var (region, regionKg) in sustainabilityContext.RegionEmissions)
            {
                evidence.Add(FormattableString.Invariant($"carbon_region_kg:{region}:{regionKg:F4}"));
            }
        }
        else
        {
            evidence.Add("carbon_monthly_kg:0");
            evidence.Add("carbon_has_real_data:false");
        }

        _logger.LogInformation(
            "Sustainability analysis complete for service group {ServiceGroupId}: Score={Score:F2}, Confidence={Confidence:F2}, Carbon={CarbonKg:F2}kg",
            graphContext.ServiceGroupId,
            overallScore,
            confidence,
            sustainabilityContext.MonthlyCarbonKg);

        var result = new AgentAnalysisResult
        {
            Score = Math.Round(overallScore, 2),
            Confidence = Math.Round(confidence, 2),
            Findings = findings,
            Recommendations = recommendations,
            EvidenceReferences = evidence
        };

        // Enrich with AI-generated sustainability narrative when Foundry is available
        if (_foundryClient != null)
        {
            result.AINarrativeSummary = await GenerateAINarrativeAsync(
                result, graphContext, sustainabilityContext, cancellationToken);
        }

        return result;
    }

    private async Task<string?> GenerateAINarrativeAsync(
        AgentAnalysisResult result,
        ServiceGraphContext graphContext,
        SustainabilityContext sustainabilityContext,
        CancellationToken cancellationToken)
    {
        try
        {
            var criticalFindings = result.Findings
                .Where(f => f.Severity is "critical" or "high")
                .Select(f => $"- {f.Category}: {f.Description}")
                .Take(5)
                .ToList();

            var highCarbonRegions = graphContext.Nodes
                .GroupBy(n => n.Region ?? "unknown")
                .OrderByDescending(g => g.Count())
                .Take(3)
                .Select(g => $"{g.Key} ({g.Count()} services)")
                .ToList();

            var prompt = $"""
                You are an Azure sustainability advisor specialising in cloud carbon reduction.
                Provide a concise executive narrative (3-4 sentences) for a sustainability
                assessment with score {result.Score:F0}/100 and monthly carbon footprint
                of {sustainabilityContext.MonthlyCarbonKg:F1} kg CO₂e.

                Top regions by resource count:
                {(highCarbonRegions.Any() ? string.Join("\n", highCarbonRegions) : "Region data unavailable")}

                Key sustainability findings:
                {(criticalFindings.Any() ? string.Join("\n", criticalFindings) : "No critical sustainability findings")}

                Idle time rate: {sustainabilityContext.IdleTimePercent:F1}%

                Focus on the carbon impact of idle resources, region selection opportunities,
                and the single most impactful carbon-reduction action.
                """;

            return await _foundryClient!.SendPromptAsync(prompt, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "AI narrative generation failed for sustainability analysis of service group {ServiceGroupId}",
                graphContext.ServiceGroupId);
            return $"AI narrative unavailable (score: {result.Score:F0}/100, carbon: {sustainabilityContext.MonthlyCarbonKg:F1} kg CO\u2082e/month). Review sustainability findings for details.";
        }
    }

    private double AnalyzeCarbonAwareRegions(
        ServiceGraphContext graphContext,
        SustainabilityContext sustainabilityContext,
        List<Finding> findings,
        List<Recommendation> recommendations,
        List<string> evidence)
    {
        var score = 100.0;

        // Get regions and their carbon intensity
        var regionUsage = graphContext.Nodes
            .GroupBy(n => n.Metadata?.GetValueOrDefault("location") ?? "unknown")
            .Select(g => new
            {
                Region = g.Key,
                NodeCount = g.Count(),
                CarbonIntensity = GetCarbonIntensity(g.Key)
            })
            .OrderByDescending(r => r.NodeCount)
            .ToList();

        evidence.Add($"regions_used:{regionUsage.Count}");

        // Check if resources are in high-carbon regions
        var highCarbonRegions = regionUsage
            .Where(r => r.CarbonIntensity > 0.5) // gCO2/kWh > 500
            .ToList();

        if (highCarbonRegions.Any())
        {
            var highCarbonNodePercent = (highCarbonRegions.Sum(r => r.NodeCount) / (double)graphContext.Nodes.Count) * 100;

            score -= 30;
            findings.Add(new Finding
            {
                Severity = "high",
                Category = "carbon_regions",
                Description = $"{highCarbonNodePercent:F1}% of resources in high-carbon intensity regions",
                Impact = "Higher carbon emissions from less renewable energy sources"
            });

            recommendations.Add(new Recommendation
            {
                Priority = "medium",
                Category = "sustainability",
                Title = "Migrate to low-carbon regions",
                Description = $"Consider migrating workloads to regions with lower carbon intensity (e.g., West Europe, UK South, North Europe)",
                EstimatedEffort = "high"
            });

            evidence.Add($"high_carbon_regions:{string.Join(",", highCarbonRegions.Select(r => r.Region))}");
        }

        // Check for use of sustainable regions
        var lowCarbonRegions = new[] { "westeurope", "uksouth", "northeurope", "swedencentral", "norwayeast" };
        var usesLowCarbon = regionUsage.Any(r =>
            lowCarbonRegions.Any(lc => r.Region.Contains(lc, StringComparison.OrdinalIgnoreCase)));

        if (usesLowCarbon)
        {
            evidence.Add("uses_low_carbon_regions:true");
        }
        else
        {
            score -= 20;
            findings.Add(new Finding
            {
                Severity = "medium",
                Category = "carbon_regions",
                Description = "No resources deployed to low-carbon intensity regions",
                Impact = "Missing opportunity to reduce carbon footprint"
            });
        }

        return Math.Max(0, score);
    }

    private double AnalyzeResourceEfficiency(
        ServiceGraphContext graphContext,
        SustainabilityContext sustainabilityContext,
        List<Finding> findings,
        List<Recommendation> recommendations,
        List<string> evidence)
    {
        var score = 100.0;

        // Check CPU efficiency
        var avgCpuUtilization = sustainabilityContext.AverageCpuUtilizationPercent;
        evidence.Add($"avg_cpu_utilization:{avgCpuUtilization:F1}%");

        if (avgCpuUtilization < 20)
        {
            score -= 30;
            findings.Add(new Finding
            {
                Severity = "high",
                Category = "resource_efficiency",
                Description = $"Very low average CPU utilization ({avgCpuUtilization:F1}%)",
                Impact = "Wasting compute resources and associated carbon emissions"
            });

            recommendations.Add(new Recommendation
            {
                Priority = "high",
                Category = "sustainability",
                Title = "Rightsize compute resources",
                Description = "Reduce instance sizes or consolidate workloads to improve efficiency",
                EstimatedEffort = "medium"
            });
        }
        else if (avgCpuUtilization < 40)
        {
            score -= 15;
            findings.Add(new Finding
            {
                Severity = "medium",
                Category = "resource_efficiency",
                Description = $"Low average CPU utilization ({avgCpuUtilization:F1}%)",
                Impact = "Moderate resource waste"
            });
        }

        // Check for serverless adoption
        var serverlessResources = graphContext.Nodes
            .Where(n => IsServerlessResourceType(n.NodeType))
            .ToList();

        if (!serverlessResources.Any() && graphContext.Nodes.Count > 0)
        {
            score -= 20;
            findings.Add(new Finding
            {
                Severity = "medium",
                Category = "resource_efficiency",
                Description = "No serverless resources detected",
                Impact = "Missing carbon-efficient compute option that scales to zero"
            });

            recommendations.Add(new Recommendation
            {
                Priority = "low",
                Category = "sustainability",
                Title = "Consider serverless for variable workloads",
                Description = "Use Azure Functions or Container Apps for sporadic workloads to reduce idle resource consumption",
                EstimatedEffort = "high"
            });
        }
        else if (serverlessResources.Any())
        {
            var serverlessPercent = (serverlessResources.Count / (double)graphContext.Nodes.Count) * 100;
            evidence.Add($"serverless_adoption_percent:{serverlessPercent:F1}%");
        }

        return Math.Max(0, score);
    }

    private double AnalyzeIdleResources(
        SustainabilityContext sustainabilityContext,
        List<Finding> findings,
        List<Recommendation> recommendations,
        List<string> evidence)
    {
        var score = 100.0;

        // Check for idle time
        var idleTimePercent = sustainabilityContext.IdleTimePercent;
        evidence.Add($"idle_time_percent:{idleTimePercent:F1}%");

        if (idleTimePercent > 40)
        {
            score -= 30;
            findings.Add(new Finding
            {
                Severity = "high",
                Category = "idle_waste",
                Description = $"Resources idle {idleTimePercent:F1}% of the time",
                Impact = "Significant waste of energy and carbon emissions"
            });

            recommendations.Add(new Recommendation
            {
                Priority = "high",
                Category = "sustainability",
                Title = "Implement auto-shutdown policies",
                Description = "Configure automatic shutdown during off-hours for non-production workloads",
                EstimatedEffort = "low"
            });
        }
        else if (idleTimePercent > 20)
        {
            score -= 15;
            findings.Add(new Finding
            {
                Severity = "medium",
                Category = "idle_waste",
                Description = $"Resources idle {idleTimePercent:F1}% of the time",
                Impact = "Moderate energy waste"
            });
        }

        // Check for scheduled scaling
        if (!sustainabilityContext.HasScheduledScaling && idleTimePercent > 10)
        {
            score -= 15;
            findings.Add(new Finding
            {
                Severity = "medium",
                Category = "idle_waste",
                Description = "No scheduled scaling detected",
                Impact = "Cannot reduce carbon footprint during predictable low-usage periods"
            });

            recommendations.Add(new Recommendation
            {
                Priority = "medium",
                Category = "sustainability",
                Title = "Configure scheduled auto-scaling",
                Description = "Scale down resources during off-peak hours to reduce energy consumption",
                EstimatedEffort = "low"
            });
        }
        else if (sustainabilityContext.HasScheduledScaling)
        {
            evidence.Add("has_scheduled_scaling:true");
        }

        return Math.Max(0, score);
    }

    private double AnalyzeRenewableEnergy(
        ServiceGraphContext graphContext,
        List<Finding> findings,
        List<Recommendation> recommendations,
        List<string> evidence)
    {
        var score = 100.0;

        // Azure regions with high renewable energy
        var renewableRegions = new[]
        {
            "swedencentral",
            "norwayeast",
            "westeurope",
            "uksouth",
            "northeurope"
        };

        var regions = graphContext.Nodes
            .Select(n => n.Metadata?.GetValueOrDefault("location"))
            .OfType<string>()
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .ToList();

        var renewableDeployments = regions
            .Count(r => renewableRegions.Any(rr => r.Contains(rr, StringComparison.OrdinalIgnoreCase)));

        if (regions.Any())
        {
            var renewablePercent = (renewableDeployments / (double)regions.Count) * 100;
            evidence.Add($"renewable_region_percent:{renewablePercent:F1}%");

            if (renewablePercent < 30)
            {
                score -= 25;
                findings.Add(new Finding
                {
                    Severity = "medium",
                    Category = "renewable_energy",
                    Description = $"Only {renewablePercent:F1}% of deployments in high-renewable regions",
                    Impact = "Higher reliance on fossil fuel energy sources"
                });

                recommendations.Add(new Recommendation
                {
                    Priority = "low",
                    Category = "sustainability",
                    Title = "Prefer regions with high renewable energy",
                    Description = "Deploy new workloads to Sweden Central, Norway East, or UK South for cleaner energy",
                    EstimatedEffort = "low"
                });
            }
        }

        // Check for carbon-aware workload scheduling
        if (!graphContext.Nodes.Any(n =>
            n.Metadata?.ContainsKey("carbonAwareScheduling") == true))
        {
            score -= 15;
            findings.Add(new Finding
            {
                Severity = "low",
                Category = "renewable_energy",
                Description = "No carbon-aware workload scheduling detected",
                Impact = "Missing opportunity to shift compute to low-carbon time windows"
            });

            recommendations.Add(new Recommendation
            {
                Priority = "low",
                Category = "sustainability",
                Title = "Implement carbon-aware scheduling",
                Description = "Use Azure Carbon Optimization APIs to schedule batch workloads during low-carbon periods",
                EstimatedEffort = "medium"
            });
        }
        else
        {
            evidence.Add("has_carbon_aware_scheduling:true");
        }

        return Math.Max(0, score);
    }

    private double CalculateConfidence(SustainabilityContext context)
    {
        var confidence = 1.0;

        // Reduce confidence if carbon data is unavailable
        if (context.MonthlyCarbonKg == 0)
        {
            confidence *= 0.5;
        }

        if (!context.HasUtilizationMetrics)
        {
            confidence *= 0.7;
        }

        if (!context.HasCarbonIntensityData)
        {
            confidence *= 0.6;
        }

        return confidence;
    }

    private double GetCarbonIntensity(string region)
    {
        // Carbon intensity estimates (gCO2/kWh) for Azure regions
        // Source: Azure sustainability documentation and grid carbon intensity data
        var carbonIntensityMap = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            { "swedencentral", 0.01 },
            { "norwayeast", 0.02 },
            { "northeurope", 0.15 },
            { "westeurope", 0.20 },
            { "uksouth", 0.25 },
            { "francecentral", 0.05 },
            { "germanywestcentral", 0.35 },
            { "eastus", 0.45 },
            { "eastus2", 0.45 },
            { "westus", 0.30 },
            { "westus2", 0.25 },
            { "centralus", 0.55 },
            { "southcentralus", 0.50 },
            { "australiaeast", 0.80 },
            { "australiasoutheast", 0.75 },
            { "southeastasia", 0.70 },
            { "eastasia", 0.65 },
            { "japaneast", 0.50 },
            { "japanwest", 0.48 }
        };

        // Try to match region
        foreach (var kvp in carbonIntensityMap)
        {
            if (region.Contains(kvp.Key, StringComparison.OrdinalIgnoreCase))
            {
                return kvp.Value;
            }
        }

        // Default to medium intensity if unknown
        return 0.40;
    }

    private bool IsServerlessResourceType(string nodeType)
    {
        var serverlessTypes = new[]
        {
            "FunctionApp",
            "LogicApp",
            "ContainerApp"
        };

        return serverlessTypes.Any(t => nodeType.Contains(t, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>
/// Sustainability context for agent analysis
/// </summary>
public class SustainabilityContext
{
    public double MonthlyCarbonKg { get; set; }
    public double AverageCpuUtilizationPercent { get; set; }
    public double IdleTimePercent { get; set; }
    public bool HasScheduledScaling { get; set; }
    public bool HasUtilizationMetrics { get; set; }
    public bool HasCarbonIntensityData { get; set; }
    /// <summary>Per-region carbon emissions in kg CO₂e/month from Azure Carbon Optimization API.</summary>
    public IReadOnlyDictionary<string, double> RegionEmissions { get; set; } = new Dictionary<string, double>();
}
