using Atlas.AgentOrchestrator.Integrations.Azure;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace Atlas.AgentOrchestrator.Agents;

/// <summary>
/// Reliability Agent - Evaluates system reliability and availability
/// Consumes Service Knowledge Graph to assess resilience patterns
/// </summary>
public class ReliabilityAgent
{
    private readonly ILogger<ReliabilityAgent> _logger;
    private readonly IAzureAIFoundryClient? _foundryClient;

    public ReliabilityAgent(
        ILogger<ReliabilityAgent> logger,
        IAzureAIFoundryClient? foundryClient = null)
    {
        _logger = logger;
        _foundryClient = foundryClient;
    }

    /// <summary>
    /// Analyze reliability based on service knowledge graph and telemetry
    /// </summary>
    public async Task<AgentAnalysisResult> AnalyzeReliabilityAsync(
        ServiceGraphContext graphContext,
        ReliabilityContext reliabilityContext,
        CancellationToken cancellationToken = default)
    {
        var activity = Activity.Current;
        activity?.SetTag("agent.type", "reliability");
        activity?.SetTag("service.group.id", graphContext.ServiceGroupId);

        var findings = new List<Finding>();
        var recommendations = new List<Recommendation>();
        var evidence = new List<string>();

        // Analyze redundancy and failover
        var redundancyScore = AnalyzeRedundancy(graphContext, findings, recommendations, evidence);

        // Analyze health monitoring
        var monitoringScore = AnalyzeMonitoring(reliabilityContext, findings, recommendations, evidence);

        // Analyze SLA compliance
        var slaScore = AnalyzeSLA(reliabilityContext, findings, recommendations, evidence);

        // Analyze backup and recovery
        var backupScore = AnalyzeBackupRecovery(graphContext, findings, recommendations, evidence);

        // Calculate overall reliability score (0-100)
        var overallScore = (redundancyScore + monitoringScore + slaScore + backupScore) / 4.0;

        // Calculate confidence based on telemetry availability
        var confidence = CalculateConfidence(reliabilityContext);

        _logger.LogInformation(
            "Reliability analysis complete for service group {ServiceGroupId}: Score={Score:F2}, Confidence={Confidence:F2}, Availability={Availability:F2}%",
            graphContext.ServiceGroupId,
            overallScore,
            confidence,
            reliabilityContext.AvailabilityPercent);

        var result = new AgentAnalysisResult
        {
            Score = Math.Round(overallScore, 2),
            Confidence = Math.Round(confidence, 2),
            Findings = findings,
            Recommendations = recommendations,
            EvidenceReferences = evidence
        };

        if (_foundryClient != null)
        {
            result.AINarrativeSummary = await GenerateAINarrativeAsync(result, graphContext.ServiceGroupId, reliabilityContext, cancellationToken);
        }

        return result;
    }

