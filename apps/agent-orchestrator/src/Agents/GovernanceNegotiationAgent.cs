using Atlas.AgentOrchestrator.Integrations.Azure;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace Atlas.AgentOrchestrator.Agents;

/// <summary>
/// T033: Governance negotiation and conflict mediation outputs.
/// When conflicts are detected the agent calls Azure AI Foundry (GPT-4) to reason
/// about trade-offs and produce a human-readable mediation narrative.
/// </summary>
public class GovernanceNegotiationAgent
{
    private readonly ILogger<GovernanceNegotiationAgent> _logger;
    private readonly IAzureAIFoundryClient? _foundryClient;

    public GovernanceNegotiationAgent(
        ILogger<GovernanceNegotiationAgent> logger,
        IAzureAIFoundryClient? foundryClient = null)
    {
        _logger = logger;
        _foundryClient = foundryClient;
    }

    /// <summary>
    /// Negotiate governance conflicts between recommendation and policy constraints
    /// </summary>
    public async Task<NegotiationOutcome> NegotiateAsync(
        RecommendationProposal proposal,
        PolicyConstraints constraints,
        CancellationToken cancellationToken = default)
    {
        var activity = Activity.Current;
        activity?.SetTag("proposal.id", proposal.Id);
        activity?.SetTag("proposal.type", proposal.ActionType);

        _logger.LogInformation(
            "Starting governance negotiation for proposal {ProposalId} ({ActionType})",
            proposal.Id,
            proposal.ActionType);

        var conflicts = IdentifyConflicts(proposal, constraints);

        if (!conflicts.Any())
        {
            return NegotiationOutcome.Approved(proposal, "No conflicts detected");
        }

        var mediationResults = new List<MediationResult>();

        foreach (var conflict in conflicts)
        {
            var mediation = await MediateConflictAsync(conflict, proposal, constraints, cancellationToken);
            mediationResults.Add(mediation);
        }

        // Determine overall outcome
        var hasBlockingConflicts = mediationResults.Any(m => m.Severity == "blocking");
        var hasWarningConflicts = mediationResults.Any(m => m.Severity == "warning");

        if (hasBlockingConflicts)
        {
            _logger.LogWarning(
                "Governance negotiation blocked proposal {ProposalId} due to {Count} blocking conflicts",
                proposal.Id,
                mediationResults.Count(m => m.Severity == "blocking"));

            return NegotiationOutcome.Blocked(proposal, mediationResults);
        }

        if (hasWarningConflicts)
        {
            _logger.LogInformation(
                "Governance negotiation escalated proposal {ProposalId} with {Count} warnings",
                proposal.Id,
                mediationResults.Count(m => m.Severity == "warning"));

            return NegotiationOutcome.Escalated(proposal, mediationResults);
        }

        return NegotiationOutcome.ApprovedWithConditions(proposal, mediationResults);
    }

    private List<PolicyConflict> IdentifyConflicts(
        RecommendationProposal proposal,
        PolicyConstraints constraints)
    {
        var conflicts = new List<PolicyConflict>();

        // Check cost constraints
        if (proposal.EstimatedMonthlyCost > constraints.MaxMonthlyCost)
        {
            conflicts.Add(new PolicyConflict
            {
                Type = "cost_exceeded",
                Severity = "blocking",
                PolicyRule = "max_monthly_cost",
                ProposedValue = proposal.EstimatedMonthlyCost.ToString(),
                ConstraintValue = constraints.MaxMonthlyCost.ToString(),
                Message = $"Estimated cost ${proposal.EstimatedMonthlyCost}/month exceeds maximum ${constraints.MaxMonthlyCost}/month"
            });
        }

        // Check availability constraints
        if (proposal.RequiredSla > constraints.MaxSla)
        {
            conflicts.Add(new PolicyConflict
            {
                Type = "sla_exceeded",
                Severity = "warning",
                PolicyRule = "max_sla",
                ProposedValue = proposal.RequiredSla.ToString(),
                ConstraintValue = constraints.MaxSla.ToString(),
                Message = $"Required SLA {proposal.RequiredSla}% exceeds standard {constraints.MaxSla}%"
            });
        }

        // Check data residency
        if (!string.IsNullOrEmpty(constraints.RequiredDataResidency) &&
            proposal.TargetRegion != constraints.RequiredDataResidency)
        {
            conflicts.Add(new PolicyConflict
            {
                Type = "data_residency_violation",
                Severity = "blocking",
                PolicyRule = "data_residency",
                ProposedValue = proposal.TargetRegion,
                ConstraintValue = constraints.RequiredDataResidency,
                Message = $"Target region {proposal.TargetRegion} violates data residency requirement {constraints.RequiredDataResidency}"
            });
        }

        return conflicts;
    }

