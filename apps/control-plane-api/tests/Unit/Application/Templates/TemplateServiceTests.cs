using Atlas.ControlPlane.Application.Templates;
using Atlas.ControlPlane.Domain.Entities;
using Atlas.ControlPlane.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Atlas.ControlPlane.Tests.Unit.Application.Templates;

public class TemplateServiceTests : IDisposable
{
    private readonly AtlasDbContext _db;
    private readonly TemplateService _sut;
    private readonly Guid _serviceGroupId = Guid.NewGuid();
    private readonly Guid _recommendationId = Guid.NewGuid();
    private readonly Guid _templateId = Guid.NewGuid();

    public TemplateServiceTests()
    {
        var options = new DbContextOptionsBuilder<AtlasDbContext>()
            .UseInMemoryDatabase(databaseName: $"TemplateTests_{Guid.NewGuid()}")
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
            Title = "Rightsize VM",
            Category = "FinOps",
            Description = "Reduce VM size",
            Summary = "Optimize VM cost",
            RecommendationType = "configure",
            ActionType = "modify",
            Priority = "medium",
            Status = "pending",
            Confidence = 0.9m,
            Rationale = "Underutilized",
            Impact = "$100/month",
            ProposedChanges = "Change SKU",
            ApprovalMode = "single",
            RequiredApprovals = 1,
            ReceivedApprovals = 0,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        var template = new RecommendationTemplate
        {
            Id = _templateId,
            TemplateName = "vm-rightsize",
            Category = "FinOps",
            ProblemPattern = "Underutilized VM",
            SolutionPattern = "Resize to smaller SKU",
            IacTemplate = "vm {{vmName}} sku {{targetSku}}",
            EstimatedSavingsRange = 100,
            TypicalRiskScore = 0.2m,
            UsageCount = 0,
            AverageSuccessRate = 1.0m,
            CreatedBy = "system",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _db.ServiceGroups.Add(serviceGroup);
        _db.Recommendations.Add(recommendation);
        _db.RecommendationTemplates.Add(template);
        _db.SaveChanges();

        _sut = new TemplateService(_db, NullLogger<TemplateService>.Instance);
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task FindApplicableTemplatesAsync_ReturnsTemplatesForCategory()
    {
        // Arrange
        var recommendation = await _db.Recommendations.FindAsync(_recommendationId);

        // Act
        var result = await _sut.FindApplicableTemplatesAsync(recommendation!);

        // Assert
        Assert.NotEmpty(result);
        Assert.Contains(result, t => t.Category == "FinOps");
    }

    [Fact]
    public async Task ApplyTemplateAsync_CreatesUsageRecord()
    {
        // Arrange
        var parameters = new Dictionary<string, string>
        {
            ["vmName"] = "vm1",
            ["targetSku"] = "Standard_D2s_v3"
        };

        // Act
        var result = await _sut.ApplyTemplateAsync(_templateId, _recommendationId, parameters, "test-user");

        // Assert
        Assert.NotNull(result);
        Assert.Equal("vm-rightsize", result.TemplateName);
        Assert.Contains("Standard_D2s_v3", result.GeneratedIac);
    }

    [Fact]
    public async Task GetTemplateLibraryAsync_ReturnsLibraryItems()
    {
        // Act
        var result = await _sut.GetTemplateLibraryAsync(category: "FinOps");

        // Assert
        Assert.NotEmpty(result);
        Assert.All(result, item => Assert.Equal("FinOps", item.Category));
    }
}
