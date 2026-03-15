using Atlas.AgentOrchestrator.Agents;
using Microsoft.Extensions.Logging.Abstractions;

namespace Atlas.AgentOrchestrator.Tests.Unit;

/// <summary>
/// Unit tests for GovernanceNegotiationAgent — conflict detection, mediation outcomes,
/// and rule-based fallback when no AI Foundry client is available.
/// </summary>
public class GovernanceNegotiationAgentTests
{
    private static GovernanceNegotiationAgent CreateAgent() =>
        new(NullLogger<GovernanceNegotiationAgent>.Instance, foundryClient: null);

    // ──────────────────────────────────────────────
    // No-conflict scenarios
    // ──────────────────────────────────────────────

    [Fact]
    public async Task NegotiateAsync_NoCostOrSlaOrRegionConflict_ReturnsApproved()
    {
        var agent = CreateAgent();
        var proposal = new RecommendationProposal
        {
            Id = Guid.NewGuid(),
            ActionType = "scale-up",
            EstimatedMonthlyCost = 50m,
            RequiredSla = 99.9m,
            TargetRegion = "eastus",
        };
        var constraints = new PolicyConstraints
        {
            MaxMonthlyCost = 500m,
            MaxSla = 99.99m,
            RequiredDataResidency = "eastus",
        };

        var outcome = await agent.NegotiateAsync(proposal, constraints);

        Assert.Equal("approved", outcome.Status);
        Assert.Empty(outcome.MediationResults);
    }

    // ──────────────────────────────────────────────
    // Cost conflict (blocking)
    // ──────────────────────────────────────────────

    [Fact]
    public async Task NegotiateAsync_CostExceedsMaximum_ReturnsBlocked()
    {
        var agent = CreateAgent();
        var proposal = new RecommendationProposal
        {
            Id = Guid.NewGuid(),
            ActionType = "scale-up",
            EstimatedMonthlyCost = 1000m,
            RequiredSla = 99.0m,
            TargetRegion = "eastus",
        };
        var constraints = new PolicyConstraints
        {
            MaxMonthlyCost = 100m,
            MaxSla = 99.9m,
            RequiredDataResidency = "eastus",
        };

        var outcome = await agent.NegotiateAsync(proposal, constraints);

        Assert.Equal("blocked", outcome.Status);
        Assert.NotEmpty(outcome.MediationResults);
        Assert.Contains(outcome.MediationResults, m => m.Conflict.Type == "cost_exceeded");
    }

    // ──────────────────────────────────────────────
    // SLA conflict (warning → escalated)
    // ──────────────────────────────────────────────

    [Fact]
    public async Task NegotiateAsync_SlaExceedsMaximum_ReturnsEscalated()
    {
        var agent = CreateAgent();
        var proposal = new RecommendationProposal
        {
            Id = Guid.NewGuid(),
            ActionType = "region-migration",
            EstimatedMonthlyCost = 50m,
            RequiredSla = 99.99m,
            TargetRegion = "eastus",
        };
        var constraints = new PolicyConstraints
        {
            MaxMonthlyCost = 500m,
            MaxSla = 99.9m,         // proposal requires MORE than constraint allows
            RequiredDataResidency = "eastus",
        };

        var outcome = await agent.NegotiateAsync(proposal, constraints);

        Assert.Equal("escalated", outcome.Status);
        Assert.Contains(outcome.MediationResults, m => m.Conflict.Type == "sla_exceeded");
    }

    // ──────────────────────────────────────────────
    // Data residency conflict (blocking)
    // ──────────────────────────────────────────────

    [Fact]
    public async Task NegotiateAsync_RegionViolatesDataResidency_ReturnsBlocked()
    {
        var agent = CreateAgent();
        var proposal = new RecommendationProposal
        {
            Id = Guid.NewGuid(),
            ActionType = "deploy",
            EstimatedMonthlyCost = 50m,
            RequiredSla = 99.0m,
            TargetRegion = "westus",
        };
        var constraints = new PolicyConstraints
        {
            MaxMonthlyCost = 500m,
            MaxSla = 99.9m,
            RequiredDataResidency = "uksouth",
        };

        var outcome = await agent.NegotiateAsync(proposal, constraints);

        Assert.Equal("blocked", outcome.Status);
        Assert.Contains(outcome.MediationResults, m => m.Conflict.Type == "data_residency_violation");
    }

    // ──────────────────────────────────────────────
    // Multiple conflicts
    // ──────────────────────────────────────────────

    [Fact]
    public async Task NegotiateAsync_MultipleConflictsIncludingBlocking_ReturnsBlocked()
    {
        var agent = CreateAgent();
        var proposal = new RecommendationProposal
        {
            Id = Guid.NewGuid(),
            ActionType = "full-upgrade",
            EstimatedMonthlyCost = 2000m,
            RequiredSla = 99.99m,
            TargetRegion = "southasia",
        };
        var constraints = new PolicyConstraints
        {
            MaxMonthlyCost = 500m,
            MaxSla = 99.9m,
            RequiredDataResidency = "northeurope",
        };

        var outcome = await agent.NegotiateAsync(proposal, constraints);

        // Any blocking conflict should result in blocked status
        Assert.Equal("blocked", outcome.Status);
        Assert.True(outcome.MediationResults.Count >= 2, "Expected at least 2 mediation results for 2+ conflicts");
    }

    // ──────────────────────────────────────────────
    // MediationResult has non-null resolution
    // ──────────────────────────────────────────────

    [Fact]
    public async Task NegotiateAsync_ConflictDetected_MediationResultHasNonEmptyResolution()
    {
        var agent = CreateAgent();
        var proposal = new RecommendationProposal
        {
            Id = Guid.NewGuid(),
            ActionType = "scale-out",
            EstimatedMonthlyCost = 999m,
            RequiredSla = 99.0m,
            TargetRegion = "eastus",
        };
        var constraints = new PolicyConstraints
        {
            MaxMonthlyCost = 100m,
            MaxSla = 99.9m,
            RequiredDataResidency = "eastus",
        };

        var outcome = await agent.NegotiateAsync(proposal, constraints);

        foreach (var m in outcome.MediationResults)
        {
            Assert.False(string.IsNullOrWhiteSpace(m.Resolution),
                $"MediationResult for {m.Conflict.Type} has empty Resolution");
        }
    }

    // ──────────────────────────────────────────────
    // Outcome has correct proposal reference
    // ──────────────────────────────────────────────

    [Fact]
    public async Task NegotiateAsync_AnyOutcome_ProposalReferencePreserved()
    {
        var agent = CreateAgent();
        var proposalId = Guid.NewGuid();
        var proposal = new RecommendationProposal
        {
            Id = proposalId,
            ActionType = "test-action",
            EstimatedMonthlyCost = 10m,
            RequiredSla = 99.0m,
            TargetRegion = "eastus",
        };
        var constraints = new PolicyConstraints
        {
            MaxMonthlyCost = 500m,
            MaxSla = 99.9m,
            RequiredDataResidency = "eastus",
        };

        var outcome = await agent.NegotiateAsync(proposal, constraints);

        Assert.Equal(proposalId, outcome.Proposal.Id);
    }
}
