using Atlas.AgentOrchestrator.Agents;
using Microsoft.Extensions.Logging.Abstractions;

namespace Atlas.AgentOrchestrator.Tests.Unit;

/// <summary>
/// Unit tests for BestPracticeEngine — specifically the built-in rule fallback
/// when no database is available, and evaluation correctness for key rules.
/// </summary>
public class BestPracticeEngineTests
{
    // ─────────────────────────────────────────────────────────────────────────
    // Rule loading
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task LoadRulesAsync_WithNullDataSource_FallsBackToBuiltInRules()
    {
        var engine = new BestPracticeEngine(NullLogger<BestPracticeEngine>.Instance, dataSource: null);

        await engine.LoadRulesAsync("test", CancellationToken.None);

        var rules = engine.GetLoadedRules();
        Assert.NotEmpty(rules);
    }

    [Fact]
    public async Task LoadRulesAsync_BuiltInRules_CoverAllMajorSources()
    {
        var engine = new BestPracticeEngine(NullLogger<BestPracticeEngine>.Instance, dataSource: null);

        await engine.LoadRulesAsync("test", CancellationToken.None);

        var sources = engine.GetLoadedRules().Select(r => r.Source).Distinct().ToHashSet();
        Assert.Contains("WAF", sources);
        Assert.Contains("PSRule", sources);
        Assert.Contains("AzureQuickReview", sources);
        Assert.Contains("ArchitectureCenter", sources);
    }

    [Fact]
    public async Task LoadRulesAsync_BuiltInRules_ContainAtLeast90Rules()
    {
        var engine = new BestPracticeEngine(NullLogger<BestPracticeEngine>.Instance, dataSource: null);

        await engine.LoadRulesAsync("test", CancellationToken.None);

        Assert.True(engine.GetLoadedRules().Count >= 90,
            $"Expected at least 90 built-in rules but found {engine.GetLoadedRules().Count}");
    }

    [Fact]
    public async Task LoadRulesAsync_BuiltInRules_AllRulesHaveRequiredFields()
    {
        var engine = new BestPracticeEngine(NullLogger<BestPracticeEngine>.Instance, dataSource: null);

        await engine.LoadRulesAsync("test", CancellationToken.None);

        foreach (var rule in engine.GetLoadedRules())
        {
            Assert.False(string.IsNullOrWhiteSpace(rule.RuleId), $"Rule missing RuleId: {rule.Name}");
            Assert.False(string.IsNullOrWhiteSpace(rule.Name), $"Rule missing Name: {rule.RuleId}");
            Assert.False(string.IsNullOrWhiteSpace(rule.Severity), $"Rule {rule.RuleId} missing Severity");
            Assert.False(string.IsNullOrWhiteSpace(rule.RemediationGuidance), $"Rule {rule.RuleId} missing RemediationGuidance");
            Assert.Contains(rule.Severity, new[] { "Critical", "High", "Medium", "Low" });
        }
    }

