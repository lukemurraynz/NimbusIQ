param projectName string
param environment string
param location string
param tags object

param containerAppEnvironmentId string
param acrLoginServer string
@description('User-assigned managed identity resource ID for ACR pull and Container App identity')
param acrPullIdentityResourceId string
@description('User-assigned managed identity client ID used by DefaultAzureCredential at runtime')
param acrPullIdentityClientId string
@secure()
param keyVaultSecretConnectionString string
param postgresConnectionString string = '' // Direct connection string (dev only; prod should use Key Vault)
param enableAIFoundry bool = false
@description('Azure AI Foundry project name')
param aiFoundryProjectName string = ''
@description('Azure AI Foundry project API endpoint')
param aiFoundryProjectEndpoint string = ''
@description('Azure AI Foundry project connection string format: <endpoint>;<subscriptionId>;<resourceGroup>;<projectName>')
param aiFoundryProjectConnectionString string = ''
@description('Azure AI Foundry capability host name')
param aiFoundryCapabilityHostName string = ''
@description('Azure AI Services endpoint')
param aiServicesEndpoint string = ''
@description('Full image names for services (set by azd deploy outputs). When empty, a public bootstrap image is used.')
param controlPlaneImageName string = ''
param agentOrchestratorImageName string = ''
param frontendImageName string = ''
@description('Allowed CIDR ranges for frontend public ingress. Empty array means unrestricted ingress.')
param frontendAllowedIpCidrs array = []
@description('Enable read-only anonymous API access in production for dashboard/demo scenarios.')
param allowAnonymousReadOnlyInProd bool = true
@description('Enable full anonymous API access in production for demo scenarios. Overrides read-only mode when true.')
param allowAnonymousFullInProd bool = false

@description('Public bootstrap image used until azd deploy publishes images to ACR. Must listen on port 8080.')
param bootstrapImage string = 'mcr.microsoft.com/dotnet/samples:aspnetapp'

// Local variables
// Frontend proxy targets Control Plane API via its externally-routable FQDN.
// This avoids reliance on internal DNS host patterns that can vary across Container Apps environments.

// Control Plane API environment variables
// In dev mode (postgresConnectionString provided), use direct string
// In prod mode, use Key Vault secret reference
var controlPlaneApiEnv = [
  {
    name: 'ASPNETCORE_ENVIRONMENT'
    value: 'Production'
  }
]
var controlPlaneApiEnvWithDb = !empty(postgresConnectionString)
  ? concat(controlPlaneApiEnv, [
      {
        name: 'ConnectionStrings__DefaultConnection'
        value: postgresConnectionString
      }
    ])
  : concat(controlPlaneApiEnv, [
      {
        name: 'ConnectionStrings__DefaultConnection'
        secretRef: 'postgresconnectionstring'
      }
    ])
var controlPlaneApiFoundryEnv = enableAIFoundry && !empty(aiFoundryProjectEndpoint)
  ? [
      {
        name: 'AzureAIFoundry__ProjectEndpoint'
        value: aiFoundryProjectEndpoint
      }
      {
        name: 'AzureAIFoundry__ProjectConnectionString'
        value: aiFoundryProjectConnectionString
      }
      {
        name: 'AzureAIFoundry__ModelDeployment'
        value: 'gpt-4o'
      }
      {
        name: 'AzureAIFoundry__DefaultModelDeployment'
        value: 'gpt-4o'
      }
    ]
  : []
var controlPlaneApiFinalEnv = concat(controlPlaneApiEnvWithDb, controlPlaneApiFoundryEnv, [
  {
    name: 'NIMBUSIQ_ALLOW_ANONYMOUS'
    value: 'true'
  }
  {
    name: 'NIMBUSIQ_ALLOW_ANONYMOUS_IN_PROD_READONLY'
    value: (!allowAnonymousFullInProd && allowAnonymousReadOnlyInProd) ? 'true' : 'false'
  }
  {
    name: 'NIMBUSIQ_ALLOW_ANONYMOUS_IN_PROD_FULL'
    value: allowAnonymousFullInProd ? 'true' : 'false'
  }
  {
    name: 'NIMBUSIQ_APPLY_MIGRATIONS_ON_STARTUP'
    value: 'true'
  }
  {
    name: 'AZURE_CLIENT_ID'
    value: acrPullIdentityClientId
  }
])

// Agent Orchestrator environment variables
var agentOrchestratorEnvBase = [
  {
    name: 'AzureResourceGraph__SubscriptionId'
    value: subscription().subscriptionId
  }
  {
    name: 'AgentFramework__UseFoundryOrchestration'
    value: enableAIFoundry ? 'true' : 'false'
  }
  {
    name: 'AgentFramework__UseManagedIdentity'
    value: 'true'
  }
]
var agentOrchestratorEnvWithDb = !empty(postgresConnectionString)
  ? concat(agentOrchestratorEnvBase, [
      {
        name: 'ConnectionStrings__DefaultConnection'
        value: postgresConnectionString
      }
    ])
  : concat(agentOrchestratorEnvBase, [
      {
        name: 'ConnectionStrings__DefaultConnection'
        secretRef: 'postgresconnectionstring'
      }
    ])
