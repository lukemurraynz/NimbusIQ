#!/usr/bin/env pwsh
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-AzdEnvValue {
  param(
    [Parameter(Mandatory)]
    [string]$Name
  )

  $line = azd env get-values | Select-String "^$Name=" | Select-Object -First 1
  if (-not $line) {
    return $null
  }

  $raw = $line.Line.Substring($Name.Length + 1)
  return $raw.Trim('"')
}

$subscriptionId = Get-AzdEnvValue -Name 'AZURE_SUBSCRIPTION_ID'
if ([string]::IsNullOrWhiteSpace($subscriptionId)) {
  $subscriptionId = az account show --query id -o tsv
}

$runtimeUamiPrincipalId = Get-AzdEnvValue -Name 'SERVICE_MANAGED_IDENTITY_PRINCIPAL_ID'
$resourceGroupName = Get-AzdEnvValue -Name 'AZURE_RESOURCE_GROUP'
$controlPlaneSamiPrincipalId = $null

if (-not [string]::IsNullOrWhiteSpace($resourceGroupName)) {
  $controlPlaneApiUrl = Get-AzdEnvValue -Name 'CONTROL_PLANE_API_URL'
  if ([string]::IsNullOrWhiteSpace($controlPlaneApiUrl)) {
    $controlPlaneApiUrl = Get-AzdEnvValue -Name 'CONTROL_PLANE_API_BASE_URL'
  }

  $controlPlaneAppName = $null
  if (-not [string]::IsNullOrWhiteSpace($controlPlaneApiUrl)) {
    try {
      $controlPlaneAppName = ([System.Uri]$controlPlaneApiUrl).Host.Split('.')[0]
    }
    catch {
      $controlPlaneAppName = $null
    }
  }

  if ([string]::IsNullOrWhiteSpace($controlPlaneAppName)) {
    $projectName = Get-AzdEnvValue -Name 'NIMBUSIQ_PROJECT_NAME'
    if ([string]::IsNullOrWhiteSpace($projectName)) {
      $projectName = 'nimbusiq'
    }

    # Infrastructure template currently uses fixed resource suffix '-prod'.
    $controlPlaneAppName = "$projectName-control-plane-api-prod"
  }

  $controlPlaneSamiPrincipalId = az containerapp show `
    --resource-group "$resourceGroupName" `
    --name "$controlPlaneAppName" `
    --query "identity.principalId" `
    -o tsv
}

$principalIds = @($runtimeUamiPrincipalId, $controlPlaneSamiPrincipalId) |
Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
Select-Object -Unique

if ((@($principalIds) | Measure-Object).Count -eq 0) {
  throw 'No managed identity principal IDs were discovered from azd environment outputs or Container Apps resources.'
}

$scope = "/subscriptions/$subscriptionId"
$requiredRoles = @(
  'Reader',
  'Monitoring Reader',
  'Cost Management Reader',
  'Carbon Optimization Reader' # Required for Azure Carbon Optimization API (AzureCarbonClient)
)

foreach ($principalId in $principalIds) {
  Write-Host "Ensuring subscription roles for principal $principalId at $scope"

  foreach ($role in $requiredRoles) {
    $existingCount = az role assignment list --assignee "$principalId" --scope "$scope" --query "[?roleDefinitionName=='$role'] | length(@)" -o tsv

    if ($existingCount -gt 0) {
      Write-Host "Role already assigned: $role"
      continue
    }

    Write-Host "Assigning role: $role"
    az role assignment create --assignee "$principalId" --role "$role" --scope "$scope" --only-show-errors | Out-Null
  }
}

Write-Host 'Runtime subscription RBAC role assignment complete.'
