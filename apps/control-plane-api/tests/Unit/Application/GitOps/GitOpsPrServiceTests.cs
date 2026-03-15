using Atlas.ControlPlane.Application.GitOps;
using Atlas.ControlPlane.Domain.Entities;
using Atlas.ControlPlane.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Atlas.ControlPlane.Tests.Unit.Application.GitOps;

public class GitOpsPrServiceTests : IDisposable
{
    private readonly AtlasDbContext _db;
    private readonly GitOpsPrService _sut;
    private readonly Guid _serviceGroupId = Guid.NewGuid();
    private readonly Guid _recommendationId = Guid.NewGuid();
    private readonly Guid _changeSetId = Guid.NewGuid();

    public GitOpsPrServiceTests()
    {
        var options = new DbContextOptionsBuilder<AtlasDbContext>()
            .UseInMemoryDatabase(databaseName: $"GitOpsTests_{Guid.NewGuid()}")
            .Options;

        _db = new AtlasDbContext(options);

        var serviceGroup = new ServiceGroup
        {
            Id = _serviceGroupId,
            ExternalKey = "test-sg",
            Name = "Test Service Group"
        };

        var recommendation = new Recommendation
        {
            Id = _recommendationId,
            ServiceGroupId = _serviceGroupId,
            CorrelationId = Guid.NewGuid(),
            AnalysisRunId = Guid.NewGuid(),
            ResourceId = "/subscriptions/test/vms/vm1",
            TargetEnvironment = "production",
            Title = "Update VM SKU",
            Category = "FinOps",
            Description = "Resize VM",
            Summary = "Optimize VM cost",
            RecommendationType = "configure",
            ActionType = "modify",
            Priority = "medium",
            Status = "approved",
            Confidence = 0.9m,
            Rationale = "Reduce costs",
            Impact = "$100/month",
            ProposedChanges = "Change SKU",
            ApprovalMode = "single",
            RequiredApprovals = 1,
            ReceivedApprovals = 1,
            ApprovedBy = "test-user",
            ApprovedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        var changeSet = new IacChangeSet
        {
            Id = _changeSetId,
            RecommendationId = _recommendationId,
            Format = "bicep",
            ArtifactUri = "https://storage.blob/changeset-123.zip",
            PrTitle = "Optimize VM SKU for vm1",
            PrDescription = "Automated change to reduce costs",
            Status = "generated",
            CreatedAt = DateTime.UtcNow
        };

        _db.ServiceGroups.Add(serviceGroup);
        _db.Recommendations.Add(recommendation);
        _db.IacChangeSets.Add(changeSet);
        _db.SaveChanges();

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["GitOps:PreviewMode"] = "true"
            })
            .Build();

        _sut = new GitOpsPrService(_db, NullLogger<GitOpsPrService>.Instance, configuration);
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task CreatePullRequestAsync_CreatesPrForApprovedRecommendation()
    {
        // Act
        var pr = await _sut.CreatePullRequestAsync(
            _recommendationId,
            _changeSetId,
            "https://github.com/org/repo",
            "main");

        // Assert
        Assert.NotNull(pr);
        Assert.Equal(_recommendationId, pr.RecommendationId);
        Assert.Equal(_changeSetId, pr.ChangeSetId);
        Assert.Equal("created", pr.Status);
    }

    [Fact]
    public async Task UpdatePrStatusAsync_UpdatesExistingPr()
    {
        // Arrange
        var pr = await _sut.CreatePullRequestAsync(
            _recommendationId,
            _changeSetId,
            "https://github.com/org/repo");

        // Act
        var updated = await _sut.UpdatePrStatusAsync(pr.Id, "merged", mergeCommitSha: "abc123");

        // Assert
        Assert.NotNull(updated);
        Assert.Equal("merged", updated.Status);
        Assert.NotNull(updated.MergedAt);
    }

    [Fact]
    public async Task ListPullRequestsAsync_FiltersAndSortsPrs()
    {
        // Arrange
        await _sut.CreatePullRequestAsync(_recommendationId, _changeSetId, "https://github.com/org/repo");

        // Act
        var prs = await _sut.ListPullRequestsAsync(status: "created");

        // Assert
        Assert.NotEmpty(prs);
        Assert.All(prs, pr => Assert.Equal("created", pr.Status));
    }
}