    private async Task<string?> GenerateAINarrativeAsync(
        AgentAnalysisResult result,
        Guid serviceGroupId,
        ReliabilityContext reliabilityContext,
        CancellationToken cancellationToken)
    {
        try
        {
            var criticalFindings = result.Findings
                .Where(f => f.Severity is "critical" or "high")
                .Select(f => $"- {f.Category}: {f.Description}")
                .Take(5)
                .ToList();

            var prompt = $"""
                You are an Azure reliability advisor. Provide a concise executive narrative (3-4 sentences)
                for a reliability assessment with score {result.Score:F0}/100 and availability {reliabilityContext.AvailabilityPercent:F2}%.

                Key reliability concerns:
                {(criticalFindings.Any() ? string.Join("\n", criticalFindings) : "No critical reliability findings")}

                Focus on SLA impact, single points of failure, and the most critical remediation actions.
                """;

            return await _foundryClient!.SendPromptAsync(prompt, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AI narrative generation failed for reliability analysis of service group {ServiceGroupId}", serviceGroupId);
            return $"AI narrative unavailable (score: {result.Score:F0}/100, availability: {reliabilityContext.AvailabilityPercent:F2}%). Review reliability findings for details.";
        }
    }

    private double AnalyzeRedundancy(
        ServiceGraphContext context,
        List<Finding> findings,
        List<Recommendation> recommendations,
        List<string> evidence)
    {
        var score = 100.0;

        // Check for single instance resources
        var singleInstanceResources = context.Nodes
            .Where(n => IsCriticalResourceType(n.NodeType) &&
                       GetInstanceCount(n) == 1)
            .ToList();

        if (singleInstanceResources.Any())
        {
            score -= 30;
            findings.Add(new Finding
            {
                Severity = "high",
                Category = "redundancy",
                Description = $"{singleInstanceResources.Count} critical resources running as single instance",
                Impact = "Single point of failure - no redundancy for critical services"
            });

            recommendations.Add(new Recommendation
            {
                Priority = "high",
                Category = "reliability",
                Title = "Add redundancy for critical resources",
                Description = $"Deploy at least 2 instances for {singleInstanceResources.Count} critical resources",
                EstimatedEffort = "medium"
            });

            evidence.Add($"single_instance_critical_count:{singleInstanceResources.Count}");
        }

        // Check for availability zones
        var zonesUsed = context.Nodes
            .Select(n => n.Metadata?.GetValueOrDefault("availabilityZone"))
            .Where(z => !string.IsNullOrEmpty(z))
            .Distinct()
            .Count();

        evidence.Add($"availability_zones_used:{zonesUsed}");

        if (zonesUsed < 2)
        {
            score -= 20;
            findings.Add(new Finding
            {
                Severity = "high",
                Category = "redundancy",
                Description = $"Services deployed in {zonesUsed} availability zone(s)",
                Impact = "Vulnerable to zone-level failures"
            });

            recommendations.Add(new Recommendation
            {
                Priority = "high",
                Category = "reliability",
                Title = "Deploy across multiple availability zones",
                Description = "Use zone-redundant deployment for high availability (99.99% SLA)",
                EstimatedEffort = "medium"
            });
        }
        else if (zonesUsed >= 2)
        {
            evidence.Add("multi_zone_deployment:true");
        }

        // Check for regions
        var regions = context.Nodes
            .Select(n => n.Metadata?.GetValueOrDefault("location"))
            .Where(l => !string.IsNullOrEmpty(l))
            .Distinct()
            .Count();

        evidence.Add($"regions_count:{regions}");

        if (regions < 2)
        {
            score -= 15;
            findings.Add(new Finding
            {
                Severity = "medium",
                Category = "redundancy",
                Description = "Single-region deployment",
                Impact = "No geographic failover capability"
            });

            recommendations.Add(new Recommendation
            {
                Priority = "low",
                Category = "reliability",
                Title = "Consider multi-region deployment",
                Description = "Deploy to paired Azure regions for disaster recovery",
                EstimatedEffort = "high"
            });
        }

        return Math.Max(0, score);
    }

    private double AnalyzeMonitoring(
        ReliabilityContext context,
        List<Finding> findings,
        List<Recommendation> recommendations,
        List<string> evidence)
    {
        var score = 100.0;

        // Check for health monitoring
        if (!context.HasHealthEndpoints)
        {
            score -= 25;
            findings.Add(new Finding
            {
                Severity = "high",
                Category = "monitoring",
                Description = "No health endpoints detected",
                Impact = "Cannot detect service degradation or failures automatically"
            });

            recommendations.Add(new Recommendation
            {
                Priority = "high",
                Category = "observability",
                Title = "Implement health check endpoints",
                Description = "Add /health/ready and /health/live endpoints for proactive monitoring",
                EstimatedEffort = "low"
            });
        }
        else
        {
            evidence.Add("has_health_endpoints:true");
        }

        // Check for alerting
        if (!context.HasAlerts)
        {
            score -= 20;
            findings.Add(new Finding
            {
                Severity = "high",
                Category = "monitoring",
                Description = "No alerting rules configured",
                Impact = "Failures may go unnoticed until customer reports"
            });

            recommendations.Add(new Recommendation
            {
                Priority = "high",
                Category = "observability",
                Title = "Configure alert rules",
                Description = "Set up alerts for availability, latency, and error rates",
                EstimatedEffort = "low"
            });
        }
        else
        {
            evidence.Add("has_alerts:true");
        }

        // Check for distributed tracing
        if (!context.HasDistributedTracing)
        {
            score -= 15;
            findings.Add(new Finding
            {
                Severity = "medium",
                Category = "monitoring",
                Description = "No distributed tracing detected",
                Impact = "Difficult to diagnose cross-service failures"
            });

            recommendations.Add(new Recommendation
            {
                Priority = "medium",
                Category = "observability",
                Title = "Implement distributed tracing",
                Description = "Add Application Insights or OpenTelemetry for end-to-end tracing",
                EstimatedEffort = "medium"
            });
        }
        else
        {
            evidence.Add("has_distributed_tracing:true");
        }

        return Math.Max(0, score);
    }

    private double AnalyzeSLA(
        ReliabilityContext context,
        List<Finding> findings,
        List<Recommendation> recommendations,
        List<string> evidence)
    {
        var score = 100.0;

        var availability = context.AvailabilityPercent;
        evidence.Add($"availability_percent:{availability:F3}%");

        // 99.9% = 43.2 minutes downtime/month
        // 99.95% = 21.6 minutes
        // 99.99% = 4.32 minutes

        if (availability < 99.0)
        {
            score -= 50;
            findings.Add(new Finding
            {
                Severity = "critical",
                Category = "sla",
                Description = $"Availability is {availability:F2}% (below 99%)",
                Impact = "Not meeting basic availability expectations"
            });

            recommendations.Add(new Recommendation
            {
                Priority = "critical",
                Category = "reliability",
                Title = "Urgent: Improve availability",
                Description = "Investigate and resolve frequent outages - availability is critically low",
                EstimatedEffort = "high"
            });
        }
        else if (availability < 99.9)
        {
            score -= 30;
            findings.Add(new Finding
            {
                Severity = "high",
                Category = "sla",
                Description = $"Availability is {availability:F2}% (below 99.9%)",
                Impact = "May not meet customer SLA expectations"
            });

            recommendations.Add(new Recommendation
            {
                Priority = "high",
                Category = "reliability",
                Title = "Improve availability to 99.9%+",
                Description = "Add redundancy and implement health checks",
                EstimatedEffort = "medium"
            });
        }
        else if (availability < 99.95)
        {
            score -= 10;
            findings.Add(new Finding
            {
                Severity = "medium",
                Category = "sla",
                Description = $"Availability is {availability:F2}% (meeting basic SLA)",
                Impact = "Consider targeting 99.95%+ for production workloads"
            });
        }

        // Check error rate
        var errorRate = context.ErrorRatePercent;
        evidence.Add($"error_rate_percent:{errorRate:F3}%");

        if (errorRate > 1.0)
        {
            score -= 20;
            findings.Add(new Finding
            {
                Severity = "high",
                Category = "errors",
                Description = $"Error rate is {errorRate:F2}% (>1%)",
                Impact = "High error rate degrades user experience"
            });

            recommendations.Add(new Recommendation
            {
                Priority = "high",
                Category = "reliability",
                Title = "Reduce error rate",
                Description = "Investigate and fix causes of errors - target <0.1%",
                EstimatedEffort = "medium"
            });
        }
        else if (errorRate > 0.1)
        {
            score -= 5;
            findings.Add(new Finding
            {
                Severity = "low",
                Category = "errors",
                Description = $"Error rate is {errorRate:F2}%",
                Impact = "Minor error rate - monitor trends"
            });
        }

        return Math.Max(0, score);
    }

    private double AnalyzeBackupRecovery(
        ServiceGraphContext context,
        List<Finding> findings,
        List<Recommendation> recommendations,
        List<string> evidence)
    {
        var score = 100.0;

        // Check for backup-enabled resources
        var storageResources = context.Nodes
            .Where(n => IsStorageResourceType(n.NodeType))
            .ToList();

        var backupEnabledResources = storageResources
            .Where(n => n.Metadata?.GetValueOrDefault("backupEnabled")?.Equals("true", StringComparison.OrdinalIgnoreCase) == true)
            .ToList();

        if (storageResources.Any() && !backupEnabledResources.Any())
        {
            score -= 30;
            findings.Add(new Finding
            {
                Severity = "high",
                Category = "backup",
                Description = $"{storageResources.Count} storage resources without backup configured",
                Impact = "Data loss risk - no recovery mechanism"
            });

            recommendations.Add(new Recommendation
            {
                Priority = "high",
                Category = "reliability",
                Title = "Enable backup for storage resources",
                Description = "Configure Azure Backup or geo-redundant replication",
                EstimatedEffort = "low"
            });

            evidence.Add($"storage_resources_no_backup:{storageResources.Count}");
        }
        else if (storageResources.Any())
        {
            var backupCoverage = (backupEnabledResources.Count / (double)storageResources.Count) * 100;
            evidence.Add($"backup_coverage_percent:{backupCoverage:F1}%");

            if (backupCoverage < 80)
            {
                score -= 15;
                findings.Add(new Finding
                {
                    Severity = "medium",
                    Category = "backup",
                    Description = $"Only {backupCoverage:F1}% of storage resources have backup enabled",
                    Impact = "Partial data loss risk"
                });

                recommendations.Add(new Recommendation
                {
                    Priority = "medium",
                    Category = "reliability",
                    Title = "Increase backup coverage",
                    Description = $"Enable backup for remaining {storageResources.Count - backupEnabledResources.Count} storage resources",
                    EstimatedEffort = "low"
                });
            }
        }

        // Check for disaster recovery plan
        var hasDRConfig = context.Nodes
            .Any(n => n.Metadata?.ContainsKey("drRegion") == true);

        if (!hasDRConfig)
        {
            score -= 20;
            findings.Add(new Finding
            {
                Severity = "medium",
                Category = "disaster_recovery",
                Description = "No disaster recovery configuration detected",
                Impact = "Extended downtime in case of regional failure"
            });

            recommendations.Add(new Recommendation
            {
                Priority = "medium",
                Category = "reliability",
                Title = "Implement disaster recovery plan",
                Description = "Configure cross-region failover and document recovery procedures",
                EstimatedEffort = "high"
            });
        }
        else
        {
            evidence.Add("has_dr_config:true");
        }

        return Math.Max(0, score);
    }

    private double CalculateConfidence(ReliabilityContext context)
    {
        var confidence = 1.0;

        // Reduce confidence if telemetry is incomplete
        if (context.AvailabilityPercent == 0)
        {
            confidence *= 0.3;
        }

        if (!context.HasMetrics)
        {
            confidence *= 0.5;
        }

        if (!context.HasHealthEndpoints)
        {
            confidence *= 0.8;
        }

        return confidence;
    }

    private bool IsCriticalResourceType(string nodeType)
    {
        var criticalTypes = new[]
        {
            "VirtualMachine",
            "AppService",
            "FunctionApp",
            "ContainerApp",
            "SQL",
            "PostgreSQL",
            "CosmosDB",
            "LoadBalancer",
            "ApplicationGateway"
        };

        return criticalTypes.Any(t => nodeType.Contains(t, StringComparison.OrdinalIgnoreCase));
    }

    private bool IsStorageResourceType(string nodeType)
    {
        var storageTypes = new[]
        {
            "StorageAccount",
            "SQL",
            "PostgreSQL",
            "MySQL",
            "CosmosDB",
            "Backup"
        };

        return storageTypes.Any(t => nodeType.Contains(t, StringComparison.OrdinalIgnoreCase));
    }

    private int GetInstanceCount(ServiceNodeDto node)
    {
        // Try to get instance count from metadata
        if (node.Metadata?.TryGetValue("instanceCount", out var countStr) == true &&
            int.TryParse(countStr, out var count))
        {
            return count;
        }

        // Default to 1 if not specified
        return 1;
    }
}

/// <summary>
/// Reliability context for agent analysis
/// </summary>
public class ReliabilityContext
{
    public double AvailabilityPercent { get; set; }
    public double ErrorRatePercent { get; set; }
    public bool HasHealthEndpoints { get; set; }
    public bool HasAlerts { get; set; }
    public bool HasDistributedTracing { get; set; }
    public bool HasMetrics { get; set; }
}
