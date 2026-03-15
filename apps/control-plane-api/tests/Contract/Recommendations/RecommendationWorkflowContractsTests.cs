using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Atlas.ControlPlane.Domain.Entities;
using Atlas.ControlPlane.Infrastructure.Data;
using Microsoft.Extensions.DependencyInjection;

namespace Atlas.ControlPlane.Tests.Contract.Recommendations;

public class RecommendationWorkflowContractsTests : IClassFixture<ContractTestFactory>
{
    private readonly ContractTestFactory _factory;
    private readonly HttpClient _client;

    public RecommendationWorkflowContractsTests(ContractTestFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task ConfidenceExplainer_ReturnsContractShape()
    {
        var recommendationId = SeedRecommendation();

        var response = await _client.GetAsync($"/api/v1/recommendations/{recommendationId}/confidence-explainer");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.True(body.TryGetProperty("recommendationId", out _));
        Assert.True(body.TryGetProperty("confidenceScore", out _));
        Assert.True(body.TryGetProperty("confidenceSource", out _));
        Assert.True(body.TryGetProperty("trustScore", out _));
        Assert.True(body.TryGetProperty("factors", out var factors));
        Assert.Equal(JsonValueKind.Array, factors.ValueKind);
    }

    [Fact]
    public async Task PolicyImpactSimulation_ReturnsDecisionAndSimulationPayload()
    {
        var recommendationId = SeedRecommendation();

        var response = await _client.PostAsJsonAsync(
            $"/api/v1/recommendations/{recommendationId}/policy-impact-simulation",
            new { policyThreshold = 60 });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.True(body.TryGetProperty("policyDecision", out _));
        Assert.True(body.TryGetProperty("simulation", out var simulation));
        Assert.True(simulation.TryGetProperty("projectedScores", out _));
        Assert.True(simulation.TryGetProperty("riskDeltas", out _));
    }

    [Fact]
    public async Task PolicyImpactSimulation_AcceptsNumericStringImpactHint()
    {
        var recommendationId = SeedRecommendation("{\"scoreImprovement\":\"4.25\"}");

        var response = await _client.PostAsJsonAsync(
            $"/api/v1/recommendations/{recommendationId}/policy-impact-simulation",
            new { policyThreshold = 60 });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.True(body.TryGetProperty("simulation", out var simulation));
        Assert.True(simulation.TryGetProperty("projectedScores", out _));
    }

    [Fact]
    public async Task PriorityQueue_ReturnsRiskWeightedScores()
    {
        SeedRecommendation();

        var response = await _client.GetAsync("/api/v1/recommendations/priority-queue?limit=10");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.True(body.TryGetProperty("value", out var value));
        Assert.Equal(JsonValueKind.Array, value.ValueKind);

        foreach (var item in value.EnumerateArray())
        {
            Assert.True(item.TryGetProperty("riskWeightedScore", out _));
            Assert.True(item.TryGetProperty("reason", out _));
        }
    }

    [Fact]
    public async Task GuardrailLint_ReturnsFindingsForChangeSet()
    {
        var changeSetId = SeedChangeSet();

        var response = await _client.PostAsJsonAsync($"/api/v1/change-sets/{changeSetId}/guardrail-lint", new { });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.True(body.TryGetProperty("passed", out _));
        Assert.True(body.TryGetProperty("findings", out var findings));
        Assert.Equal(JsonValueKind.Array, findings.ValueKind);
    }

    private Guid SeedRecommendation(string? estimatedImpact = null)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AtlasDbContext>();

        var serviceGroupId = Guid.NewGuid();
        var analysisRunId = Guid.NewGuid();
        var recommendationId = Guid.NewGuid();

        db.ServiceGroups.Add(new ServiceGroup
        {
            Id = serviceGroupId,
            ExternalKey = $"sg-{Guid.NewGuid():N}",
            Name = "Workflow Contract SG",
            Description = "Seeded for workflow contract tests",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });

        db.AnalysisRuns.Add(new AnalysisRun
        {
            Id = analysisRunId,
            ServiceGroupId = serviceGroupId,
            CorrelationId = Guid.NewGuid(),
            TriggeredBy = "contract-test",
            Status = "completed",
            CreatedAt = DateTime.UtcNow
        });

        db.Recommendations.Add(new Recommendation
        {
            Id = recommendationId,
            ServiceGroupId = serviceGroupId,
            CorrelationId = Guid.NewGuid(),
            AnalysisRunId = analysisRunId,
            ResourceId = "/subscriptions/test/resourceGroups/rg/providers/Microsoft.Storage/storageAccounts/sa-workflow",
            Category = "FinOps",
            RecommendationType = "cost",
            ActionType = "optimize",
            TargetEnvironment = "prod",
            Title = "Workflow contract recommendation",
            Description = "Seed recommendation for confidence and simulation contracts",
            Rationale = "Reduce cost and risk",
            Impact = "Improves cost efficiency",
            ProposedChanges = "Enable lower SKU and tighten network",
            Summary = "Workflow contract summary",
            Confidence = 0.82m,
            ConfidenceSource = "ai_foundry",
            EstimatedImpact = estimatedImpact,
            EvidenceReferences = "[\"resource-graph://query/123\",\"cost-mgmt://report/456\"]",
            ApprovalMode = "single",
            RequiredApprovals = 1,
            ReceivedApprovals = 0,
            Status = "pending",
            Priority = "high",
            ValidUntil = DateTime.UtcNow.AddDays(4),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });

        db.ScoreSnapshots.AddRange(
            new ScoreSnapshot
            {
                Id = Guid.NewGuid(),
                ServiceGroupId = serviceGroupId,
                Category = "Architecture",
                Score = 70,
                Confidence = 0.8,
                ResourceCount = 10,
                RecordedAt = DateTime.UtcNow,
                CreatedAt = DateTime.UtcNow
            },
            new ScoreSnapshot
            {
                Id = Guid.NewGuid(),
                ServiceGroupId = serviceGroupId,
                Category = "FinOps",
                Score = 62,
                Confidence = 0.8,
                ResourceCount = 10,
                RecordedAt = DateTime.UtcNow,
                CreatedAt = DateTime.UtcNow
            },
            new ScoreSnapshot
            {
                Id = Guid.NewGuid(),
                ServiceGroupId = serviceGroupId,
                Category = "Reliability",
                Score = 75,
                Confidence = 0.8,
                ResourceCount = 10,
                RecordedAt = DateTime.UtcNow,
                CreatedAt = DateTime.UtcNow
            },
            new ScoreSnapshot
            {
                Id = Guid.NewGuid(),
                ServiceGroupId = serviceGroupId,
                Category = "Sustainability",
                Score = 68,
                Confidence = 0.8,
                ResourceCount = 10,
                RecordedAt = DateTime.UtcNow,
                CreatedAt = DateTime.UtcNow
            });

        db.SaveChanges();
        return recommendationId;
    }

    private Guid SeedChangeSet()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AtlasDbContext>();

        var recommendationId = SeedRecommendation();
        var changeSetId = Guid.NewGuid();

        var bicep = "resource stg 'Microsoft.Storage/storageAccounts@2023-05-01' = {\n  name: 'workflowstorage001'\n  location: resourceGroup().location\n  sku: { name: 'Standard_LRS' }\n  kind: 'StorageV2'\n  properties: { publicNetworkAccess: 'Enabled' }\n}";

        db.IacChangeSets.Add(new IacChangeSet
        {
            Id = changeSetId,
            RecommendationId = recommendationId,
            Format = "bicep",
            ArtifactUri = "nimbusiq-inline:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(bicep)),
            PrTitle = "Workflow contract PR",
            PrDescription = "Workflow contract",
            Status = "generated",
            CreatedAt = DateTime.UtcNow
        });

        db.SaveChanges();
        return changeSetId;
    }
}
