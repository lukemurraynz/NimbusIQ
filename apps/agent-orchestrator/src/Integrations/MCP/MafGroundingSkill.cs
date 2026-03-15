using Microsoft.Extensions.Logging;
using Polly.CircuitBreaker;

namespace Atlas.AgentOrchestrator.Integrations.MCP;

/// <summary>
/// Reusable grounding skill that combines Azure MCP and Learn MCP evidence for
/// Microsoft Agent Framework workflows. Enforces policy contracts and surfaces grounding provenance.
/// </summary>
public sealed class MafGroundingSkill
{
    private readonly ILogger<MafGroundingSkill> _logger;
    private readonly LearnMcpClient? _learnMcpClient;
    private readonly AzureMcpToolClient? _azureMcpToolClient;
    private readonly MafMcpResilienceWrapper? _resilienceWrapper;

    public MafGroundingSkill(
        ILogger<MafGroundingSkill> logger,
        LearnMcpClient? learnMcpClient = null,
        AzureMcpToolClient? azureMcpToolClient = null,
        MafMcpResilienceWrapper? resilienceWrapper = null)
    {
        _logger = logger;
        _learnMcpClient = learnMcpClient;
        _azureMcpToolClient = azureMcpToolClient;
        _resilienceWrapper = resilienceWrapper;
    }

    public async Task<MafGroundingResult> BuildGroundingContextAsync(
        MafGroundingRequest request,
        string? requestingAgentId = null,
        CancellationToken cancellationToken = default)
    {
        var result = new MafGroundingResult
        {
            GeneratedAt = DateTimeOffset.UtcNow,
            RequestingAgentId = requestingAgentId,
            LearnMcpEnabled = _learnMcpClient is not null,
            AzureMcpEnabled = _azureMcpToolClient is not null,
            SuggestedConstraint = string.IsNullOrWhiteSpace(request.Constraint)
                ? "optimize_within_policy"
                : request.Constraint.Trim(),
            SuggestedObjectives =
            [
                "reduce_cost",
                "maintain_sla",
                "enforce_security"
            ]
        };

        if (_learnMcpClient is null)
        {
            result.Warnings.Add("Learn MCP client not configured; skipping documentation grounding.");
        }
        else
        {
            await PopulateLearnGroundingAsync(result, request, requestingAgentId, cancellationToken);
        }

        if (_azureMcpToolClient is null)
        {
            result.Warnings.Add("Azure MCP client not configured; skipping Azure MCP tool grounding.");
        }
        else
        {
            await PopulateAzureGroundingAsync(result, request, requestingAgentId, cancellationToken);
        }

        // Validate against skill policy if agent ID is provided
        if (!string.IsNullOrWhiteSpace(requestingAgentId))
        {
            var policyValidation = MafSkillPolicy.ValidateGroundingPolicy(requestingAgentId, result);
            if (!policyValidation.IsValid)
            {
                result.PolicyViolations.AddRange(policyValidation.Violations);
                _logger.LogWarning(
                    "Policy validation failed for agent {AgentId}: {Violations}",
                    requestingAgentId,
                    string.Join("; ", policyValidation.Violations));
            }
            else
            {
                result.PolicyCompliant = true;
            }
        }

        return result;
    }

