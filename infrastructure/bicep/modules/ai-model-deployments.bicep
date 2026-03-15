targetScope = 'resourceGroup'

@description('Azure AI Services account name to deploy models to')
param aiServicesAccountName string

@description('Capacity for gpt-4o deployment (GlobalStandard TPM units)')
param gpt4oCapacity int = 10

@description('Capacity for text-embedding-ada-002 deployment (GlobalStandard TPM units)')
param embeddingCapacity int = 10

// Existing account reference — this module is only called after the account module settles,
// so the ETag will be stable and the PUT won't conflict.
resource aiServices 'Microsoft.CognitiveServices/accounts@2025-09-01' existing = {
  name: aiServicesAccountName
}

resource gpt4oDeployment 'Microsoft.CognitiveServices/accounts/deployments@2025-09-01' = {
  parent: aiServices
  name: 'gpt-4o'
  properties: {
    model: {
      format: 'OpenAI'
      name: 'gpt-4o'
      version: '2024-08-06'
    }
  }
  sku: {
    name: 'GlobalStandard'
    capacity: gpt4oCapacity
  }
}

// Sequential: text-embedding must wait for gpt-4o to avoid concurrent PUT conflicts
// on the same parent account.
resource embeddingDeployment 'Microsoft.CognitiveServices/accounts/deployments@2025-09-01' = {
  parent: aiServices
  name: 'text-embedding-ada-002'
  properties: {
    model: {
      format: 'OpenAI'
      name: 'text-embedding-ada-002'
      version: '2'
    }
  }
  sku: {
    name: 'GlobalStandard'
    capacity: embeddingCapacity
  }
  dependsOn: [
    gpt4oDeployment
  ]
}

output gpt4oDeploymentName string = gpt4oDeployment.name
output embeddingDeploymentName string = embeddingDeployment.name
