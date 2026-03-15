using Atlas.AgentOrchestrator.Agents;
using Microsoft.Extensions.Logging.Abstractions;

namespace Atlas.AgentOrchestrator.Tests.Integration;

/// <summary>
/// Integration-level tests for <see cref="SustainabilityAgent"/> covering carbon
/// analysis pipeline and sustainability recommendations.
/// These tests use the agent with no AI Foundry client so they run without
/// any external infrastructure dependencies.
/// </summary>
public class SustainabilityAgentIntegrationTests
{
  private static SustainabilityAgent CreateAgent() =>
      new(NullLogger<SustainabilityAgent>.Instance, foundryClient: null);

  // ─────────────────────────────────────────────────────────────
  // Basic analysis without AI Foundry client
  // ─────────────────────────────────────────────────────────────

  [Fact]
  public async Task AnalyzeSustainabilityAsync_WithNullFoundryClient_CompletesWithoutException()
  {
    var agent = CreateAgent();
    var graphContext = new ServiceGraphContext
    {
      ServiceGroupId = Guid.NewGuid()
    };
    var sustainabilityContext = new SustainabilityContext
    {
      MonthlyCarbonKg = 0,
      HasCarbonIntensityData = false
    };

    var result = await agent.AnalyzeSustainabilityAsync(graphContext, sustainabilityContext);

    // Should produce a result even without AI Foundry client
    Assert.NotNull(result);
    Assert.NotNull(result.Findings);
    Assert.NotNull(result.Recommendations);
  }

  [Fact]
  public async Task AnalyzeSustainabilityAsync_WithCarbonData_PreservesCarbonMetrics()
  {
    // Verify that agent doesn't modify input context carbon data
    var agent = CreateAgent();
    const double originalEmissions = 125.5;
    var graphContext = new ServiceGraphContext
    {
      ServiceGroupId = Guid.NewGuid()
    };
    var sustainabilityContext = new SustainabilityContext
    {
      MonthlyCarbonKg = originalEmissions,
      HasCarbonIntensityData = true
    };

    await agent.AnalyzeSustainabilityAsync(graphContext, sustainabilityContext);

    Assert.Equal(originalEmissions, sustainabilityContext.MonthlyCarbonKg);
  }

  // ─────────────────────────────────────────────────────────────
  // Carbon intensity analysis with various emission profiles
  // ─────────────────────────────────────────────────────────────

  [Theory]
  [InlineData(0)]
  [InlineData(50)]
  [InlineData(200)]
  [InlineData(500)]
  public async Task AnalyzeSustainabilityAsync_WithVariousCarbonLevels_CompletesSuccessfully(double emissionsKg)
  {
    var agent = CreateAgent();
    var graphContext = new ServiceGraphContext
    {
      ServiceGroupId = Guid.NewGuid()
    };
    var sustainabilityContext = new SustainabilityContext
    {
      MonthlyCarbonKg = emissionsKg,
      HasCarbonIntensityData = emissionsKg > 0
    };

    var result = await agent.AnalyzeSustainabilityAsync(graphContext, sustainabilityContext);

    // Verify result structure
    Assert.NotNull(result);
    Assert.NotNull(result.Findings);
    Assert.NotNull(result.Recommendations);
    Assert.True(result.Score >= 0 && result.Score <= 100);
    Assert.True(result.Confidence >= 0 && result.Confidence <= 1.0);
  }

  [Fact]
  public async Task AnalyzeSustainabilityAsync_WithRegionData_AnalyzesRegionalEmissions()
  {
    var agent = CreateAgent();
    var graphContext = new ServiceGraphContext
    {
      ServiceGroupId = Guid.NewGuid(),
      Nodes = new List<ServiceNodeDto>
            {
                new() { Id = Guid.NewGuid(), Name = "app-service-1", Region = "eastus" },
                new() { Id = Guid.NewGuid(), Name = "storage-1", Region = "westeurope" }
            }
    };
    var sustainabilityContext = new SustainabilityContext
    {
      MonthlyCarbonKg = 150,
      HasCarbonIntensityData = true,
      RegionEmissions = new Dictionary<string, double>
            {
                { "eastus", 80 },
                { "westeurope", 70 }
            }
    };

    var result = await agent.AnalyzeSustainabilityAsync(graphContext, sustainabilityContext);

    // Verify regional analysis completed
    Assert.NotNull(result);
    Assert.NotNull(result.Findings);
    Assert.Contains(result.EvidenceReferences, e => e.Contains("carbon_region_kg:"));
  }

  // ─────────────────────────────────────────────────────────────
  // Sustainability recommendations validation
  // ─────────────────────────────────────────────────────────────