    private async Task PopulateLearnGroundingAsync(
        MafGroundingResult result,
        MafGroundingRequest request,
        string? requestingAgentId,
        CancellationToken cancellationToken)
    {
        var learnMcpClient = _learnMcpClient!;

        try
        {
            var lookupConstraint = string.IsNullOrWhiteSpace(request.Constraint)
                ? "tradeoffs"
                : request.Constraint;

            // Use resilience wrapper if available; otherwise call directly
            var docs = _resilienceWrapper is not null
                ? await _resilienceWrapper.ExecuteLearnMcpAsync(
                    ct => learnMcpClient.SearchDocsAsync(
                        $"Azure Well-Architected Framework {lookupConstraint}",
                        "waf",
                        cancellationToken: ct,
                        toolCallContext: request.ToolCallContext),
                    "SearchDocsAsync",
                    cancellationToken)
                : await learnMcpClient.SearchDocsAsync(
                    $"Azure Well-Architected Framework {lookupConstraint}",
                    "waf",
                    cancellationToken: cancellationToken,
                    toolCallContext: request.ToolCallContext);

            foreach (var doc in docs.Take(3))
            {
                result.LearnReferences.Add(new MafGroundingReference
                {
                    Title = doc.Title,
                    Url = doc.Url,
                    Summary = doc.Summary,
                    Scope = doc.Scope,
                    ResourceType = doc.ResourceType,
                    RequestedByAgent = requestingAgentId,
                    Pillar = ExtractPillarFromConstraint(request.Constraint),
                    RetrievedAt = DateTimeOffset.UtcNow
                });
            }

            foreach (var resourceType in request.ResourceTypes
                .Where(static r => !string.IsNullOrWhiteSpace(r))
                .Select(static r => r!)
                .Take(3))
            {
                var resourceDocs = _resilienceWrapper is not null
                    ? await _resilienceWrapper.ExecuteLearnMcpAsync(
                        ct => learnMcpClient.GetResourceGuidanceAsync(
                            resourceType,
                            "Reliability",
                            ct,
                            request.ToolCallContext),
                        "GetResourceGuidanceAsync",
                        cancellationToken)
                    : await learnMcpClient.GetResourceGuidanceAsync(
                        resourceType,
                        "Reliability",
                        cancellationToken,
                        request.ToolCallContext);

                foreach (var resourceDoc in resourceDocs.Take(2))
                {
                    result.LearnReferences.Add(new MafGroundingReference
                    {
                        Title = resourceDoc.Title,
                        Url = resourceDoc.Url,
                        Summary = resourceDoc.Summary,
                        Scope = resourceDoc.Scope,
                        ResourceType = resourceType,
                        RequestedByAgent = requestingAgentId,
                        Pillar = "Reliability",
                        RetrievedAt = DateTimeOffset.UtcNow
                    });
                }
            }
        }
        catch (BrokenCircuitException ex)
        {
            _logger.LogError(ex, "Learn MCP circuit breaker is open; grounding degraded");
            result.Warnings.Add("Learn MCP circuit breaker open; using cached or minimal grounding.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Learn MCP grounding failed in MAF grounding skill");
            result.Warnings.Add($"Learn MCP grounding failed: {ex.Message}");
        }
    }

