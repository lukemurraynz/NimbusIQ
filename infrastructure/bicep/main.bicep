targetScope = 'subscription'

@description('Azure region for all resources')
param location string = 'uksouth'

@description('Environment name (prod)')
@allowed(['prod'])
param environment string = 'prod'

@description('Project name prefix')
@minLength(3)
@maxLength(12)
param projectName string = 'nimbusiq'

@description('PostgreSQL admin password - OPTIONAL (managed identity is primary auth)')
@secure()
param postgresAdminPassword string = ''

@description('Allow broad Azure services access to PostgreSQL when public networking is enabled. Keep false for least privilege.')
param allowAzureServicesToPostgres bool = false

@description('Explicit IPv4 allowlist for PostgreSQL firewall rules when public networking is enabled (for example Container Apps egress IPs).')
param postgresAllowedIpAddresses array = []

@description('Full image names for Container Apps (typically set by azd deploy outputs)')
param controlPlaneImageName string = ''
param agentOrchestratorImageName string = ''
param frontendImageName string = ''

@description('Allowed CIDR ranges for frontend public ingress. Empty array leaves frontend ingress unrestricted.')
param frontendAllowedIpCidrs array = []

@description('Enable read-only anonymous API policies in production for dashboard/demo access.')
param allowAnonymousReadOnlyInProd bool = false

@description('Enable full anonymous API policies in production for demo mode. Overrides read-only mode when true.')
param allowAnonymousFullInProd bool = false

@description('Public bootstrap image used until azd deploy publishes images to ACR')
param bootstrapImage string = 'mcr.microsoft.com/dotnet/samples:aspnetapp'

@description('Skip Container App creation (not recommended when using azd deploy)')
param skipContainerApps bool = true

@description('Enable Azure AI Foundry and AI Services (requires Azure OpenAI access)')
param enableAIFoundry bool = true

@description('Enable private networking with VNet (BYO VNet topology)')
param enablePrivateNetworking bool = false

@description('Enable AI model deployments (gpt-4o, text-embedding-ada-002) on the AI Services account')
param enableModelDeployments bool = false

@description('Enable AI Services capability host for Agents (preview)')
param enableCapabilityHost bool = false

@description('Enable Network Security Perimeter for Foundry resources')
param enableNetworkSecurityPerimeter bool = false

@description('NSP mode: Learning or Enforced')
@allowed(['Learning', 'Enforced'])
param nspMode string = 'Learning'

@description('Tags to apply to all resources')
param tags object = {
  project: 'nimbusiq'
  environment: environment
  managedBy: 'bicep'
}

// Generate unique suffix for globally unique resources
var uniqueSuffix = uniqueString(subscription().subscriptionId, projectName, environment)
var resourceGroupName = '${projectName}-${environment}-rg'
var runtimeIdentityName = '${projectName}-acr-pull-${environment}'
var keyVaultName = '${projectName}kv${substring(uniqueSuffix, 0, 8)}'
var aiFoundryProjectName = '${projectName}-project-${environment}'

// Network configuration
var vnetName = '${projectName}-vnet-${environment}'
var vnetAddressPrefix = '10.0.0.0/16'
var containerAppsSubnetName = 'snet-container-apps'
var containerAppsSubnetPrefix = '10.0.0.0/23'
var postgresSubnetName = 'snet-postgres'
var postgresSubnetPrefix = '10.0.2.0/24'
var aiFoundrySubnetName = 'snet-ai-foundry'
var aiFoundrySubnetPrefix = '10.0.3.0/24'
var privateEndpointsSubnetName = 'snet-private-endpoints'
var privateEndpointsSubnetPrefix = '10.0.4.0/24'

var postgresAllowIpFirewallRules = [
  for ip in postgresAllowedIpAddresses: {
    name: 'allow-ip-${replace(ip, '.', '-')}'
    startIpAddress: ip
    endIpAddress: ip
  }
]

var postgresPublicFirewallRules = concat(
  allowAzureServicesToPostgres
    ? [
        {
          name: 'allow-azure-services'
          startIpAddress: '0.0.0.0'
          endIpAddress: '0.0.0.0'
        }
      ]
    : [],
  postgresAllowIpFirewallRules
)

// Resource Group
resource rg 'Microsoft.Resources/resourceGroups@2024-11-01' = {
  name: resourceGroupName
  location: location
  tags: tags
}

