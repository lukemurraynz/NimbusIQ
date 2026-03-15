using System.Text.Json;
using Atlas.ControlPlane.Infrastructure.Azure;
using Microsoft.Extensions.Logging;

namespace Atlas.ControlPlane.Application.Services;

/// <summary>
/// Analyzes metadata completeness gaps by resource type.
/// Helps diagnose why completeness scores are low by showing which metadata fields
/// are missing and which resource types contribute most to the gap.
/// </summary>
public class CompletenessAnalyzer
{
  private readonly ILogger<CompletenessAnalyzer> _logger;

  public CompletenessAnalyzer(ILogger<CompletenessAnalyzer> logger)
  {
    _logger = logger;
  }

  /// <summary>
  /// Analyzes metadata completeness for a set of resources.
  /// Returns a detailed breakdown of which resources are missing which metadata.
  /// </summary>
  public CompletenessAnalysisResult Analyze(IReadOnlyList<DiscoveredAzureResource> resources)
  {
    var result = new CompletenessAnalysisResult
    {
      TotalResources = resources.Count,
      AnalyzedAt = DateTime.UtcNow
    };

    if (resources.Count == 0)
      return result;

    // Analyze by resource type
    var byResourceType = resources
        .GroupBy(r => r.ResourceType)
        .OrderByDescending(g => g.Count())
        .ToList();

    foreach (var group in byResourceType)
    {
      var gaps = AnalyzeGaps(group.ToList());
      result.ResourceTypeAnalysis.Add(gaps);
    }

    // Calculate overall gap percentages
    result.OverallTagsCoverage = Math.Round(
        (double)resources.Count(r => HasMeaningfulTags(r.Tags)) / resources.Count * 100, 1);
    result.OverallRegionCoverage = Math.Round(
        (double)resources.Count(r => HasValidRegion(r.Location)) / resources.Count * 100, 1);
    result.OverallSkuCoverage = Math.Round(
        (double)resources.Count(r => !string.IsNullOrWhiteSpace(r.Sku)) / resources.Count * 100, 1);
    result.OverallKindCoverage = Math.Round(
        (double)resources.Count(r => !string.IsNullOrWhiteSpace(r.Kind)) / resources.Count * 100, 1);

    // Identify the biggest gap
    var coverageByField = new List<(string Field, double Coverage)>
        {
            ("Tags", result.OverallTagsCoverage),
            ("Region", result.OverallRegionCoverage),
            ("SKU", result.OverallSkuCoverage),
            ("Kind", result.OverallKindCoverage)
        };

    var (bottleneckField, bottleneckCoverage) = coverageByField.OrderBy(g => g.Coverage).First();
    result.PrimaryBottleneck = new BottleneckInfo
    {
      Field = bottleneckField,
      Coverage = bottleneckCoverage,
      AffectedCount = (int)((100 - bottleneckCoverage) / 100 * resources.Count),
      WeightInFormula = bottleneckField switch
      {
        "Tags" => 30,
        "Region" => 25,
        "SKU" => 25,
        "Kind" => 20,
        _ => 0
      }
    };

    _logger.LogInformation(
        "Completeness analysis: {Overall}% overall, bottleneck is {Field} at {Coverage}% " +
        "affecting {Affected} resources (weight={Weight}%)",
        Math.Round((result.OverallTagsCoverage * 0.30 +
                   result.OverallRegionCoverage * 0.25 +
                   result.OverallSkuCoverage * 0.25 +
                   result.OverallKindCoverage * 0.20), 1),
        result.PrimaryBottleneck.Field,
        result.PrimaryBottleneck.Coverage,
        result.PrimaryBottleneck.AffectedCount,
        result.PrimaryBottleneck.WeightInFormula);

    return result;
  }

  private ResourceTypeGapAnalysis AnalyzeGaps(List<DiscoveredAzureResource> resources)
  {
    var resourceType = resources.First().ResourceType;
    var total = resources.Count;

    var tagsCount = resources.Count(r => HasMeaningfulTags(r.Tags));
    var regionCount = resources.Count(r => HasValidRegion(r.Location));
    var skuCount = resources.Count(r => !string.IsNullOrWhiteSpace(r.Sku));
    var kindCount = resources.Count(r => !string.IsNullOrWhiteSpace(r.Kind));

    var completenessScore = Math.Round(
        ((double)tagsCount / total * 0.30) +
        ((double)regionCount / total * 0.25) +
        ((double)skuCount / total * 0.25) +
        ((double)kindCount / total * 0.20) * 100, 1);

    var gaps = new List<string>();
    if (tagsCount < total)
      gaps.Add($"Tags: {total - tagsCount} missing");
    if (regionCount < total)
      gaps.Add($"Region: {total - regionCount} missing");
    if (skuCount < total)
      gaps.Add($"SKU: {total - skuCount} missing");
    if (kindCount < total)
      gaps.Add($"Kind: {total - kindCount} missing");

    return new ResourceTypeGapAnalysis
    {
      ResourceType = resourceType,
      ResourceCount = total,
      CompletenessScore = completenessScore,
      TagsCoverage = Math.Round((double)tagsCount / total * 100, 1),
      RegionCoverage = Math.Round((double)regionCount / total * 100, 1),
      SkuCoverage = Math.Round((double)skuCount / total * 100, 1),
      KindCoverage = Math.Round((double)kindCount / total * 100, 1),
      MetadataGaps = gaps
    };
  }

