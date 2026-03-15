using System.Reflection;
using System.Text.Json;
using Atlas.ControlPlane.Domain.Entities;
using Atlas.ControlPlane.Application.Services;
using Microsoft.Extensions.Logging;
using DomainRecommendation = Atlas.ControlPlane.Domain.Entities.Recommendation;

namespace Atlas.ControlPlane.Application.Recommendations;

public sealed class AvmIacExampleService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<AvmIacExampleService> _logger;
    private readonly AIChatService? _aiChatService;
    private readonly IRecommendationGroundingClient? _groundingClient;

    private static readonly IReadOnlyDictionary<string, AvmModuleSelection> ModuleMap =
        new Dictionary<string, AvmModuleSelection>(StringComparer.OrdinalIgnoreCase)
        {
            ["microsoft.storage/storageaccounts"] = new("avm/res/storage/storage-account", "avm-res-storage-storageaccount"),
            ["microsoft.keyvault/vaults"] = new("avm/res/key-vault/vault", "avm-res-keyvault-vault"),
            ["microsoft.web/sites"] = new("avm/res/web/site", "avm-res-web-site"),
            ["microsoft.containerservice/managedclusters"] = new("avm/res/container-service/managed-cluster", "avm-res-containerservice-managedcluster"),
            ["microsoft.network/privateendpoints"] = new("avm/res/network/private-endpoint", "avm-res-network-privateendpoint"),
            ["microsoft.resources/resourcegroups"] = new("avm/res/resources/resource-group", "avm-res-resources-resourcegroup"),
            ["microsoft.cognitiveservices/accounts"] = new("avm/res/cognitive-services/account", "avm-res-cognitiveservices-account"),
            ["microsoft.machinelearningservices/workspaces"] = new("avm/res/machine-learning-services/workspace", "avm-res-machinelearningservices-workspace"),
            ["microsoft.containerregistry/registries"] = new("avm/res/container-registry/registry", "avm-res-containerregistry-registry"),
            ["microsoft.appconfiguration/configurationstores"] = new("avm/res/app-configuration/configuration-store", "avm-res-appconfiguration-configurationstore"),
            ["microsoft.operationalinsights/workspaces"] = new("avm/res/operational-insights/workspace", "avm-res-operationalinsights-workspace"),
            ["microsoft.network/virtualnetworks"] = new("avm/res/network/virtual-network", "avm-res-network-virtualnetwork"),
            // Path uses the unusual 'db-for-postgre-sql' segment — verified against bicep-registry-modules.
            ["microsoft.dbforpostgresql/flexibleservers"] = new("avm/res/db-for-postgre-sql/flexible-server", "avm-res-postgresql-flexibleserver"),
        };

    // Loaded from Prompts/iac-remediation-system.md (embedded resource) so the prompt can be
    // updated independently of compiled code.
    private static readonly string IacRemediationSystemPrompt = LoadEmbeddedPrompt("iac-remediation-system.md");

    public AvmIacExampleService(
        IHttpClientFactory httpClientFactory,
        ILogger<AvmIacExampleService> logger,
        AIChatService? aiChatService = null,
        IRecommendationGroundingClient? groundingClient = null)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _aiChatService = aiChatService;
        _groundingClient = groundingClient;
    }

    private static string LoadEmbeddedPrompt(string fileName)
    {
        var assembly = typeof(AvmIacExampleService).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .Single(n => n.EndsWith(fileName, StringComparison.OrdinalIgnoreCase));
        using var stream = assembly.GetManifestResourceStream(resourceName)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Trim();
    }

    public async Task<AvmIacExampleResult> BuildExamplesAsync(
        DomainRecommendation recommendation,
        CancellationToken cancellationToken = default)
    {
        // Collect all affected resource IDs: primary ResourceId plus any additional from ImpactedServices.
        var allResourceIds = CollectAllResourceIds(recommendation);
        var primaryType = NormalizeResourceType(allResourceIds[0]);

        // Resolve unique module+version pairs (deduplicated by module path so we fetch versions once).
        var modulesByResourceId = allResourceIds
            .Select(rid => (ResourceId: rid, NormalizedType: NormalizeResourceType(rid), Module: ResolveModule(NormalizeResourceType(rid))))
            .ToList();

        var uniqueModules = modulesByResourceId
            .Select(x => x.Module)
            .DistinctBy(m => m.BicepModulePath)
            .ToList();

        var bicepVersionTasks = uniqueModules.ToDictionary(
            m => m.BicepModulePath,
            m => TryGetLatestBicepVersionAsync(m.BicepModulePath, cancellationToken));
        var terraformVersionTasks = uniqueModules.ToDictionary(
            m => m.TerraformModuleName,
            m => TryGetLatestTerraformVersionAsync(m.TerraformModuleName, cancellationToken));

        await Task.WhenAll(bicepVersionTasks.Values.Concat<Task>(terraformVersionTasks.Values));

        var bicepVersions = bicepVersionTasks.ToDictionary(
            kvp => kvp.Key,
            kvp => kvp.Value.Result ?? "latest");
        var terraformVersions = terraformVersionTasks.ToDictionary(
            kvp => kvp.Key,
            kvp => kvp.Value.Result ?? "latest");

        var primaryModule = modulesByResourceId[0].Module;
        var primaryBicepVersion = bicepVersions[primaryModule.BicepModulePath];
        var primaryTerraformVersion = terraformVersions[primaryModule.TerraformModuleName];

        var bicepModuleBlocks = modulesByResourceId.Select(x =>
        {
            var resourceName = x.ResourceId.Split('/').LastOrDefault() ?? "resource";
            var bv = bicepVersions[x.Module.BicepModulePath];
            var bicepSecurityParams = GetBicepSecurityDefaultsBlock(x.Module.BicepModulePath);
            return $$"""
module remediation_{{resourceName.Replace('-', '_').Replace('.', '_').ToLowerInvariant()}} 'br/public:{{x.Module.BicepModulePath}}:{{bv}}' = {
  name: 'remediation-${uniqueString(resourceGroup().id, '{{resourceName}}')}'
  params: {
    name: '{{resourceName}}'
    location: resourceGroup().location
{{bicepSecurityParams}}    tags: {
      deployedBy: 'nimbusiq'
      recommendationId: '{{recommendation.Id}}'
      actionType: '{{recommendation.ActionType}}'
    }
  }
}
""";
        });

        var deterministicBicep = string.Join("\n", bicepModuleBlocks);

        var tfProviderBlock = """
terraform {
  required_version = ">= 1.9.0"
  required_providers {
    azurerm = {
      source  = "hashicorp/azurerm"
      version = "~> 4.0"
    }
  }
}

provider "azurerm" {
  features {}
}

variable "location" {
  description = "Azure region for resource deployment."
  type        = string
}

variable "resource_group_name" {
  description = "Name of the resource group to deploy resources into."
  type        = string
}

""";

        var tfModuleBlocks = modulesByResourceId.Select(x =>
        {
            var resourceName = x.ResourceId.Split('/').LastOrDefault() ?? "resource";
            var moduleNameSafe = resourceName.Replace('-', '_').Replace('.', '_').ToLowerInvariant();
            var tv = terraformVersions[x.Module.TerraformModuleName];
            var tfSecurityParams = GetTerraformSecurityDefaultsBlock(x.Module.TerraformModuleName);
            return $$"""
module "{{moduleNameSafe}}_remediation" {
  source  = "Azure/{{x.Module.TerraformModuleName}}/azurerm"
  version = "{{tv}}"

  name                = "{{resourceName}}"
  location            = var.location
  resource_group_name = var.resource_group_name
{{tfSecurityParams}}
  tags = {
    deployedBy       = "nimbusiq"
    recommendationId = "{{recommendation.Id}}"
    actionType       = "{{recommendation.ActionType}}"
  }
}
""";
        });

        var deterministicTerraform = tfProviderBlock + string.Join("\n", tfModuleBlocks);

        var aiGenerated = await TryGenerateWithFoundryAgentAsync(
            recommendation,
            primaryModule,
            primaryBicepVersion,
            primaryTerraformVersion,
            deterministicBicep,
            deterministicTerraform,
            cancellationToken);

        var summary = aiGenerated?.Summary ??
            "Generated deterministic remediation templates grounded in published AVM modules and latest resolved versions.";
        var bicepExample = aiGenerated?.BicepExample ?? deterministicBicep;
        var terraformExample = aiGenerated?.TerraformExample ?? deterministicTerraform;
        var generatedBy = aiGenerated is not null ? "foundry_agent" : "deterministic_fallback";

        var citedModules = modulesByResourceId
            .Select(x => new AvmModuleReference(
                x.Module.BicepModulePath,
                bicepVersions[x.Module.BicepModulePath],
                x.Module.TerraformModuleName,
                terraformVersions[x.Module.TerraformModuleName]))
            .DistinctBy(r => r.BicepModulePath)
            .ToList();

        return new AvmIacExampleResult
        {
            RecommendationId = recommendation.Id,
            ResourceType = string.IsNullOrWhiteSpace(primaryType) ? "unknown" : primaryType,
            Summary = summary,
            BicepModulePath = primaryModule.BicepModulePath,
            BicepVersion = primaryBicepVersion,
            TerraformModuleName = primaryModule.TerraformModuleName,
            TerraformVersion = primaryTerraformVersion,
            BicepExample = bicepExample.Trim(),
            TerraformExample = terraformExample.Trim(),
            GeneratedBy = generatedBy,
            CitedModules = citedModules,
            EvidenceUrls =
            [
                "https://azure.github.io/Azure-Verified-Modules/indexes/bicep/bicep-resource-modules/",
                "https://azure.github.io/Azure-Verified-Modules/indexes/terraform/tf-resource-modules/",
                ..citedModules.Select(m => $"https://registry.terraform.io/modules/Azure/{m.TerraformModuleName}/azurerm"),
                ..citedModules.Select(m => $"https://mcr.microsoft.com/v2/bicep/{m.BicepModulePath}/tags/list")
            ],
            GeneratedAtUtc = DateTime.UtcNow
        };
    }

    private static IReadOnlyList<string> CollectAllResourceIds(DomainRecommendation recommendation)
    {
        var ids = new List<string>();

        if (!string.IsNullOrWhiteSpace(recommendation.ResourceId))
        {
            ids.Add(recommendation.ResourceId);
        }

        if (!string.IsNullOrWhiteSpace(recommendation.ImpactedServices))
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<string[]>(recommendation.ImpactedServices);
                if (parsed is { Length: > 0 })
                {
                    foreach (var id in parsed)
                    {
                        if (!string.IsNullOrWhiteSpace(id) &&
                            !ids.Contains(id, StringComparer.OrdinalIgnoreCase))
                        {
                            ids.Add(id);
                        }
                    }
                }
            }
            catch (JsonException)
            {
                // ImpactedServices was not a valid JSON array; primary ResourceId is sufficient.
            }
        }

        if (ids.Count == 0)
        {
            ids.Add("unknown");
        }

        return ids;
    }

    private static string NormalizeResourceType(string? resourceId)
    {
        if (string.IsNullOrWhiteSpace(resourceId))
        {
            return string.Empty;
        }

        var parts = resourceId.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var providersIndex = Array.FindIndex(parts, p => string.Equals(p, "providers", StringComparison.OrdinalIgnoreCase));
        if (providersIndex < 0 || providersIndex + 2 >= parts.Length)
        {
            return string.Empty;
        }

        var provider = parts[providersIndex + 1].ToLowerInvariant();
        var typeName = parts[providersIndex + 2].ToLowerInvariant();
        return $"{provider}/{typeName}";
    }

    private static AvmModuleSelection ResolveModule(string normalizedType)
    {
        if (!string.IsNullOrWhiteSpace(normalizedType) && ModuleMap.TryGetValue(normalizedType, out var selected))
        {
            return selected;
        }

        return new AvmModuleSelection("avm/res/resources/resource-group", "avm-res-resources-resourcegroup");
    }

    private async Task<string?> TryGetLatestBicepVersionAsync(string modulePath, CancellationToken cancellationToken)
    {
        try
        {
            var client = _httpClientFactory.CreateClient(nameof(AvmIacExampleService));
            using var response = await client.GetAsync($"https://mcr.microsoft.com/v2/bicep/{modulePath}/tags/list", cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (!doc.RootElement.TryGetProperty("tags", out var tags) || tags.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var versions = tags.EnumerateArray()
                .Select(t => t.GetString())
                .Where(v => !string.IsNullOrWhiteSpace(v) && IsStableSemver(v!))
                .ToList();

            return versions
                .OrderByDescending(v => ParseSemver(v!))
                .FirstOrDefault();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to resolve latest Bicep AVM version for {ModulePath}", modulePath);
            return null;
        }
    }

    private async Task<string?> TryGetLatestTerraformVersionAsync(string moduleName, CancellationToken cancellationToken)
    {
        try
        {
            var client = _httpClientFactory.CreateClient(nameof(AvmIacExampleService));
            using var response = await client.GetAsync($"https://registry.terraform.io/v1/modules/Azure/{moduleName}/azurerm/versions", cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (!doc.RootElement.TryGetProperty("modules", out var modules) || modules.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var versions = modules
                .EnumerateArray()
                .SelectMany(m => m.TryGetProperty("versions", out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray() : [])
                .Select(v => v.TryGetProperty("version", out var versionEl) ? versionEl.GetString() : null)
                .Where(v => !string.IsNullOrWhiteSpace(v) && IsStableSemver(v!))
                .ToList();

            return versions
                .OrderByDescending(v => ParseSemver(v!))
                .FirstOrDefault();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to resolve latest Terraform AVM version for {ModuleName}", moduleName);
            return null;
        }
    }

    private static bool IsStableSemver(string version) =>
        System.Text.RegularExpressions.Regex.IsMatch(version, @"^\d+\.\d+\.\d+$");

    private static Version ParseSemver(string version)
    {
        if (Version.TryParse(version, out var parsed))
        {
            return parsed;
        }

        return new Version(0, 0, 0);
    }

    private sealed record AvmModuleSelection(string BicepModulePath, string TerraformModuleName);

    // Returns 4-space-indented Bicep params with a trailing newline, or empty string for unmapped types.
    private static string GetBicepSecurityDefaultsBlock(string bicepModulePath) =>
        bicepModulePath switch
        {
            // NOTE: Container Registry private endpoints require Premium SKU.
            // privateEndpoints is an array of objects — there is no enablePrivateEndpoint bool.
            "avm/res/container-registry/registry" =>
                "    acrSku: 'Premium'\n    anonymousPullEnabled: false\n    azureADAuthenticationAsArmPolicyStatus: 'enabled'\n    privateEndpoints: [\n      {\n        subnetResourceId: '<REPLACE_WITH_SUBNET_RESOURCE_ID>'\n        privateDnsZoneGroup: {\n          privateDnsZoneGroupConfigs: [\n            {\n              privateDnsZoneResourceId: '<REPLACE_WITH_PRIVATE_DNS_ZONE_RESOURCE_ID>'\n            }\n          ]\n        }\n      }\n    ]\n",
            "avm/res/key-vault/vault" =>
            // purgeProtection required for soft-delete guarantee; publicNetworkAccess forces private-only access
            "    sku: 'standard'\n    enableRbacAuthorization: true\n    enableSoftDelete: true\n    softDeleteRetentionInDays: 90\n    enablePurgeProtection: true\n    publicNetworkAccess: 'Disabled'\n    privateEndpoints: [\n      {\n        subnetResourceId: '<REPLACE_WITH_SUBNET_RESOURCE_ID>'\n        privateDnsZoneGroup: {\n          privateDnsZoneGroupConfigs: [\n            {\n              privateDnsZoneResourceId: '<REPLACE_WITH_PRIVATE_DNS_ZONE_RESOURCE_ID>'\n            }\n          ]\n        }\n      }\n    ]\n",
            "avm/res/storage/storage-account" =>
                // service field is REQUIRED in privateEndpoints (blob|table|queue|file|web|dfs)
                "    skuName: 'Standard_LRS'\n    kind: 'StorageV2'\n    allowBlobPublicAccess: false\n    minimumTlsVersion: 'TLS1_2'\n    supportsHttpsTrafficOnly: true\n    publicNetworkAccess: 'Disabled'\n    networkAcls: {\n      bypass: [\n        'AzureServices'\n      ]\n      defaultAction: 'Deny'\n    }\n    privateEndpoints: [\n      {\n        subnetResourceId: '<REPLACE_WITH_SUBNET_RESOURCE_ID>'\n        service: 'blob'\n        privateDnsZoneGroup: {\n          privateDnsZoneGroupConfigs: [\n            {\n              privateDnsZoneResourceId: '<REPLACE_WITH_PRIVATE_DNS_ZONE_RESOURCE_ID>'\n            }\n          ]\n        }\n      }\n    ]\n",
            // httpsOnly + managed identity are the minimum WAF baseline for App Service / Function App
            "avm/res/web/site" =>
                "    httpsOnly: true\n    publicNetworkAccess: 'Disabled'\n    managedIdentities: {\n      systemAssigned: true\n    }\n    siteConfig: {\n      minTlsVersion: '1.2'\n      ftpsState: 'FtpsOnly'\n      http20Enabled: true\n    }\n",
            // Cognitive Services: disable public access + managed identity; networkAcls defaultAction must be Deny
            "avm/res/cognitive-services/account" =>
                "    publicNetworkAccess: 'Disabled'\n    managedIdentities: {\n      systemAssigned: true\n    }\n    networkAcls: {\n      defaultAction: 'Deny'\n      ipRules: []\n      virtualNetworkRules: []\n    }\n    privateEndpoints: [\n      {\n        subnetResourceId: '<REPLACE_WITH_SUBNET_RESOURCE_ID>'\n        privateDnsZoneGroup: {\n          privateDnsZoneGroupConfigs: [\n            {\n              privateDnsZoneResourceId: '<REPLACE_WITH_PRIVATE_DNS_ZONE_RESOURCE_ID>'\n            }\n          ]\n        }\n      }\n    ]\n",
            // App Configuration: disable local auth (shared-key access) and restrict public network access
            "avm/res/app-configuration/configuration-store" =>
                "    disableLocalAuth: true\n    publicNetworkAccess: 'Disabled'\n    privateEndpoints: [\n      {\n        subnetResourceId: '<REPLACE_WITH_SUBNET_RESOURCE_ID>'\n        privateDnsZoneGroup: {\n          privateDnsZoneGroupConfigs: [\n            {\n              privateDnsZoneResourceId: '<REPLACE_WITH_PRIVATE_DNS_ZONE_RESOURCE_ID>'\n            }\n          ]\n        }\n      }\n    ]\n",
            // availabilityZone is an INT (1|2|3) — not a string. Verified against bicep-registry-modules AVM schema.
            // highAvailability: 'ZoneRedundant' is the actual switch that enables HA across zones;
            // setting availabilityZone alone only pins the primary replica and does NOT enable HA.
            // Zone 3 places the primary in the third physical zone; Azure picks the standby zone automatically.
            "avm/res/db-for-postgre-sql/flexible-server" =>
                "    availabilityZone: 3\n    highAvailability: 'ZoneRedundant'\n    backupRetentionDays: 7\n    geoRedundantBackup: 'Enabled'\n",
            _ => string.Empty
        };

    // Returns 2-space-indented HCL params with a trailing newline, or empty string for unmapped types.
    private static string GetTerraformSecurityDefaultsBlock(string tfModuleName) =>
        tfModuleName switch
        {
            // availability_zone is a string ("1"|"2"|"3") in TF AVM — the AzureRM provider uses strings for zones.
            // high_availability.mode = "ZoneRedundant" is the actual HA enablement; the zone param alone does nothing.
            "avm-res-postgresql-flexibleserver" =>
                "  availability_zone   = \"3\"\n  high_availability = {\n    mode = \"ZoneRedundant\"\n  }\n  backup_retention_days = 7\n  geo_redundant_backup  = \"Enabled\"",
            // NOTE: Container Registry private endpoints require SKU = "Premium".
            // private_endpoints is a map of objects — there is no enable_private_endpoint bool.
            "avm-res-containerregistry-registry" =>
                "  sku                    = \"Premium\"\n  anonymous_pull_enabled = false\n  private_endpoints = {\n    pe1 = {\n      subnet_resource_id            = \"<REPLACE_WITH_SUBNET_RESOURCE_ID>\"\n      private_dns_zone_resource_ids = [\"<REPLACE_WITH_PRIVATE_DNS_ZONE_RESOURCE_ID>\"]\n    }\n  }",
            "avm-res-keyvault-vault" =>
            "  sku_name                      = \"standard\"\n  enable_rbac_authorization    = true\n  soft_delete_retention_days   = 90\n  purge_protection_enabled     = true\n  public_network_access_enabled = false\n  private_endpoints = {\n    pe1 = {\n      subnet_resource_id            = \"<REPLACE_WITH_SUBNET_RESOURCE_ID>\"\n      private_dns_zone_resource_ids = [\"<REPLACE_WITH_PRIVATE_DNS_ZONE_RESOURCE_ID>\"]\n    }\n  }",
            "avm-res-storage-storageaccount" =>
                // subresource_names = [\"blob\"] — set to the target service (blob/table/queue/file/web/dfs)
                "  account_tier                    = \"Standard\"\n  account_replication_type       = \"LRS\"\n  account_kind                   = \"StorageV2\"\n  min_tls_version                = \"TLS1_2\"\n  allow_nested_items_to_be_public = false\n  https_traffic_only_enabled     = true\n  public_network_access_enabled  = false\n  network_rules = {\n    default_action = \"Deny\"\n    bypass         = [\"AzureServices\"]\n  }\n  private_endpoints = {\n    pe1 = {\n      subnet_resource_id            = \"<REPLACE_WITH_SUBNET_RESOURCE_ID>\"\n      subresource_names             = [\"blob\"]\n      private_dns_zone_resource_ids = [\"<REPLACE_WITH_PRIVATE_DNS_ZONE_RESOURCE_ID>\"]\n    }\n  }",
            "avm-res-web-site" =>
                "  https_only            = true\n  public_network_access = \"Disabled\"\n  managed_identities    = { system_assigned = true }\n  site_config = {\n    minimum_tls_version = \"1.2\"\n    ftps_state          = \"FtpsOnly\"\n    http2_enabled       = true\n  }",
            "avm-res-cognitiveservices-account" =>
                "  public_network_access = \"Disabled\"\n  managed_identities    = { system_assigned = true }\n  network_acls = {\n    default_action = \"Deny\"\n  }\n  private_endpoints = {\n    pe1 = {\n      subnet_resource_id            = \"<REPLACE_WITH_SUBNET_RESOURCE_ID>\"\n      private_dns_zone_resource_ids = [\"<REPLACE_WITH_PRIVATE_DNS_ZONE_RESOURCE_ID>\"]\n    }\n  }",
            "avm-res-appconfiguration-configurationstore" =>
                "  local_auth_disabled   = true\n  public_network_access = \"Disabled\"\n  private_endpoints = {\n    pe1 = {\n      subnet_resource_id            = \"<REPLACE_WITH_SUBNET_RESOURCE_ID>\"\n      private_dns_zone_resource_ids = [\"<REPLACE_WITH_PRIVATE_DNS_ZONE_RESOURCE_ID>\"]\n    }\n  }",
            _ => string.Empty
        };

    private async Task<FoundryGeneratedTemplates?> TryGenerateWithFoundryAgentAsync(
        DomainRecommendation recommendation,
        AvmModuleSelection module,
        string bicepVersion,
        string terraformVersion,
        string deterministicBicep,
        string deterministicTerraform,
        CancellationToken cancellationToken)
    {
        if (_aiChatService is null || !_aiChatService.IsAIAvailable)
        {
            return null;
        }

        try
        {
            // Pre-fetch Learn MCP grounding docs before building the prompt.
            // TryGroundAsync queries WAF docs using both axes:
            //   1. recommendation.Category  → WAF pillar (Security, Reliability, …)
            //   2. resource type from ResourceId → specific Azure resource being changed
            // This ensures the LLM guidance aligns to the recommendation intent AND the
            // impacted resource — satisfying the dual-axis grounding requirement.
            var learnGrounding = await TryFetchIacLearnGroundingAsync(recommendation, cancellationToken);

            var groundingPayload = new
            {
                recommendation.Id,
                recommendation.ResourceId,
                recommendation.RecommendationType,
                recommendation.ActionType,
                recommendation.Category,
                recommendation.Title,
                recommendation.Summary,
                recommendation.Description,
                module = new
                {
                    bicepPath = module.BicepModulePath,
                    bicepVersion,
                    terraformName = module.TerraformModuleName,
                    terraformVersion
                },
                deterministicTemplates = new
                {
                    bicep = deterministicBicep,
                    terraform = deterministicTerraform
                },
                // Citation titles + URLs from Learn MCP, scoped to the WAF pillar and
                // resource type. Null when the grounding client is unavailable or times out.
                learnDocsContext = learnGrounding is not null
                    ? (object)new
                    {
                        resourceTypeQuery = learnGrounding.Provenance.GroundingQuery,
                        citations = learnGrounding.Citations.Take(5).Select(c => new
                        {
                            c.Title,
                            c.Url,
                            c.Query
                        }).ToArray(),
                        evidenceUrls = learnGrounding.EvidenceUrls.Take(5).ToArray()
                    }
                    : null
            };

            // Use structured JSON generation instead of the chat endpoint — the chat path wraps
            // the prompt in a markdown-framing system prompt and caps tokens at 1200, both of
            // which prevent the model from returning the strict JSON the IaC templates require.
            // System prompt is loaded from Prompts/iac-remediation-system.md (embedded resource).
            var systemPrompt = IacRemediationSystemPrompt;

            var userPrompt = $$"""
Improve the deterministic Bicep and Terraform templates in the grounding context to correctly
remediate the recommendation. The templates must target the actual affected resource types,
not a generic resource-group placeholder. Return improved templates as strict JSON.

Grounding context:
{{JsonSerializer.Serialize(groundingPayload)}}
""";

            // 4096 tokens — sufficient for multi-resource Bicep + Terraform blocks
            var rawResponse = await _aiChatService.GenerateStructuredJsonAsync(
                systemPrompt,
                userPrompt,
                maxTokens: 4096,
                cancellationToken);

            var extractedJson = ExtractJsonObject(rawResponse);
            if (string.IsNullOrWhiteSpace(extractedJson))
            {
                _logger.LogWarning(
                    "Foundry agent returned no extractable JSON for recommendation {RecommendationId}; using deterministic fallback.",
                    recommendation.Id);
                return null;
            }

            _logger.LogDebug(
                "Foundry raw response for {RecommendationId}: {Response}",
                recommendation.Id, rawResponse);

            // Use PropertyNameCaseInsensitive because the model returns camelCase JSON
            // (e.g. "bicepExample") while the C# record uses PascalCase properties.
            var parsed = JsonSerializer.Deserialize<FoundryGeneratedTemplates>(
                extractedJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (parsed is null ||
                string.IsNullOrWhiteSpace(parsed.BicepExample) ||
                string.IsNullOrWhiteSpace(parsed.TerraformExample))
            {
                _logger.LogWarning(
                    "Foundry agent JSON was incomplete for recommendation {RecommendationId}; using deterministic fallback.",
                    recommendation.Id);
                return null;
            }

            return parsed;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Foundry agent generation failed for recommendation {RecommendationId}; using deterministic fallback.",
                recommendation.Id);
            return null;
        }
    }

    private async Task<GroundingEnrichmentResult?> TryFetchIacLearnGroundingAsync(
        DomainRecommendation recommendation,
        CancellationToken cancellationToken)
    {
        if (_groundingClient is null)
        {
            return null;
        }

        try
        {
            // Time-box the grounding call so a slow or unavailable Learn MCP server
            // does not delay IaC generation. 8 s is generous for a cached/fast response.
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(8));
            return await _groundingClient.TryGroundAsync(recommendation, cts.Token);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(
                ex,
                "Learn grounding fetch skipped for IaC generation of {RecommendationId}; continuing without context.",
                recommendation.Id);
            return null;
        }
    }

    private static string? ExtractJsonObject(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var trimmed = text.Trim();
        if (trimmed.StartsWith("{"))
        {
            return trimmed;
        }

        var codeFenceStart = trimmed.IndexOf("```", StringComparison.Ordinal);
        if (codeFenceStart >= 0)
        {
            var codeFenceEnd = trimmed.LastIndexOf("```", StringComparison.Ordinal);
            if (codeFenceEnd > codeFenceStart)
            {
                var fencedContent = trimmed[(codeFenceStart + 3)..codeFenceEnd].Trim();
                if (fencedContent.StartsWith("json", StringComparison.OrdinalIgnoreCase))
                {
                    fencedContent = fencedContent[4..].Trim();
                }

                if (fencedContent.StartsWith("{"))
                {
                    return fencedContent;
                }
            }
        }

        var firstBrace = trimmed.IndexOf('{');
        var lastBrace = trimmed.LastIndexOf('}');
        if (firstBrace >= 0 && lastBrace > firstBrace)
        {
            return trimmed[firstBrace..(lastBrace + 1)];
        }

        return null;
    }

    private sealed record FoundryGeneratedTemplates
    {
        public string Summary { get; init; } = string.Empty;
        public string BicepExample { get; init; } = string.Empty;
        public string TerraformExample { get; init; } = string.Empty;
    }
}

public sealed class AvmIacExampleResult
{
    public Guid RecommendationId { get; init; }
    public string ResourceType { get; init; } = string.Empty;
    public string Summary { get; init; } = string.Empty;
    public string BicepModulePath { get; init; } = string.Empty;
    public string BicepVersion { get; init; } = string.Empty;
    public string TerraformModuleName { get; init; } = string.Empty;
    public string TerraformVersion { get; init; } = string.Empty;
    public string BicepExample { get; init; } = string.Empty;
    public string TerraformExample { get; init; } = string.Empty;
    public string GeneratedBy { get; init; } = "deterministic_fallback";
    public IReadOnlyList<AvmModuleReference> CitedModules { get; init; } = [];
    public IReadOnlyList<string> EvidenceUrls { get; init; } = [];
    public DateTime GeneratedAtUtc { get; init; }
}

public sealed record AvmModuleReference(
    string BicepModulePath,
    string BicepVersion,
    string TerraformModuleName,
    string TerraformVersion);