// Virtual Network (AVM Module) - BYO VNet for private backend connectivity
module vnet 'br/public:avm/res/network/virtual-network:0.5.2' = if (enablePrivateNetworking) {
  scope: rg
  name: 'vnet-${uniqueSuffix}'
  params: {
    name: vnetName
    location: location
    tags: tags
    addressPrefixes: [vnetAddressPrefix]

    subnets: [
      {
        name: containerAppsSubnetName
        addressPrefix: containerAppsSubnetPrefix
        delegation: 'Microsoft.App/environments'
      }
      {
        name: postgresSubnetName
        addressPrefix: postgresSubnetPrefix
        delegation: 'Microsoft.DBforPostgreSQL/flexibleServers'
      }
      {
        name: aiFoundrySubnetName
        addressPrefix: aiFoundrySubnetPrefix
        privateEndpointNetworkPolicies: 'Disabled'
        privateLinkServiceNetworkPolicies: 'Disabled'
      }
      {
        name: privateEndpointsSubnetName
        addressPrefix: privateEndpointsSubnetPrefix
        privateEndpointNetworkPolicies: 'Disabled'
      }
    ]
  }
}

// Private DNS Zones for private endpoints
module privateDnsZoneKeyVault 'br/public:avm/res/network/private-dns-zone:0.6.0' = if (enablePrivateNetworking) {
  scope: rg
  name: 'pdns-keyvault-${uniqueSuffix}'
  params: {
    name: 'privatelink.vaultcore.azure.net'
    location: 'global'
    tags: tags
    virtualNetworkLinks: [
      {
        name: 'vnet-link-keyvault'
        virtualNetworkResourceId: enablePrivateNetworking ? (vnet.?outputs.resourceId ?? '') : ''
        registrationEnabled: false
      }
    ]
  }
}

module privateDnsZoneAcr 'br/public:avm/res/network/private-dns-zone:0.6.0' = if (enablePrivateNetworking) {
  scope: rg
  name: 'pdns-acr-${uniqueSuffix}'
  params: {
    name: 'privatelink.azurecr.io'
    location: 'global'
    tags: tags
    virtualNetworkLinks: [
      {
        name: 'vnet-link-acr'
        virtualNetworkResourceId: enablePrivateNetworking ? (vnet.?outputs.resourceId ?? '') : ''
        registrationEnabled: false
      }
    ]
  }
}

module privateDnsZoneAiServices 'br/public:avm/res/network/private-dns-zone:0.6.0' = if (enablePrivateNetworking) {
  scope: rg
  name: 'pdns-aiservices-${uniqueSuffix}'
  params: {
    name: 'privatelink.cognitiveservices.azure.com'
    location: 'global'
    tags: tags
    virtualNetworkLinks: [
      {
        name: 'vnet-link-ai'
        virtualNetworkResourceId: enablePrivateNetworking ? (vnet.?outputs.resourceId ?? '') : ''
        registrationEnabled: false
      }
    ]
  }
}

module privateDnsZoneML 'br/public:avm/res/network/private-dns-zone:0.6.0' = if (enablePrivateNetworking) {
  scope: rg
  name: 'pdns-ml-${uniqueSuffix}'
  params: {
    name: 'privatelink.api.azureml.ms'
    location: 'global'
    tags: tags
    virtualNetworkLinks: [
      {
        name: 'vnet-link-ml'
        virtualNetworkResourceId: enablePrivateNetworking ? (vnet.?outputs.resourceId ?? '') : ''
        registrationEnabled: false
      }
    ]
  }
}

module privateDnsZoneMLNotebooks 'br/public:avm/res/network/private-dns-zone:0.6.0' = if (enablePrivateNetworking) {
  scope: rg
  name: 'pdns-mlnotebooks-${uniqueSuffix}'
  params: {
    name: 'privatelink.notebooks.azure.net'
    location: 'global'
    tags: tags
    virtualNetworkLinks: [
      {
        name: 'vnet-link-mlnotebooks'
        virtualNetworkResourceId: enablePrivateNetworking ? (vnet.?outputs.resourceId ?? '') : ''
        registrationEnabled: false
      }
    ]
  }
}

module privateDnsZonePostgres 'br/public:avm/res/network/private-dns-zone:0.6.0' = if (enablePrivateNetworking) {
  scope: rg
  name: 'pdns-postgres-${uniqueSuffix}'
  params: {
    name: 'privatelink.postgres.database.azure.com'
    location: 'global'
    tags: tags
    virtualNetworkLinks: [
      {
        name: 'vnet-link-postgres'
        virtualNetworkResourceId: enablePrivateNetworking ? (vnet.?outputs.resourceId ?? '') : ''
        registrationEnabled: false
      }
    ]
  }
}

