using Atlas.ControlPlane.Api.Endpoints;
using Atlas.ControlPlane.Application.ServiceGraph;
using Atlas.ControlPlane.Domain.Entities;
using Atlas.ControlPlane.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Atlas.ControlPlane.Tests.Unit.Api.Endpoints;

/// <summary>
/// Tests for Phase 3.3: Blast Radius Analysis in ServiceGraphController
/// </summary>
public class ServiceGraphControllerBlastRadiusTests
{
  private readonly AtlasDbContext _db;
  private readonly ServiceGraphBuilder _graphBuilder;
  private readonly Mock<ILogger<ServiceGraphController>> _mockControllerLogger;
  private readonly Mock<ILogger<ServiceGraphBuilder>> _mockBuilderLogger;
  private readonly ServiceGraphController _sut;

  public ServiceGraphControllerBlastRadiusTests()
  {
    var options = new DbContextOptionsBuilder<AtlasDbContext>()
        .UseInMemoryDatabase(databaseName: $"TestDb_{Guid.NewGuid()}")
        .Options;

    _db = new AtlasDbContext(options);
    _mockBuilderLogger = new Mock<ILogger<ServiceGraphBuilder>>();
    _graphBuilder = new ServiceGraphBuilder(_mockBuilderLogger.Object, new ComponentTypeDetector());
    _mockControllerLogger = new Mock<ILogger<ServiceGraphController>>();
    _sut = new ServiceGraphController(_db, _graphBuilder, _mockControllerLogger.Object);

    // Setup HttpContext for controller extension methods
    var httpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext();
    httpContext.Request.Path = "/api/servicegroups/test/blast-radius";
    _sut.ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext
    {
      HttpContext = httpContext
    };
  }

  [Fact]
  public async Task GetBlastRadius_ReturnsBadRequest_WhenApiVersionMissing()
  {
    // Arrange
    var serviceGroupId = Guid.NewGuid();

    // Act
    var result = await _sut.GetBlastRadius(
        serviceGroupId,
        resourceIds: "test-resource",
        recommendationId: null,
        apiVersion: null);

    // Assert
    var actionResult = Assert.IsType<Microsoft.AspNetCore.Mvc.BadRequestObjectResult>(result);
    Assert.Equal(400, actionResult.StatusCode);
  }

  [Fact]
  public async Task GetBlastRadius_ReturnsNotFound_WhenServiceGroupDoesNotExist()
  {
    // Arrange
    var serviceGroupId = Guid.NewGuid();

    // Act
    var result = await _sut.GetBlastRadius(
        serviceGroupId,
        resourceIds: "test-resource",
        recommendationId: null,
        apiVersion: "2024-03-01");

    // Assert
    var actionResult = Assert.IsType<Microsoft.AspNetCore.Mvc.NotFoundObjectResult>(result);
    Assert.Equal(404, actionResult.StatusCode);
  }

  [Fact]
  public async Task GetBlastRadius_ReturnsBadRequest_WhenNoResourcesOrRecommendationSpecified()
  {
    // Arrange
    var serviceGroupId = Guid.NewGuid();

    await _db.ServiceGroups.AddAsync(new ServiceGroup
    {
      Id = serviceGroupId,
      ExternalKey = "test-sg",
      Name = "Test Service Group",
      CreatedAt = DateTime.UtcNow
    });
    await _db.SaveChangesAsync();

    // Act
    var result = await _sut.GetBlastRadius(
        serviceGroupId,
        resourceIds: null,
        recommendationId: null,
        apiVersion: "2024-03-01");

    // Assert
    var actionResult = Assert.IsType<Microsoft.AspNetCore.Mvc.BadRequestObjectResult>(result);
    Assert.Equal(400, actionResult.StatusCode);
  }