  [Fact]
  public async Task AnalyzeSustainabilityAsync_WithHighEmissionsAndIdleResources_GeneratesRecommendations()
  {
    var agent = CreateAgent();
    var graphContext = new ServiceGraphContext
    {
      ServiceGroupId = Guid.NewGuid(),
      Nodes = new List<ServiceNodeDto>
            {
                new() { Id = Guid.NewGuid(), Name = "vm-1", NodeType = "Microsoft.Compute/virtualMachines" },
                new() { Id = Guid.NewGuid(), Name = "storage-1", NodeType = "Microsoft.Storage/storageAccounts" }
            }
    };
    var sustainabilityContext = new SustainabilityContext
    {
      MonthlyCarbonKg = 800, // High emissions
      HasCarbonIntensityData = true,
      IdleTimePercent = 60,
      AverageCpuUtilizationPercent = 20,
      HasUtilizationMetrics = true
    };

    var result = await agent.AnalyzeSustainabilityAsync(graphContext, sustainabilityContext);

    // Should generate recommendations to reduce emissions
    Assert.NotNull(result);
    Assert.NotNull(result.Recommendations);
    Assert.True(result.Recommendations.Any(), "Should produce recommendations for high emissions with idle resources");
  }

  [Fact]
  public async Task AnalyzeSustainabilityAsync_WithZeroEmissions_ReturnsHighScore()
  {
    var agent = CreateAgent();
    var graphContext = new ServiceGraphContext
    {
      ServiceGroupId = Guid.NewGuid()
    };
    var sustainabilityContext = new SustainabilityContext
    {
      MonthlyCarbonKg = 0,
      HasCarbonIntensityData = false,
      HasUtilizationMetrics = true,
      AverageCpuUtilizationPercent = 80,
      IdleTimePercent = 5
    };

    var result = await agent.AnalyzeSustainabilityAsync(graphContext, sustainabilityContext);

    // Should complete successfully with high score for efficient usage
    Assert.NotNull(result);
    Assert.NotNull(result.Findings);
    Assert.True(result.Score > 70, "Should have high sustainability score with efficient usage");
  }

  // ─────────────────────────────────────────────────────────────
  // Error handling and graceful degradation
  // ─────────────────────────────────────────────────────────────

  [Fact]
  public async Task AnalyzeSustainabilityAsync_WithEmptyContext_HandlesGracefully()
  {
    var agent = CreateAgent();
    var graphContext = new ServiceGraphContext
    {
      ServiceGroupId = Guid.NewGuid()
    };
    var sustainabilityContext = new SustainabilityContext(); // Empty context

    var result = await agent.AnalyzeSustainabilityAsync(graphContext, sustainabilityContext);

    // Should handle incomplete context without throwing
    Assert.NotNull(result);
    Assert.NotNull(result.Findings);
    Assert.True(result.Confidence >= 0, "Confidence should be calculated even with minimal data");
  }


  // ─────────────────────────────────────────────────────────────
  // Evidence tracking for downstream persistence
  // ─────────────────────────────────────────────────────────────

  [Fact]
  public async Task AnalyzeSustainabilityAsync_WithCarbonData_EmitsCarbonEvidence()
  {
    var agent = CreateAgent();
    var graphContext = new ServiceGraphContext
    {
      ServiceGroupId = Guid.NewGuid()
    };
    var sustainabilityContext = new SustainabilityContext
    {
      MonthlyCarbonKg = 150.5,
      HasCarbonIntensityData = true,
      RegionEmissions = new Dictionary<string, double>
            {
                { "uksouth", 80.25 },
                { "northeurope", 70.25 }
            }
    };

    var result = await agent.AnalyzeSustainabilityAsync(graphContext, sustainabilityContext);

    // Verify carbon evidence is recorded for downstream persistence
    Assert.Contains(result.EvidenceReferences, e => e.Contains("carbon_monthly_kg:"));
    Assert.Contains(result.EvidenceReferences, e => e == "carbon_has_real_data:true");
    Assert.Contains(result.EvidenceReferences, e => e.Contains("carbon_region_kg:uksouth:"));
    Assert.Contains(result.EvidenceReferences, e => e.Contains("carbon_region_kg:northeurope:"));
  }

  [Fact]
  public async Task AnalyzeSustainabilityAsync_WithoutCarbonData_EmitsZeroCarbonEvidence()
  {
    var agent = CreateAgent();
    var graphContext = new ServiceGraphContext
    {
      ServiceGroupId = Guid.NewGuid()
    };
    var sustainabilityContext = new SustainabilityContext
    {
      MonthlyCarbonKg = 0,
      HasCarbonIntensityData = false
    };

    var result = await agent.AnalyzeSustainabilityAsync(graphContext, sustainabilityContext);

    // Verify zero carbon evidence when no data available
    Assert.Contains(result.EvidenceReferences, e => e == "carbon_monthly_kg:0");
    Assert.Contains(result.EvidenceReferences, e => e == "carbon_has_real_data:false");
  }
}