// PostgreSQL Flexible Server (AVM Module)
module postgresql 'br/public:avm/res/db-for-postgre-sql/flexible-server:0.5.0' = {
  scope: rg
  name: 'postgresql-${uniqueSuffix}'
  params: {
    name: '${projectName}-pg-${uniqueSuffix}'
    location: location
    tags: tags

    // Authentication via Azure AD (managed identity)
    activeDirectoryAuth: 'Enabled'
    passwordAuth: empty(postgresAdminPassword) ? 'Disabled' : 'Enabled'
    administratorLogin: empty(postgresAdminPassword) ? null : 'nimbusiqadmin'
    administratorLoginPassword: empty(postgresAdminPassword) ? null : postgresAdminPassword

    // Azure AD administrators
    administrators: [
      {
        objectId: acrPullIdentity.outputs.principalId
        principalName: acrPullIdentity.outputs.name
        principalType: 'ServicePrincipal'
      }
    ]

    // SKU - Burstable B1ms (1 vCore, 2GB RAM) for cost-effective start
    skuName: 'Standard_B1ms'
    tier: 'Burstable'

    storageSizeGB: 32
    version: '16'

    // High availability disabled initially (can enable later)
    highAvailability: 'Disabled'

    // Backup retention
    backupRetentionDays: 7
    geoRedundantBackup: 'Disabled'

    // VNet integration for private networking (FR-NET-001)
    delegatedSubnetResourceId: enablePrivateNetworking
      ? '${vnet.?outputs.resourceId ?? ''}/subnets/${postgresSubnetName}'
      : null
    privateDnsZoneArmResourceId: enablePrivateNetworking ? privateDnsZonePostgres.?outputs.resourceId ?? null : null

    // Public networking allowlist for PostgreSQL access (least privilege).
    firewallRules: !enablePrivateNetworking ? postgresPublicFirewallRules : []

    // Database
    databases: [
      {
        name: 'atlas'
        charset: 'UTF8'
        collation: 'en_US.utf8'
      }
    ]

    // Extensions
    configurations: [
      {
        name: 'pg_stat_statements.track'
        value: 'all'
        source: 'user-override'
      }
      {
        name: 'azure.extensions'
        value: 'pg_stat_statements'
        source: 'user-override'
      }
    ]
  }
}

// Azure Container Registry (AVM Module)
module acr 'br/public:avm/res/container-registry/registry:0.7.1' = {
  scope: rg
  name: 'acr-${uniqueSuffix}'
  params: {
    name: '${projectName}acr${uniqueSuffix}'
    location: location
    tags: tags

    // Standard is required for ACR Tasks (remote builds used by azd deploy)
    acrSku: enablePrivateNetworking ? 'Premium' : 'Standard'

    // Managed identity for AKS pull
    managedIdentities: {
      systemAssigned: true
    }

    // Anonymous pull disabled
    anonymousPullEnabled: false

    // Public network access - disabled when private networking enabled (FR-NET-003)
    publicNetworkAccess: enablePrivateNetworking ? 'Disabled' : 'Enabled'

    // Private endpoints for secure access (FR-NET-001)
    privateEndpoints: enablePrivateNetworking
      ? [
          {
            name: 'pe-acr-${uniqueSuffix}'
            subnetResourceId: '${vnet.?outputs.resourceId ?? ''}/subnets/${privateEndpointsSubnetName}'
            privateDnsZoneResourceIds: [privateDnsZoneAcr.?outputs.resourceId ?? '']
            service: 'registry'
          }
        ]
      : []

    // Retention policy (90 days)
    retentionPolicyStatus: 'enabled'
    retentionPolicyDays: 90
  }
}

