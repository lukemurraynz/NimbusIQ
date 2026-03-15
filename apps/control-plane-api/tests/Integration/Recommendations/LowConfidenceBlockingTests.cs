using Atlas.ControlPlaneApi.Domain.Entities;
using Atlas.ControlPlaneApi.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Atlas.ControlPlaneApi.Tests.Integration.Recommendations;

/// <summary>
/// T030: Integration test for low-confidence recommendation blocking
/// Validates that recommendations below confidence threshold are blocked from approval
/// </summary>
public class LowConfidenceBlockingTests : IAsyncLifetime
{
    private AtlasDbContext? _dbContext;
    private readonly string _connectionString;
    private const decimal CONFIDENCE_THRESHOLD = 0.60m; // Recommendations below 60% confidence are blocked

    public LowConfidenceBlockingTests()
    {
        _connectionString = $"Host=localhost;Database=atlas_test_{Guid.NewGuid():N};Username=postgres;Password=test";
    }

    public async Task InitializeAsync()
    {
        var options = new DbContextOptionsBuilder<AtlasDbContext>()
            .UseNpgsql(_connectionString)
            .Options;

        _dbContext = new AtlasDbContext(options);
        await _dbContext.Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync()
    {
        if (_dbContext != null)
        {
            await _dbContext.Database.EnsureDeletedAsync();
            await _dbContext.DisposeAsync();
        }
    }

    [Fact]
    public async Task LowConfidenceRecommendation_BlockedFromApproval()
    {
        // Arrange
        var serviceGroup = new ServiceGroup
        {
            Id = Guid.NewGuid(),
            Name = "test-group",
            Subscription = "sub1",
            ResourceGroup = "rg1"
        };

        var lowConfidenceRecommendation = new Recommendation
        {
            Id = Guid.NewGuid(),
            ServiceGroupId = serviceGroup.Id,
            Category = "Architecture",
            Confidence = 0.45m, // Below threshold (60%)
            Status = "pending",
            Rationale = "Uncertain recommendation",
            Impact = "Potential improvement (low confidence)",
            ProposedChanges = "Migrate to unverified pattern",
            CreatedAt = DateTime.UtcNow
        };

        _dbContext!.ServiceGroups.Add(serviceGroup);
        _dbContext.Recommendations.Add(lowConfidenceRecommendation);
        await _dbContext.SaveChangesAsync();

        // Act - Attempt to approve low-confidence recommendation
        var decision = new RecommendationDecision
        {
            Id = Guid.NewGuid(),
            RecommendationId = lowConfidenceRecommendation.Id,
            Decision = "approved",
            Rationale = "Attempting approval despite low confidence",
            SubmittedBy = "user1@example.com",
            SubmittedAt = DateTime.UtcNow
        };

        // Assert - Should fail validation (business rule: confidence >= 60% required)
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            _dbContext.RecommendationDecisions.Add(decision);
            await _dbContext.SaveChangesAsync();
        });

        // Verify recommendation remains in pending state
        var current = await _dbContext.Recommendations.FindAsync(lowConfidenceRecommendation.Id);
        Assert.NotNull(current);
        Assert.Equal("pending", current.Status);

