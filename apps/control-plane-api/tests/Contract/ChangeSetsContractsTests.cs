using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Atlas.ControlPlane.Domain.Entities;
using Atlas.ControlPlane.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Atlas.ControlPlane.Tests.Contract;

public class ChangeSetsContractsTests : IClassFixture<ContractTestFactory>
{
    private readonly ContractTestFactory _factory;
    private readonly HttpClient _client;

    public ChangeSetsContractsTests(ContractTestFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task PublishChangeSet_WithoutReleaseId_ReturnsBadRequestWithErrorCode()
    {
        // Arrange
        var changeSetId = SeedGeneratedChangeSet();

        // Act
        var response = await _client.PostAsJsonAsync($"/api/v1/change-sets/{changeSetId}/publish", new
        {
            componentName = "control-plane-api",
            componentVersion = "1.0.0"
        });

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(response.Headers.Contains("x-error-code"));
        Assert.Equal("MissingReleaseId", response.Headers.GetValues("x-error-code").FirstOrDefault());

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(problem.TryGetProperty("errorCode", out var errorCodeProperty));
        Assert.Equal("MissingReleaseId", errorCodeProperty.GetString());
    }

    [Fact]
    public async Task GenerateChangeSet_ForEligibleRecommendation_ReturnsAcceptedAndPersistsChangeSet()
    {
        // Arrange
        var recommendationId = SeedRecommendationWithoutChangeSet(status: "approved");

        // Act
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/recommendations/{recommendationId}/change-sets",
            new
            {
                format = "bicep"
            });

        // Assert
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.True(response.Headers.Contains("operation-location"));

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.TryGetProperty("id", out var idProperty));
        Assert.True(body.TryGetProperty("recommendationId", out var recommendationIdProperty));
        Assert.True(body.TryGetProperty("format", out var formatProperty));
        Assert.True(body.TryGetProperty("status", out var statusProperty));
        Assert.Equal(recommendationId, recommendationIdProperty.GetGuid());
        Assert.Equal("bicep", formatProperty.GetString());
        Assert.Equal("generated", statusProperty.GetString());

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AtlasDbContext>();
        var changeSetId = idProperty.GetGuid();
        var changeSet = await db.IacChangeSets.FindAsync(changeSetId);
        Assert.NotNull(changeSet);
        Assert.Equal(recommendationId, changeSet!.RecommendationId);
    }

    [Fact]
    public async Task GenerateChangeSet_ForRejectedRecommendation_ReturnsBadRequest()
    {
        // Arrange
        var recommendationId = SeedRecommendationWithoutChangeSet(status: "rejected");

        // Act
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/recommendations/{recommendationId}/change-sets",
            new
            {
                format = "bicep"
            });

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(response.Headers.Contains("x-error-code"));
        Assert.Equal("InvalidRecommendationStatus", response.Headers.GetValues("x-error-code").FirstOrDefault());
    }

    [Fact]
    public async Task PublishChangeSet_WithAttestationMetadata_PublishesAndPersistsAttestation()
    {
        // Arrange
        var changeSetId = SeedGeneratedChangeSet();

        // Act
        var response = await _client.PostAsJsonAsync($"/api/v1/change-sets/{changeSetId}/publish", new
        {
            releaseId = "release-contract-1",
            componentName = "control-plane-api",
            componentVersion = "1.0.0",
            mockDetected = false,
            validationScopeId = "ephemeral-rg-contract"
        });

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.TryGetProperty("status", out var statusProperty));
        Assert.Equal("published", statusProperty.GetString());
        Assert.True(body.TryGetProperty("attestationId", out var attestationIdProperty));
        Assert.NotEqual(Guid.Empty, attestationIdProperty.GetGuid());

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AtlasDbContext>();

        var changeSet = await db.IacChangeSets.FindAsync(changeSetId);
        Assert.NotNull(changeSet);
        Assert.Equal("published", changeSet!.Status);

        var attestation = await db.ReleaseAttestations
            .SingleAsync(a => a.IacChangeSetId == changeSetId && a.ReleaseId == "release-contract-1");

        Assert.Equal("real_dependency_validation", attestation.AttestationType);
        Assert.Equal("real_only", attestation.ValidationMode);
        Assert.Equal("passed", attestation.MockDetectionResult);
        Assert.True(attestation.ValidationPassed);
    }

    [Fact]
    public async Task PublishChangeSet_WhenMockDetected_BlocksPromotion()
    {
        // Arrange
        var changeSetId = SeedGeneratedChangeSet();

        // Act
        var response = await _client.PostAsJsonAsync($"/api/v1/change-sets/{changeSetId}/publish", new
        {
            releaseId = "release-contract-2",
            componentName = "control-plane-api",
            componentVersion = "1.0.0",
            mockDetected = true,
            mockDetectionDetails = "Mock endpoint detected during release validation"
        });

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(response.Headers.Contains("x-error-code"));
        Assert.Equal("ReleaseAttestationFailed", response.Headers.GetValues("x-error-code").FirstOrDefault());

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AtlasDbContext>();

        var changeSet = await db.IacChangeSets.FindAsync(changeSetId);
        Assert.NotNull(changeSet);
        Assert.Equal("failed", changeSet!.Status);

        var attestation = await db.ReleaseAttestations
            .SingleAsync(a => a.IacChangeSetId == changeSetId && a.ReleaseId == "release-contract-2");
        Assert.False(attestation.ValidationPassed);
        Assert.Equal("failed", attestation.MockDetectionResult);
    }

    [Fact]
    public async Task ValidateChangeSet_ReturnsValidationResult()
    {
        // Arrange
        var changeSetId = SeedGeneratedChangeSet();

        // Act
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/change-sets/{changeSetId}/validate",
            new { });

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.TryGetProperty("passed", out var passedProperty));
        Assert.True(body.TryGetProperty("errors", out var errorsProperty));
        Assert.True(body.TryGetProperty("warnings", out var warningsProperty));

        Assert.Equal(JsonValueKind.Array, errorsProperty.ValueKind);
        Assert.Equal(JsonValueKind.Array, warningsProperty.ValueKind);

        // Validation must move the status to validated or failed
        Assert.True(body.TryGetProperty("status", out var statusProperty));
        Assert.True(statusProperty.GetString() is "validated" or "failed");
    }

    [Fact]
    public async Task ValueRealization_ReturnsPendingWhenNoScoresCaptured()
    {
        // Arrange
        var changeSetId = SeedGeneratedChangeSet();

        // Publish to capture baseline
        var publish = await _client.PostAsJsonAsync($"/api/v1/change-sets/{changeSetId}/publish", new
        {
            releaseId = "release-contract-3",
            componentName = "control-plane-api",
            componentVersion = "1.0.0",
            mockDetected = false
        });
        Assert.Equal(HttpStatusCode.OK, publish.StatusCode);

        // Act
        var response = await _client.GetAsync($"/api/v1/change-sets/{changeSetId}/value-realization");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.TryGetProperty("status", out var statusProperty));
        Assert.NotNull(statusProperty.GetString());
    }

    private Guid SeedGeneratedChangeSet()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AtlasDbContext>();

        var serviceGroupId = Guid.NewGuid();
        var analysisRunId = Guid.NewGuid();
        var recommendationId = Guid.NewGuid();
        var changeSetId = Guid.NewGuid();

        db.ServiceGroups.Add(new ServiceGroup
        {
            Id = serviceGroupId,
            ExternalKey = $"sg-{Guid.NewGuid():N}",
            Name = "Contract Test SG",
            Description = "Contract test seed",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });

        db.AnalysisRuns.Add(new AnalysisRun
        {
            Id = analysisRunId,
            ServiceGroupId = serviceGroupId,
            CorrelationId = Guid.NewGuid(),
            TriggeredBy = "contract-test",
            Status = "completed",
            CreatedAt = DateTime.UtcNow
        });

        db.Recommendations.Add(new Recommendation
        {
            Id = recommendationId,
            ServiceGroupId = serviceGroupId,
            CorrelationId = Guid.NewGuid(),
            AnalysisRunId = analysisRunId,
            ResourceId = "/subscriptions/test/resourceGroups/rg/providers/Microsoft.Storage/storageAccounts/sa-contract",
            Category = "Architecture",
            RecommendationType = "modernize",
            ActionType = "resize",
            TargetEnvironment = "prod",
            Title = "Contract recommendation",
            Description = "Contract test recommendation",
            Rationale = "Improve platform posture",
            Impact = "Increases reliability",
            ProposedChanges = "Resize and harden resource",
            Summary = "Contract test summary",
            Confidence = 0.92m,
            ApprovalMode = "single",
            Status = "approved",
            Priority = "high",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });

        var bicep = "resource stg 'Microsoft.Storage/storageAccounts@2023-05-01' = {\n  name: 'contractstorage001'\n  location: resourceGroup().location\n  sku: { name: 'Standard_LRS' }\n  kind: 'StorageV2'\n  properties: {}\n}";

        db.IacChangeSets.Add(new IacChangeSet
        {
            Id = changeSetId,
            RecommendationId = recommendationId,
            Format = "bicep",
            ArtifactUri = "nimbusiq-inline:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(bicep)),
            PrTitle = "Contract test PR",
            PrDescription = "Contract test",
            Status = "generated",
            CreatedAt = DateTime.UtcNow
        });

        db.SaveChanges();
        return changeSetId;
    }

    private Guid SeedRecommendationWithoutChangeSet(string status)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AtlasDbContext>();

        var serviceGroupId = Guid.NewGuid();
        var analysisRunId = Guid.NewGuid();
        var recommendationId = Guid.NewGuid();

        db.ServiceGroups.Add(new ServiceGroup
        {
            Id = serviceGroupId,
            ExternalKey = $"sg-{Guid.NewGuid():N}",
            Name = "Generation Contract SG",
            Description = "Generation contract seed",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });

        db.AnalysisRuns.Add(new AnalysisRun
        {
            Id = analysisRunId,
            ServiceGroupId = serviceGroupId,
            CorrelationId = Guid.NewGuid(),
            TriggeredBy = "contract-test",
            Status = "completed",
            CreatedAt = DateTime.UtcNow
        });

        db.Recommendations.Add(new Recommendation
        {
            Id = recommendationId,
            ServiceGroupId = serviceGroupId,
            CorrelationId = Guid.NewGuid(),
            AnalysisRunId = analysisRunId,
            ResourceId = "/subscriptions/test/resourceGroups/rg/providers/Microsoft.Compute/virtualMachines/vm-contract",
            Category = "FinOps",
            RecommendationType = "modernize",
            ActionType = "resize",
            TargetEnvironment = "prod",
            Title = "Generate contract recommendation",
            Description = "Contract generation recommendation",
            Rationale = "Improve platform posture",
            Impact = "Improves cost efficiency",
            ProposedChanges = "Resize the VM SKU",
            Summary = "Generation contract summary",
            Confidence = 0.93m,
            ApprovalMode = "single",
            Status = status,
            Priority = "high",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });

        db.SaveChanges();
        return recommendationId;
    }
}
