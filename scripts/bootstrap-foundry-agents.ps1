Param()

$ErrorActionPreference = 'Stop'

Write-Host "Bootstrapping Azure AI Foundry hosted agents..."

# Load azd environment values into process env if available
try {
  $lines = azd env get-values
  foreach ($line in $lines) {
    if ($line -match '^([^=]+)=(.*)$') {
      $name = $matches[1]
      $value = $matches[2].Trim('"')
      [System.Environment]::SetEnvironmentVariable($name, $value, 'Process')
    }
  }
}
catch {
  Write-Host "⚠️  Unable to load azd env values automatically: $($_.Exception.Message)"
}

$projectEndpoint = $env:AzureAIFoundry__ProjectEndpoint
if (-not $projectEndpoint) { $projectEndpoint = $env:AZURE_AI_PROJECT_CONNECTION_STRING }
if (-not $projectEndpoint) { $projectEndpoint = $env:AZURE_AI_PROJECT_ENDPOINT }

if (-not $projectEndpoint) {
  Write-Host "⚠️  Azure AI Foundry endpoint/connection string not found. Skipping hosted agent bootstrap."
  exit 0
}

if (-not $env:AzureAIFoundry__ProjectEndpoint) {
  $env:AzureAIFoundry__ProjectEndpoint = $projectEndpoint
}

if (-not $env:AzureAIFoundry__DefaultModelDeployment) {
  $env:AzureAIFoundry__DefaultModelDeployment = 'gpt-4o'
}

if (-not $env:AzureAIFoundry__CapabilityHostName -and -not $env:aiFoundryCapabilityHostName) {
  Write-Host "⚠️  Azure AI Foundry capability host name not found. Skipping hosted agent bootstrap."
  exit 0
}

dotnet run --project ./apps/agent-orchestrator/src/Bootstrap/Atlas.AgentOrchestrator.Bootstrap.csproj -c Release

Write-Host "✅ Foundry hosted agent bootstrap finished"