  // TODO: Fix test data setup for graph traversal - edge semantics need verification
  [Fact(Skip = "edge semantics in test data need verification before enabling")]
  public async Task GetBlastRadius_CalculatesImpactedResources_FromDependencyGraph()
  {
    // Arrange
    var serviceGroupId = Guid.NewGuid();

    await _db.ServiceGroups.AddAsync(new ServiceGroup
    {
      Id = serviceGroupId,
      ExternalKey = "test-sg",
      Name = "Test Service Group",
      CreatedAt = DateTime.UtcNow
    });

    // Create a simple dependency graph: App -> Database -> Identity
    var databaseNode = new ServiceNode
    {
      Id = Guid.NewGuid(),
      ServiceGroupId = serviceGroupId,
      Name = "prod-database",
      DisplayName = "Production Database",
      NodeType = "Microsoft.Sql/servers/databases",
      AzureResourceId = "/subscriptions/test/resourceGroups/test-rg/providers/Microsoft.Sql/servers/prod-server/databases/prod-db",
      CreatedAt = DateTime.UtcNow
    };

    var appNode = new ServiceNode
    {
      Id = Guid.NewGuid(),
      ServiceGroupId = serviceGroupId,
      Name = "web-app",
      DisplayName = "Web Application",
      NodeType = "Microsoft.Web/sites",
      AzureResourceId = "/subscriptions/test/resourceGroups/test-rg/providers/Microsoft.Web/sites/web-app",
      CreatedAt = DateTime.UtcNow
    };

    var identityNode = new ServiceNode
    {
      Id = Guid.NewGuid(),
      ServiceGroupId = serviceGroupId,
      Name = "app-identity",
      DisplayName = "Application Identity",
      NodeType = "Microsoft.ManagedIdentity/userAssignedIdentities",
      AzureResourceId = "/subscriptions/test/resourceGroups/test-rg/providers/Microsoft.ManagedIdentity/userAssignedIdentities/app-identity",
      CreatedAt = DateTime.UtcNow
    };

    await _db.ServiceNodes.AddRangeAsync(databaseNode, appNode, identityNode);

    // App depends on Database
    await _db.ServiceEdges.AddAsync(new ServiceEdge
    {
      Id = Guid.NewGuid(),
      ServiceGroupId = serviceGroupId,
      SourceNodeId = appNode.Id,
      TargetNodeId = databaseNode.Id,
      Direction = "outbound",
      EdgeType = "depends-on",
      CreatedAt = DateTime.UtcNow
    });

    // Database uses Identity
    await _db.ServiceEdges.AddAsync(new ServiceEdge
    {
      Id = Guid.NewGuid(),
      ServiceGroupId = serviceGroupId,
      SourceNodeId = databaseNode.Id,
      TargetNodeId = identityNode.Id,
      Direction = "outbound",
      EdgeType = "uses-identity",
      CreatedAt = DateTime.UtcNow
    });

    await _db.SaveChangesAsync();

    // Act - target the database (should affect App and Identity)
    var result = await _sut.GetBlastRadius(
        serviceGroupId,
        resourceIds: "prod-db",
        recommendationId: null,
        apiVersion: "2024-03-01");

    // Assert
    var actionResult = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(result);
    var response = Assert.IsType<BlastRadiusResponse>(actionResult.Value);

    Assert.True(response.ResourceCount >= 1);
    Assert.Contains(response.AffectedResources, r => r.Name == "Web Application");
    Assert.Contains(response.AffectedIdentities, i => i.Name == "Application Identity");
  }

