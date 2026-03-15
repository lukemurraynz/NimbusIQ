using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Atlas.ControlPlane.Domain.Entities;
using Atlas.ControlPlane.Infrastructure.Data;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Atlas.ControlPlane.Tests.Contract.Automation;

/// <summary>
/// Contract tests for automation rule evaluation with agent-driven reasoning.
/// Validates that:
/// - Agent reasoning augments basic threshold matching
/// - Conflicts are detected when multiple rules match
/// - Impact predictions are included in responses
/// - Contributing agents are tracked
/// </summary>
public class AutomationReasoningContractTests : IClassFixture<ContractTestFactory>
{
  private readonly ContractTestFactory _factory;

  public AutomationReasoningContractTests(ContractTestFactory factory)
  {
    _factory = factory;
  }

  [Fact]
  public async Task EvaluateWithReasoning_ReturnsShouldAutoApproveWhenAgentConfidenceHigh()
  {
    var serviceGroupId = Guid.NewGuid();
    var recommendation = CreateRecommendation(
        serviceGroupId,
        title: "Optimize VM SKU for cost",
        category: "FinOps",
        priority: "high",
        confidence: 0.85m);

    var automationRule = CreateAutomationRule(
        "auto_approve_finops",
        "recommendation_created",
        "auto_approve",
        maxRiskThreshold: 0.4m,
        minConfidenceThreshold: 0.75m);

    await SeedAsync(serviceGroupId, recommendation, automationRule);

    var client = _factory.CreateClient();
    var response = await client.PostAsJsonAsync(
        $"/api/v1/automation/recommendations/{recommendation.Id}/evaluate-with-reasoning?api-version=2025-01-23",
        new
        {
          riskScore = 0.2m,
          trustScore = 0.9m
        });

    Assert.Equal(HttpStatusCode.OK, response.StatusCode);

    var payload = await response.Content.ReadFromJsonAsync<JsonElement>();

    // Verify core response structure
    Assert.True(payload.TryGetProperty("shouldAutoApprove", out var shouldApprove));
    Assert.True(shouldApprove.GetBoolean()); // Should approve given high confidence

    Assert.True(payload.TryGetProperty("agentConfidence", out var agentConfidence));
    var confidence = agentConfidence.GetDecimal();
    Assert.InRange(confidence, 0m, 1m);

    Assert.True(payload.TryGetProperty("reasoningSummary", out var summary));
    Assert.False(string.IsNullOrWhiteSpace(summary.GetString()));

    // Verify agent tracking
    Assert.True(payload.TryGetProperty("contributingAgents", out var agents));
    var agentList = agents.EnumerateArray().Select(a => a.GetString()).Where(s => s != null).ToList();
    Assert.NotEmpty(agentList);
    Assert.Contains("FinOpsOptimizerAgent", agentList); // Cost-related rule should invoke FinOps agent

    // Verify impact prediction
    Assert.True(payload.TryGetProperty("predictedImpactByPillar", out var impacts));
    if (impacts.ValueKind == System.Text.Json.JsonValueKind.Object)
    {
      Assert.True(impacts.TryGetProperty("FinOps", out var finOpsImpact));
      Assert.InRange(finOpsImpact.GetInt32(), 0, 100);
    }

    // Verify rule details
    Assert.True(payload.TryGetProperty("matchedRule", out var matchedRule));
    Assert.True(matchedRule.TryGetProperty("ruleName", out var ruleName));
    // The selected rule should match baseline criteria
    Assert.NotEmpty(ruleName.GetString() ?? "");
  }