// Azure Key Vault (AVM Module)
module keyVault 'br/public:avm/res/key-vault/vault:0.12.0' = {
  scope: rg
  name: 'kv-${uniqueSuffix}'
  params: {
    name: keyVaultName
    location: location
    tags: tags

    sku: 'standard'

    // RBAC authorization model
    enableRbacAuthorization: true

    // Networking - private endpoint when enabled (FR-NET-003)
    publicNetworkAccess: enablePrivateNetworking ? 'Disabled' : 'Enabled'
    networkAcls: !enablePrivateNetworking
      ? {
          bypass: 'AzureServices'
          defaultAction: 'Deny'
          ipRules: [] // Add CI/CD IP ranges
        }
      : null

    // Private endpoints for secure access (FR-NET-001)
    privateEndpoints: enablePrivateNetworking
      ? [
          {
            name: 'pe-kv-${uniqueSuffix}'
            subnetResourceId: '${vnet.?outputs.resourceId ?? ''}/subnets/${privateEndpointsSubnetName}'
            privateDnsZoneResourceIds: [privateDnsZoneKeyVault.?outputs.resourceId ?? '']
            service: 'vault'
          }
        ]
      : []

    // Soft delete and purge protection
    enableSoftDelete: true
    softDeleteRetentionInDays: 90
    enablePurgeProtection: true

    // Secrets (created by deployment pipeline)
    secrets: [
      {
        name: 'postgres-connection-string'
        value: 'Host=${postgresql.outputs.fqdn};Database=atlas;Username=${acrPullIdentity.outputs.name};SSL Mode=Require'
      }
      // AI Foundry secrets added via separate deployment step (after ML workspace exists)
    ]
  }
}

// Storage Account for AI Foundry (required dependency)
module aiStorageAccount 'br/public:avm/res/storage/storage-account:0.15.0' = if (enableAIFoundry) {
  scope: rg
  name: 'storage-ai-${uniqueSuffix}'
  params: {
    name: '${projectName}aist${substring(uniqueSuffix, 0, 8)}'
    location: location
    tags: tags

    skuName: 'Standard_LRS'
    kind: 'StorageV2'
    accessTier: 'Hot'

    // Disable public access when private networking enabled (FR-NET-003)
    publicNetworkAccess: enablePrivateNetworking ? 'Disabled' : 'Enabled'
    networkAcls: !enablePrivateNetworking
      ? {
          bypass: 'AzureServices'
          defaultAction: 'Deny'
        }
      : null

    // Private endpoints for secure access
    privateEndpoints: enablePrivateNetworking
      ? [
          {
            name: 'pe-storage-blob-${uniqueSuffix}'
            subnetResourceId: '${vnet.?outputs.resourceId ?? ''}/subnets/${privateEndpointsSubnetName}'
            service: 'blob'
          }
          {
            name: 'pe-storage-file-${uniqueSuffix}'
            subnetResourceId: '${vnet.?outputs.resourceId ?? ''}/subnets/${privateEndpointsSubnetName}'
            service: 'file'
          }
        ]
      : []
  }
}

// Azure AI Services (OpenAI + Cognitive Services) for Agent Framework (FR-INT-001)
module aiServices './modules/ai-services-account.bicep' = if (enableAIFoundry) {
  scope: rg
  name: 'ai-services-${uniqueSuffix}'
  params: {
    name: '${projectName}-ai-${uniqueSuffix}'
    location: location
    tags: tags
    enablePrivateNetworking: enablePrivateNetworking
    enableCapabilityHost: enableCapabilityHost
    privateEndpointName: 'pe-aiservices-${uniqueSuffix}'
    privateEndpointSubnetResourceId: enablePrivateNetworking
      ? '${vnet.?outputs.resourceId ?? ''}/subnets/${privateEndpointsSubnetName}'
      : ''
    privateDnsZoneResourceId: enablePrivateNetworking ? (privateDnsZoneAiServices.?outputs.resourceId ?? '') : ''
  }
}

// Model deployments are in a separate module so they only start after the AI Services account
// has fully settled — prevents IfMatchPreconditionFailed (ETag conflict) on redeployment.
module aiModelDeployments './modules/ai-model-deployments.bicep' = if (enableAIFoundry && enableModelDeployments) {
  scope: rg
  name: 'ai-model-deployments-${uniqueSuffix}'
  params: {
    aiServicesAccountName: aiServices.?outputs.name ?? ''
  }
}

module aiFoundryProjectModule './modules/ai-foundry-project.bicep' = if (enableAIFoundry) {
  scope: rg
  name: 'ai-foundry-project-${uniqueSuffix}'
  params: {
    aiServicesAccountName: aiServices.?outputs.name ?? ''
    aiFoundryProjectName: aiFoundryProjectName
    location: location
  }
}

