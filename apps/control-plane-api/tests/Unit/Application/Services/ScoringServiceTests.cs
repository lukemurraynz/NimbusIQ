using Atlas.ControlPlane.Application.Services;
using Atlas.ControlPlane.Infrastructure.Azure;
using Microsoft.Extensions.Logging.Abstractions;

namespace Atlas.ControlPlane.Tests.Unit.Application.Services;

public class ScoringServiceTests
{
    private readonly ScoringService _sut = new(NullLogger<ScoringService>.Instance);
    private readonly Guid _correlationId = Guid.NewGuid();

    [Fact]
    public void Calculate_WithNoResources_ReturnsDefaultScores()
    {
        var result = _sut.Calculate([], _correlationId);

        Assert.Equal(ScoreResult.Default, result);
        Assert.Equal(0.0, result.Completeness);
        Assert.Equal(0.0, result.CostEfficiency);
        Assert.Equal(0.0, result.Availability);
        Assert.Equal(0.0, result.Security);
        Assert.Equal(0, result.ResourceCount);
    }

    [Fact]
    public void CalculateCompleteness_AllMetadataPresent_HighScore()
    {
        var resources = new List<DiscoveredAzureResource>
        {
            MakeResource(tags: """{"env":"prod"}""", location: "eastus", sku: """{"name":"S1"}""", kind: "app"),
            MakeResource(tags: """{"env":"dev"}""", location: "westus", sku: """{"name":"S2"}""", kind: "app"),
        };

        var completeness = ScoringService.CalculateCompleteness(resources, resources.Count);

        Assert.Equal(1.0, completeness);
    }

    [Fact]
    public void CalculateSecurityScore_PublicAccessDisabled_ImprovedScore()
    {
        var secureResources = new List<DiscoveredAzureResource>
        {
            MakeResource(sku: """{"publicNetworkAccess":"Disabled"}"""),
            MakeResource(sku: """{"publicNetworkAccess":"Disabled"}"""),
        };

        var unsecureResources = new List<DiscoveredAzureResource>
        {
            MakeResource(sku: """{"publicNetworkAccess":"Enabled"}"""),
            MakeResource(sku: """{"publicNetworkAccess":"Enabled"}"""),
        };

        var secureScore = ScoringService.CalculateSecurityScore(secureResources, secureResources.Count);
        var unsecureScore = ScoringService.CalculateSecurityScore(unsecureResources, unsecureResources.Count);

        Assert.True(secureScore > unsecureScore);
    }

    [Fact]
    public void DetectProductionEnvironment_ProdTagged_ReturnsTrue()
    {
        var resources = new List<DiscoveredAzureResource>
        {
            MakeResource(name: "vm-prod", tags: """{"environment":"prod"}"""),
            MakeResource(name: "vm-prod-2", tags: """{"environment":"production"}"""),
        };

        Assert.True(ScoringService.DetectProductionEnvironment(resources));
    }

    [Fact]
    public void ScoreResult_GetAverageScore_UsesWeightedFormula()
    {
        var result = new ScoreResult(
            Completeness: 0.8,
            CostEfficiency: 0.6,
            Availability: 0.7,
            Security: 0.9,
            ResourceCount: 4);

        var expected = Math.Round((0.9 * 0.35) + (0.7 * 0.30) + (0.6 * 0.20) + (0.8 * 0.15), 4);
        Assert.Equal(expected, result.GetAverageScore());
    }

    [Fact]
    public void ScoreResult_ToJson_ContainsExpectedKeys()
    {
        var result = new ScoreResult(0.8, 0.6, 0.7, 0.9, 2, TaggingCoverage: 0.5, Utilization: 0.7, Resiliency: 0.4, ManagedServiceRatio: 0.6, GreenRegionUsage: 0.8);

        var json = result.ToJson();

        Assert.Contains("completeness", json);
        Assert.Contains("cost_efficiency", json);
        Assert.Contains("availability", json);
        Assert.Contains("security", json);
        Assert.Contains("tagging_coverage", json);
        Assert.Contains("utilization", json);
        Assert.Contains("resiliency", json);
        Assert.Contains("managed_service_ratio", json);
        Assert.Contains("green_region_usage", json);
        Assert.Contains("scored_at", json);
    }

    [Fact]
    public void Calculate_IncludesTaggingUtilizationResiliencyAndGreenRegionSignals()
    {
        var resources = new List<DiscoveredAzureResource>
        {
            MakeResource(
                resourceType: "microsoft.compute/virtualmachines",
                location: "australiaeast",
                tags: """{"environment":"prod","owner":"platform"}""",
                sku: """{"name":"Standard_D2s_v5","tier":"Standard"}""",
                kind: "vm",
                properties: """{"zones":["1"],"publicNetworkAccess":"Disabled"}""")
        };

        var result = _sut.Calculate(resources, _correlationId);

        Assert.True(result.TaggingCoverage > 0);
        Assert.True(result.Utilization > 0);
        Assert.True(result.Resiliency > 0);
        Assert.True(result.ManagedServiceRatio >= 0);
        Assert.True(result.GreenRegionUsage > 0);
    }

    private static DiscoveredAzureResource MakeResource(
        string name = "resource-1",
        string resourceType = "microsoft.compute/virtualmachines",
        string location = "eastus",
        string? tags = """{"env":"prod"}""",
        string? sku = """{"tier":"Standard","name":"S1"}""",
        string? kind = "generic",
        string? resourceGroup = "rg-test",
        string? properties = null)
    {
        return new DiscoveredAzureResource(
            ArmId: $"/subscriptions/test-sub/resourceGroups/{resourceGroup}/providers/{resourceType}/{name}",
            Name: name,
            ResourceType: resourceType,
            Location: location,
            ResourceGroup: resourceGroup,
            SubscriptionId: "test-sub",
            Sku: sku,
            Tags: tags,
            Kind: kind,
            Properties: properties);
    }
}