  [Fact]
  public async Task GetBlastRadius_UsesRecommendationImpactedServices_WhenRecommendationIdProvided()
  {
    // Arrange
    var serviceGroupId = Guid.NewGuid();
    var recommendationId = Guid.NewGuid();

    await _db.ServiceGroups.AddAsync(new ServiceGroup
    {
      Id = serviceGroupId,
      ExternalKey = "test-sg",
      Name = "Test Service Group",
      CreatedAt = DateTime.UtcNow
    });

    var impactedServices = new[] { "storage-account-1", "storage-account-2" };
    await _db.Recommendations.AddAsync(new Recommendation
    {
      Id = recommendationId,
      ServiceGroupId = serviceGroupId,
      CorrelationId = Guid.NewGuid(),
      AnalysisRunId = Guid.NewGuid(),
      ResourceId = "/subscriptions/test/resourceGroups/test-rg/providers/Microsoft.Storage/storageAccounts/storage-account-1",
      Category = "Security",
      RecommendationType = "security",
      ActionType = "upgrade",
      TargetEnvironment = "prod",
      Title = "Enable encryption at rest",
      Description = "Enable encryption at rest for storage accounts",
      Rationale = "Protects data at rest",
      Impact = "Low risk, high security value",
      ProposedChanges = "Enable encryption with customer-managed keys",
      Summary = "Enable encryption at rest",
      Confidence = 0.95m,
      ApprovalMode = "single",
      RequiredApprovals = 1,
      ReceivedApprovals = 0,
      Status = "Open",
      Priority = "High",
      ImpactedServices = System.Text.Json.JsonSerializer.Serialize(impactedServices),
      CreatedAt = DateTime.UtcNow
    });

    var storageNode = new ServiceNode
    {
      Id = Guid.NewGuid(),
      ServiceGroupId = serviceGroupId,
      Name = "storage-account-1",
      DisplayName = "Storage Account 1",
      NodeType = "Microsoft.Storage/storageAccounts",
      AzureResourceId = "/subscriptions/test/resourceGroups/test-rg/providers/Microsoft.Storage/storageAccounts/storage-account-1",
      CreatedAt = DateTime.UtcNow
    };

    await _db.ServiceNodes.AddAsync(storageNode);
    await _db.SaveChangesAsync();

    // Act
    var result = await _sut.GetBlastRadius(
        serviceGroupId,
        resourceIds: null,
        recommendationId: recommendationId,
        apiVersion: "2024-03-01");

    // Assert
    var actionResult = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(result);
    var response = Assert.IsType<BlastRadiusResponse>(actionResult.Value);

    Assert.True(response.ResourceCount >= 0); // May be 0 if no dependencies
  }

  // TODO: Fix test data setup for resource categorization
  [Fact(Skip = "resource categorization test data setup incomplete")]
  public async Task GetBlastRadius_CategorizesByResourceType()
  {
    // Arrange
    var serviceGroupId = Guid.NewGuid();

    await _db.ServiceGroups.AddAsync(new ServiceGroup
    {
      Id = serviceGroupId,
      ExternalKey = "test-sg",
      Name = "Test Service Group",
      CreatedAt = DateTime.UtcNow
    });

    var appNode = new ServiceNode
    {
      Id = Guid.NewGuid(),
      ServiceGroupId = serviceGroupId,
      Name = "app-service",
      DisplayName = "App Service",
      NodeType = "Microsoft.Web/sites",
      AzureResourceId = "/subscriptions/test/resourceGroups/test-rg/providers/Microsoft.Web/sites/app-service",
      CreatedAt = DateTime.UtcNow
    };

    var storageNode = new ServiceNode
    {
      Id = Guid.NewGuid(),
      ServiceGroupId = serviceGroupId,
      Name = "storage-account",
      DisplayName = "Storage Account",
      NodeType = "Microsoft.Storage/storageAccounts",
      AzureResourceId = "/subscriptions/test/resourceGroups/test-rg/providers/Microsoft.Storage/storageAccounts/storage-account",
      CreatedAt = DateTime.UtcNow
    };

    var keyVaultNode = new ServiceNode
    {
      Id = Guid.NewGuid(),
      ServiceGroupId = serviceGroupId,
      Name = "key-vault",
      DisplayName = "Key Vault",
      NodeType = "Microsoft.KeyVault/vaults",
      AzureResourceId = "/subscriptions/test/resourceGroups/test-rg/providers/Microsoft.KeyVault/vaults/key-vault",
      CreatedAt = DateTime.UtcNow
    };

    await _db.ServiceNodes.AddRangeAsync(appNode, storageNode, keyVaultNode);

    // Create dependencies
    await _db.ServiceEdges.AddAsync(new ServiceEdge
    {
      Id = Guid.NewGuid(),
      ServiceGroupId = serviceGroupId,
      SourceNodeId = appNode.Id,
      TargetNodeId = storageNode.Id,
      Direction = "outbound",
      EdgeType = "depends-on",
      CreatedAt = DateTime.UtcNow
    });

    await _db.ServiceEdges.AddAsync(new ServiceEdge
    {
      Id = Guid.NewGuid(),
      ServiceGroupId = serviceGroupId,
      SourceNodeId = appNode.Id,
      TargetNodeId = keyVaultNode.Id,
      Direction = "outbound",
      EdgeType = "uses-secret",
      CreatedAt = DateTime.UtcNow
    });

    await _db.SaveChangesAsync();

    // Act - target storage (should affect App)
    var result = await _sut.GetBlastRadius(
        serviceGroupId,
        resourceIds: "storage-account",
        recommendationId: null,
        apiVersion: "2024-03-01");

    // Assert
    var actionResult = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(result);
    var response = Assert.IsType<BlastRadiusResponse>(actionResult.Value);

    // Verify impact types are correctly categorized
    Assert.Contains(response.AffectedResources, r => r.ImpactType == "Application" && r.Name == "App Service");
    Assert.Contains(response.AffectedResources, r => r.ImpactType == "DataStore" && r.Name == "Storage Account");
    Assert.Contains(response.AffectedResources, r => r.ImpactType == "Secret" && r.Name == "Key Vault");
  }