var aiFoundryProjectEndpoint = enableAIFoundry ? (aiFoundryProjectModule.?outputs.aiFoundryProjectEndpoint ?? '') : ''
// AIProjectClient connection string format: <base-endpoint>;<subscription>;<rg>;<project>
// The project endpoint includes /api/projects/<name> but the SDK only needs the base origin.
var aiFoundryBaseEndpoint = enableAIFoundry ? 'https://${aiServices.?outputs.name ?? ''}.services.ai.azure.com' : ''
var aiFoundryProjectConnectionString = enableAIFoundry
  ? '${aiFoundryBaseEndpoint};${subscription().subscriptionId};${resourceGroupName};${aiFoundryProjectModule.?outputs.aiFoundryProjectName ?? ''}'
  : ''

// Azure Machine Learning Workspace (AI Foundry Hub) (FR-INT-001)
module mlWorkspace 'br/public:avm/res/machine-learning-services/workspace:0.9.1' = if (enableAIFoundry) {
  scope: rg
  name: 'ml-workspace-${uniqueSuffix}'
  params: {
    name: '${projectName}-ml-${uniqueSuffix}'
    location: location
    tags: tags

    sku: 'Basic'
    kind: 'Hub' // AI Foundry Hub workspace type

    // Managed identity for secure access
    managedIdentities: {
      systemAssigned: true
    }

    // Associated resources
    associatedStorageAccountResourceId: enableAIFoundry ? (aiStorageAccount.?outputs.resourceId ?? '') : ''
    associatedKeyVaultResourceId: keyVault.outputs.resourceId

    // Disable public access when private networking enabled (FR-NET-003)
    publicNetworkAccess: enablePrivateNetworking ? 'Disabled' : 'Enabled'

    // Private endpoints for secure access (FR-NET-001)
    privateEndpoints: enablePrivateNetworking
      ? [
          {
            name: 'pe-ml-${uniqueSuffix}'
            subnetResourceId: '${vnet.?outputs.resourceId ?? ''}/subnets/${privateEndpointsSubnetName}'
            privateDnsZoneResourceIds: [
              privateDnsZoneML.?outputs.resourceId ?? ''
              privateDnsZoneMLNotebooks.?outputs.resourceId ?? ''
            ]
            service: 'amlworkspace'
          }
        ]
      : []
  }
}

// Network Security Perimeter (FR-NET-004, FR-NET-005)
module nsp './modules/nsp.bicep' = if (enableNetworkSecurityPerimeter && enableAIFoundry) {
  scope: rg
  name: 'nsp-${uniqueSuffix}'
  params: {
    projectName: projectName
    environment: environment
    location: location
    tags: tags
    nspMode: nspMode
    mlWorkspaceId: enableAIFoundry ? (mlWorkspace.?outputs.resourceId ?? '') : ''
    aiServicesId: enableAIFoundry ? (aiServices.?outputs.resourceId ?? '') : ''
    keyVaultId: keyVault.outputs.resourceId
    storageAccountId: enableAIFoundry ? (aiStorageAccount.?outputs.resourceId ?? '') : ''
    logAnalyticsWorkspaceId: logAnalytics.outputs.resourceId
    allowedInboundSubscriptionIds: [subscription().subscriptionId]
  }
}

// Container App Environment (AVM Module)
module containerAppEnv 'br/public:avm/res/app/managed-environment:0.10.1' = {
  scope: rg
  name: 'cae-${uniqueSuffix}'
  params: {
    name: '${projectName}-cae-${environment}'
    location: location
    tags: tags

    // Logging
    logAnalyticsWorkspaceResourceId: logAnalytics.outputs.resourceId

    // VNet integration for private networking (FR-NET-001)
    infrastructureSubnetId: enablePrivateNetworking
      ? '${vnet.?outputs.resourceId ?? ''}/subnets/${containerAppsSubnetName}'
      : null
    internal: enablePrivateNetworking // Internal environment when private networking enabled

    // Ensure public network access is explicit in IaC (persisted)
    publicNetworkAccess: enablePrivateNetworking ? 'Disabled' : 'Enabled'

    // Networking - consumption plan (can upgrade to workload profiles for production)
    zoneRedundant: false
  }
}

// Log Analytics Workspace (AVM Module)
module logAnalytics 'br/public:avm/res/operational-insights/workspace:0.9.1' = {
  scope: rg
  name: 'log-${uniqueSuffix}'
  params: {
    name: '${projectName}-log-${environment}'
    location: location
    tags: tags

    // Retention
    dataRetention: 30

    // SKU
    skuName: 'PerGB2018'
  }
}

