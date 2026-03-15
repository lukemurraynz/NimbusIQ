using System.Text.Json;
using Atlas.ControlPlane.Domain.Entities;

namespace Atlas.ControlPlane.Application.Recommendations;

public static class RecommendationCitationEnricher
{
  private static readonly Dictionary<string, string[]> CategoryCitationMap = new(StringComparer.OrdinalIgnoreCase)
  {
    ["FinOps"] =
      [
          "https://learn.microsoft.com/azure/well-architected/cost-optimization/",
            "https://learn.microsoft.com/azure/well-architected/cost-optimization/principles"
      ],
    ["Reliability"] =
      [
          "https://learn.microsoft.com/azure/well-architected/reliability/",
            "https://learn.microsoft.com/azure/well-architected/reliability/principles"
      ],
    ["Architecture"] =
      [
          "https://learn.microsoft.com/azure/well-architected/operational-excellence/",
            "https://learn.microsoft.com/azure/well-architected/what-is-well-architected-framework"
      ],
    ["Security"] =
      [
          "https://learn.microsoft.com/azure/well-architected/security/",
            "https://learn.microsoft.com/azure/well-architected/security/principles"
      ],
    ["Sustainability"] =
      [
          "https://learn.microsoft.com/azure/well-architected/performance-efficiency/",
            "https://learn.microsoft.com/azure/well-architected/pillars"
      ]
  };

  private static readonly Dictionary<string, string[]> ResourceTypeCitationMap = new(StringComparer.OrdinalIgnoreCase)
  {
    ["microsoft.compute/virtualmachines"] = ["https://learn.microsoft.com/azure/virtual-machines/sizes"],
    ["microsoft.storage/storageaccounts"] = ["https://learn.microsoft.com/azure/storage/common/storage-security-guide"],
    ["microsoft.keyvault/vaults"] = ["https://learn.microsoft.com/azure/key-vault/general/overview"],
    ["microsoft.containerservice/managedclusters"] = ["https://learn.microsoft.com/azure/aks/operator-best-practices-cluster-security"],
    ["microsoft.app/containerapps"] =
      [
          "https://learn.microsoft.com/azure/container-apps/ingress-overview",
            "https://learn.microsoft.com/azure/container-apps/scale-app"
      ],
    ["microsoft.sql/servers"] = ["https://learn.microsoft.com/azure/azure-sql/database/security-overview"],
    ["microsoft.web/sites"] = ["https://learn.microsoft.com/azure/app-service/overview"]
  };

  public static async Task EnrichInPlaceAsync(
      IEnumerable<Atlas.ControlPlane.Domain.Entities.Recommendation> recommendations,
      IRecommendationGroundingClient? groundingClient,
      CancellationToken cancellationToken = default)
  {
    foreach (var recommendation in recommendations)
    {
      var grounded = groundingClient is null
          ? null
          : await groundingClient.TryGroundAsync(recommendation, cancellationToken);

      var references = ParseReferences(recommendation.EvidenceReferences);
      if (grounded is not null)
      {
        references.AddRange(grounded.EvidenceUrls);
        MergeGroundingMetadata(recommendation, grounded);
      }

      if (!string.IsNullOrWhiteSpace(recommendation.Category) &&
          CategoryCitationMap.TryGetValue(recommendation.Category, out var categoryRefs))
      {
        references.AddRange(categoryRefs);
      }

      var resourceType = TryExtractResourceType(recommendation.ResourceId);
      if (!string.IsNullOrWhiteSpace(resourceType) &&
          ResourceTypeCitationMap.TryGetValue(resourceType, out var resourceRefs))
      {
        references.AddRange(resourceRefs);
      }

      if (references.Count == 0)
      {
        continue;
      }

      recommendation.EvidenceReferences = JsonSerializer.Serialize(
          references
              .Where(value => !string.IsNullOrWhiteSpace(value))
              .Distinct(StringComparer.OrdinalIgnoreCase)
              .Take(12)
              .ToArray());

      if (grounded is null)
      {
        MergeGroundingMetadata(recommendation, new GroundingEnrichmentResult(
            [],
            new GroundingProvenance(
                GroundingSource: "seeded_rule",
                GroundingQuery: $"category={recommendation.Category};resourceType={TryExtractResourceType(recommendation.ResourceId)}",
                GroundingTimestampUtc: DateTime.UtcNow,
                GroundingToolRunId: null,
                GroundingQuality: 0.40,
                GroundingRecencyScore: 0.50),
            []));
      }
    }
  }

  private static void MergeGroundingMetadata(
      Atlas.ControlPlane.Domain.Entities.Recommendation recommendation,
      GroundingEnrichmentResult grounded)
  {
    Dictionary<string, object?> changeContext;
    if (!string.IsNullOrWhiteSpace(recommendation.ChangeContext))
    {
      try
      {
        changeContext = JsonSerializer.Deserialize<Dictionary<string, object?>>(recommendation.ChangeContext!)
            ?? new Dictionary<string, object?>();
      }
      catch
      {
        changeContext = new Dictionary<string, object?>();
      }
    }
    else
    {
      changeContext = new Dictionary<string, object?>();
    }

    changeContext["grounding"] = new
    {
      groundingSource = grounded.Provenance.GroundingSource,
      groundingQuery = grounded.Provenance.GroundingQuery,
      groundingTimestampUtc = grounded.Provenance.GroundingTimestampUtc,
      groundingToolRunId = grounded.Provenance.GroundingToolRunId,
      citations = grounded.Citations.Select(c => new
      {
        url = c.Url,
        title = c.Title,
        snippetHash = c.SnippetHash,
        citationTimestampUtc = c.RetrievedAtUtc,
        source = c.Source,
        query = c.Query,
        toolRunId = c.ToolRunId,
        sourceLastUpdatedUtc = c.SourceLastUpdatedUtc
      }).ToArray()
    };

    recommendation.ChangeContext = JsonSerializer.Serialize(changeContext);
  }

  private static List<string> ParseReferences(string? raw)
  {
    if (string.IsNullOrWhiteSpace(raw))
    {
      return [];
    }

    try
    {
      var parsed = JsonSerializer.Deserialize<string[]>(raw);
      if (parsed is { Length: > 0 })
      {
        return parsed.ToList();
      }
    }
    catch (JsonException)
    {
      // Ignore and fall back to delimiter parsing.
    }

    return raw
        .Split([',', '\n', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .ToList();
  }

  private static string? TryExtractResourceType(string? resourceId)
  {
    if (string.IsNullOrWhiteSpace(resourceId))
    {
      return null;
    }

    var segments = resourceId.Split('/', StringSplitOptions.RemoveEmptyEntries);
    var providerIndex = Array.FindIndex(segments, segment => segment.Equals("providers", StringComparison.OrdinalIgnoreCase));

    if (providerIndex < 0 || providerIndex + 2 >= segments.Length)
    {
      return null;
    }

    return $"{segments[providerIndex + 1]}/{segments[providerIndex + 2]}".ToLowerInvariant();
  }
}