var agentOrchestratorFoundryEnv = enableAIFoundry && !empty(aiFoundryProjectEndpoint)
  ? [
      {
        name: 'AzureAIFoundry__ProjectEndpoint'
        value: aiFoundryProjectEndpoint
      }
      {
        name: 'AzureAIFoundry__ProjectConnectionString'
        value: aiFoundryProjectConnectionString
      }
      {
        name: 'AzureAIFoundry__ProjectApiEndpoint'
        value: aiFoundryProjectEndpoint
      }
      {
        name: 'AzureAIFoundry__ProjectName'
        value: aiFoundryProjectName
      }
      {
        name: 'AzureAIFoundry__CapabilityHostName'
        value: aiFoundryCapabilityHostName
      }
      {
        name: 'AzureAIFoundry__DefaultModelDeployment'
        value: 'gpt-4o'
      }
      {
        name: 'AzureAI__Endpoint'
        value: aiServicesEndpoint
      }
    ]
  : []
var agentOrchestratorEnv = concat(agentOrchestratorEnvWithDb, agentOrchestratorFoundryEnv, [
  {
    name: 'AZURE_CLIENT_ID'
    value: acrPullIdentityClientId
  }
])

var postgresConnectionStringSecret = [
  {
    name: 'postgresconnectionstring'
    keyVaultUrl: keyVaultSecretConnectionString
    identity: acrPullIdentityResourceId
  }
]
var frontendIngressIpRestrictions = [
  for cidr in frontendAllowedIpCidrs: {
    name: 'allow-${replace(replace(cidr, '.', '-'), '/', '-')}'
    description: 'Allow frontend access from configured CIDR ${cidr}'
    ipAddressRange: cidr
    action: 'Allow'
  }
]

resource controlPlaneApp 'Microsoft.App/containerApps@2024-03-01' = {
  name: '${projectName}-control-plane-api-${environment}'
  location: location
  tags: union(tags, {
    'azd-service-name': 'control-plane-api'
  })
  identity: {
    // Dual identity: user-assigned for ACR pull / Key Vault, system-assigned for Azure Resource Graph discovery
    type: 'SystemAssigned, UserAssigned'
    userAssignedIdentities: {
      '${acrPullIdentityResourceId}': {}
    }
  }
  properties: {
    managedEnvironmentId: containerAppEnvironmentId
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: {
        external: true
        targetPort: 8080
        transport: 'http'
        allowInsecure: false
      }
      registries: [
        {
          server: acrLoginServer
          identity: acrPullIdentityResourceId
        }
      ]
      secrets: empty(postgresConnectionString) ? postgresConnectionStringSecret : null
    }
    template: {
      containers: [
        {
          name: 'control-plane-api'
          image: empty(controlPlaneImageName) ? bootstrapImage : controlPlaneImageName
          resources: {
            cpu: json('0.5')
            memory: '1Gi'
          }
          env: controlPlaneApiFinalEnv
        }
      ]
      scale: {
        minReplicas: 1
        maxReplicas: 3
      }
    }
  }
}

resource agentOrchestratorApp 'Microsoft.App/containerApps@2024-03-01' = {
  name: '${projectName}-agent-orchestrator-${environment}'
  location: location
  tags: union(tags, {
    'azd-service-name': 'agent-orchestrator'
  })
  identity: {
    // Dual identity: user-assigned for ACR pull / Key Vault, system-assigned for Azure Resource Graph discovery
    type: 'SystemAssigned, UserAssigned'
    userAssignedIdentities: {
      '${acrPullIdentityResourceId}': {}
    }
  }
  properties: {
    managedEnvironmentId: containerAppEnvironmentId
    configuration: {
      activeRevisionsMode: 'Single'
      registries: [
        {
          server: acrLoginServer
          identity: acrPullIdentityResourceId
        }
      ]
      secrets: empty(postgresConnectionString) ? postgresConnectionStringSecret : null
    }
    template: {
      containers: [
        {
          name: 'agent-orchestrator'
          image: empty(agentOrchestratorImageName) ? bootstrapImage : agentOrchestratorImageName
          resources: {
            cpu: json('0.5')
            memory: '1Gi'
          }
          env: agentOrchestratorEnv
        }
      ]
      scale: {
        minReplicas: 1
        maxReplicas: 2
      }
    }
  }
}

resource frontendApp 'Microsoft.App/containerApps@2024-03-01' = {
  name: '${projectName}-frontend-${environment}'
  location: location
  tags: union(tags, {
    'azd-service-name': 'frontend'
  })
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${acrPullIdentityResourceId}': {}
    }
  }
  properties: {
    managedEnvironmentId: containerAppEnvironmentId
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: {
        external: true
        targetPort: 8080
        transport: 'http'
        allowInsecure: false
        ipSecurityRestrictions: length(frontendIngressIpRestrictions) > 0 ? frontendIngressIpRestrictions : null
      }
      registries: [
        {
          server: acrLoginServer
          identity: acrPullIdentityResourceId
        }
      ]
    }
    template: {
      containers: [
        {
          name: 'frontend'
          image: empty(frontendImageName) ? bootstrapImage : frontendImageName
          resources: {
            cpu: json('0.25')
            memory: '0.5Gi'
          }
          env: [
            {
              name: 'CONTROL_PLANE_API_BASE_URL'
              value: 'https://${controlPlaneApp.properties.configuration.ingress.fqdn}/api'
            }
            {
              name: 'NODE_ENV'
              value: 'production'
            }
          ]
        }
      ]
      scale: {
        minReplicas: 1
        maxReplicas: 3
      }
    }
  }
}

output controlPlaneAppFqdn string = controlPlaneApp.properties.configuration.ingress.fqdn
output frontendAppFqdn string = frontendApp.properties.configuration.ingress.fqdn
// System-assigned managed identity principal IDs for subscription-scope role assignments
output controlPlaneSystemAssignedPrincipalId string = controlPlaneApp.identity.principalId
output agentOrchestratorSystemAssignedPrincipalId string = agentOrchestratorApp.identity.principalId