    private async Task<MediationResult> MediateConflictAsync(
        PolicyConflict conflict,
        RecommendationProposal proposal,
        PolicyConstraints constraints,
        CancellationToken cancellationToken)
    {
        string resolution;

        // If Azure AI Foundry is available, ask GPT-4 to reason about the trade-offs
        // and generate a human-readable mediation narrative.
        if (_foundryClient is not null)
        {
            try
            {
                var prompt =
                    $"""
                    You are a cloud governance advisor mediating a policy conflict.

                    Conflict type: {conflict.Type}
                    Severity: {conflict.Severity}
                    Policy rule: {conflict.PolicyRule}
                    Proposed value: {conflict.ProposedValue}
                    Constraint: {conflict.ConstraintValue}
                    Description: {conflict.Message}

                    Action being proposed: {proposal.ActionType}
                    Estimated monthly cost: ${proposal.EstimatedMonthlyCost}
                    Required SLA: {proposal.RequiredSla}%
                    Target region: {proposal.TargetRegion}

                    Analyse the trade-offs and recommend a concise governance decision
                    (2–3 sentences). Consider cost impact, compliance risk and operational
                    feasibility. State whether the action should be approved, conditionally
                    approved, escalated, or blocked, and explain why.
                    """;

                var threadId = $"governance-negotiation:{proposal.Id}";
                resolution = await _foundryClient.SendPromptAsync(prompt, threadId, cancellationToken);

                _logger.LogInformation(
                    "AI-generated mediation for conflict {ConflictType}: {Resolution}",
                    conflict.Type, resolution);
            }
            catch (OperationCanceledException)
            {
                // Preserve cancellation semantics — do not swallow.
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "AI Foundry mediation failed for {ConflictType}; falling back to rule-based resolution",
                    conflict.Type);
                resolution = GetRuleBasedResolution(conflict);
            }
        }
        else
        {
            resolution = GetRuleBasedResolution(conflict);
        }

        var mediation = new MediationResult
        {
            Conflict = conflict,
            Severity = conflict.Severity,
            Resolution = resolution,
            RequiresEscalation = conflict.Severity == "blocking" || conflict.Type == "sla_exceeded"
        };

        _logger.LogInformation(
            "Mediated conflict {ConflictType} ({Severity}): {Resolution}",
            conflict.Type, conflict.Severity, mediation.Resolution);

        return mediation;
    }

    private static string GetRuleBasedResolution(PolicyConflict conflict) =>
        conflict.Type switch
        {
            "cost_exceeded" => "Recommend phased rollout to reduce initial cost",
            "sla_exceeded" => "Escalate to architecture review board",
            "data_residency_violation" => "Block deployment until region changed",
            _ => "Manual review required"
        };
}

public class RecommendationProposal
{
    public Guid Id { get; set; }
    public string ActionType { get; set; } = string.Empty;
    public decimal EstimatedMonthlyCost { get; set; }
    public decimal RequiredSla { get; set; }
    public string TargetRegion { get; set; } = string.Empty;
}

public class PolicyConstraints
{
    public decimal MaxMonthlyCost { get; set; }
    public decimal MaxSla { get; set; }
    public string RequiredDataResidency { get; set; } = string.Empty;
}

public class PolicyConflict
{
    public string Type { get; set; } = string.Empty;
    public string Severity { get; set; } = string.Empty;
    public string PolicyRule { get; set; } = string.Empty;
    public string ProposedValue { get; set; } = string.Empty;
    public string ConstraintValue { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}

public class MediationResult
{
    public PolicyConflict Conflict { get; set; } = null!;
    public string Severity { get; set; } = string.Empty;
    public string Resolution { get; set; } = string.Empty;
    public bool RequiresEscalation { get; set; }
}

public class NegotiationOutcome
{
    public string Status { get; set; } = string.Empty;
    public RecommendationProposal Proposal { get; set; } = null!;
    public string Message { get; set; } = string.Empty;
    public List<MediationResult> MediationResults { get; set; } = new();

    public static NegotiationOutcome Approved(RecommendationProposal proposal, string message)
    {
        return new NegotiationOutcome
        {
            Status = "approved",
            Proposal = proposal,
            Message = message
        };
    }

    public static NegotiationOutcome Blocked(RecommendationProposal proposal, List<MediationResult> results)
    {
        return new NegotiationOutcome
        {
            Status = "blocked",
            Proposal = proposal,
            Message = "Blocked due to policy violations",
            MediationResults = results
        };
    }

    public static NegotiationOutcome Escalated(RecommendationProposal proposal, List<MediationResult> results)
    {
        return new NegotiationOutcome
        {
            Status = "escalated",
            Proposal = proposal,
            Message = "Escalated for manual review",
            MediationResults = results
        };
    }

    public static NegotiationOutcome ApprovedWithConditions(RecommendationProposal proposal, List<MediationResult> results)
    {
        return new NegotiationOutcome
        {
            Status = "approved_with_conditions",
            Proposal = proposal,
            Message = "Approved with conditions",
            MediationResults = results
        };
    }
}