// Managed Identity for Container Apps to pull from ACR
module acrPullIdentity 'br/public:avm/res/managed-identity/user-assigned-identity:0.4.0' = {
  scope: rg
  name: 'acr-pull-identity-${uniqueSuffix}'
  params: {
    name: runtimeIdentityName
    location: location
    tags: tags
  }
}

// Grant AcrPull role to the managed identity
module acrPullRoleAssignment 'br/public:avm/ptn/authorization/resource-role-assignment:0.1.1' = {
  scope: rg
  name: 'acr-pull-role-${uniqueSuffix}'
  params: {
    principalId: acrPullIdentity.outputs.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId(
      'Microsoft.Authorization/roleDefinitions',
      '7f951dda-4ed3-4680-a7ca-43fe172d538d'
    ) // AcrPull
    resourceId: acr.outputs.resourceId
  }
}

// Grant Key Vault Secrets User role to the managed identity
module keyVaultSecretsRoleAssignment 'br/public:avm/ptn/authorization/resource-role-assignment:0.1.1' = {
  scope: rg
  name: 'kv-secrets-role-${uniqueSuffix}'
  params: {
    principalId: acrPullIdentity.outputs.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId(
      'Microsoft.Authorization/roleDefinitions',
      '4633458b-17de-408a-b874-0445c86b69e6'
    ) // Key Vault Secrets User
    resourceId: keyVault.outputs.resourceId
  }
}

// Grant Cognitive Services User role to the managed identity for AI Services access (FR-MCP-002)
module aiServicesRoleAssignment 'br/public:avm/ptn/authorization/resource-role-assignment:0.1.1' = if (enableAIFoundry) {
  scope: rg
  name: 'ai-services-role-${uniqueSuffix}'
  params: {
    principalId: acrPullIdentity.outputs.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId(
      'Microsoft.Authorization/roleDefinitions',
      'a97b65f3-24c7-4388-baec-2e87135dc908'
    ) // Cognitive Services User
    resourceId: aiServices.?outputs.resourceId ?? ''
  }
}

// Grant Azure AI User role to the project managed identity on the AI Services account (FR-MCP-002)
module aiFoundryProjectAiUserRoleAssignment 'br/public:avm/ptn/authorization/resource-role-assignment:0.1.1' = if (enableAIFoundry) {
  scope: rg
  name: 'ai-project-ai-user-role-${uniqueSuffix}'
  params: {
    principalId: aiFoundryProjectModule.?outputs.aiFoundryProjectPrincipalId ?? ''
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId(
      'Microsoft.Authorization/roleDefinitions',
      '53ca6127-db72-4b80-b1b0-d745d6d5456d'
    ) // Azure AI User
    resourceId: aiServices.?outputs.resourceId ?? ''
  }
}

// Grant Azure AI Developer role to the managed identity for project-level operations (FR-MCP-002)
module aiFoundryProjectDeveloperRoleAssignment 'br/public:avm/ptn/authorization/resource-role-assignment:0.1.1' = if (enableAIFoundry) {
  scope: rg
  name: 'ai-project-dev-role-${uniqueSuffix}'
  params: {
    principalId: acrPullIdentity.outputs.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId(
      'Microsoft.Authorization/roleDefinitions',
      '64702f94-c441-49e6-a78b-ef80e0188fee'
    ) // Azure AI Developer
    resourceId: aiFoundryProjectModule.?outputs.aiFoundryProjectId ?? ''
  }
}

// Grant AzureML Data Scientist role to the managed identity for Foundry access (FR-MCP-002)
module mlWorkspaceRoleAssignment 'br/public:avm/ptn/authorization/resource-role-assignment:0.1.1' = if (enableAIFoundry) {
  scope: rg
  name: 'ml-workspace-role-${uniqueSuffix}'
  params: {
    principalId: acrPullIdentity.outputs.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId(
      'Microsoft.Authorization/roleDefinitions',
      'f6c7c914-8db3-469d-8ca1-694a8f32e121'
    ) // AzureML Data Scientist
    resourceId: mlWorkspace.?outputs.resourceId ?? ''
  }
}

