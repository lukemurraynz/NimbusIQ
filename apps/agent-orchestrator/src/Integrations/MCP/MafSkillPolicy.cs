using System.Collections.Frozen;

namespace Atlas.AgentOrchestrator.Integrations.MCP;

/// <summary>
/// Defines policy contracts for MAF skills—what grounding evidence each agent/pillar must include.
/// Enforces that critical agents cannot execute without adequate MCP grounding.
/// </summary>
public static class MafSkillPolicy
{
    /// <summary>
    /// Agent/pillar identifiers that must include Learn MCP references.
    /// </summary>
    public static readonly FrozenSet<string> MustIncludeLearnReferences = FrozenSet.Create(
        StringComparer.OrdinalIgnoreCase,
        "ReliabilityAnalysis",
        "SecurityAnalysis",
        "CostOptimization",
        "PerformanceAnalysis",
        "OperationalExcellence"
    );

    /// <summary>
    /// Agent/pillar identifiers that must include Azure MCP tool grounding.
    /// </summary>
    public static readonly FrozenSet<string> MustIncludeAzureTools = FrozenSet.Create(
        StringComparer.OrdinalIgnoreCase,
        "CostOptimization",
        "SecurityAnalysis",
        "AutomationFramework"
    );

    /// <summary>
    /// Minimum required number of Learn references for critical agents.
    /// </summary>
    public const int MinimumLearnReferencesRequired = 2;

    /// <summary>
    /// Validates that grounding result meets policy requirements for a given agent.
    /// </summary>
    public static PolicyValidationResult ValidateGroundingPolicy(
        string agentId,
        MafGroundingResult grounding)
    {
        if (string.IsNullOrWhiteSpace(agentId))
            throw new ArgumentException("Agent ID cannot be null or whitespace.", nameof(agentId));

        if (grounding == null)
            throw new ArgumentNullException(nameof(grounding));

        var violations = new List<string>();

        // Check Learn MCP requirement
        if (MustIncludeLearnReferences.Contains(agentId))
        {
            if (!grounding.LearnMcpEnabled)
            {
                violations.Add(
                    $"Agent '{agentId}' requires Learn MCP grounding, but Learn MCP is not enabled.");
            }

            if (grounding.LearnReferences.Count < MinimumLearnReferencesRequired)
            {
                violations.Add(
                    $"Agent '{agentId}' requires at least {MinimumLearnReferencesRequired} Learn references; " +
                    $"found {grounding.LearnReferences.Count}.");
            }
        }

        // Check Azure MCP requirement
        if (MustIncludeAzureTools.Contains(agentId))
        {
            if (!grounding.AzureMcpEnabled)
            {
                violations.Add(
                    $"Agent '{agentId}' requires Azure MCP grounding, but Azure MCP is not enabled.");
            }

            if (grounding.AzureToolNames.Count == 0)
            {
                violations.Add(
                    $"Agent '{agentId}' requires Azure MCP tools, but none were populated.");
            }
        }

        return violations.Count == 0
            ? PolicyValidationResult.Success()
            : PolicyValidationResult.Failure(violations);
    }
}

/// <summary>
/// Result of a grounding policy validation check.
/// </summary>
public sealed class PolicyValidationResult
{
    public bool IsValid { get; }
    public List<string> Violations { get; }

    private PolicyValidationResult(bool isValid, List<string> violations)
    {
        IsValid = isValid;
        Violations = violations ?? [];
    }

    public static PolicyValidationResult Success() => new(true, []);

    public static PolicyValidationResult Failure(List<string> violations) =>
        new(false, violations ?? []);

    public override string ToString() =>
        IsValid
            ? "Policy validation passed."
            : $"Policy validation failed:\n  - {string.Join("\n  - ", Violations)}";
}
