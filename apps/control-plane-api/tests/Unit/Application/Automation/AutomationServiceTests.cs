using Atlas.ControlPlane.Application.Automation;
using Atlas.ControlPlane.Domain.Entities;
using Atlas.ControlPlane.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Atlas.ControlPlane.Tests.Unit.Application.Automation;

public class AutomationServiceTests : IDisposable
{
    private readonly AtlasDbContext _db;
    private readonly AutomationService _sut;
    private readonly Guid _serviceGroupId = Guid.NewGuid();
    private readonly Guid _recommendationId = Guid.NewGuid();
    private readonly Guid _ruleId = Guid.NewGuid();

    public AutomationServiceTests()
    {
        var options = new DbContextOptionsBuilder<AtlasDbContext>()
            .UseInMemoryDatabase(databaseName: $"AutomationTests_{Guid.NewGuid()}")
            .Options;

        _db = new AtlasDbContext(options);

        var serviceGroup = new ServiceGroup
        {
            Id = _serviceGroupId,
            ExternalKey = "test-sg",
            Name = "Test Service Group"
        };

        var recommendation = new Recommendation
        {
            Id = _recommendationId,
            ServiceGroupId = _serviceGroupId,
            CorrelationId = Guid.NewGuid(),
            AnalysisRunId = Guid.NewGuid(),
            ResourceId = "/subscriptions/test/vms/dev-vm1",
            TargetEnvironment = "development",
            Title = "Enable auto-shutdown",
            Category = "FinOps",
            Description = "Auto-shutdown dev VMs",
            Summary = "Reduce dev costs overnight",
            RecommendationType = "configure",
            ActionType = "modify",
            Priority = "low",
            Status = "pending",
            Confidence = 0.95m,
            Rationale = "Dev VMs unused at night",
            Impact = "$200/month",
            ProposedChanges = "Set shutdown schedule",
            ApprovalMode = "single",
            RequiredApprovals = 1,
            ReceivedApprovals = 0,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        var automationRule = new AutomationRule
        {
            Id = _ruleId,
            RuleName = "Auto-approve FinOps low-risk",
            Trigger = "recommendation_created",
            TriggerCriteria = "{\"category\":\"FinOps\"}",
            MaxRiskThreshold = 0.3m,
            MinConfidenceThreshold = 0.9m,
            ActionType = "auto_approve",
            RequiresAttestation = false,
            IsEnabled = true,
            ExecutionCount = 0,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _db.ServiceGroups.Add(serviceGroup);
        _db.Recommendations.Add(recommendation);
        _db.AutomationRules.Add(automationRule);
        _db.SaveChanges();

        _sut = new AutomationService(_db, NullLogger<AutomationService>.Instance);
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task EvaluateForAutomationAsync_WithMatchingRule_ReturnsTrue()
    {
        // Arrange
        var recommendation = await _db.Recommendations.FindAsync(_recommendationId);

        // Act
        var result = await _sut.EvaluateForAutomationAsync(recommendation!, riskScore: 0.2m, trustScore: 0.95m);

        // Assert
        Assert.True(result.ShouldAutoApprove);
        Assert.NotNull(result.MatchedRule);
        Assert.Equal("Auto-approve FinOps low-risk", result.MatchedRule.RuleName);
    }

    [Fact]
    public async Task EvaluateForAutomationAsync_WithHighRisk_ReturnsFalse()
    {
        // Arrange
        var recommendation = await _db.Recommendations.FindAsync(_recommendationId);

        // Act
        var result = await _sut.EvaluateForAutomationAsync(recommendation!, riskScore: 0.8m, trustScore: 0.95m);

        // Assert
        Assert.False(result.ShouldAutoApprove);
        Assert.Null(result.MatchedRule);
    }

    [Fact]
    public async Task ExecuteAutomationAsync_CreatesExecution()
    {
        // Act
        var execution = await _sut.ExecuteAutomationAsync(_recommendationId, _ruleId);

        // Assert
        Assert.NotNull(execution);
        Assert.Equal(_recommendationId, execution.RecommendationId);
        Assert.Equal(_ruleId, execution.AutomationRuleId);
        Assert.Equal("succeeded", execution.Status);

        // Verify recommendation was auto-approved
        var recommendation = await _db.Recommendations.FindAsync(_recommendationId);
        Assert.Equal("approved", recommendation!.Status);
        Assert.StartsWith("automation:", recommendation.ApprovedBy ?? "");
    }
}
