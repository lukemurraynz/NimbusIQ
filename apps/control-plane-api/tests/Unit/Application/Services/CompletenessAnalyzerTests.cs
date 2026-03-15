using Atlas.ControlPlane.Application.Services;
using Atlas.ControlPlane.Infrastructure.Azure;
using Microsoft.Extensions.Logging;
using Moq;

namespace Atlas.ControlPlane.Tests.Unit.Application.Services;

/// <summary>
/// Unit tests for CompletenessAnalyzer service.
/// Tests metadata completeness analysis across resource types.
/// </summary>
public class CompletenessAnalyzerTests
{
  private readonly Mock<ILogger<CompletenessAnalyzer>> _mockLogger;
  private readonly CompletenessAnalyzer _sut;

  public CompletenessAnalyzerTests()
  {
    _mockLogger = new Mock<ILogger<CompletenessAnalyzer>>();
    _sut = new CompletenessAnalyzer(_mockLogger.Object);
  }

  #region Happy Path Tests

  [Fact]
  public void Analyze_WithEmptyResourceList_ReturnsZeroTotalResources()
  {
    // Arrange
    var resources = new List<DiscoveredAzureResource>();

    // Act
    var result = _sut.Analyze(resources);

    // Assert
    Assert.Equal(0, result.TotalResources);
    Assert.Empty(result.ResourceTypeAnalysis);
  }

  [Fact]
  public void Analyze_WithCompleteResources_ReturnsHighCoverageScores()
  {
    // Arrange
    var resources = new List<DiscoveredAzureResource>
        {
            new(
                "/subscriptions/sub1/resourceGroups/rg1/providers/Microsoft.Storage/storageAccounts/sa1",
                "sa1",
                "Microsoft.Storage/storageAccounts",
                "eastus",
                "rg1",
                "sub1",
                "Standard_LRS",
                "{\"env\": \"prod\", \"owner\": \"team-a\"}",
                "StorageV2",
                null),
            new(
                "/subscriptions/sub1/resourceGroups/rg1/providers/Microsoft.Storage/storageAccounts/sa2",
                "sa2",
                "Microsoft.Storage/storageAccounts",
                "westus",
                "rg1",
                "sub1",
                "Premium_LRS",
                "{\"env\": \"prod\"}",
                "StorageV2",
                null)
        };

    // Act
    var result = _sut.Analyze(resources);

    // Assert
    Assert.Equal(2, result.TotalResources);
    Assert.Equal(100.0, result.OverallTagsCoverage);
    Assert.Equal(100.0, result.OverallRegionCoverage);
    Assert.Equal(100.0, result.OverallSkuCoverage);
    Assert.Equal(100.0, result.OverallKindCoverage);
    Assert.Equal(100.0, result.CalculatedCompletenessScore);
    Assert.Single(result.ResourceTypeAnalysis);
  }

  [Fact]
  public void Analyze_WithPartialCompleteness_CalculatesCorrectScores()
  {
    // Arrange: 3 resources
    // - Resource 1: has tags, region, SKU, no Kind
    // - Resource 2: no tags, has region, SKU, no Kind
    // - Resource 3: has tags, no region (global), SKU, no Kind
    var resources = new List<DiscoveredAzureResource>
        {
            new(
                "/subscriptions/sub1/resourceGroups/rg/providers/Microsoft.Storage/storageAccounts/sa1",
                "sa1",
                "Microsoft.Storage/storageAccounts",
                "eastus",
                "rg",
                "sub1",
                "Standard_LRS",
                "{\"env\": \"prod\"}",
                null,
                null),
            new(
                "/subscriptions/sub1/resourceGroups/rg/providers/Microsoft.Storage/storageAccounts/sa2",
                "sa2",
                "Microsoft.Storage/storageAccounts",
                "westus",
                "rg",
                "sub1",
                "Standard_LRS",
                null,
                null,
                null),
            new(
                "/subscriptions/sub1/resourceGroups/rg/providers/Microsoft.Storage/storageAccounts/sa3",
                "sa3",
                "Microsoft.Storage/storageAccounts",
                "global",
                "rg",
                "sub1",
                "Standard_LRS",
                "{\"owner\": \"team-b\"}",
                null,
                null)
        };

    // Act
    var result = _sut.Analyze(resources);

    // Assert
    Assert.Equal(3, result.TotalResources);
    Assert.Equal(66.7, result.OverallTagsCoverage);
    Assert.Equal(66.7, result.OverallRegionCoverage);
    Assert.Equal(100.0, result.OverallSkuCoverage);
    Assert.Equal(0.0, result.OverallKindCoverage);
    Assert.Equal(61.7, result.CalculatedCompletenessScore);
  }