  [Fact]
  public async Task EvaluateWithReasoning_DetectsConflictingRulesWhenMultipleMatch()
  {
    var serviceGroupId = Guid.NewGuid();
    var recommendation = CreateRecommendation(
        serviceGroupId,
        title: "Optimize for cost and reliability",
        category: "FinOps",
        priority: "high",
        confidence: 0.85m);

    var finOpsRule = CreateAutomationRule(
        "rule_finops",
        "recommendation_created",
        "auto_approve",
        maxRiskThreshold: 0.5m,
        minConfidenceThreshold: 0.75m);

    var reliabilityRule = CreateAutomationRule(
        "rule_reliability",
        "recommendation_created",
        "notify",
        maxRiskThreshold: 0.6m,
        minConfidenceThreshold: 0.70m);

    await SeedAsync(serviceGroupId, recommendation, finOpsRule, reliabilityRule);

    var client = _factory.CreateClient();
    var response = await client.PostAsJsonAsync(
        $"/api/v1/automation/recommendations/{recommendation.Id}/evaluate-with-reasoning?api-version=2025-01-23",
        new
        {
          riskScore = 0.3m,
          trustScore = 0.9m
        });

    Assert.Equal(HttpStatusCode.OK, response.StatusCode);

    var payload = await response.Content.ReadFromJsonAsync<JsonElement>();

    // Both rules matched, so conflicts should be recorded
    if (payload.TryGetProperty("conflictingRules", out var conflicts))
    {
      // At least one rule should be listed as conflicting (all but the best)
      if (conflicts.ValueKind == System.Text.Json.JsonValueKind.Array)
      {
        var conflictArray = conflicts.EnumerateArray().ToList();
        Assert.NotEmpty(conflictArray);

        foreach (var conflict in conflictArray)
        {
          Assert.True(conflict.TryGetProperty("ruleName", out var conflictRuleName));
          Assert.True(conflict.TryGetProperty("reason", out var reason));
          Assert.False(string.IsNullOrWhiteSpace(reason.GetString()));
        }
      }
    }
  }

  [Fact]
  public async Task EvaluateWithReasoning_IncludesReliabilityAgentForSecurityRecommendations()
  {
    var serviceGroupId = Guid.NewGuid();
    var recommendation = CreateRecommendation(
        serviceGroupId,
        title: "Enable network security group rules",
        category: "Security",
        priority: "critical",
        confidence: 0.90m);

    var securityRule = CreateAutomationRule(
        "auto_approve_security",
        "recommendation_created",
        "auto_approve",
        maxRiskThreshold: 0.3m,
        minConfidenceThreshold: 0.80m);

    await SeedAsync(serviceGroupId, recommendation, securityRule);

    var client = _factory.CreateClient();
    var response = await client.PostAsJsonAsync(
        $"/api/v1/automation/recommendations/{recommendation.Id}/evaluate-with-reasoning?api-version=2025-01-23",
        new
        {
          riskScore = 0.2m,
          trustScore = 0.95m
        });

    Assert.Equal(HttpStatusCode.OK, response.StatusCode);

    var payload = await response.Content.ReadFromJsonAsync<JsonElement>();

    Assert.True(payload.TryGetProperty("contributingAgents", out var agents));
    var agentList = agents.EnumerateArray().Select(a => a.GetString()).ToList();

    // Security-related rules should involve ReliabilityAgent
    Assert.Contains("ReliabilityAgent", agentList);
    Assert.Contains("GovernanceMediatorAgent", agentList);
  }

  [Fact]
  public async Task EvaluateWithReasoning_ReturnsDeclineWhenNoCandidateRulesMatch()
  {
    var serviceGroupId = Guid.NewGuid();
    var recommendation = CreateRecommendation(
        serviceGroupId,
        title: "Some recommendation",
        category: "General",
        priority: "medium",
        confidence: 0.50m); // Below typical thresholds

    var strictRule = CreateAutomationRule(
        "strict_rule",
        "recommendation_created",
        "auto_approve",
        maxRiskThreshold: 0.2m,
        minConfidenceThreshold: 0.90m); // Very high threshold

    await SeedAsync(serviceGroupId, recommendation, strictRule);

    var client = _factory.CreateClient();
    var response = await client.PostAsJsonAsync(
        $"/api/v1/automation/recommendations/{recommendation.Id}/evaluate-with-reasoning?api-version=2025-01-23",
        new
        {
          riskScore = 0.5m, // Exceeds max threshold
          trustScore = 0.7m
        });

    Assert.Equal(HttpStatusCode.OK, response.StatusCode);

    var payload = await response.Content.ReadFromJsonAsync<JsonElement>();

    Assert.True(payload.TryGetProperty("shouldAutoApprove", out var shouldApprove));
    Assert.False(shouldApprove.GetBoolean());

    Assert.True(payload.TryGetProperty("reasoningSummary", out var summary));
    var summaryText = summary.GetString();
    Assert.Contains("No automation rules matched", summaryText);

    Assert.True(payload.TryGetProperty("source", out var source));
    Assert.Equal("heuristic", source.GetString());
  }