    [Fact]
    public async Task LoadRulesAsync_CalledTwice_DoesNotDuplicateRules()
    {
        var engine = new BestPracticeEngine(NullLogger<BestPracticeEngine>.Instance, dataSource: null);

        await engine.LoadRulesAsync("test", CancellationToken.None);
        int firstCount = engine.GetLoadedRules().Count;

        await engine.LoadRulesAsync("test", CancellationToken.None);
        int secondCount = engine.GetLoadedRules().Count;

        Assert.Equal(firstCount, secondCount);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Evaluation — Security
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task EvaluateAsync_WafSec001_ReturnsViolation_WhenPublicNetworkAccessEnabled()
    {
        var engine = BuildLoadedEngine();
        await engine.LoadRulesAsync("test", CancellationToken.None);

        var resource = ResourceWithProps("Microsoft.Storage/storageAccounts", new()
        {
            ["publicNetworkAccess"] = "Enabled"
        });

        var results = await engine.EvaluateResourcesAsync(new[] { resource }, CancellationToken.None);

        Assert.Contains(results, r => r.RuleId == "WAF-SEC-001" && !r.IsCompliant);
    }

    [Fact]
    public async Task EvaluateAsync_WafSec001_ReturnsCompliant_WhenPublicNetworkDisabled()
    {
        var engine = BuildLoadedEngine();
        await engine.LoadRulesAsync("test", CancellationToken.None);

        var resource = ResourceWithProps("Microsoft.Storage/storageAccounts", new()
        {
            ["publicNetworkAccess"] = "Disabled"
        });

        var sec001 = (await engine.EvaluateResourcesAsync(new[] { resource }, CancellationToken.None))
            .FirstOrDefault(r => r.RuleId == "WAF-SEC-001");

        Assert.NotNull(sec001);
        Assert.True(sec001.IsCompliant);
    }

    [Fact]
    public async Task EvaluateAsync_PsRuleAcr001_ReturnsViolation_WhenAdminUserEnabled()
    {
        var engine = BuildLoadedEngine();
        await engine.LoadRulesAsync("test", CancellationToken.None);

        var resource = ResourceWithProps("Microsoft.ContainerRegistry/registries", new()
        {
            ["adminUserEnabled"] = "true"
        });

        var result = (await engine.EvaluateResourcesAsync(new[] { resource }, CancellationToken.None))
            .FirstOrDefault(r => r.RuleId == "PSRULE-ACR-001");

        Assert.NotNull(result);
        Assert.False(result.IsCompliant);
    }

    [Fact]
    public async Task EvaluateAsync_PsRuleRedis001_ReturnsViolation_WhenNonSslPortEnabled()
    {
        var engine = BuildLoadedEngine();
        await engine.LoadRulesAsync("test", CancellationToken.None);

        var resource = ResourceWithProps("Microsoft.Cache/redis", new()
        {
            ["enableNonSslPort"] = "true"
        });

        var result = (await engine.EvaluateResourcesAsync(new[] { resource }, CancellationToken.None))
            .FirstOrDefault(r => r.RuleId == "PSRULE-REDIS-001");

        Assert.NotNull(result);
        Assert.False(result.IsCompliant);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Evaluation — Operations / Tags
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task EvaluateAsync_AqrTag001_ReturnsViolation_WhenRequiredTagsMissing()
    {
        var engine = BuildLoadedEngine();
        await engine.LoadRulesAsync("test", CancellationToken.None);

        var resource = new BestPracticeResourceInfo
        {
            AzureResourceId = "/subscriptions/sub/resourceGroups/rg/providers/Microsoft.Web/sites/myapp",
            ResourceName = "myapp",
            ResourceType = "Microsoft.Web/sites",
            Tags = new Dictionary<string, string>(), // no tags
            Properties = new Dictionary<string, object>()
        };

        var result = (await engine.EvaluateResourcesAsync(new[] { resource }, CancellationToken.None))
            .FirstOrDefault(r => r.RuleId == "AQR-TAG-001");

        Assert.NotNull(result);
        Assert.False(result.IsCompliant);
    }

    [Fact]
    public async Task EvaluateAsync_AqrTag001_ReturnsCompliant_WhenAllRequiredTagsPresent()
    {
        var engine = BuildLoadedEngine();
        await engine.LoadRulesAsync("test", CancellationToken.None);

        var resource = new BestPracticeResourceInfo
        {
            AzureResourceId = "/subscriptions/sub/resourceGroups/rg/providers/Microsoft.Web/sites/myapp",
            ResourceName = "myapp",
            ResourceType = "Microsoft.Web/sites",
            Tags = new Dictionary<string, string>
            {
                ["costCentre"] = "ENG-001",
                ["owner"] = "team-atlas",
                ["environment"] = "prod"
            },
            Properties = new Dictionary<string, object>()
        };

        var result = (await engine.EvaluateResourcesAsync(new[] { resource }, CancellationToken.None))
            .FirstOrDefault(r => r.RuleId == "AQR-TAG-001");

        Assert.NotNull(result);
        Assert.True(result.IsCompliant);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────────

    private static BestPracticeEngine BuildLoadedEngine()
        => new(NullLogger<BestPracticeEngine>.Instance, dataSource: null);

    private static BestPracticeResourceInfo ResourceWithProps(
        string resourceType,
        Dictionary<string, object> props)
        => new()
        {
            AzureResourceId = $"/subscriptions/sub/resourceGroups/rg/providers/{resourceType}/testResource",
            ResourceName = "testResource",
            ResourceType = resourceType,
            Tags = new Dictionary<string, string>(),
            Properties = props
        };
}
