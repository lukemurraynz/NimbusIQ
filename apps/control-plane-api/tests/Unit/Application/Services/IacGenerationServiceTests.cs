using Atlas.ControlPlane.Application.Services;
using Atlas.ControlPlane.Domain.Entities;
using Atlas.ControlPlane.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Atlas.ControlPlane.Tests.Unit.Application.Services;

public class IacGenerationServiceTests : IDisposable
{
    private readonly AtlasDbContext _db;
    private readonly IacGenerationService _sut;

    public IacGenerationServiceTests()
    {
        var options = new DbContextOptionsBuilder<AtlasDbContext>()
            .UseInMemoryDatabase(databaseName: $"IacGenerationTests_{Guid.NewGuid()}")
            .Options;

        _db = new AtlasDbContext(options);
        _sut = new IacGenerationService(_db, NullLogger<IacGenerationService>.Instance);
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task GenerateAsync_WithEligibleRecommendation_CreatesChangeSetAndRollbackPlan()
    {
        // Arrange
        var recommendationId = SeedRecommendation(actionType: "resize", recommendationType: "modernize");

        // Act
        var changeSet = await _sut.GenerateAsync(recommendationId, "bicep");

        // Assert
        Assert.NotEqual(Guid.Empty, changeSet.Id);
        Assert.Equal(recommendationId, changeSet.RecommendationId);
        Assert.Equal("bicep", changeSet.Format);
        Assert.Equal("generated", changeSet.Status);
        Assert.False(string.IsNullOrWhiteSpace(changeSet.ArtifactUri));

        var persistedChangeSet = await _db.IacChangeSets.FindAsync(changeSet.Id);
        Assert.NotNull(persistedChangeSet);

        var rollbackPlan = await _db.RollbackPlans.SingleAsync(plan => plan.IacChangeSetId == changeSet.Id);
        Assert.NotNull(rollbackPlan);
    }

    [Fact]
    public async Task GenerateAsync_WithoutActionType_ThrowsInvalidOperationException()
    {
        // Arrange
        var recommendationId = SeedRecommendation(actionType: null, recommendationType: "modernize");

        // Act / Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _sut.GenerateAsync(recommendationId, "bicep"));

        Assert.Contains("ActionType is required", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private Guid SeedRecommendation(string? actionType, string? recommendationType)
    {
        var serviceGroupId = Guid.NewGuid();
        var analysisRunId = Guid.NewGuid();
        var recommendationId = Guid.NewGuid();

        _db.ServiceGroups.Add(new ServiceGroup
        {
            Id = serviceGroupId,
            ExternalKey = $"sg-{Guid.NewGuid():N}",
            Name = "IaC Generation Test SG",
            Description = "IaC generation test seed",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });

        _db.AnalysisRuns.Add(new AnalysisRun
        {
            Id = analysisRunId,
            ServiceGroupId = serviceGroupId,
            CorrelationId = Guid.NewGuid(),
            TriggeredBy = "unit-test",
            Status = "completed",
            CreatedAt = DateTime.UtcNow
        });

        _db.Recommendations.Add(new Recommendation
        {
            Id = recommendationId,
            ServiceGroupId = serviceGroupId,
            CorrelationId = Guid.NewGuid(),
            AnalysisRunId = analysisRunId,
            ResourceId = "/subscriptions/test/resourceGroups/rg/providers/Microsoft.Compute/virtualMachines/vm-unit",
            Category = "FinOps",
            RecommendationType = recommendationType ?? string.Empty,
            ActionType = actionType ?? string.Empty,
            TargetEnvironment = "prod",
            Title = "Unit generation recommendation",
            Description = "Unit generation recommendation",
            Rationale = "Reduce costs",
            Impact = "Improves cost efficiency",
            ProposedChanges = "Resize the VM SKU",
            Summary = "Unit generation summary",
            Confidence = 0.95m,
            ApprovalMode = "single",
            Status = "approved",
            Priority = "high",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });

        _db.SaveChanges();
        return recommendationId;
    }
}