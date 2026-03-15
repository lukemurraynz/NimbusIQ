targetScope = 'resourceGroup'

@description('Azure AI Services account name')
param name string

@description('Azure region for the account')
param location string

@description('Tags to apply to the account')
param tags object = {}

@description('Enable private networking for the account')
param enablePrivateNetworking bool = false

@description('Private endpoint name for AI Services')
param privateEndpointName string = ''

@description('Subnet resource ID for the private endpoint')
param privateEndpointSubnetResourceId string = ''

@description('Private DNS zone resource ID for Cognitive Services private link')
param privateDnsZoneResourceId string = ''

@description('Enable AI Services capability host (preview feature).')
param enableCapabilityHost bool = false

resource aiServices 'Microsoft.CognitiveServices/accounts@2025-09-01' = {
  name: name
  location: location
  tags: tags
  kind: 'AIServices'
  sku: {
    name: 'S0'
  }
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    allowProjectManagement: true
    customSubDomainName: name
    publicNetworkAccess: enablePrivateNetworking ? 'Disabled' : 'Enabled'
    // When using private networking, network rules are handled by private endpoints.
    // When public, allow access — auth is enforced via managed identity (disableLocalAuth: true).
    networkAcls: !enablePrivateNetworking
      ? {
          bypass: 'AzureServices'
          defaultAction: 'Allow'
        }
      : null
    disableLocalAuth: true
  }
}

resource aiFoundryCapabilityHost 'Microsoft.CognitiveServices/accounts/capabilityHosts@2025-10-01-preview' = if (enableCapabilityHost) {
  parent: aiServices
  name: 'agents'
  properties: any({
    capabilityHostKind: 'Agents'
    enablePublicHostingEnvironment: true
  })
}

resource aiServicesPrivateEndpoint 'Microsoft.Network/privateEndpoints@2024-10-01' = if (enablePrivateNetworking) {
  name: privateEndpointName
  location: location
  tags: tags
  properties: {
    subnet: {
      id: privateEndpointSubnetResourceId
    }
    privateLinkServiceConnections: [
      {
        name: '${name}-connection'
        properties: {
          privateLinkServiceId: aiServices.id
          groupIds: [
            'account'
          ]
        }
      }
    ]
  }
}

resource aiServicesPrivateDnsZoneGroup 'Microsoft.Network/privateEndpoints/privateDnsZoneGroups@2024-10-01' = if (enablePrivateNetworking && !empty(privateDnsZoneResourceId)) {
  parent: aiServicesPrivateEndpoint
  name: 'default'
  properties: {
    privateDnsZoneConfigs: [
      {
        name: 'cognitiveservices-dns'
        properties: {
          privateDnsZoneId: privateDnsZoneResourceId
        }
      }
    ]
  }
}

output resourceId string = aiServices.id
output endpoint string = aiServices.properties.endpoint
output name string = aiServices.name
output principalId string = aiServices.identity.principalId
output capabilityHostName string = enableCapabilityHost ? aiFoundryCapabilityHost.name : ''
