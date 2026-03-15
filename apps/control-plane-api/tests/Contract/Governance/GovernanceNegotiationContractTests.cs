using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Atlas.ControlPlane.Domain.Entities;
using Atlas.ControlPlane.Infrastructure.Data;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Atlas.ControlPlane.Tests.Contract.Governance;

public class GovernanceNegotiationContractTests : IClassFixture<ContractTestFactory>
{
    private readonly ContractTestFactory _factory;

    public GovernanceNegotiationContractTests(ContractTestFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Negotiate_ReturnsAgentReasoningAndSwotForTwoRecommendations()
    {
        var serviceGroupId = Guid.NewGuid();
        var recommendationA = CreateRecommendation(serviceGroupId, "Enable diagnostics", "Reliability", "high");
        var recommendationB = CreateRecommendation(serviceGroupId, "Right-size compute", "FinOps", "critical");

        await SeedAsync(serviceGroupId, recommendationA, recommendationB);

        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/v1/governance/negotiate", new
        {
            serviceGroupId,
            conflictIds = new[] { recommendationA.Id, recommendationB.Id },
            preferences = new Dictionary<string, string>
            {
                ["priorityPillar"] = "Reliability",
                ["naturalLanguageContext"] = "Prefer safer rollout sequencing."
            }
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.True(payload.TryGetProperty("resolution", out _));
        Assert.True(payload.TryGetProperty("confidence", out var confidence));
        Assert.InRange(confidence.GetDouble(), 0, 1);
        Assert.True(payload.TryGetProperty("reasoning", out _));
        Assert.True(payload.TryGetProperty("agentReasoningSource", out var source));
        Assert.False(string.IsNullOrWhiteSpace(source.GetString()));

        var compromises = payload.GetProperty("compromises");
        Assert.Equal(JsonValueKind.Array, compromises.ValueKind);
        Assert.Equal(2, compromises.GetArrayLength());

        foreach (var compromise in compromises.EnumerateArray())
        {
            Assert.True(compromise.TryGetProperty("recommendationId", out _));
            Assert.True(compromise.TryGetProperty("tradeoff", out _));
            Assert.True(compromise.TryGetProperty("pillar", out _));
            Assert.True(compromise.TryGetProperty("swot", out var swot));
            Assert.True(swot.TryGetProperty("strengths", out var strengths));
            Assert.True(swot.TryGetProperty("weaknesses", out var weaknesses));
            Assert.True(swot.TryGetProperty("opportunities", out var opportunities));
            Assert.True(swot.TryGetProperty("threats", out var threats));
            Assert.Equal(JsonValueKind.Array, strengths.ValueKind);
            Assert.Equal(JsonValueKind.Array, weaknesses.ValueKind);
            Assert.Equal(JsonValueKind.Array, opportunities.ValueKind);
            Assert.Equal(JsonValueKind.Array, threats.ValueKind);
        }
    }

    [Fact]
    public async Task Negotiate_RejectsRequestsThatDoNotContainExactlyTwoRecommendations()
    {
        var serviceGroupId = Guid.NewGuid();
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/v1/governance/negotiate", new
        {
            serviceGroupId,
            conflictIds = new[] { Guid.NewGuid() }
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private async Task SeedAsync(Guid serviceGroupId, params Recommendation[] recommendations)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AtlasDbContext>();

        db.ServiceGroups.Add(new ServiceGroup
        {
            Id = serviceGroupId,
            ExternalKey = $"sg-{serviceGroupId:N}",
            Name = "Governance Test Group"
        });

        db.Recommendations.AddRange(recommendations);
        await db.SaveChangesAsync();
    }

    private static Recommendation CreateRecommendation(Guid serviceGroupId, string title, string category, string priority)
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
            Confidence = 0.82m,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
    }
}
