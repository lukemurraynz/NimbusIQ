using Atlas.ControlPlane.Api.Endpoints;
using Atlas.ControlPlane.Infrastructure.Data;
using Atlas.ControlPlane.Domain.Entities;
using Atlas.ControlPlane.Application.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Atlas.ControlPlane.Tests.Unit.Api.Endpoints;

/// <summary>
/// Tests for Phase 3.2: Score Explainability features in ScoresController
/// </summary>
public class ScoresControllerExplainabilityTests
{
  private readonly AtlasDbContext _db;
  private readonly Mock<ILogger<ScoreSimulationService>> _mockLogger;
  private readonly ScoreSimulationService _simulationService;
  private readonly ScoresController _sut;

  public ScoresControllerExplainabilityTests()
  {
    var options = new DbContextOptionsBuilder<AtlasDbContext>()
        .UseInMemoryDatabase(databaseName: $"TestDb_{Guid.NewGuid()}")
        .Options;

    _db = new AtlasDbContext(options);
    _mockLogger = new Mock<ILogger<ScoreSimulationService>>();
    _simulationService = new ScoreSimulationService(_db, _mockLogger.Object);
    var mockCompletenessAnalyzer = new Mock<CompletenessAnalyzer>(new Mock<ILogger<CompletenessAnalyzer>>().Object);
    var mockScoresControllerLogger = new Mock<ILogger<ScoresController>>();
    var mockInsightService = new Mock<IImpactFactorInsightService>();
    _sut = new ScoresController(_db, _simulationService, mockCompletenessAnalyzer.Object, mockScoresControllerLogger.Object, mockInsightService.Object);

    // Setup HttpContext for controller extension methods
    var httpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext();
    httpContext.Request.Path = "/api/servicegroups/test/scores/Security/explainability";
    _sut.ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext
    {
      HttpContext = httpContext
    };
  }

  [Fact]
  public async Task GetExplainabilityAsync_ReturnsNotFound_WhenNoScoreExists()
  {
    // Arrange
    var serviceGroupId = Guid.NewGuid();
    var category = "Security";

    // Act
    var result = await _sut.GetExplainabilityAsync(
        serviceGroupId,
        category,
        targetScore: 80.0,
        apiVersion: "2024-03-01");

    // Assert
    var actionResult = Assert.IsType<Microsoft.AspNetCore.Mvc.NotFoundObjectResult>(result.Result);
    Assert.Equal(404, actionResult.StatusCode);
  }

  [Fact]
  public async Task GetExplainabilityAsync_ReturnsBadRequest_WhenApiVersionMissing()
  {
    // Arrange
    var serviceGroupId = Guid.NewGuid();
    var category = "Security";

    // Act
    var result = await _sut.GetExplainabilityAsync(
        serviceGroupId,
        category,
        targetScore: 80.0,
        apiVersion: null);

    // Assert
    var actionResult = Assert.IsType<Microsoft.AspNetCore.Mvc.BadRequestObjectResult>(result.Result);
    Assert.Equal(400, actionResult.StatusCode);
  }