  [Fact]
  public void Analyze_IdentifiesPrimaryBottleneck()
  {
    // Arrange: Kind will be the bottleneck at 0%
    var resources = new List<DiscoveredAzureResource>
        {
            new(
                "/subscriptions/sub1/resourceGroups/rg/providers/Microsoft.Storage/storageAccounts/sa1",
                "sa1",
                "Microsoft.Storage/storageAccounts",
                "eastus",
                "rg",
                "sub1",
                "Standard_LRS",
                "{\"env\": \"prod\"}",
                null,
                null)
        };

    // Act
    var result = _sut.Analyze(resources);

    // Assert
    Assert.NotNull(result.PrimaryBottleneck);
    Assert.Equal("Kind", result.PrimaryBottleneck.Field);
    Assert.Equal(0.0, result.PrimaryBottleneck.Coverage);
    Assert.Equal(1, result.PrimaryBottleneck.AffectedCount);
    Assert.Equal(20, result.PrimaryBottleneck.WeightInFormula);
  }

  #endregion

  #region Edge Case Tests

  [Fact]
  public void Analyze_WithEmptyTags_TreatsAsNoTags()
  {
    // Arrange
    var resources = new List<DiscoveredAzureResource>
        {
            new(
                "/subscriptions/sub1/resourceGroups/rg/providers/Microsoft.Compute/virtualMachines/vm1",
                "vm1",
                "Microsoft.Compute/virtualMachines",
                "eastus",
                "rg",
                "sub1",
                "Standard_D2s_v3",
                "{}",
                null,
                null)
        };

    // Act
    var result = _sut.Analyze(resources);

    // Assert
    Assert.Equal(0.0, result.OverallTagsCoverage);
  }

  [Fact]
  public void Analyze_WithNullTags_TreatsAsNoTags()
  {
    // Arrange
    var resources = new List<DiscoveredAzureResource>
        {
            new(
                "/subscriptions/sub1/resourceGroups/rg/providers/Microsoft.Compute/virtualMachines/vm1",
                "vm1",
                "Microsoft.Compute/virtualMachines",
                "eastus",
                "rg",
                "sub1",
                "Standard_D2s_v3",
                null,
                null,
                null)
        };

    // Act
    var result = _sut.Analyze(resources);

    // Assert
    Assert.Equal(0.0, result.OverallTagsCoverage);
  }

  [Fact]
  public void Analyze_WithHiddenTagsOnly_TreatsAsNoMeaningfulTags()
  {
    // Arrange: Only "hidden-" prefixed tags should not count
    var resources = new List<DiscoveredAzureResource>
        {
            new(
                "/subscriptions/sub1/resourceGroups/rg/providers/Microsoft.Storage/storageAccounts/sa1",
                "sa1",
                "Microsoft.Storage/storageAccounts",
                "eastus",
                "rg",
                "sub1",
                "Standard_LRS",
                "{\"hidden-tracked\": \"true\", \"hidden-internal\": \"yes\"}",
                null,
                null)
        };

    // Act
    var result = _sut.Analyze(resources);

    // Assert
    Assert.Equal(0.0, result.OverallTagsCoverage);
  }

  [Fact]
  public void Analyze_WithMixedHiddenAndVisibleTags_CountsVisibleOnly()
  {
    // Arrange: Only visible tags should count
    var resources = new List<DiscoveredAzureResource>
        {
            new(
                "/subscriptions/sub1/resourceGroups/rg/providers/Microsoft.Storage/storageAccounts/sa1",
                "sa1",
                "Microsoft.Storage/storageAccounts",
                "eastus",
                "rg",
                "sub1",
                "Standard_LRS",
                "{\"env\": \"prod\", \"hidden-tracked\": \"true\"}",
                null,
                null)
        };

    // Act
    var result = _sut.Analyze(resources);

    // Assert
    Assert.Equal(100.0, result.OverallTagsCoverage);
  }

  [Fact]
  public void Analyze_WithGlobalLocation_TreatsAsInvalidRegion()
  {
    // Arrange
    var resources = new List<DiscoveredAzureResource>
        {
            new(
                "/subscriptions/sub1/providers/Microsoft.Authorization/roleAssignments/ra1",
                "ra1",
                "Microsoft.Authorization/roleAssignments",
                "global",
                null,
                "sub1",
                null,
                null,
                null,
                null)
        };

    // Act
    var result = _sut.Analyze(resources);

    // Assert
    Assert.Equal(0.0, result.OverallRegionCoverage);
  }

