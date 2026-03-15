using Atlas.ControlPlane.Application.Services;
using Atlas.ControlPlane.Domain.Entities;
using Atlas.ControlPlane.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Atlas.ControlPlane.Tests.Unit.Application.Services;

public class ExecutiveNarrativeServiceTests : IDisposable
{
    private readonly AtlasDbContext _db;
    private readonly ExecutiveNarrativeService _sut;
    private readonly Guid _serviceGroupId = Guid.NewGuid();

    public ExecutiveNarrativeServiceTests()
    {
        var options = new DbContextOptionsBuilder<AtlasDbContext>()
            .UseInMemoryDatabase(databaseName: $"NarrativeTests_{Guid.NewGuid()}")
            .Options;

        _db = new AtlasDbContext(options);

        _db.ServiceGroups.Add(new ServiceGroup
        {
            Id = _serviceGroupId,
            ExternalKey = "test-sg",
            Name = "Test Service Group"
        });
        _db.SaveChanges();

        // No AI service — tests the rule-based fallback path
        _sut = new ExecutiveNarrativeService(
            _db,
            NullLogger<ExecutiveNarrativeService>.Instance,
            aiChatService: null);
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task GenerateAsync_WithScoresAndRecs_ReturnsRuleBasedNarrative()
    {
        SeedScores();
        SeedRecommendations(pending: 5, critical: 2);

        var result = await _sut.GenerateAsync(_serviceGroupId);

        Assert.Equal("rule_engine", result.ConfidenceSource);
        Assert.Contains("Overall posture score:", result.Summary);
        Assert.Contains("2 high-priority", result.Summary);
        Assert.Contains("5 total pending", result.Summary);
        Assert.NotEmpty(result.Highlights);
    }

    [Fact]
    public async Task GenerateAsync_WithNoData_ReturnsBaselineSummary()
    {
        var result = await _sut.GenerateAsync(_serviceGroupId);

        Assert.Equal("rule_engine", result.ConfidenceSource);
        Assert.Contains("0/100", result.Summary);
        Assert.Empty(result.Highlights);
    }

    [Fact]
    public async Task GenerateAsync_WithDrift_IncludesDriftInfo()
    {
        SeedScores();
        _db.DriftSnapshots.Add(new DriftSnapshot
        {
            Id = Guid.NewGuid(),
            ServiceGroupId = _serviceGroupId,
            SnapshotTime = DateTime.UtcNow,
            DriftScore = 72.5m,
            TotalViolations = 8,
            CreatedAt = DateTime.UtcNow
        });
        _db.SaveChanges();

        var result = await _sut.GenerateAsync(_serviceGroupId);

        Assert.Contains("drift score", result.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("8 violation", result.Summary);
    }

    [Fact]
    public async Task GenerateAsync_Highlights_ReflectScoreTrends()
    {
        _db.ScoreSnapshots.Add(new ScoreSnapshot
        {
            Id = Guid.NewGuid(),
            ServiceGroupId = _serviceGroupId,
            Category = "Reliability",
            Score = 85,
            Confidence = 0.9,
            DeltaFromPrevious = """{"previousScore":70,"delta":15}""",
            ResourceCount = 10,
            RecordedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow
        });
        _db.ScoreSnapshots.Add(new ScoreSnapshot
        {
            Id = Guid.NewGuid(),
            ServiceGroupId = _serviceGroupId,
            Category = "FinOps",
            Score = 40,
            Confidence = 0.8,
            DeltaFromPrevious = """{"previousScore":55,"delta":-15}""",
            ResourceCount = 10,
            RecordedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow
        });
        _db.SaveChanges();

        var result = await _sut.GenerateAsync(_serviceGroupId);

        var reliability = result.Highlights.First(h => h.Category == "Reliability");
        Assert.Equal("improving", reliability.Trend);
        Assert.Equal("success", reliability.Severity);

        var finops = result.Highlights.First(h => h.Category == "FinOps");
        Assert.Equal("declining", finops.Trend);
        Assert.Equal("warning", finops.Severity);
    }

    [Fact]
    public async Task GenerateAsync_TimestampIsRecent()
    {
        var before = DateTime.UtcNow;
        var result = await _sut.GenerateAsync(_serviceGroupId);

        Assert.InRange(result.GeneratedAt, before, DateTime.UtcNow.AddSeconds(5));
    }

    private void SeedScores()
    {
        foreach (var cat in new[] { "Architecture", "FinOps", "Reliability", "Sustainability" })
        {
            _db.ScoreSnapshots.Add(new ScoreSnapshot
            {
                Id = Guid.NewGuid(),
                ServiceGroupId = _serviceGroupId,
                Category = cat,
                Score = 70,
                Confidence = 0.85,
                ResourceCount = 10,
                RecordedAt = DateTime.UtcNow,
                CreatedAt = DateTime.UtcNow
            });
        }
        _db.SaveChanges();
    }

    private void SeedRecommendations(int pending, int critical)
    {
        for (var i = 0; i < critical; i++)
        {
            _db.Recommendations.Add(CreateRecommendation($"Critical Rec {i}", "Reliability", "critical"));
        }

        for (var i = 0; i < pending - critical; i++)
        {
            _db.Recommendations.Add(CreateRecommendation($"Medium Rec {i}", "Architecture", "medium"));
        }
        _db.SaveChanges();
    }

    private Recommendation CreateRecommendation(string title, string category, string priority) => new()
    {
        Id = Guid.NewGuid(),
        ServiceGroupId = _serviceGroupId,
        CorrelationId = Guid.NewGuid(),
        AnalysisRunId = Guid.Empty,
        ResourceId = "/subscriptions/test/resourceGroups/test",
        Title = title,
        Category = category,
        Status = "pending",
        Priority = priority,
        RecommendationType = "rule_based",
        ActionType = "optimize",
        TargetEnvironment = "prod",
        Description = "Test description",
        Rationale = "Test rationale",
        Impact = "Test impact",
        ProposedChanges = "Test changes",
        Summary = "Test summary",
        ApprovalMode = "single",
        Confidence = 0.9m,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    };
}