        // Verify no decisions were recorded
        var decisions = await _dbContext.RecommendationDecisions
            .Where(d => d.RecommendationId == lowConfidenceRecommendation.Id)
            .ToListAsync();
        Assert.Empty(decisions);
    }

    [Fact]
    public async Task MediumConfidenceRecommendation_AllowedWithWarning()
    {
        // Arrange
        var serviceGroup = new ServiceGroup
        {
            Id = Guid.NewGuid(),
            Name = "test-group",
            Subscription = "sub1",
            ResourceGroup = "rg1"
        };

        var mediumConfidenceRecommendation = new Recommendation
        {
            Id = Guid.NewGuid(),
            ServiceGroupId = serviceGroup.Id,
            Category = "FinOps",
            Confidence = 0.65m, // Above threshold but below high confidence (80%)
            Status = "pending",
            Rationale = "Moderate confidence cost optimization",
            Impact = "May reduce cost by 15-20%",
            ProposedChanges = "Change storage tier",
            CreatedAt = DateTime.UtcNow,
            Warnings = new List<string> { "Medium confidence - requires extra scrutiny" }
        };

        _dbContext!.ServiceGroups.Add(serviceGroup);
        _dbContext.Recommendations.Add(mediumConfidenceRecommendation);
        await _dbContext.SaveChangesAsync();

        // Act - Approve medium-confidence recommendation (allowed with warning)
        var decision1 = new RecommendationDecision
        {
            Id = Guid.NewGuid(),
            RecommendationId = mediumConfidenceRecommendation.Id,
            Decision = "approved",
            Rationale = "Reviewed with extra scrutiny - approved",
            SubmittedBy = "user1@example.com",
            SubmittedAt = DateTime.UtcNow
        };

        _dbContext.RecommendationDecisions.Add(decision1);
        await _dbContext.SaveChangesAsync();

        // Assert - Status updated to pending_second_approval (dual-control still applies)
        var afterFirstApproval = await _dbContext.Recommendations.FindAsync(mediumConfidenceRecommendation.Id);
        Assert.NotNull(afterFirstApproval);
        Assert.Equal("pending_second_approval", afterFirstApproval.Status);

        // Verify warning is preserved
        Assert.Contains("Medium confidence", afterFirstApproval.Warnings.First());
    }

    [Fact]
    public async Task HighConfidenceRecommendation_ApprovedNormally()
    {
        // Arrange
        var serviceGroup = new ServiceGroup
        {
            Id = Guid.NewGuid(),
            Name = "test-group",
            Subscription = "sub1",
            ResourceGroup = "rg1"
        };

        var highConfidenceRecommendation = new Recommendation
        {
            Id = Guid.NewGuid(),
            ServiceGroupId = serviceGroup.Id,
            Category = "Reliability",
            Confidence = 0.92m, // High confidence (>= 80%)
            Status = "pending",
            Rationale = "Well-established reliability pattern",
            Impact = "Improves uptime by 99.9%",
            ProposedChanges = "Add health checks and circuit breakers",
            CreatedAt = DateTime.UtcNow
        };

        _dbContext!.ServiceGroups.Add(serviceGroup);
        _dbContext.Recommendations.Add(highConfidenceRecommendation);
        await _dbContext.SaveChangesAsync();

        // Act - Two approvals
        var decision1 = new RecommendationDecision
        {
            Id = Guid.NewGuid(),
            RecommendationId = highConfidenceRecommendation.Id,
            Decision = "approved",
            Rationale = "Strong confidence - approved",
            SubmittedBy = "user1@example.com",
            SubmittedAt = DateTime.UtcNow
        };

        var decision2 = new RecommendationDecision
        {
            Id = Guid.NewGuid(),
            RecommendationId = highConfidenceRecommendation.Id,
            Decision = "approved",
            Rationale = "Second approval - high confidence validated",
            SubmittedBy = "user2@example.com",
            SubmittedAt = DateTime.UtcNow
        };

        _dbContext.RecommendationDecisions.Add(decision1);
        await _dbContext.SaveChangesAsync();
        
        _dbContext.RecommendationDecisions.Add(decision2);
        await _dbContext.SaveChangesAsync();

        // Assert - Fully approved
        var final = await _dbContext.Recommendations.FindAsync(highConfidenceRecommendation.Id);
        Assert.NotNull(final);
        Assert.Equal("approved", final.Status);
    }

    [Fact]
    public async Task LowConfidenceRecommendation_CanBeRejected()
    {
        // Arrange
        var serviceGroup = new ServiceGroup
        {
            Id = Guid.NewGuid(),
            Name = "test-group",
            Subscription = "sub1",
            ResourceGroup = "rg1"
        };

        var lowConfidenceRecommendation = new Recommendation
        {
            Id = Guid.NewGuid(),
            ServiceGroupId = serviceGroup.Id,
            Category = "Sustainability",
            Confidence = 0.35m, // Very low confidence
            Status = "pending",
            Rationale = "Speculative green region migration",
            Impact = "Unknown impact on operations",
            ProposedChanges = "Migrate to experimental region",
            CreatedAt = DateTime.UtcNow
        };

        _dbContext!.ServiceGroups.Add(serviceGroup);
        _dbContext.Recommendations.Add(lowConfidenceRecommendation);
        await _dbContext.SaveChangesAsync();

        // Act - Reject low-confidence recommendation (rejection always allowed)
        var decision = new RecommendationDecision
        {
            Id = Guid.NewGuid(),
            RecommendationId = lowConfidenceRecommendation.Id,
            Decision = "rejected",
            Rationale = "Too low confidence - needs more data",
            SubmittedBy = "user1@example.com",
            SubmittedAt = DateTime.UtcNow
        };

        _dbContext.RecommendationDecisions.Add(decision);
        await _dbContext.SaveChangesAsync();

        // Assert - Successfully rejected
        var rejected = await _dbContext.Recommendations.FindAsync(lowConfidenceRecommendation.Id);
        Assert.NotNull(rejected);
        Assert.Equal("rejected", rejected.Status);
    }

    [Theory]
    [InlineData(0.59, false)] // Just below threshold
    [InlineData(0.60, true)]  // At threshold
    [InlineData(0.61, true)]  // Just above threshold
    [InlineData(0.80, true)]  // High confidence
    [InlineData(0.95, true)]  // Very high confidence
    public async Task Recommendation_ConfidenceThreshold_EnforcedCorrectly(decimal confidence, bool shouldAllowApproval)
    {
        // Arrange
        var serviceGroup = new ServiceGroup
        {
            Id = Guid.NewGuid(),
            Name = "test-group",
            Subscription = "sub1",
            ResourceGroup = "rg1"
        };

        var recommendation = new Recommendation
        {
            Id = Guid.NewGuid(),
            ServiceGroupId = serviceGroup.Id,
            Category = "Architecture",
            Confidence = confidence,
            Status = "pending",
            Rationale = $"Test recommendation with {confidence:P0} confidence",
            Impact = "Test impact",
            ProposedChanges = "Test changes",
            CreatedAt = DateTime.UtcNow
        };

        _dbContext!.ServiceGroups.Add(serviceGroup);
        _dbContext.Recommendations.Add(recommendation);
        await _dbContext.SaveChangesAsync();

        // Act
        var decision = new RecommendationDecision
        {
            Id = Guid.NewGuid(),
            RecommendationId = recommendation.Id,
            Decision = "approved",
            Rationale = "Test approval",
            SubmittedBy = "user1@example.com",
            SubmittedAt = DateTime.UtcNow
        };

        // Assert
        if (shouldAllowApproval)
        {
            // Should succeed
            _dbContext.RecommendationDecisions.Add(decision);
            await _dbContext.SaveChangesAsync();

            var updated = await _dbContext.Recommendations.FindAsync(recommendation.Id);
            Assert.NotNull(updated);
            Assert.NotEqual("pending", updated.Status); // Should be pending_second_approval
        }
        else
        {
            // Should fail
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                _dbContext.RecommendationDecisions.Add(decision);
                await _dbContext.SaveChangesAsync();
            });
        }
    }
}
