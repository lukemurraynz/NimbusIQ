using Atlas.ControlPlaneApi.Domain.Entities;
using Atlas.ControlPlaneApi.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Atlas.ControlPlaneApi.Tests.Integration.Recommendations;

/// <summary>
/// T029: Integration test for dual-control approval workflow
/// Validates that recommendations require two independent approvals
/// </summary>
public class DualControlApprovalTests : IAsyncLifetime
{
    private AtlasDbContext? _dbContext;
    private readonly string _connectionString;

    public DualControlApprovalTests()
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
    public async Task Recommendation_RequiresTwoApprovals_BeforeExecution()
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
            Confidence = 0.85m,
            Status = "pending",
            Rationale = "Migrate to managed services",
            Impact = "Reduces operational burden",
            ProposedChanges = "Move from VMs to Container Apps",
            CreatedAt = DateTime.UtcNow
        };

        _dbContext!.ServiceGroups.Add(serviceGroup);
        _dbContext.Recommendations.Add(recommendation);
        await _dbContext.SaveChangesAsync();

        // Act - First approval
        var decision1 = new RecommendationDecision
        {
            Id = Guid.NewGuid(),
            RecommendationId = recommendation.Id,
            Decision = "approved",
            Rationale = "Architecture improvement aligns with strategy",
            SubmittedBy = "user1@example.com",
            SubmittedAt = DateTime.UtcNow
        };

        _dbContext.RecommendationDecisions.Add(decision1);
        await _dbContext.SaveChangesAsync();

        // Assert - Still pending after first approval
        var afterFirstApproval = await _dbContext.Recommendations.FindAsync(recommendation.Id);
        Assert.NotNull(afterFirstApproval);
        Assert.Equal("pending_second_approval", afterFirstApproval.Status);

        var decisions = await _dbContext.RecommendationDecisions
            .Where(d => d.RecommendationId == recommendation.Id)
            .ToListAsync();
        Assert.Single(decisions);

        // Act - Second approval (different user)
        var decision2 = new RecommendationDecision
        {
            Id = Guid.NewGuid(),
            RecommendationId = recommendation.Id,
            Decision = "approved",
            Rationale = "Reviewed and approved",
            SubmittedBy = "user2@example.com",
            SubmittedAt = DateTime.UtcNow
        };

        _dbContext.RecommendationDecisions.Add(decision2);
        await _dbContext.SaveChangesAsync();

        // Assert - Approved after second approval
        var afterSecondApproval = await _dbContext.Recommendations.FindAsync(recommendation.Id);
        Assert.NotNull(afterSecondApproval);
        Assert.Equal("approved", afterSecondApproval.Status);

        var allDecisions = await _dbContext.RecommendationDecisions
            .Where(d => d.RecommendationId == recommendation.Id)
            .ToListAsync();
        Assert.Equal(2, allDecisions.Count);
    }

    [Fact]
    public async Task Recommendation_RejectedByEitherApprover_PreventsFurtherApprovals()
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
            Category = "FinOps",
            Confidence = 0.75m,
            Status = "pending",
            Rationale = "Optimize storage SKU",
            Impact = "Reduces cost by 30%",
            ProposedChanges = "Change from Premium_LRS to Standard_LRS",
            CreatedAt = DateTime.UtcNow
        };

        _dbContext!.ServiceGroups.Add(serviceGroup);
        _dbContext.Recommendations.Add(recommendation);
        await _dbContext.SaveChangesAsync();

        // Act - First user rejects
        var decision1 = new RecommendationDecision
        {
            Id = Guid.NewGuid(),
            RecommendationId = recommendation.Id,
            Decision = "rejected",
            Rationale = "Current SKU required for performance SLA",
            SubmittedBy = "user1@example.com",
            SubmittedAt = DateTime.UtcNow
        };

        _dbContext.RecommendationDecisions.Add(decision1);
        await _dbContext.SaveChangesAsync();

        // Assert - Immediately rejected (no second approval needed)
        var afterRejection = await _dbContext.Recommendations.FindAsync(recommendation.Id);
        Assert.NotNull(afterRejection);
        Assert.Equal("rejected", afterRejection.Status);

        // Verify audit trail captured rejection rationale
        var decisions = await _dbContext.RecommendationDecisions
            .Where(d => d.RecommendationId == recommendation.Id)
            .ToListAsync();
        Assert.Single(decisions);
        Assert.Equal("rejected", decisions[0].Decision);
        Assert.Contains("performance SLA", decisions[0].Rationale);
    }

    [Fact]
    public async Task Recommendation_CannotBeApprovedBySameUserTwice()
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
            Category = "Reliability",
            Confidence = 0.90m,
            Status = "pending",
            Rationale = "Add health checks",
            Impact = "Improves uptime",
            ProposedChanges = "Add liveness and readiness probes",
            CreatedAt = DateTime.UtcNow
        };

        _dbContext!.ServiceGroups.Add(serviceGroup);
        _dbContext.Recommendations.Add(recommendation);
        await _dbContext.SaveChangesAsync();

        // Act - First approval
        var decision1 = new RecommendationDecision
        {
            Id = Guid.NewGuid(),
            RecommendationId = recommendation.Id,
            Decision = "approved",
            Rationale = "Important reliability improvement",
            SubmittedBy = "user1@example.com",
            SubmittedAt = DateTime.UtcNow
        };

        _dbContext.RecommendationDecisions.Add(decision1);
        await _dbContext.SaveChangesAsync();

        // Act - Attempt second approval by same user
        var decision2 = new RecommendationDecision
        {
            Id = Guid.NewGuid(),
            RecommendationId = recommendation.Id,
            Decision = "approved",
            Rationale = "Still approved",
            SubmittedBy = "user1@example.com", // Same user
            SubmittedAt = DateTime.UtcNow
        };

        // Assert - Should fail validation (business rule: different users required)
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            _dbContext.RecommendationDecisions.Add(decision2);
            await _dbContext.SaveChangesAsync();
        });

        // Verify still in pending_second_approval state
        var current = await _dbContext.Recommendations.FindAsync(recommendation.Id);
        Assert.NotNull(current);
        Assert.Equal("pending_second_approval", current.Status);
    }

    [Fact]
    public async Task Recommendation_AuditTrail_CapturesAllDecisions()
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
            Category = "Sustainability",
            Confidence = 0.70m,
            Status = "pending",
            Rationale = "Use green regions",
            Impact = "Reduces carbon footprint",
            ProposedChanges = "Migrate to francecentral",
            CreatedAt = DateTime.UtcNow
        };

        _dbContext!.ServiceGroups.Add(serviceGroup);
        _dbContext.Recommendations.Add(recommendation);
        await _dbContext.SaveChangesAsync();

        // Act - Two approvals from different users
        var decision1 = new RecommendationDecision
        {
            Id = Guid.NewGuid(),
            RecommendationId = recommendation.Id,
            Decision = "approved",
            Rationale = "Aligns with sustainability goals",
            SubmittedBy = "sustainability-lead@example.com",
            SubmittedAt = DateTime.UtcNow
        };

        var decision2 = new RecommendationDecision
        {
            Id = Guid.NewGuid(),
            RecommendationId = recommendation.Id,
            Decision = "approved",
            Rationale = "Migration plan validated",
            SubmittedBy = "ops-lead@example.com",
            SubmittedAt = DateTime.UtcNow.AddMinutes(5)
        };

        _dbContext.RecommendationDecisions.Add(decision1);
        await _dbContext.SaveChangesAsync();
        
        _dbContext.RecommendationDecisions.Add(decision2);
        await _dbContext.SaveChangesAsync();

        // Assert - Complete audit trail captured
        var auditTrail = await _dbContext.RecommendationDecisions
            .Where(d => d.RecommendationId == recommendation.Id)
            .OrderBy(d => d.SubmittedAt)
            .ToListAsync();

        Assert.Equal(2, auditTrail.Count);
        Assert.Equal("sustainability-lead@example.com", auditTrail[0].SubmittedBy);
        Assert.Equal("ops-lead@example.com", auditTrail[1].SubmittedBy);
        Assert.True(auditTrail[1].SubmittedAt > auditTrail[0].SubmittedAt);
    }
}
