// Network Security Perimeter Module (FR-NET-004, FR-NET-005)
// This module deploys Network Security Perimeter resources at resource group scope

@description('The name of the project')
param projectName string

@description('The environment name')
param environment string

@description('The location for resources')
param location string

@description('Tags to apply to resources')
param tags object

@description('NSP mode - Learning or Enforced')
@allowed([
  'Learning'
  'Enforced'
])
param nspMode string = 'Learning'

@description('Enable NSP association for Azure ML workspace. Disable when subscription support is unavailable.')
param enableMlWorkspaceAssociation bool = false

@description('ML Workspace resource ID for NSP association')
param mlWorkspaceId string

@description('AI Services resource ID for NSP association')
param aiServicesId string

@description('Key Vault resource ID for NSP association')
param keyVaultId string

@description('Storage Account resource ID for NSP association (AI Foundry storage)')
param storageAccountId string

@description('Log Analytics Workspace resource ID for NSP association')
param logAnalyticsWorkspaceId string

@description('Subscription IDs allowed inbound access to NSP-protected resources (e.g., the subscription hosting Container Apps)')
param allowedInboundSubscriptionIds array

var inboundSubscriptionResourceIds = [
  for subId in allowedInboundSubscriptionIds: startsWith(subId, '/subscriptions/') ? subId : '/subscriptions/${subId}'
]
var resourceManagerHost = replace(replace(az.environment().resourceManager, 'https://', ''), '/', '')
var activeDirectoryHost = replace(replace(az.environment().authentication.loginEndpoint, 'https://', ''), '/', '')

// Network Security Perimeter (FR-NET-004, FR-NET-005)
resource networkSecurityPerimeter 'Microsoft.Network/networkSecurityPerimeters@2024-10-01' = {
  name: '${projectName}-nsp-${environment}'
  location: location
  tags: union(tags, {
    'nsp-mode': nspMode
  })
  properties: {}
}

// NSP Profile for AI Foundry resources (FR-NET-004)
resource nspProfile 'Microsoft.Network/networkSecurityPerimeters/profiles@2024-10-01' = {
  parent: networkSecurityPerimeter
  name: 'ai-foundry-profile'
  properties: {}
}

// Inbound: allow Container Apps (same subscription) to reach NSP-protected Key Vault, Storage, Log Analytics.
// In Enforced mode, NSP overrides both firewall rules and the AzureServices bypass, so this is required
// for non-VNet-integrated deployments. With private endpoints this is defence-in-depth.
resource inboundSubscriptionRule 'Microsoft.Network/networkSecurityPerimeters/profiles/accessRules@2024-10-01' = {
  parent: nspProfile
  name: 'allow-same-subscription-inbound'
  properties: {
    direction: 'Inbound'
    subscriptions: [
      for subResourceId in inboundSubscriptionResourceIds: {
        id: subResourceId
      }
    ]
  }
}

// Outbound: allow perimeter resources to reach essential Azure management and AI endpoints
resource outboundAzureEndpoints 'Microsoft.Network/networkSecurityPerimeters/profiles/accessRules@2024-10-01' = {
  parent: nspProfile
  name: 'allow-azure-management-outbound'
  properties: {
    direction: 'Outbound'
    fullyQualifiedDomainNames: [
      resourceManagerHost
      activeDirectoryHost
    ]
  }
}

// NSP Association for ML Workspace (AI Foundry Hub) (FR-NET-004)
resource nspAssociationMLWorkspace 'Microsoft.Network/networkSecurityPerimeters/resourceAssociations@2024-10-01' = if (enableMlWorkspaceAssociation) {
  parent: networkSecurityPerimeter
  name: 'mlworkspace-association'
  properties: {
    privateLinkResource: {
      id: mlWorkspaceId
    }
    profile: {
      id: nspProfile.id
    }
    accessMode: nspMode
  }
}

// NSP Association for AI Services (FR-NET-004)
resource nspAssociationAIServices 'Microsoft.Network/networkSecurityPerimeters/resourceAssociations@2024-10-01' = {
  parent: networkSecurityPerimeter
  name: 'aiservices-association'
  properties: {
    privateLinkResource: {
      id: aiServicesId
    }
    profile: {
      id: nspProfile.id
    }
    accessMode: nspMode
  }
}

// NSP Association for Key Vault — GA-supported, protects secrets/keys used by AI Foundry and application
resource nspAssociationKeyVault 'Microsoft.Network/networkSecurityPerimeters/resourceAssociations@2024-10-01' = {
  parent: networkSecurityPerimeter
  name: 'keyvault-association'
  properties: {
    privateLinkResource: {
      id: keyVaultId
    }
    profile: {
      id: nspProfile.id
    }
    accessMode: nspMode
  }
}

// NSP Association for Storage Account — GA-supported, protects AI Foundry artifacts/data
resource nspAssociationStorage 'Microsoft.Network/networkSecurityPerimeters/resourceAssociations@2024-10-01' = {
  parent: networkSecurityPerimeter
  name: 'storage-association'
  properties: {
    privateLinkResource: {
      id: storageAccountId
    }
    profile: {
      id: nspProfile.id
    }
    accessMode: nspMode
  }
}

// NSP Association for Log Analytics — GA-supported, protects audit/diagnostic logs
resource nspAssociationLogAnalytics 'Microsoft.Network/networkSecurityPerimeters/resourceAssociations@2024-10-01' = {
  parent: networkSecurityPerimeter
  name: 'loganalytics-association'
  properties: {
    privateLinkResource: {
      id: logAnalyticsWorkspaceId
    }
    profile: {
      id: nspProfile.id
    }
    accessMode: nspMode
  }
}

// Outputs
output nspName string = networkSecurityPerimeter.name
output nspId string = networkSecurityPerimeter.id
output nspProfileId string = nspProfile.id