  [Fact]
  public void Analyze_WithMixedLocationCases_HandlesCase_Insensitive()
  {
    // Arrange: "GLOBAL", "Global", "global" should all be treated as invalid
    var resources = new List<DiscoveredAzureResource>
        {
            new(
                "/subscriptions/sub1/providers/Microsoft.Authorization/roleAssignments/ra1",
                "ra1",
                "Microsoft.Authorization/roleAssignments",
                "GLOBAL",
                null,
                "sub1",
                null,
                null,
                null,
                null),
            new(
                "/subscriptions/sub1/providers/Microsoft.Authorization/roleAssignments/ra2",
                "ra2",
                "Microsoft.Authorization/roleAssignments",
                "Global",
                null,
                "sub1",
                null,
                null,
                null,
                null)
        };

    // Act
    var result = _sut.Analyze(resources);

    // Assert
    Assert.Equal(0.0, result.OverallRegionCoverage);
  }

  [Fact]
  public void Analyze_WithInvalidJsonTags_TreatsAsNoTags()
  {
    // Arrange: Malformed JSON tags
    var resources = new List<DiscoveredAzureResource>
        {
            new(
                "/subscriptions/sub1/resourceGroups/rg/providers/Microsoft.Storage/storageAccounts/sa1",
                "sa1",
                "Microsoft.Storage/storageAccounts",
                "eastus",
                "rg",
                "sub1",
                "Standard_LRS",
                "{invalid json}",
                null,
                null)
        };

    // Act
    var result = _sut.Analyze(resources);

    // Assert
    Assert.Equal(0.0, result.OverallTagsCoverage);
  }

  #endregion

  #region Multiple Resource Type Tests

  [Fact]
  public void Analyze_WithMultipleResourceTypes_GroupsByType()
  {
    // Arrange
    var resources = new List<DiscoveredAzureResource>
        {
            new(
                "/subscriptions/sub1/resourceGroups/rg/providers/Microsoft.Storage/storageAccounts/sa1",
                "sa1",
                "Microsoft.Storage/storageAccounts",
                "eastus",
                "rg",
                "sub1",
                "Standard_LRS",
                "{\"env\": \"prod\"}",
                "StorageV2",
                null),
            new(
                "/subscriptions/sub1/resourceGroups/rg/providers/Microsoft.Compute/virtualMachines/vm1",
                "vm1",
                "Microsoft.Compute/virtualMachines",
                "eastus",
                "rg",
                "sub1",
                "Standard_D2s_v3",
                null,
                null,
                null),
            new(
                "/subscriptions/sub1/resourceGroups/rg/providers/Microsoft.Storage/storageAccounts/sa2",
                "sa2",
                "Microsoft.Storage/storageAccounts",
                "westus",
                "rg",
                "sub1",
                "Standard_LRS",
                null,
                null,
                null)
        };

    // Act
    var result = _sut.Analyze(resources);

    // Assert
    Assert.Equal(3, result.TotalResources);
    Assert.Equal(2, result.ResourceTypeAnalysis.Count);

    var storageAnalysis = result.ResourceTypeAnalysis
        .FirstOrDefault(r => r.ResourceType == "Microsoft.Storage/storageAccounts");
    Assert.NotNull(storageAnalysis);
    Assert.Equal(2, storageAnalysis.ResourceCount);

    var vmAnalysis = result.ResourceTypeAnalysis
        .FirstOrDefault(r => r.ResourceType == "Microsoft.Compute/virtualMachines");
    Assert.NotNull(vmAnalysis);
    Assert.Equal(1, vmAnalysis.ResourceCount);
  }

  [Fact]
  public void Analyze_ResourceTypeAnalysis_CalculatesCorrectGaps()
  {
    // Arrange
    var resources = new List<DiscoveredAzureResource>
        {
            new(
                "/subscriptions/sub1/resourceGroups/rg/providers/Microsoft.Storage/storageAccounts/sa1",
                "sa1",
                "Microsoft.Storage/storageAccounts",
                "eastus",
                "rg",
                "sub1",
                "Standard_LRS",
                null,
                null,
                null),
            new(
                "/subscriptions/sub1/resourceGroups/rg/providers/Microsoft.Storage/storageAccounts/sa2",
                "sa2",
                "Microsoft.Storage/storageAccounts",
                "westus",
                "rg",
                "sub1",
                null,
                null,
                null,
                null)
        };

    // Act
    var result = _sut.Analyze(resources);

    // Assert
    var analysis = result.ResourceTypeAnalysis.First();
    Assert.Equal("Microsoft.Storage/storageAccounts", analysis.ResourceType);
    Assert.Equal(2, analysis.ResourceCount);
    Assert.Equal(0.0, analysis.TagsCoverage);
    Assert.Equal(100.0, analysis.RegionCoverage);
    Assert.Equal(50.0, analysis.SkuCoverage);
    Assert.Equal(0.0, analysis.KindCoverage);

    Assert.Contains("Tags: 2 missing", analysis.MetadataGaps);
    Assert.Contains("SKU: 1 missing", analysis.MetadataGaps);
    Assert.Contains("Kind: 2 missing", analysis.MetadataGaps);
  }

