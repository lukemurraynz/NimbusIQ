using System.Text.Json;
using Atlas.ControlPlane.Application.Recommendations;

namespace Atlas.ControlPlane.Tests.Unit.Application.Recommendations;

public class RecommendationCitationEnricherTests
{
  [Fact]
  public async Task EnrichInPlace_AppendsCategoryAndResourceCitations()
  {
    var recommendation = new Atlas.ControlPlane.Domain.Entities.Recommendation
    {
      Id = Guid.NewGuid(),
      ServiceGroupId = Guid.NewGuid(),
      CorrelationId = Guid.NewGuid(),
      AnalysisRunId = Guid.NewGuid(),
      ResourceId = "/subscriptions/a/resourceGroups/rg/providers/Microsoft.Compute/virtualMachines/vm1",
      Category = "FinOps",
      RecommendationType = "cost",
      ActionType = "optimize",
      TargetEnvironment = "prod",
      Title = "Optimize VM SKU",
      Description = "desc",
      Rationale = "why",
      Impact = "impact",
      ProposedChanges = "changes",
      Summary = "summary",
      Confidence = 0.8m,
      ApprovalMode = "single",
      RequiredApprovals = 1,
      ReceivedApprovals = 0,
      Status = "pending",
      Priority = "high",
      CreatedAt = DateTime.UtcNow,
      UpdatedAt = DateTime.UtcNow,
      EvidenceReferences = "[]"
    };

    await RecommendationCitationEnricher.EnrichInPlaceAsync([recommendation], groundingClient: null);

    var refs = JsonSerializer.Deserialize<string[]>(recommendation.EvidenceReferences ?? "[]");
    Assert.NotNull(refs);
    Assert.Contains("https://learn.microsoft.com/azure/well-architected/cost-optimization/", refs!);
    Assert.Contains("https://learn.microsoft.com/azure/virtual-machines/sizes", refs!);
  }
}
