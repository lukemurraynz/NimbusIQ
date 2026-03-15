using Atlas.ControlPlane.Application.Services;
using Atlas.ControlPlane.Domain.Entities;
using Atlas.ControlPlane.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Atlas.ControlPlane.Tests.Unit.Application.Services;

public class ScoreSimulationServiceTests : IDisposable
{
    private readonly AtlasDbContext _db;
    private readonly ScoreSimulationService _sut;
    private readonly Guid _serviceGroupId = Guid.NewGuid();

    public ScoreSimulationServiceTests()
    {
        var options = new DbContextOptionsBuilder<AtlasDbContext>()
            .UseInMemoryDatabase(databaseName: $"SimulationTests_{Guid.NewGuid()}")
            .Options;

        _db = new AtlasDbContext(options);

        _db.ServiceGroups.Add(new ServiceGroup
        {
            Id = _serviceGroupId,
            ExternalKey = "test-sg",
            Name = "Test Service Group"
        });
        _db.SaveChanges();

        _sut = new ScoreSimulationService(_db, NullLogger<ScoreSimulationService>.Instance);
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task SimulateAsync_WithPositiveImpact_IncreasesProjectedScores()
    {
        SeedBaselineScores(70);

        var changes = new List<HypotheticalChange>
        {
            new() { ChangeType = "optimize", Category = "Reliability", Description = "Add redundancy", EstimatedImpact = 10 }
        };

        var result = await _sut.SimulateAsync(_serviceGroupId, changes);

        Assert.Equal(70, result.CurrentScores["Reliability"]);
        Assert.Equal(80, result.ProjectedScores["Reliability"]);
        Assert.Equal(10, result.Deltas["Reliability"]);
    }

    [Fact]
    public async Task SimulateAsync_WithNegativeImpact_DecreasesProjectedScores()
    {
        SeedBaselineScores(70);

        var changes = new List<HypotheticalChange>
        {
            new() { ChangeType = "migrate", Category = "FinOps", Description = "Switch to premium tier", EstimatedImpact = -15 }
        };

        var result = await _sut.SimulateAsync(_serviceGroupId, changes);

        Assert.Equal(55, result.ProjectedScores["FinOps"]);
        Assert.Equal(-15, result.Deltas["FinOps"]);
    }

    [Fact]
    public async Task SimulateAsync_ClampsScoreTo0_100()
    {
        SeedBaselineScores(95);

        var changes = new List<HypotheticalChange>
        {
            new() { ChangeType = "optimize", Category = "Architecture", Description = "Massive improvement", EstimatedImpact = 20 }
        };

        var result = await _sut.SimulateAsync(_serviceGroupId, changes);

        Assert.Equal(100, result.ProjectedScores["Architecture"]);
    }

    [Fact]
    public async Task SimulateAsync_WithNoScoreData_UsesDefaultBaseline()
    {
        var changes = new List<HypotheticalChange>
        {
            new() { ChangeType = "optimize", Category = "Reliability", Description = "Add HA", EstimatedImpact = 5 }
        };

        var result = await _sut.SimulateAsync(_serviceGroupId, changes);

        // Default baseline is 50 when no scores exist
        Assert.Equal(50.0, result.CurrentScores["Reliability"]);
        Assert.Equal(55, result.ProjectedScores["Reliability"]);
    }

    [Fact]
    public async Task SimulateAsync_MultipleChanges_AggregatesImpact()
    {
        SeedBaselineScores(60);

        var changes = new List<HypotheticalChange>
        {
            new() { ChangeType = "optimize", Category = "Reliability", Description = "Add caching", EstimatedImpact = 5 },
            new() { ChangeType = "optimize", Category = "Reliability", Description = "Add monitoring", EstimatedImpact = 8 }
        };

        var result = await _sut.SimulateAsync(_serviceGroupId, changes);

        Assert.Equal(73, result.ProjectedScores["Reliability"]);
        Assert.Equal(13, result.Deltas["Reliability"]);
    }

    [Fact]
    public async Task SimulateAsync_RiskDeltas_ReflectScoreChanges()
    {
        SeedBaselineScores(70);

        var changes = new List<HypotheticalChange>
        {
            new() { ChangeType = "optimize", Category = "Reliability", Description = "Improve", EstimatedImpact = 10 },
            new() { ChangeType = "migrate", Category = "FinOps", Description = "Degrade", EstimatedImpact = -8 }
        };

        var result = await _sut.SimulateAsync(_serviceGroupId, changes);

        Assert.Equal("reduced", result.RiskDeltas["Reliability"].RiskLevel);
        Assert.False(result.RiskDeltas["Reliability"].MitigationNeeded);

        Assert.Equal("increased", result.RiskDeltas["FinOps"].RiskLevel);
        Assert.True(result.RiskDeltas["FinOps"].MitigationNeeded);
    }

    [Fact]
    public async Task SimulateAsync_ConfidenceDecreasesWithMoreChanges()
    {
        SeedBaselineScores(70);

        var fewChanges = new List<HypotheticalChange>
        {
            new() { ChangeType = "optimize", Category = "Reliability", Description = "One", EstimatedImpact = 5 }
        };

        var manyChanges = Enumerable.Range(0, 8).Select(i => new HypotheticalChange
        {
            ChangeType = "optimize",
            Category = "Reliability",
            Description = $"Change {i}",
            EstimatedImpact = 2
        }).ToList();

        var fewResult = await _sut.SimulateAsync(_serviceGroupId, fewChanges);
        var manyResult = await _sut.SimulateAsync(_serviceGroupId, manyChanges);

        Assert.True(fewResult.Confidence > manyResult.Confidence,
            $"Few changes confidence ({fewResult.Confidence}) should exceed many changes ({manyResult.Confidence})");
    }

    [Fact]
    public async Task SimulateAsync_CostDeltas_PopulatedFromRecommendations()
    {
        SeedBaselineScores(70);
        SeedRecommendationsWithCosts();

        var changes = new List<HypotheticalChange>
        {
            new() { ChangeType = "optimize", Category = "FinOps", Description = "Cut costs", EstimatedImpact = 5 }
        };

        var result = await _sut.SimulateAsync(_serviceGroupId, changes);

        Assert.True(result.CostDeltas.ContainsKey("FinOps"));
        Assert.True(result.CostDeltas["FinOps"].EstimatedMonthlySavings >= 0);
    }

    [Fact]
    public async Task SimulateAsync_CostDeltas_ParsesNumericStringsInEstimatedImpact()
    {
        SeedBaselineScores(70);
        _db.Recommendations.Add(new Recommendation
        {
            Id = Guid.NewGuid(),
            ServiceGroupId = _serviceGroupId,
            CorrelationId = Guid.NewGuid(),
            AnalysisRunId = Guid.Empty,
            ResourceId = "/subscriptions/test/resourceGroups/test",
            Title = "Cost optimization (string values)",
            Category = "FinOps",
            Status = "pending",
            Priority = "high",
            RecommendationType = "rule_based",
            ActionType = "optimize",
            TargetEnvironment = "prod",
            Description = "Reduce cost",
            Rationale = "Save money",
            Impact = "Cost reduction",
            ProposedChanges = "Downsize VMs",
            Summary = "Cost savings opportunity",
            ApprovalMode = "single",
            Confidence = 0.9m,
            EstimatedImpact = """{"monthlySavings":"500","implementationCost":"200"}""",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        _db.SaveChanges();

        var changes = new List<HypotheticalChange>
        {
            new() { ChangeType = "optimize", Category = "FinOps", Description = "Cut costs", EstimatedImpact = 3 }
        };

        var result = await _sut.SimulateAsync(_serviceGroupId, changes);

        Assert.True(result.CostDeltas.ContainsKey("FinOps"));
        Assert.True(result.CostDeltas["FinOps"].EstimatedMonthlySavings > 0);
        Assert.True(result.CostDeltas["FinOps"].EstimatedImplementationCost > 0);
    }

    [Fact]
    public async Task SimulateAsync_ReasoningMessage_ReflectsImpactLevel()
    {
        SeedBaselineScores(70);

        var positiveChanges = new List<HypotheticalChange>
        {
            new() { ChangeType = "optimize", Category = "Architecture", Description = "Major", EstimatedImpact = 8 },
            new() { ChangeType = "optimize", Category = "Reliability", Description = "Major", EstimatedImpact = 5 }
        };

        var result = await _sut.SimulateAsync(_serviceGroupId, positiveChanges);

        Assert.Contains("improvement", result.Reasoning, StringComparison.OrdinalIgnoreCase);
    }

    private void SeedBaselineScores(double score)
    {
        foreach (var cat in new[] { "Architecture", "FinOps", "Reliability", "Sustainability" })
        {
            _db.ScoreSnapshots.Add(new ScoreSnapshot
            {
                Id = Guid.NewGuid(),
                ServiceGroupId = _serviceGroupId,
                Category = cat,
                Score = score,
                Confidence = 0.85,
                ResourceCount = 10,
                RecordedAt = DateTime.UtcNow,
                CreatedAt = DateTime.UtcNow
            });
        }
        _db.SaveChanges();
    }

    private void SeedRecommendationsWithCosts()
    {
        _db.Recommendations.Add(new Recommendation
        {
            Id = Guid.NewGuid(),
            ServiceGroupId = _serviceGroupId,
            CorrelationId = Guid.NewGuid(),
            AnalysisRunId = Guid.Empty,
            ResourceId = "/subscriptions/test/resourceGroups/test",
            Title = "Cost optimization",
            Category = "FinOps",
            Status = "pending",
            Priority = "high",
            RecommendationType = "rule_based",
            ActionType = "optimize",
            TargetEnvironment = "prod",
            Description = "Reduce cost",
            Rationale = "Save money",
            Impact = "Cost reduction",
            ProposedChanges = "Downsize VMs",
            Summary = "Cost savings opportunity",
            ApprovalMode = "single",
            Confidence = 0.9m,
            EstimatedImpact = """{"monthlySavings":500,"implementationCost":200}""",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        _db.SaveChanges();
    }
}