  #endregion

  #region Bottleneck Priority Tests

  [Fact]
  public void BottleneckInfo_RemediationPriority_Tags_IsHigh()
  {
    // Arrange
    var resources = new List<DiscoveredAzureResource>
        {
            new(
                "/subscriptions/sub1/resourceGroups/rg/providers/Microsoft.Storage/storageAccounts/sa1",
                "sa1",
                "Microsoft.Storage/storageAccounts",
                "eastus",
                "rg",
                "sub1",
                "Standard_LRS",
                null,
                "StorageV2",
                null)
        };

    // Act
    var result = _sut.Analyze(resources);

    // Assert
    Assert.Equal("Tags", result.PrimaryBottleneck.Field);
    Assert.Equal("High", result.PrimaryBottleneck.RemediationPriority);
  }

  [Fact]
  public void BottleneckInfo_RemediationPriority_SKU_IsMedium()
  {
    // Arrange: SKU at 0% while others at 100%
    var resources = new List<DiscoveredAzureResource>
        {
            new(
                "/subscriptions/sub1/resourceGroups/rg/providers/Microsoft.Storage/storageAccounts/sa1",
                "sa1",
                "Microsoft.Storage/storageAccounts",
                "eastus",
                "rg",
                "sub1",
                null,
                "{\"env\": \"prod\"}",
                "StorageV2",
                null)
        };

    // Act
    var result = _sut.Analyze(resources);

    // Assert
    Assert.Equal("SKU", result.PrimaryBottleneck.Field);
    Assert.Equal("Medium", result.PrimaryBottleneck.RemediationPriority);
  }

  [Fact]
  public void BottleneckInfo_EstimatedImpactOnScore_CalculatesCorrectly()
  {
    // Arrange: Kind at 0% with weight 20
    var resources = new List<DiscoveredAzureResource>
        {
            new(
                "/subscriptions/sub1/resourceGroups/rg/providers/Microsoft.Storage/storageAccounts/sa1",
                "sa1",
                "Microsoft.Storage/storageAccounts",
                "eastus",
                "rg",
                "sub1",
                "Standard_LRS",
                "{\"env\": \"prod\"}",
                null,
                null)
        };

    // Act
    var result = _sut.Analyze(resources);

    // Assert
    Assert.Equal(20.0, result.PrimaryBottleneck.EstimatedImpactOnScore);
  }

  [Fact]
  public void BottleneckInfo_RemediationAdvice_ProvidesActionableGuidance()
  {
    // Arrange
    var resources = new List<DiscoveredAzureResource>
        {
            new(
                "/subscriptions/sub1/resourceGroups/rg/providers/Microsoft.Storage/storageAccounts/sa1",
                "sa1",
                "Microsoft.Storage/storageAccounts",
                "eastus",
                "rg",
                "sub1",
                "Standard_LRS",
                null,
                "StorageV2",
                null)
        };

    // Act
    var result = _sut.Analyze(resources);

    // Assert
    Assert.Contains("governance tags", result.PrimaryBottleneck.RemediationAdvice);
    Assert.Contains("1 resources", result.PrimaryBottleneck.RemediationAdvice);
  }

  #endregion

  #region Logging Tests

  [Fact]
  public void Analyze_LogsCompletenessAnalysis()
  {
    // Arrange
    var resources = new List<DiscoveredAzureResource>
        {
            new(
                "/subscriptions/sub1/resourceGroups/rg/providers/Microsoft.Storage/storageAccounts/sa1",
                "sa1",
                "Microsoft.Storage/storageAccounts",
                "eastus",
                "rg",
                "sub1",
                "Standard_LRS",
                null,
                null,
                null)
        };

    // Act
    _sut.Analyze(resources);

    // Assert
    _mockLogger.Verify(
        x => x.Log(
            LogLevel.Information,
            It.IsAny<EventId>(),
            It.IsAny<It.IsAnyType>(),
            It.IsAny<Exception?>(),
            It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
        Times.Once);
  }

  #endregion
}
