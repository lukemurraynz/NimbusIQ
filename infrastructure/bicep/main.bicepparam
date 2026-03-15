using './main.bicep'

// Resolve key deployment settings from azd environment values when present.
// This keeps `AZURE_LOCATION` and feature toggles consistent across azd envs/CI.
param location = readEnvironmentVariable('AZURE_LOCATION', 'uksouth')
param environment = 'prod'
param projectName = readEnvironmentVariable('NIMBUSIQ_PROJECT_NAME', 'nimbusiq')
// Optional - when empty, password auth stays disabled and managed identity is primary auth.
param postgresAdminPassword = readEnvironmentVariable('NIMBUSIQ_POSTGRES_ADMIN_PASSWORD', '')
param allowAzureServicesToPostgres = bool(readEnvironmentVariable('NIMBUSIQ_POSTGRES_ALLOW_AZURE_SERVICES', 'false'))
param postgresAllowedIpAddresses = empty(readEnvironmentVariable('NIMBUSIQ_POSTGRES_ALLOWED_IPS', ''))
  ? []
  : split(replace(readEnvironmentVariable('NIMBUSIQ_POSTGRES_ALLOWED_IPS', ''), ' ', ''), ',')

// Feature toggles (default off for broad subscription compatibility)
param enableAIFoundry = bool(readEnvironmentVariable('NIMBUSIQ_ENABLE_AI_FOUNDRY', 'true'))
param enableModelDeployments = bool(readEnvironmentVariable('NIMBUSIQ_ENABLE_MODEL_DEPLOYMENTS', 'true'))
param enableCapabilityHost = bool(readEnvironmentVariable('NIMBUSIQ_ENABLE_CAPABILITY_HOST', 'false'))
param enablePrivateNetworking = bool(readEnvironmentVariable('NIMBUSIQ_ENABLE_PRIVATE_NETWORKING', 'false'))
param enableNetworkSecurityPerimeter = bool(readEnvironmentVariable('NIMBUSIQ_ENABLE_NSP', 'false'))
param nspMode = readEnvironmentVariable('NIMBUSIQ_NSP_MODE', 'Learning') // Learning -> Enforced after validation when NSP is enabled

// Container App images
// - First provision: service image vars are unset -> bootstrap image is used.
// - After `azd deploy`: azd writes SERVICE_*_IMAGE_NAME -> reprovision keeps deployed images.
param controlPlaneImageName = readEnvironmentVariable('SERVICE_CONTROL_PLANE_API_IMAGE_NAME', '')
param agentOrchestratorImageName = readEnvironmentVariable('SERVICE_AGENT_ORCHESTRATOR_IMAGE_NAME', '')
param frontendImageName = readEnvironmentVariable('SERVICE_FRONTEND_IMAGE_NAME', '')
param frontendAllowedIpCidrs = empty(readEnvironmentVariable('NIMBUSIQ_FRONTEND_ALLOWED_IP_CIDRS', ''))
  ? []
  : split(replace(readEnvironmentVariable('NIMBUSIQ_FRONTEND_ALLOWED_IP_CIDRS', ''), ' ', ''), ',')
param allowAnonymousReadOnlyInProd = bool(readEnvironmentVariable('NIMBUSIQ_ALLOW_ANONYMOUS_IN_PROD_READONLY', 'true'))
param allowAnonymousFullInProd = bool(readEnvironmentVariable('NIMBUSIQ_ALLOW_ANONYMOUS_IN_PROD_FULL', 'false'))
param bootstrapImage = readEnvironmentVariable('NIMBUSIQ_BOOTSTRAP_IMAGE', 'mcr.microsoft.com/dotnet/samples:aspnetapp')

// Skip Container Apps during provision - let azd deploy handle them after building images
// NOTE: azd deploy requires the target Container Apps to exist and be tagged with `azd-service-name`.
// Default to provisioning them, but allow opting out if you manage apps separately.
param skipContainerApps = bool(readEnvironmentVariable('NIMBUSIQ_SKIP_CONTAINER_APPS', 'false'))

param tags = {
  project: projectName
  environment: 'prod'
  managedBy: 'bicep'
}