// Deploy Container Apps with azd-service-name tags
// NOTE: When skipContainerApps=true, let azd deploy handle Container App creation after image builds
module containerApps 'modules/container-apps.bicep' = if (!skipContainerApps) {
  name: 'container-apps-${uniqueSuffix}'
  scope: rg
  params: {
    projectName: projectName
    environment: environment
    location: location
    tags: tags
    containerAppEnvironmentId: containerAppEnv.outputs.resourceId
    acrLoginServer: acr.outputs.loginServer
    acrPullIdentityResourceId: acrPullIdentity.outputs.resourceId
    acrPullIdentityClientId: acrPullIdentity.outputs.clientId
    keyVaultSecretConnectionString: '${keyVault.outputs.uri}secrets/postgres-connection-string'
    postgresConnectionString: 'Host=${postgresql.outputs.fqdn};Database=atlas;Username=${acrPullIdentity.outputs.name};SSL Mode=Require'
    enableAIFoundry: enableAIFoundry
    aiFoundryProjectName: enableAIFoundry ? (aiFoundryProjectModule.?outputs.aiFoundryProjectName ?? '') : ''
    aiFoundryProjectEndpoint: aiFoundryProjectEndpoint
    aiFoundryProjectConnectionString: aiFoundryProjectConnectionString
    aiFoundryCapabilityHostName: enableAIFoundry ? (aiServices.?outputs.capabilityHostName ?? '') : ''
    aiServicesEndpoint: enableAIFoundry ? (aiServices.?outputs.endpoint ?? '') : ''
    // Image names (set by azd deploy outputs). Falls back to bootstrap image when empty.
    controlPlaneImageName: controlPlaneImageName
    agentOrchestratorImageName: agentOrchestratorImageName
    frontendImageName: frontendImageName
    frontendAllowedIpCidrs: frontendAllowedIpCidrs
    allowAnonymousReadOnlyInProd: allowAnonymousReadOnlyInProd
    allowAnonymousFullInProd: allowAnonymousFullInProd
    bootstrapImage: bootstrapImage
  }
  dependsOn: [
    acrPullRoleAssignment
    keyVaultSecretsRoleAssignment
  ]
}

// Cognitive Services OpenAI User for the shared user-assigned MI used by AZURE_CLIENT_ID.
// Not gated on skipContainerApps — UAMI needs this role even when container apps are deployed
// separately by azd deploy so that the token audience fix in code cannot be undermined by missing RBAC.
module acrPullIdentityOpenAIUserRole 'br/public:avm/ptn/authorization/resource-role-assignment:0.1.1' = if (enableAIFoundry) {
  scope: rg
  name: 'ai-openai-user-uami-${uniqueSuffix}'
  params: {
    name: guid(
      acrPullIdentity.outputs.principalId,
      '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd',
      aiServices.?outputs.resourceId ?? ''
    )
    principalId: acrPullIdentity.outputs.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId(
      'Microsoft.Authorization/roleDefinitions',
      '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd' // Cognitive Services OpenAI User
    )
    resourceId: aiServices.?outputs.resourceId ?? ''
  }
}

// Cognitive Services OpenAI User for the control-plane-api system-assigned identity (SAMI).
// SAMI is the fallback identity when DefaultAzureCredential resolves before UAMI.
// Belt-and-suspenders: grant the same role to SAMI so either identity can call AI inference.
module controlPlaneSamiOpenAIUserRole 'br/public:avm/ptn/authorization/resource-role-assignment:0.1.1' = if (!skipContainerApps && enableAIFoundry) {
  scope: rg
  name: 'ai-openai-user-cp-sami-${uniqueSuffix}'
  params: {
    name: guid(
      containerApps.?outputs.controlPlaneSystemAssignedPrincipalId ?? '',
      '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd',
      aiServices.?outputs.resourceId ?? ''
    )
    principalId: containerApps.?outputs.controlPlaneSystemAssignedPrincipalId ?? ''
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId(
      'Microsoft.Authorization/roleDefinitions',
      '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd' // Cognitive Services OpenAI User
    )
    resourceId: aiServices.?outputs.resourceId ?? ''
  }
}

// Cognitive Services OpenAI User for the agent-orchestrator system-assigned identity (SAMI).
module agentOrchestratorSamiOpenAIUserRole 'br/public:avm/ptn/authorization/resource-role-assignment:0.1.1' = if (!skipContainerApps && enableAIFoundry) {
  scope: rg
  name: 'ai-openai-user-ao-sami-${uniqueSuffix}'
  params: {
    name: guid(
      containerApps.?outputs.agentOrchestratorSystemAssignedPrincipalId ?? '',
      '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd',
      aiServices.?outputs.resourceId ?? ''
    )
    principalId: containerApps.?outputs.agentOrchestratorSystemAssignedPrincipalId ?? ''
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId(
      'Microsoft.Authorization/roleDefinitions',
      '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd' // Cognitive Services OpenAI User
    )
    resourceId: aiServices.?outputs.resourceId ?? ''
  }
}