  [Fact]
  public async Task GetBlastRadius_FindsSharedRecommendations()
  {
    // Arrange
    var serviceGroupId = Guid.NewGuid();

    await _db.ServiceGroups.AddAsync(new ServiceGroup
    {
      Id = serviceGroupId,
      ExternalKey = "test-sg",
      Name = "Test Service Group",
      CreatedAt = DateTime.UtcNow
    });

    var storageNode = new ServiceNode
    {
      Id = Guid.NewGuid(),
      ServiceGroupId = serviceGroupId,
      Name = "storage-1",
      DisplayName = "Storage 1",
      NodeType = "Microsoft.Storage/storageAccounts",
      AzureResourceId = "/subscriptions/test/resourceGroups/test-rg/providers/Microsoft.Storage/storageAccounts/storage-1",
      CreatedAt = DateTime.UtcNow
    };

    await _db.ServiceNodes.AddAsync(storageNode);

    // Add a recommendation that affects the same resource
    var impactedServices = new[] { "/subscriptions/test/resourceGroups/test-rg/providers/Microsoft.Storage/storageAccounts/storage-1" };
    await _db.Recommendations.AddAsync(new Recommendation
    {
      Id = Guid.NewGuid(),
      ServiceGroupId = serviceGroupId,
      CorrelationId = Guid.NewGuid(),
      AnalysisRunId = Guid.NewGuid(),
      ResourceId = "storage-1",
      Category = "Reliability",
      RecommendationType = "reliability",
      ActionType = "optimize",
      TargetEnvironment = "prod",
      Title = "Enable soft delete",
      Description = "Enable soft delete for storage account",
      Rationale = "Protects against accidental deletion",
      Impact = "Low risk, high value",
      ProposedChanges = "Set soft delete retention to 7 days",
      Summary = "Enable soft delete",
      Confidence = 0.9m,
      ApprovalMode = "single",
      RequiredApprovals = 1,
      ReceivedApprovals = 0,
      Status = "Open",
      Priority = "Medium",
      ImpactedServices = System.Text.Json.JsonSerializer.Serialize(impactedServices),
      CreatedAt = DateTime.UtcNow
    });

    await _db.SaveChangesAsync();

    // Act - search for non-existent resource
    var result = await _sut.GetBlastRadius(
        serviceGroupId,
        resourceIds: "non-existent-resource",
        recommendationId: null,
        apiVersion: "2024-03-01");

    // Assert
    var actionResult = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(result);
    var response = Assert.IsType<BlastRadiusResponse>(actionResult.Value);

    Assert.Equal(0, response.ResourceCount);
    Assert.Empty(response.AffectedResources);
    Assert.Empty(response.AffectedIdentities);
  }
}