  [Fact]
  public async Task GetExplainabilityAsync_ReturnsExplainability_WithWafPillarsAndPathToTarget()
  {
    // Arrange
    var serviceGroupId = Guid.NewGuid();
    var category = "Security";
    var analysisRunId = Guid.NewGuid();

    await _db.ServiceGroups.AddAsync(new ServiceGroup
    {
      Id = serviceGroupId,
      ExternalKey = "test-sg",
      Name = "Test Service Group",
      CreatedAt = DateTime.UtcNow
    });

    var dimensions = new
    {
      wafPillars = new Dictionary<string, double>
      {
        ["Security"] = 0.65,
        ["Reliability"] = 0.72
      },
      dimensions = new Dictionary<string, double>
      {
        ["identity"] = 0.61,
        ["network"] = 0.72
      },
      topImpactFactors = new[]
        {
                new { factor = "Missing network encryption", severity = "Critical", affectedResources = 5 },
                new { factor = "Weak authentication", severity = "High", affectedResources = 3 }
            }
    };

    await _db.ScoreSnapshots.AddAsync(new ScoreSnapshot
    {
      Id = Guid.NewGuid(),
      ServiceGroupId = serviceGroupId,
      AnalysisRunId = analysisRunId,
      Category = category,
      Score = 65.0,
      Confidence = 0.85,
      Dimensions = System.Text.Json.JsonSerializer.Serialize(dimensions),
      RecordedAt = DateTime.UtcNow
    });

    await _db.Recommendations.AddAsync(new Recommendation
    {
      Id = Guid.NewGuid(),
      ServiceGroupId = serviceGroupId,
      CorrelationId = Guid.NewGuid(),
      AnalysisRunId = analysisRunId,
      ResourceId = "/subscriptions/test/resourceGroups/rg/providers/Microsoft.KeyVault/vaults/test-kv",
      Category = category,
      RecommendationType = "security",
      ActionType = "optimize",
      TargetEnvironment = "prod",
      Title = "Enable customer-managed encryption for sensitive resources",
      Description = "Improve encryption controls for resources missing customer-managed keys.",
      Rationale = "Grounded in agent-generated security findings for the analysis run.",
      Impact = "Improves the security score by addressing the most severe finding.",
      ProposedChanges = "Enable CMK-backed encryption and rotate secrets.",
      Summary = "Security recommendation",
      Confidence = 0.91m,
      ConfidenceSource = "ai_foundry",
      EstimatedImpact = System.Text.Json.JsonSerializer.Serialize(new
      {
        securityDelta = 12.0,
        implementationCost = "Medium"
      }),
      ApprovalMode = "single",
      RequiredApprovals = 1,
      ReceivedApprovals = 0,
      Status = "pending",
      Priority = "high",
      CreatedAt = DateTime.UtcNow,
      UpdatedAt = DateTime.UtcNow
    });

    await _db.SaveChangesAsync();

    // Act
    var result = await _sut.GetExplainabilityAsync(
        serviceGroupId,
        category,
        targetScore: 80.0,
        apiVersion: "2024-03-01");

    // Assert
    var actionResult = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(result.Result);
    var response = Assert.IsType<ScoreExplainabilityResponse>(actionResult.Value);

    Assert.Equal(serviceGroupId, response.ServiceGroupId);
    Assert.Equal(category, response.Category);
    Assert.Equal(analysisRunId, response.AnalysisRunId);
    Assert.Equal(65.0, response.CurrentScore);
    Assert.Equal(80.0, response.TargetScore);
    Assert.Equal(15.0, response.Gap);
    Assert.Contains("identity", response.ContributingDimensions.Keys);
    Assert.Equal("Score = 100 × security_posture (identity, network isolation, data protection)", response.ScoringFormula);
    Assert.NotEmpty(response.WafPillarScores);
    Assert.Equal(0.65, response.WafPillarScores["Security"]);
    Assert.NotEmpty(response.TopContributors);
    Assert.Equal("Missing network encryption", response.TopContributors[0].Factor);
    Assert.Equal(5, response.TopContributors[0].Count);
    Assert.NotEmpty(response.PathToTarget);
    Assert.Equal("Enable customer-managed encryption for sensitive resources", response.PathToTarget[0].Action);
    Assert.Equal("Medium", response.PathToTarget[0].Effort);
  }

  [Fact]
  public async Task GetExplainabilityAsync_GeneratesSmallGapActions_WhenGapIsUnder10()
  {
    // Arrange
    var serviceGroupId = Guid.NewGuid();
    var category = "FinOps";

    await _db.ServiceGroups.AddAsync(new ServiceGroup
    {
      Id = serviceGroupId,
      ExternalKey = "test-sg",
      Name = "Test Service Group",
      CreatedAt = DateTime.UtcNow
    });

    var dimensions = new
    {
      WafPillars = new Dictionary<string, double> { ["CostOptimization"] = 0.73 },
      TopImpactFactors = new[]
        {
                new { Factor = "Unattached disks", Severity = "Medium", Count = 2 }
            }
    };

    await _db.ScoreSnapshots.AddAsync(new ScoreSnapshot
    {
      Id = Guid.NewGuid(),
      ServiceGroupId = serviceGroupId,
      Category = category,
      Score = 73.0,
      Confidence = 0.90,
      Dimensions = System.Text.Json.JsonSerializer.Serialize(dimensions),
      RecordedAt = DateTime.UtcNow
    });

    await _db.SaveChangesAsync();

    // Act
    var result = await _sut.GetExplainabilityAsync(
        serviceGroupId,
        category,
        targetScore: 80.0,
        apiVersion: "2024-03-01");

    // Assert
    var actionResult = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(result.Result);
    var response = Assert.IsType<ScoreExplainabilityResponse>(actionResult.Value);

    Assert.Equal(7.0, response.Gap);
    Assert.NotEmpty(response.PathToTarget);
    Assert.Single(response.PathToTarget);
    Assert.Contains("Unattached disks", response.PathToTarget[0].Action);
    Assert.Equal("Low", response.PathToTarget[0].Effort);
  }

  [Fact]
  public async Task GetExplainabilityAsync_GeneratesMediumGapActions_WhenGapIs10To25()
  {
    // Arrange
    var serviceGroupId = Guid.NewGuid();
    var category = "Reliability";

    await _db.ServiceGroups.AddAsync(new ServiceGroup
    {
      Id = serviceGroupId,
      ExternalKey = "test-sg",
      Name = "Test Service Group",
      CreatedAt = DateTime.UtcNow
    });

    await _db.ScoreSnapshots.AddAsync(new ScoreSnapshot
    {
      Id = Guid.NewGuid(),
      ServiceGroupId = serviceGroupId,
      Category = category,
      Score = 60.0,
      Confidence = 0.88,
      Dimensions = System.Text.Json.JsonSerializer.Serialize(new
      {
        WafPillars = new Dictionary<string, double> { ["Reliability"] = 0.60 }
      }),
      RecordedAt = DateTime.UtcNow
    });

    await _db.SaveChangesAsync();

    // Act
    var result = await _sut.GetExplainabilityAsync(
        serviceGroupId,
        category,
        targetScore: 80.0,
        apiVersion: "2024-03-01");

    // Assert
    var actionResult = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(result.Result);
    var response = Assert.IsType<ScoreExplainabilityResponse>(actionResult.Value);

    Assert.Equal(20.0, response.Gap);
    Assert.NotEmpty(response.PathToTarget);
    Assert.True(response.PathToTarget.Count >= 2);
    Assert.Contains("Medium", response.PathToTarget.Select(a => a.Effort));
  }

