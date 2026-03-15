targetScope = 'resourceGroup'

@description('Azure AI Services account name hosting the project')
param aiServicesAccountName string

@description('Azure AI Foundry project name')
param aiFoundryProjectName string

@description('Azure region for the project resources')
param location string

resource aiServicesAccount 'Microsoft.CognitiveServices/accounts@2025-09-01' existing = {
  name: aiServicesAccountName
}

// AI Foundry project used by agent-orchestrator at runtime.
resource aiFoundryProject 'Microsoft.CognitiveServices/accounts/projects@2025-09-01' = {
  parent: aiServicesAccount
  name: aiFoundryProjectName
  location: location
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    displayName: aiFoundryProjectName
    description: 'NimbusIQ agent orchestration project'
  }
}

output aiFoundryProjectName string = aiFoundryProject.name
output aiFoundryProjectId string = aiFoundryProject.id
output aiFoundryProjectPrincipalId string = aiFoundryProject.identity.principalId
output aiFoundryProjectEndpoint string = aiFoundryProject.properties.endpoints['AI Foundry API']