    private async Task PopulateAzureGroundingAsync(
        MafGroundingResult result,
        MafGroundingRequest request,
        string? requestingAgentId,
        CancellationToken cancellationToken)
    {
        var azureMcpToolClient = _azureMcpToolClient!;

        try
        {
            // Capability-based routing: select tools based on requesting agent's needs
            var toolFilter = DetermineToolCapabilities(requestingAgentId);

            var tools = _resilienceWrapper is not null
                ? await _resilienceWrapper.ExecuteAzureMcpAsync(
                    ct => azureMcpToolClient.ListToolsAsync(ct),
                    "ListToolsAsync",
                    cancellationToken)
                : await azureMcpToolClient.ListToolsAsync(cancellationToken);

            // Filter tools by agent capability requirements
            result.AzureToolNames = tools
                .Where(tool => toolFilter.IsToolRelevant(tool.Name ?? string.Empty))
                .Select(static tool => tool.Name)
                .OfType<string>()
                .Where(static name => !string.IsNullOrWhiteSpace(name))
                .Take(toolFilter.MaxToolsToReturn)
                .ToList();

            result.ToolCapabilitiesApplied = toolFilter.CapabilityDescription;

            if (!string.IsNullOrWhiteSpace(request.SubscriptionId))
            {
                var endDate = DateTime.UtcNow;
                var lookbackDays = request.CostLookbackDays > 0 ? request.CostLookbackDays : 30;
                var startDate = endDate.AddDays(-lookbackDays);

                var costSnapshot = _resilienceWrapper is not null
                    ? await _resilienceWrapper.ExecuteAzureMcpAsync(
                        ct => azureMcpToolClient.QueryCostAsync(
                            request.SubscriptionId,
                            startDate,
                            endDate,
                            ct,
                            request.ToolCallContext),
                        "QueryCostAsync",
                        cancellationToken)
                    : await azureMcpToolClient.QueryCostAsync(
                        request.SubscriptionId,
                        startDate,
                        endDate,
                        cancellationToken,
                        request.ToolCallContext);

                result.AzureCostSnapshot = costSnapshot;
                result.CostRetrievedByAgent = requestingAgentId;
            }
        }
        catch (BrokenCircuitException ex)
        {
            _logger.LogError(ex, "Azure MCP circuit breaker is open; tool grounding degraded");
            result.Warnings.Add("Azure MCP circuit breaker open; using cached or minimal tool list.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Azure MCP grounding failed in MAF grounding skill");
            result.Warnings.Add($"Azure MCP grounding failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Determines which tools are relevant for a given agent based on its capability.
    /// Implements capability-based routing instead of returning all tools.
    /// </summary>
    private static ToolCapabilityFilter DetermineToolCapabilities(string? agentId)
    {
        if (string.IsNullOrWhiteSpace(agentId))
        {
            return new ToolCapabilityFilter
            {
                CapabilityDescription = "No agent specified; returning all tools",
                MaxToolsToReturn = 25,
                IsToolRelevant = static _ => true
            };
        }

        // Route specific tools based on agent capability
        return agentId switch
        {
            var id when id.Contains("Cost", StringComparison.OrdinalIgnoreCase) =>
                new ToolCapabilityFilter
                {
                    CapabilityDescription = "Cost optimization: prioritizing pricing/billing tools",
                    MaxToolsToReturn = 15,
                    IsToolRelevant = name =>
                        name.Contains("cost", StringComparison.OrdinalIgnoreCase)
                        || name.Contains("price", StringComparison.OrdinalIgnoreCase)
                        || name.Contains("billing", StringComparison.OrdinalIgnoreCase)
                        || name.Contains("sku", StringComparison.OrdinalIgnoreCase)
                },

            var id when id.Contains("Security", StringComparison.OrdinalIgnoreCase) =>
                new ToolCapabilityFilter
                {
                    CapabilityDescription = "Security analysis: prioritizing security/compliance tools",
                    MaxToolsToReturn = 15,
                    IsToolRelevant = name =>
                        name.Contains("security", StringComparison.OrdinalIgnoreCase)
                        || name.Contains("compliance", StringComparison.OrdinalIgnoreCase)
                        || name.Contains("policy", StringComparison.OrdinalIgnoreCase)
                        || name.Contains("identity", StringComparison.OrdinalIgnoreCase)
                        || name.Contains("access", StringComparison.OrdinalIgnoreCase)
                },

            var id when id.Contains("Reliability", StringComparison.OrdinalIgnoreCase) =>
                new ToolCapabilityFilter
                {
                    CapabilityDescription = "Reliability assessment: prioritizing availability/resilience tools",
                    MaxToolsToReturn = 15,
                    IsToolRelevant = name =>
                        name.Contains("health", StringComparison.OrdinalIgnoreCase)
                        || name.Contains("monitor", StringComparison.OrdinalIgnoreCase)
                        || name.Contains("backup", StringComparison.OrdinalIgnoreCase)
                        || name.Contains("disaster", StringComparison.OrdinalIgnoreCase)
                        || name.Contains("recovery", StringComparison.OrdinalIgnoreCase)
                },

            _ =>
                new ToolCapabilityFilter
                {
                    CapabilityDescription = $"Agent {agentId} has default tool access",
                    MaxToolsToReturn = 20,
                    IsToolRelevant = static _ => true
                }
        };
    }

    /// <summary>
    /// Helper to extract pillar name from constraint string.
    /// </summary>
    private static string ExtractPillarFromConstraint(string? constraint)
    {
        if (string.IsNullOrWhiteSpace(constraint))
            return "General";

        return constraint switch
        {
            var c when c.Contains("reliability", StringComparison.OrdinalIgnoreCase) => "Reliability",
            var c when c.Contains("security", StringComparison.OrdinalIgnoreCase) => "Security",
            var c when c.Contains("cost", StringComparison.OrdinalIgnoreCase) => "Cost Optimization",
            var c when c.Contains("performance", StringComparison.OrdinalIgnoreCase) => "Performance",
            var c when c.Contains("operations", StringComparison.OrdinalIgnoreCase) => "Operational Excellence",
            _ => "General"
        };
    }

    /// <summary>
    /// Describes tool capability filtering for an agent.
    /// </summary>
    private sealed class ToolCapabilityFilter
    {
        public string CapabilityDescription { get; set; } = string.Empty;
        public int MaxToolsToReturn { get; set; } = 20;
        public Func<string, bool> IsToolRelevant { get; set; } = static _ => true;
    }
}

public sealed class MafGroundingRequest
{
    public Guid ServiceGroupId { get; set; }
    public string? SubscriptionId { get; set; }
    public string? Constraint { get; set; }
    public List<string> ResourceTypes { get; set; } = [];
    public int CostLookbackDays { get; set; } = 30;
    public ToolCallContext? ToolCallContext { get; set; }
}

public sealed class MafGroundingResult
{
    public DateTimeOffset GeneratedAt { get; set; }
    public string? RequestingAgentId { get; set; }
    public bool LearnMcpEnabled { get; set; }
    public bool AzureMcpEnabled { get; set; }
    public bool PolicyCompliant { get; set; }
    public string SuggestedConstraint { get; set; } = "optimize_within_policy";
    public List<string> SuggestedObjectives { get; set; } = [];
    public List<MafGroundingReference> LearnReferences { get; set; } = [];
    public List<string> AzureToolNames { get; set; } = [];
    public string ToolCapabilitiesApplied { get; set; } = string.Empty;
    public string? CostRetrievedByAgent { get; set; }
    public Dictionary<string, object>? AzureCostSnapshot { get; set; }
    public List<string> Warnings { get; set; } = [];
    public List<string> PolicyViolations { get; set; } = [];
}

public sealed class MafGroundingReference
{
    public string Title { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string? Scope { get; set; }
    public string? ResourceType { get; set; }
    public string? RequestedByAgent { get; set; }
    public string? Pillar { get; set; }
    public DateTimeOffset RetrievedAt { get; set; }
}