  [Fact]
  public async Task GetExplainabilityAsync_GeneratesLargeGapActions_WhenGapIsOver25()
  {
    // Arrange
    var serviceGroupId = Guid.NewGuid();
    var category = "Architecture";

    await _db.ServiceGroups.AddAsync(new ServiceGroup
    {
      Id = serviceGroupId,
      ExternalKey = "test-sg",
      Name = "Test Service Group",
      CreatedAt = DateTime.UtcNow
    });

    await _db.ScoreSnapshots.AddAsync(new ScoreSnapshot
    {
      Id = Guid.NewGuid(),
      ServiceGroupId = serviceGroupId,
      Category = category,
      Score = 45.0,
      Confidence = 0.75,
      Dimensions = System.Text.Json.JsonSerializer.Serialize(new
      {
        WafPillars = new Dictionary<string, double> { ["PerformanceEfficiency"] = 0.45 }
      }),
      RecordedAt = DateTime.UtcNow
    });

    await _db.SaveChangesAsync();

    // Act
    var result = await _sut.GetExplainabilityAsync(
        serviceGroupId,
        category,
        targetScore: 80.0,
        apiVersion: "2024-03-01");

    // Assert
    var actionResult = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(result.Result);
    var response = Assert.IsType<ScoreExplainabilityResponse>(actionResult.Value);

    Assert.Equal(35.0, response.Gap);
    Assert.NotEmpty(response.PathToTarget);
    Assert.True(response.PathToTarget.Count >= 3);
    Assert.Contains("High", response.PathToTarget.Select(a => a.Effort));
  }

  [Fact]
  public async Task GetExplainabilityAsync_ReturnsEmptyPathToTarget_WhenScoreMeetsTarget()
  {
    // Arrange
    var serviceGroupId = Guid.NewGuid();
    var category = "Security";

    await _db.ServiceGroups.AddAsync(new ServiceGroup
    {
      Id = serviceGroupId,
      ExternalKey = "test-sg",
      Name = "Test Service Group",
      CreatedAt = DateTime.UtcNow
    });

    await _db.ScoreSnapshots.AddAsync(new ScoreSnapshot
    {
      Id = Guid.NewGuid(),
      ServiceGroupId = serviceGroupId,
      Category = category,
      Score = 85.0,
      Confidence = 0.92,
      Dimensions = System.Text.Json.JsonSerializer.Serialize(new
      {
        WafPillars = new Dictionary<string, double> { ["Security"] = 0.85 }
      }),
      RecordedAt = DateTime.UtcNow
    });

    await _db.SaveChangesAsync();

    // Act
    var result = await _sut.GetExplainabilityAsync(
        serviceGroupId,
        category,
        targetScore: 80.0,
        apiVersion: "2024-03-01");

    // Assert
    var actionResult = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(result.Result);
    var response = Assert.IsType<ScoreExplainabilityResponse>(actionResult.Value);

    Assert.Equal(-5.0, response.Gap); // Negative gap (already exceeds target)
    Assert.Empty(response.PathToTarget);
  }

  [Fact]
  public async Task GetExplainabilityAsync_UsesDefaultWafPillars_WhenDimensionsAreMissing()
  {
    // Arrange
    var serviceGroupId = Guid.NewGuid();
    var category = "Sustainability";

    await _db.ServiceGroups.AddAsync(new ServiceGroup
    {
      Id = serviceGroupId,
      ExternalKey = "test-sg",
      Name = "Test Service Group",
      CreatedAt = DateTime.UtcNow
    });

    await _db.ScoreSnapshots.AddAsync(new ScoreSnapshot
    {
      Id = Guid.NewGuid(),
      ServiceGroupId = serviceGroupId,
      Category = category,
      Score = 70.0,
      Confidence = 0.80,
      Dimensions = null, // No dimensions provided
      RecordedAt = DateTime.UtcNow
    });

    await _db.SaveChangesAsync();

    // Act
    var result = await _sut.GetExplainabilityAsync(
        serviceGroupId,
        category,
        targetScore: 80.0,
        apiVersion: "2024-03-01");

    // Assert
    var actionResult = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(result.Result);
    var response = Assert.IsType<ScoreExplainabilityResponse>(actionResult.Value);

    Assert.NotEmpty(response.WafPillarScores);
    Assert.Contains("sustainability", response.WafPillarScores.Keys);
  }
}