  private static bool HasMeaningfulTags(string? tags)
  {
    if (string.IsNullOrWhiteSpace(tags)) return false;
    if (tags is "{}" or "null") return false;

    try
    {
      using var doc = JsonDocument.Parse(tags);
      var count = 0;
      foreach (var property in doc.RootElement.EnumerateObject())
      {
        if (!property.Name.StartsWith("hidden-", StringComparison.OrdinalIgnoreCase))
          count++;
      }

      return count > 0;
    }
    catch
    {
      return false;
    }
  }

  private static bool HasValidRegion(string? location)
  {
    return !string.IsNullOrWhiteSpace(location) &&
           !location.Equals("global", StringComparison.OrdinalIgnoreCase);
  }
}

/// <summary>
/// Result of completeness analysis across all resources.
/// </summary>
public class CompletenessAnalysisResult
{
  public int TotalResources { get; set; }
  public DateTime AnalyzedAt { get; set; }

  /// <summary>
  /// Overall metadata coverage by field (0-100%).
  /// </summary>
  public double OverallTagsCoverage { get; set; }
  public double OverallRegionCoverage { get; set; }
  public double OverallSkuCoverage { get; set; }
  public double OverallKindCoverage { get; set; }

  /// <summary>
  /// The primary bottleneck limiting completeness score.
  /// </summary>
  public BottleneckInfo PrimaryBottleneck { get; set; } = new();

  /// <summary>
  /// Per-resource-type breakdown of gaps.
  /// Ordered by impact (highest count first).
  /// </summary>
  public List<ResourceTypeGapAnalysis> ResourceTypeAnalysis { get; set; } = new();

  /// <summary>
  /// Calculated completeness score (0-100%).
  /// </summary>
  public double CalculatedCompletenessScore => Math.Round(
      (OverallTagsCoverage * 0.30) +
      (OverallRegionCoverage * 0.25) +
      (OverallSkuCoverage * 0.25) +
      (OverallKindCoverage * 0.20), 1);
}

/// <summary>
/// The primary metadata field limiting completeness.
/// </summary>
public class BottleneckInfo
{
  /// <summary>Which metadata field is the bottleneck (Tags, Region, SKU, Kind)</summary>
  public string Field { get; set; } = string.Empty;

  /// <summary>Coverage percentage for this field (0-100%)</summary>
  public double Coverage { get; set; }

  /// <summary>How many resources are missing this field</summary>
  public int AffectedCount { get; set; }

  /// <summary>Weight of this field in completeness formula (30, 25, 25, or 20)</summary>
  public int WeightInFormula { get; set; }

  /// <summary>Estimated impact on completeness score (0-100%)</summary>
  public double EstimatedImpactOnScore =>
      Math.Round((100 - Coverage) / 100 * WeightInFormula, 1);

  /// <summary>
  /// Remediation priority (H/M/L) based on impact and ease of fix.
  /// High: Easy to fix (e.g., add tags to resources)
  /// Medium: Moderate effort (e.g., ensure SKU is captured in ARG)
  /// Low: Hard to fix or by design (e.g., "global" resources, management-plane resources)
  /// </summary>
  public string RemediationPriority => Field switch
  {
    "Tags" => "High", // Easy to add tags
    "Region" => "Low", // "global" resources are by design
    "SKU" => "Medium", // Some resource types don't have SKU
    "Kind" => "Low", // Not all resources have Kind
    _ => "Unknown"
  };

  /// <summary>
  /// Recommended remediation action.
  /// </summary>
  public string RemediationAdvice => Field switch
  {
    "Tags" => $"Add governance tags (environment, owner, cost-center) to {AffectedCount} resources. Use Azure Policy to enforce tagging. Check ServiceGroupAnalysisPage for which resources lack tags.",
    "Region" => $"{AffectedCount} resources have location='global' (by design for management-plane resources). Consider excluding from completeness scoring or treating separately.",
    "SKU" => $"{AffectedCount} resources missing SKU metadata. Verify Azure Resource Graph includes sku field for this resource type. Some IaaS resources may not populate SKU in ARG.",
    "Kind" => $"{AffectedCount} resources missing Kind field. This is normal for many resource types. Consider lower priority than Tags/Region/SKU.",
    _ => "Unknown remediation"
  };
}

/// <summary>
/// Gap analysis for a specific resource type.
/// </summary>
public class ResourceTypeGapAnalysis
{
  /// <summary>Azure resource type (e.g., Microsoft.Compute/virtualMachines)</summary>
  public string ResourceType { get; set; } = string.Empty;

  /// <summary>Number of resources of this type</summary>
  public int ResourceCount { get; set; }

  /// <summary>Completeness score for this resource type (0-100%)</summary>
  public double CompletenessScore { get; set; }

  /// <summary>Metadata coverage by field (0-100%)</summary>
  public double TagsCoverage { get; set; }
  public double RegionCoverage { get; set; }
  public double SkuCoverage { get; set; }
  public double KindCoverage { get; set; }

  /// <summary>
  /// List of which metadata fields are missing (e.g., "Tags: 5 missing", "SKU: 2 missing")
  /// </summary>
  public List<string> MetadataGaps { get; set; } = new();

  /// <summary>
  /// Estimated cost impact: completeness gaps × resource count.
  /// (Lower completeness = higher governance/compliance risk per resource)
  /// </summary>
  public double ImpactScore => CompletenessScore / 100 * ResourceCount;
}
