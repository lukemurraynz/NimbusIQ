using System.Text.Json;
using Atlas.ControlPlane.Infrastructure.Azure;
using Microsoft.Extensions.Logging;

namespace Atlas.ControlPlane.Application.Services;

/// <summary>
/// Computes well-being scores for a set of discovered Azure resources.
/// Scores range 0.0–1.0 (inclusive) where 1.0 is the best possible state.
/// </summary>
public class ScoringService
{
    private static readonly HashSet<string> GreenRegions = new(StringComparer.OrdinalIgnoreCase)
    {
        "westeurope", "northeurope", "swedencentral", "francecentral", "norwayeast",
        "australiaeast", "australiasoutheast",
        "japaneast", "japanwest",
        "canadacentral", "canadaeast",
        "brazilsouth"
    };

    private static readonly string[] ManagedServiceTypePatterns =
    {
        "containerapps", "managedclusters", "appservice", "web/sites",
        "postgresql/flexibleservers", "mysql/flexibleservers", "sql/servers",
        "cache/redis", "cosmosdb", "servicebus", "eventhubs"
    };

    private readonly ILogger<ScoringService> _logger;

    public ScoringService(ILogger<ScoringService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Calculates dimension scores for the discovered resources.
    /// </summary>
    public ScoreResult Calculate(
        IReadOnlyList<DiscoveredAzureResource> resources,
        Guid correlationId)
    {
        if (resources.Count == 0)
        {
            _logger.LogInformation(
                "No resources to score; returning default scores [correlation={CorrelationId}]",
                correlationId);
            return ScoreResult.Default;
        }

        var total = resources.Count;

        var completeness = CalculateCompleteness(resources, total);
        var taggingCoverage = CalculateTaggingCoverage(resources, total);

        var costEfficiency = CalculateCostEfficiency(resources, total);
        var availability = CalculateAvailability(resources, total);
        var utilization = CalculateUtilizationScore(resources, total);

        var security = CalculateSecurityScore(resources, total);
        var resiliency = CalculateResiliencyScore(resources, total);

        var managedServiceRatio = CalculateManagedServiceRatio(resources, total);
        var greenRegionUsage = CalculateGreenRegionUsage(resources, total);

        // Blend architecture and sustainability signals from agent patterns into Path A.
        completeness = Round((completeness * 0.85) + (managedServiceRatio * 0.15));
        utilization = Round((utilization * 0.75) + (greenRegionUsage * 0.25));

        var isProduction = DetectProductionEnvironment(resources);
        if (isProduction)
        {
            availability = Round(availability * 0.95 + (availability >= 0.8 ? 0.05 : 0.0));
            resiliency = Round(resiliency * 0.95 + (resiliency >= 0.8 ? 0.05 : 0.0));
        }

        _logger.LogInformation(
            "Scores calculated: completeness={Completeness}, cost={Cost}, availability={Availability}, security={Security}, " +
            "tagging={Tagging}, utilization={Utilization}, resiliency={Resiliency}, managed={ManagedRatio}, green={GreenRegion} " +
            "for {Total} resources (production={IsProduction}) [correlation={CorrelationId}]",
            completeness, costEfficiency, availability, security, taggingCoverage, utilization, resiliency,
            managedServiceRatio, greenRegionUsage, total, isProduction, correlationId);

        return new ScoreResult(
            Completeness: completeness,
            CostEfficiency: costEfficiency,
            Availability: availability,
            Security: security,
            ResourceCount: total,
            IsProductionEnvironment: isProduction,
            TaggingCoverage: taggingCoverage,
            Utilization: utilization,
            Resiliency: resiliency,
            ManagedServiceRatio: managedServiceRatio,
            GreenRegionUsage: greenRegionUsage);
    }

    /// <summary>
    /// Multi-factor completeness: Tags 30%, Region 25%, SKU 25%, Kind 20%.
    /// </summary>
    internal static double CalculateCompleteness(IReadOnlyList<DiscoveredAzureResource> resources, int total)
    {
        if (total == 0) return 0.0;

        var tagScore = (double)resources.Count(r => HasMeaningfulTags(r.Tags)) / total;
        var regionScore = (double)resources.Count(r =>
            !string.IsNullOrWhiteSpace(r.Location) &&
            !string.Equals(r.Location, "global", StringComparison.OrdinalIgnoreCase)) / total;
        var skuScore = (double)resources.Count(r => !string.IsNullOrWhiteSpace(r.Sku)) / total;
        var kindScore = (double)resources.Count(r => !string.IsNullOrWhiteSpace(r.Kind)) / total;

        return Round((tagScore * 0.30) + (regionScore * 0.25) + (skuScore * 0.25) + (kindScore * 0.20));
    }

    internal static double CalculateTaggingCoverage(IReadOnlyList<DiscoveredAzureResource> resources, int total)
    {
        if (total == 0) return 0.0;
        return Round((double)resources.Count(r => HasMeaningfulTags(r.Tags)) / total);
    }

    internal static double CalculateCostEfficiency(IReadOnlyList<DiscoveredAzureResource> resources, int total)
    {
        if (total == 0) return 0.0;

        var nonBillableTypes = new[]
        {
            "userassignedidentities", "managedidentities", "roleassignments", "roledefinitions",
            "locks", "policyassignments", "policydefinitions", "policysetdefinitions",
            "diagnosticsettings", "activitylogalerts", "actiongroups", "privateendpoints",
            "privatednszone", "networkwatchers", "networkintentpolicies", "networksecuritygroups/securityrules"
        };

        var billableTypes = new[]
        {
            "virtualmachines", "storageaccounts", "sqldatabases", "cosmosdb", "functionapps",
            "webapps", "managedclusters", "containerapps", "redis", "postgresql", "mysql",
            "mariadb", "sqlservers", "loadbalancers", "applicationgateways", "frontdoors",
            "cognitiveservices", "signalr", "eventgrids", "eventhubs", "servicebusnamespaces",
            "keyvaults", "disks", "publicipaddresses", "virtualnetworkgateways", "expressroutecircuits",
            "firewalls", "containerregistries", "loganalytics", "appinsights", "apimanagement",
            "searchservices", "batchaccounts"
        };

        var billableResources = resources.Where(r =>
            !nonBillableTypes.Any(nb => r.ResourceType.Contains(nb, StringComparison.OrdinalIgnoreCase))).ToList();

        var billableTotal = billableResources.Count;
        if (billableTotal == 0) return 1.0;

        var withCostData = billableResources.Count(r =>
            billableTypes.Any(t => r.ResourceType.Contains(t, StringComparison.OrdinalIgnoreCase)));

        return Round((double)withCostData / billableTotal);
    }

    internal static double CalculateAvailability(IReadOnlyList<DiscoveredAzureResource> resources, int total)
    {
        if (total == 0) return 0.0;

        var lowAvailabilityPatterns = new[] { "\"tier\":\"Basic\"", "\"tier\":\"Free\"", "\"tier\":\"Developer\"", "\"name\":\"Free\"" };
        var withLowAvailability = resources.Count(r =>
            r.Sku != null && lowAvailabilityPatterns.Any(p => r.Sku.Contains(p, StringComparison.OrdinalIgnoreCase)));

        return Round(1.0 - ((double)withLowAvailability / total));
    }

    internal static double CalculateUtilizationScore(IReadOnlyList<DiscoveredAzureResource> resources, int total)
    {
        if (total == 0) return 0.0;

        var inefficiencySignals = new[] { "premium", "_v1", "basic", "free", "developer" };
        var inefficient = resources.Count(r =>
        {
            var meta = string.Join(" ", r.Sku ?? string.Empty, r.Kind ?? string.Empty, r.Properties ?? string.Empty).ToLowerInvariant();
            return inefficiencySignals.Any(signal => meta.Contains(signal, StringComparison.OrdinalIgnoreCase));
        });

        return Round(1.0 - ((double)inefficient / total));
    }

    /// <summary>
    /// Metadata-based security scoring across five equal controls.
    /// </summary>
    internal static double CalculateSecurityScore(IReadOnlyList<DiscoveredAzureResource> resources, int total)
    {
        double publicAccessScore = 0;
        double tlsScore = 0;
        double identityScore = 0;
        double firewallScore = 0;
        double encryptionScore = 0;

        foreach (var resource in resources)
        {
            var resourceType = resource.ResourceType ?? string.Empty;
            var allMeta = string.Join(" ", resource.Sku ?? "", resource.Kind ?? "", resource.Tags ?? "", resource.ResourceType ?? "", resource.Properties ?? "");
            var metaLower = allMeta.ToLowerInvariant();

            if (metaLower.Contains("publicnetworkaccess") && metaLower.Contains("enabled"))
                publicAccessScore += 0.0;
            else if (metaLower.Contains("publicnetworkaccess") && (metaLower.Contains("disabled") || metaLower.Contains("secured")))
                publicAccessScore += 1.0;
            else
                publicAccessScore += 0.5;

            if (metaLower.Contains("https") || metaLower.Contains("tls") || metaLower.Contains("ssl"))
                tlsScore += 1.0;
            else if (resourceType.Contains("storage", StringComparison.OrdinalIgnoreCase) ||
                     resourceType.Contains("web", StringComparison.OrdinalIgnoreCase) ||
                     resourceType.Contains("sql", StringComparison.OrdinalIgnoreCase))
                tlsScore += 0.3;
            else
                tlsScore += 0.5;

            if (metaLower.Contains("managedidentity") || metaLower.Contains("systemassigned") ||
                metaLower.Contains("userassigned") || resourceType.Contains("identities", StringComparison.OrdinalIgnoreCase))
                identityScore += 1.0;
            else
                identityScore += 0.5;

            if (resourceType.Contains("privateendpoint", StringComparison.OrdinalIgnoreCase) ||
                resourceType.Contains("firewall", StringComparison.OrdinalIgnoreCase) ||
                metaLower.Contains("firewall") || metaLower.Contains("privateendpoint") || metaLower.Contains("vnet"))
                firewallScore += 1.0;
            else
                firewallScore += 0.5;

            if (metaLower.Contains("encrypt") || metaLower.Contains("cmk") ||
                metaLower.Contains("byok") || metaLower.Contains("keyvault"))
                encryptionScore += 1.0;
            else
                encryptionScore += 0.5;
        }

        if (total == 0) return 0.5;

        var score = (publicAccessScore / total * 0.20) +
                    (tlsScore / total * 0.20) +
                    (identityScore / total * 0.20) +
                    (firewallScore / total * 0.20) +
                    (encryptionScore / total * 0.20);

        return Round(score);
    }

    internal static double CalculateResiliencyScore(IReadOnlyList<DiscoveredAzureResource> resources, int total)
    {
        if (total == 0) return 0.0;
        var resilientCount = resources.Count(HasResiliencySignal);
        return Round((double)resilientCount / total);
    }

    internal static double CalculateManagedServiceRatio(IReadOnlyList<DiscoveredAzureResource> resources, int total)
    {
        if (total == 0) return 0.0;
        var managedCount = resources.Count(r => ManagedServiceTypePatterns.Any(pattern =>
            r.ResourceType.Contains(pattern, StringComparison.OrdinalIgnoreCase)));
        return Round((double)managedCount / total);
    }

    internal static double CalculateGreenRegionUsage(IReadOnlyList<DiscoveredAzureResource> resources, int total)
    {
        if (total == 0) return 0.0;
        var greenCount = resources.Count(r => !string.IsNullOrWhiteSpace(r.Location) && GreenRegions.Contains(r.Location));
        return Round((double)greenCount / total);
    }

    /// <summary>
    /// Detect production environment from resource tags or naming patterns.
    /// </summary>
    internal static bool DetectProductionEnvironment(IReadOnlyList<DiscoveredAzureResource> resources)
    {
        var prodIndicators = 0;
        foreach (var resource in resources)
        {
            var tags = resource.Tags?.ToLowerInvariant() ?? "";
            var name = resource.Name?.ToLowerInvariant() ?? "";
            var resourceGroup = resource.ResourceGroup?.ToLowerInvariant() ?? "";

            if (tags.Contains("\"environment\"") && (tags.Contains("prod") || tags.Contains("production")))
                prodIndicators++;
            else if (name.Contains("-prod") || name.Contains("prod-") || name.EndsWith("prod", StringComparison.Ordinal))
                prodIndicators++;
            else if (resourceGroup.Contains("-prod") || resourceGroup.Contains("prod-") || resourceGroup.Contains("production"))
                prodIndicators++;
        }

        return resources.Count > 0 && (double)prodIndicators / resources.Count >= 0.25;
    }

    private static bool HasResiliencySignal(DiscoveredAzureResource resource)
    {
        var meta = string.Join(" ", resource.Sku ?? string.Empty, resource.Kind ?? string.Empty, resource.Properties ?? string.Empty)
            .ToLowerInvariant();

        var resiliencySignals = new[] { "zones", "zone", "zoneredundant", "geo-redundant", "zrs", "grs", "gzone" };
        return resiliencySignals.Any(signal => meta.Contains(signal, StringComparison.OrdinalIgnoreCase));
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

    private static double Round(double value) =>
        Math.Round(Math.Max(0.0, Math.Min(1.0, value)), 4);
}

/// <summary>Per-dimension score result (0.0–1.0 scale).</summary>
public record ScoreResult(
    double Completeness,
    double CostEfficiency,
    double Availability,
    double Security,
    int ResourceCount,
    bool IsProductionEnvironment = false,
    double TaggingCoverage = 0.0,
    double Utilization = 0.0,
    double Resiliency = 0.0,
    double ManagedServiceRatio = 0.0,
    double GreenRegionUsage = 0.0)
{
    /// <summary>Default scores when no resources are available.</summary>
    public static readonly ScoreResult Default = new(0.0, 0.0, 0.0, 0.0, 0);

    /// <summary>
    /// Returns weighted average with higher emphasis on security and reliability signals.
    /// </summary>
    public double GetAverageScore() =>
        Math.Round(
            (Security * 0.35) +
            (Availability * 0.30) +
            (CostEfficiency * 0.20) +
            (Completeness * 0.15),
            4);

    /// <summary>Serializes the scores to a compact JSON string for storage in JSON payloads.</summary>
    public string ToJson() =>
        JsonSerializer.Serialize(new
        {
            completeness = Completeness,
            cost_efficiency = CostEfficiency,
            availability = Availability,
            security = Security,
            tagging_coverage = TaggingCoverage,
            utilization = Utilization,
            resiliency = Resiliency,
            managed_service_ratio = ManagedServiceRatio,
            green_region_usage = GreenRegionUsage,
            resource_count = ResourceCount,
            scored_at = DateTime.UtcNow.ToString("O")
        });
}
