using Atlas.ControlPlane.Application.Release;
using Atlas.ControlPlane.Domain.Entities;
using Atlas.ControlPlane.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Atlas.ControlPlane.Tests.Unit.Application.Release;

public class ReleaseAttestationServiceTests : IDisposable
{
    private readonly AtlasDbContext _db;
    private readonly ReleaseAttestationService _sut;

    public ReleaseAttestationServiceTests()
    {
        var options = new DbContextOptionsBuilder<AtlasDbContext>()
            .UseInMemoryDatabase($"release_attestation_tests_{Guid.NewGuid():N}")
            .Options;

        _db = new AtlasDbContext(options);
        _sut = new ReleaseAttestationService(_db, NullLogger<ReleaseAttestationService>.Instance);
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task CreateAttestationAsync_PersistsRealOnlyPassedAttestation_WhenNoMockDetected()
    {
        // Arrange
        var changeSetId = await SeedChangeSetAsync();

        // Act
        var attestation = await _sut.CreateAttestationAsync(
            iacChangeSetId: changeSetId,
            releaseId: "release-2026-03-05",
            componentName: "control-plane-api",
            componentVersion: "1.0.0",
            mockDetected: false,
            mockDetectionDetails: null,
            validationScopeId: "ephemeral-rg-01");

        // Assert
        Assert.Equal(changeSetId, attestation.IacChangeSetId);
        Assert.Equal("release-2026-03-05", attestation.ReleaseId);
        Assert.Equal("real_dependency_validation", attestation.AttestationType);
        Assert.Equal("real_only", attestation.ValidationMode);
        Assert.Equal("passed", attestation.MockDetectionResult);
        Assert.True(attestation.ValidationPassed);
        Assert.Null(attestation.PromotionBlockReason);
        Assert.Equal("ephemeral-rg-01", attestation.ValidationScopeId);
        Assert.True(attestation.CreatedAt <= DateTime.UtcNow);
        Assert.True(attestation.AttestedAt <= DateTime.UtcNow);

        var persisted = await _db.ReleaseAttestations.AsNoTracking().SingleAsync(a => a.Id == attestation.Id);
        Assert.Equal("passed", persisted.MockDetectionResult);
        Assert.True(persisted.ValidationPassed);
    }

    [Fact]
    public async Task ValidateReleaseAsync_ReturnsFalse_WhenAnyComponentAttestationFails()
    {
        // Arrange
        var releaseId = "release-2026-03-05-b";
        var passingChangeSetId = await SeedChangeSetAsync();
        var failingChangeSetId = await SeedChangeSetAsync();

        await _sut.CreateAttestationAsync(
            iacChangeSetId: passingChangeSetId,
            releaseId: releaseId,
            componentName: "control-plane-api",
            componentVersion: "1.0.0",
            mockDetected: false,
            mockDetectionDetails: null);

        await _sut.CreateAttestationAsync(
            iacChangeSetId: failingChangeSetId,
            releaseId: releaseId,
            componentName: "agent-orchestrator",
            componentVersion: "1.0.0",
            mockDetected: true,
            mockDetectionDetails: "Mock endpoint found in release validation");

        // Act
        var releaseValid = await _sut.ValidateReleaseAsync(releaseId);

        // Assert
        Assert.False(releaseValid);
    }

    private async Task<Guid> SeedChangeSetAsync()
    {
        var serviceGroup = new ServiceGroup
        {
            Id = Guid.NewGuid(),
            ExternalKey = $"sg-{Guid.NewGuid():N}",
            Name = "Test Service Group",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        var analysisRun = new AnalysisRun
        {
            Id = Guid.NewGuid(),
            ServiceGroupId = serviceGroup.Id,
            CorrelationId = Guid.NewGuid(),
            TriggeredBy = "unit-test",
            Status = "completed",
            CreatedAt = DateTime.UtcNow
        };

        var recommendation = new Recommendation
        {
            Id = Guid.NewGuid(),
            ServiceGroupId = serviceGroup.Id,
            CorrelationId = Guid.NewGuid(),
            AnalysisRunId = analysisRun.Id,
            ResourceId = "/subscriptions/test/resourceGroups/rg/providers/Microsoft.Storage/storageAccounts/sa1",
            Category = "Architecture",
            RecommendationType = "modernize",
            ActionType = "resize",
            TargetEnvironment = "prod",
            Title = "Scale storage",
            Description = "Increase storage SKU",
            Rationale = "Improve reliability",
            Impact = "Lower risk",
            ProposedChanges = "Switch to zone-redundant storage",
            Summary = "Upgrade storage account",
            Confidence = 0.9m,
            ApprovalMode = "single",
            Status = "approved",
            Priority = "high",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        var content = "resource stg 'Microsoft.Storage/storageAccounts@2023-05-01' = {\n  name: 'examplestorage001'\n  location: resourceGroup().location\n  sku: { name: 'Standard_LRS' }\n  kind: 'StorageV2'\n  properties: {}\n}";

        var changeSet = new IacChangeSet
        {
            Id = Guid.NewGuid(),
            RecommendationId = recommendation.Id,
            Format = "bicep",
            ArtifactUri = "nimbusiq-inline:" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(content)),
            PrTitle = "Test PR",
            PrDescription = "Test",
            Status = "validated",
            CreatedAt = DateTime.UtcNow
        };

        _db.ServiceGroups.Add(serviceGroup);
        _db.AnalysisRuns.Add(analysisRun);
        _db.Recommendations.Add(recommendation);
        _db.IacChangeSets.Add(changeSet);
        await _db.SaveChangesAsync();

        return changeSet.Id;
    }
}