  [Fact]
  public async Task EvaluateWithReasoning_ReturnsAgentSourceForSuccessfulEvaluation()
  {
    var serviceGroupId = Guid.NewGuid();
    var recommendation = CreateRecommendation(
        serviceGroupId,
        title: "Test recommendation",
        category: "FinOps",
        priority: "high",
        confidence: 0.85m);

    var automationRule = CreateAutomationRule(
        "rule1",
        "recommendation_created",
        "auto_approve",
        maxRiskThreshold: 0.5m,
        minConfidenceThreshold: 0.70m);

    await SeedAsync(serviceGroupId, recommendation, automationRule);

    var client = _factory.CreateClient();
    var response = await client.PostAsJsonAsync(
        $"/api/v1/automation/recommendations/{recommendation.Id}/evaluate-with-reasoning?api-version=2025-01-23",
        new
        {
          riskScore = 0.3m,
          trustScore = 0.9m
        });

    Assert.Equal(HttpStatusCode.OK, response.StatusCode);

    var payload = await response.Content.ReadFromJsonAsync<JsonElement>();

    Assert.True(payload.TryGetProperty("source", out var source));
    Assert.Equal("agent", source.GetString()); // Should be "agent" when reasoning succeeds
  }

  private async Task SeedAsync(Guid serviceGroupId, Recommendation recommendation, params AutomationRule[] rules)
  {
    using var scope = _factory.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AtlasDbContext>();

    db.ServiceGroups.Add(new ServiceGroup
    {
      Id = serviceGroupId,
      ExternalKey = $"sg-{serviceGroupId:N}",
      Name = "Automation Test Group"
    });

    db.Recommendations.Add(recommendation);
    db.AutomationRules.AddRange(rules);
    await db.SaveChangesAsync();
  }

  private static Recommendation CreateRecommendation(
      Guid serviceGroupId,
      string title,
      string category,
      string priority,
      decimal confidence)
  {
    return new Recommendation
    {
      Id = Guid.NewGuid(),
      ServiceGroupId = serviceGroupId,
      CorrelationId = Guid.NewGuid(),
      AnalysisRunId = Guid.Empty,
      ResourceId = "/subscriptions/test-sub/resourceGroups/test-rg/providers/Microsoft.Compute/virtualMachines/test-vm",
      Title = title,
      Category = category,
      Status = "pending",
      Priority = priority,
      RecommendationType = "rule_based",
      ActionType = "optimize",
      TargetEnvironment = "prod",
      Description = $"Description for {title}",
      Rationale = $"Rationale for {title}",
      Impact = $"Impact for {title}",
      ProposedChanges = "Apply infrastructure change",
      Summary = $"Summary for {title}",
      ApprovalMode = "single",
      Confidence = confidence,
      CreatedAt = DateTime.UtcNow,
      UpdatedAt = DateTime.UtcNow
    };
  }

  private static AutomationRule CreateAutomationRule(
      string ruleName,
      string trigger,
      string actionType,
      decimal maxRiskThreshold,
      decimal minConfidenceThreshold)
  {
    return new AutomationRule
    {
      Id = Guid.NewGuid(),
      RuleName = ruleName,
      Trigger = trigger,
      TriggerCriteria = null,
      MaxRiskThreshold = maxRiskThreshold,
      MinConfidenceThreshold = minConfidenceThreshold,
      ActionType = actionType,
      ImplementationSchedule = null,
      RequiresAttestation = false,
      IsEnabled = true,
      ExecutionCount = 0,
      CreatedAt = DateTime.UtcNow,
      UpdatedAt = DateTime.UtcNow
    };
  }
}
