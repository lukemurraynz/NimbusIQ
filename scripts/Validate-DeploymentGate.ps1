[CmdletBinding()]
param(
    [string]$ApiBaseUrl = '',
    [switch]$RequireFoundry
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Get-AzdEnvValue {
    param([Parameter(Mandatory)][string]$Name)

    $line = azd env get-values | Select-String "^$Name=" | Select-Object -First 1
    if (-not $line) {
        return $null
    }

    return $line.Line.Substring($Name.Length + 1).Trim('"')
}

function Resolve-ApiBaseUrl {
    param([string]$ExplicitUrl)

    if (-not [string]::IsNullOrWhiteSpace($ExplicitUrl)) {
        return $ExplicitUrl.TrimEnd('/')
    }

    $fromEnv = Get-AzdEnvValue -Name 'CONTROL_PLANE_API_BASE_URL'
    if (-not [string]::IsNullOrWhiteSpace($fromEnv)) {
        return $fromEnv.TrimEnd('/')
    }

    $fromOutput = Get-AzdEnvValue -Name 'CONTROL_PLANE_API_URL'
    if (-not [string]::IsNullOrWhiteSpace($fromOutput)) {
        return $fromOutput.TrimEnd('/')
    }

    throw 'CONTROL_PLANE_API_BASE_URL / CONTROL_PLANE_API_URL was not found in the azd environment.'
}

function Get-RequireFoundrySetting {
    param([bool]$SwitchProvided)

    if ($SwitchProvided) {
        return $true
    }

    $raw = Get-AzdEnvValue -Name 'NIMBUSIQ_ENABLE_AI_FOUNDRY'
    if ([string]::IsNullOrWhiteSpace($raw)) {
        return $true
    }

    return $raw -in @('true', 'True', 'TRUE', '1', 'yes', 'on')
}

function Invoke-GateCheck {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$Url,
        [int[]]$AllowedStatusCodes = @(200)
    )

    Write-Host "Checking $Name -> $Url"
    $response = Invoke-WebRequest -Uri $Url -Method Get -TimeoutSec 30 -SkipHttpErrorCheck

    if ($AllowedStatusCodes -notcontains [int]$response.StatusCode) {
        throw "$Name check failed with status $($response.StatusCode)."
    }

    Write-Host "✅ $Name passed with status $($response.StatusCode)"
    return $response
}

$resolvedApiBaseUrl = Resolve-ApiBaseUrl -ExplicitUrl $ApiBaseUrl
$shouldRequireFoundry = Get-RequireFoundrySetting -SwitchProvided $RequireFoundry.IsPresent

Write-Host "Deployment gate starting for $resolvedApiBaseUrl"
Write-Host "Require Foundry health: $shouldRequireFoundry"

Invoke-GateCheck -Name 'Live health' -Url "$resolvedApiBaseUrl/health/live" | Out-Null
Invoke-GateCheck -Name 'Ready health' -Url "$resolvedApiBaseUrl/health/ready" | Out-Null

if ($shouldRequireFoundry) {
    $foundryResponse = Invoke-GateCheck -Name 'Foundry health' -Url "$resolvedApiBaseUrl/health/foundry"
    Write-Host "Foundry payload: $($foundryResponse.Content)"
}
else {
    Write-Host 'Skipping Foundry health gate because AI Foundry is disabled for this environment.'
}

Write-Host '✅ Deployment gate passed.'