// Subscription-scope runtime roles are assigned idempotently in azd postprovision hooks
// to avoid RoleAssignmentExists failures on repeated azd up/provision operations.

// Outputs
output resourceGroupName string = rg.name
output postgresqlServerName string = postgresql.outputs.name
output postgresqlConnectionString string = 'Host=${postgresql.outputs.fqdn};Database=atlas;Username=${acrPullIdentity.outputs.name};SSL Mode=Require'
output acrName string = acr.outputs.name
output acrLoginServer string = acr.outputs.loginServer
output keyVaultName string = keyVault.outputs.name
output containerAppEnvironmentName string = containerAppEnv.outputs.name
output logAnalyticsWorkspaceId string = logAnalytics.outputs.resourceId

// Outputs for azd to use
output AZURE_RESOURCE_GROUP string = rg.name
output AZURE_CONTAINER_REGISTRY_ENDPOINT string = acr.outputs.loginServer
output AZURE_CONTAINER_APPS_ENVIRONMENT_ID string = containerAppEnv.outputs.resourceId
output AZURE_CONTAINER_REGISTRY_NAME string = acr.outputs.name
output AZURE_KEY_VAULT_ENDPOINT string = keyVault.outputs.uri
output AZURE_LOG_ANALYTICS_WORKSPACE_ID string = logAnalytics.outputs.resourceId
output AZURE_AI_PROJECT_ID string = enableAIFoundry ? aiFoundryProjectModule.?outputs.aiFoundryProjectId ?? '' : ''
output AZURE_AI_PROJECT_NAME string = enableAIFoundry ? aiFoundryProjectModule.?outputs.aiFoundryProjectName ?? '' : ''
output AZURE_AI_PROJECT_ENDPOINT string = aiFoundryProjectEndpoint
output AZURE_AI_PROJECT_CONNECTION_STRING string = aiFoundryProjectConnectionString

// Managed identity for Container Apps
output SERVICE_MANAGED_IDENTITY_ID string = acrPullIdentity.outputs.resourceId
output SERVICE_MANAGED_IDENTITY_PRINCIPAL_ID string = acrPullIdentity.outputs.principalId
output SERVICE_MANAGED_IDENTITY_CLIENT_ID string = acrPullIdentity.outputs.clientId

// Database connection string for services (using managed identity)
output DATABASE_CONNECTION_STRING string = 'Host=${postgresql.outputs.fqdn};Database=atlas;Username=${acrPullIdentity.outputs.name};SSL Mode=Require'

// AI Foundry and Network Security Perimeter outputs
output aiFoundryWorkspaceName string = enableAIFoundry ? mlWorkspace.?outputs.name ?? '' : ''
output aiFoundryWorkspaceId string = enableAIFoundry ? mlWorkspace.?outputs.resourceId ?? '' : ''
// Note: Discovery URL must be retrieved post-deployment via: az ml workspace show -n <name> -g <rg> --query discoveryUrl -o tsv
output aiFoundryProjectName string = enableAIFoundry ? aiFoundryProjectModule.?outputs.aiFoundryProjectName ?? '' : ''
output aiFoundryProjectId string = enableAIFoundry ? aiFoundryProjectModule.?outputs.aiFoundryProjectId ?? '' : ''
output aiFoundryProjectEndpoint string = aiFoundryProjectEndpoint
output aiFoundryProjectConnectionString string = aiFoundryProjectConnectionString
output aiFoundryCapabilityHostName string = enableAIFoundry ? aiServices.?outputs.capabilityHostName ?? '' : ''
output aiServicesName string = enableAIFoundry ? aiServices.?outputs.name ?? '' : ''
output aiServicesEndpoint string = enableAIFoundry ? aiServices.?outputs.endpoint ?? '' : ''
output networkSecurityPerimeterName string = (enableNetworkSecurityPerimeter && enableAIFoundry)
  ? nsp.?outputs.nspName ?? ''
  : ''
output nspMode string = nspMode
output vnetName string = enablePrivateNetworking ? vnet.?outputs.name ?? '' : ''
