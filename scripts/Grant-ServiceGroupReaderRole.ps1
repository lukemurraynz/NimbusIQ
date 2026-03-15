<#
.SYNOPSIS
    Grants Reader role to Container App managed identities for service group and subscription discovery.

.DESCRIPTION
    Atlas needs tenant-wide Reader access so the control-plane-api can discover Azure Service Groups
    (when the preview feature is registered) and enumerate all accessible subscriptions as a fallback.

    The script assigns roles to the user-assigned managed identity (used by DefaultAzureCredential
    via AZURE_CLIENT_ID) and optionally to system-assigned identities on the Container Apps.

    Least-privilege default:
    - Tenant-wide Reader fallback is disabled unless explicitly enabled.
    - Service Group Reader (preview role) is preferred when available.
    - Reader fallback at tenant-level scopes is only used when explicitly requested.

    Two scopes are handled:
    1. Root management group (/providers/Microsoft.Management/managementGroups/<tenantId>)
       — Reader role so the MI can enumerate all subscriptions via ARM/Resource Graph.
    2. Service Groups scope (/providers/Microsoft.Management/serviceGroups/<tenantId>)
       — Service Group Reader role, only when the Service Groups preview feature is registered.

    Called automatically by the azd postdeploy hook. The script is idempotent and safe to
    re-run — it checks for existing assignments before creating one.

    If the caller lacks the 'User Access Administrator' or 'Owner' role at the required scope,
    the script prints a warning and exits cleanly (exit code 0) so it does not block
    the overall deployment.

.NOTES
    Requires: az CLI, PowerShell 7+ (pwsh)
    Requires (for assignment): Owner or User Access Administrator at the root management group
    Feature flag (optional): Service Groups preview
        (az feature register --namespace Microsoft.Management --name ServiceGroups)

.EXAMPLE
    # Runs automatically via 'azd deploy' postdeploy hook.
    # Can also be run manually after deployment:
    ./scripts/Grant-ServiceGroupReaderRole.ps1

    # Preview only — no changes made:
    ./scripts/Grant-ServiceGroupReaderRole.ps1 -WhatIf
#>

[CmdletBinding(SupportsShouldProcess)]
param(
    # Override resource group (default: read from azd env AZURE_RESOURCE_GROUP)
    [string]$ResourceGroup = '',
    # Override project name (default: read from azd env or fallback to 'atlas')
    [string]$ProjectName = '',
    # Override environment name (default: read from azd env AZURE_ENV_NAME or fallback to 'prod')
    [string]$EnvironmentName = ''
    ,
    # Opt-in: allow tenant-wide Reader fallback at management-group/service-group scopes.
    # Default false to avoid broad RBAC assignments unless explicitly required.
    [switch]$EnableTenantWideReader
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------
function Write-Step([string]$msg)  { Write-Host "▶ $msg" -ForegroundColor Cyan }
function Write-OK([string]$msg)    { Write-Host "  ✅ $msg" -ForegroundColor Green }
function Write-Warn([string]$msg)  { Write-Host "  ⚠️  $msg" -ForegroundColor Yellow }
function Write-Fail([string]$msg)  { Write-Host "  ❌ $msg" -ForegroundColor Red }

Write-Host ""
Write-Host "🔐 Grant Reader Roles — Atlas post-deploy hook" -ForegroundColor Magenta
Write-Host "================================================" -ForegroundColor Magenta
Write-Host ""

# ---------------------------------------------------------------------------
# Step 1: Resolve environment values from azd
# ---------------------------------------------------------------------------
Write-Step "Reading azd environment values..."

$envValues = @{}
try {
    $rawValues = azd env get-values 2>$null
    if ($rawValues) {
        foreach ($line in $rawValues) {
            if ($line -match '^([^=]+)="?([^"]*)"?$') {
                $envValues[$Matches[1]] = $Matches[2]
            }
        }
    }
}
catch {
    Write-Warn "Could not read azd environment values — falling back to parameters and defaults."
}

if ([string]::IsNullOrEmpty($ResourceGroup)) {
    $ResourceGroup = $envValues['AZURE_RESOURCE_GROUP'] `
                  ?? $envValues['AZURE_RESOURCE_GROUP_NAME'] `
                  ?? ''
}
if ([string]::IsNullOrEmpty($ProjectName)) {
    $ProjectName = $envValues['NIMBUSIQ_PROJECT_NAME'] `
                ?? $envValues['ATLAS_PROJECT_NAME'] `
                ?? $envValues['AZURE_PROJECT_NAME'] `
                ?? 'nimbusiq'
}
if ([string]::IsNullOrEmpty($EnvironmentName)) {
    # Bicep uses a short environment name (e.g. 'prod'), not the full azd env name (e.g. 'atlas-prod').
    # Strip the project prefix from AZURE_ENV_NAME to derive the Bicep environment.
    $azdEnvName = $envValues['AZURE_ENV_NAME'] ?? 'prod'
    $prefix = "$ProjectName-"
    if ($azdEnvName.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        $EnvironmentName = $azdEnvName.Substring($prefix.Length)
    } else {
        $EnvironmentName = $azdEnvName
    }
}

# Validate derived environment against actual Container Apps in the target resource group.
# This avoids mismatches when AZURE_ENV_NAME (e.g. "nimbusiq-dev") differs from the
# Bicep environment suffix used in resource names (e.g. "...-prod").
try {
    $expectedControlPlaneApp = "${ProjectName}-control-plane-api-${EnvironmentName}"
    $appExists = az containerapp show `
        --resource-group $ResourceGroup `
        --name $expectedControlPlaneApp `
        --query "name" `
        --output tsv 2>$null

    if ([string]::IsNullOrWhiteSpace($appExists)) {
        $candidateNames = @(az containerapp list `
            --resource-group $ResourceGroup `
            --query "[?starts_with(name, '${ProjectName}-control-plane-api-')].name" `
            --output tsv 2>$null)

        if ($candidateNames.Count -eq 1) {
            $candidate = $candidateNames[0].Trim()
            if ($candidate -match "^${ProjectName}-control-plane-api-(.+)$") {
                $derived = $Matches[1]
                if (-not [string]::IsNullOrWhiteSpace($derived) -and $derived -ne $EnvironmentName) {
                    Write-Warn "Derived environment '$EnvironmentName' does not match deployed app names. Using '$derived' from '$candidate'."
                    $EnvironmentName = $derived
                }
            }
        }
    }
}
catch {
    Write-Warn "Could not validate environment name against Container Apps; continuing with '$EnvironmentName'."
}

if ([string]::IsNullOrEmpty($ResourceGroup)) {
    Write-Fail "AZURE_RESOURCE_GROUP is not set. Run 'azd provision' first or pass -ResourceGroup."
    exit 1
}

Write-OK "Resource Group : $ResourceGroup"
Write-OK "Project        : $ProjectName"
Write-OK "Environment    : $EnvironmentName"

if (-not $EnableTenantWideReader.IsPresent) {
    $optInRaw = $env:NIMBUSIQ_ENABLE_TENANT_WIDE_READER
    if (-not [string]::IsNullOrWhiteSpace($optInRaw)) {
        $optInNormalized = $optInRaw.Trim().ToLowerInvariant()
        if ($optInNormalized -in @('1', 'true', 'yes', 'on')) {
            $EnableTenantWideReader = $true
        }
    }
}

Write-OK "Tenant-wide Reader fallback enabled: $EnableTenantWideReader"

# ---------------------------------------------------------------------------
# Step 2: Verify az CLI is logged in and get tenant ID
# ---------------------------------------------------------------------------
Write-Step "Verifying Azure CLI login..."

$accountJson = az account show --output json 2>$null
if (-not $accountJson) {
    Write-Fail "Not logged in. Run 'az login' (or 'azd auth login') first."
    exit 1
}

$account  = $accountJson | ConvertFrom-Json
$tenantId = $account.tenantId

Write-OK "Subscription   : $($account.name) ($($account.id))"
Write-OK "Tenant         : $tenantId"

# ---------------------------------------------------------------------------
# Step 3: Retrieve managed identity principal IDs from the Container Apps
# ---------------------------------------------------------------------------
Write-Step "Retrieving managed identity principal IDs..."

function Get-ManagedIdentityPrincipalIds {
    [CmdletBinding()]
    param(
        [string]$AppName,
        [string]$Rg
    )

    $json = az containerapp show `
        --resource-group $Rg `
        --name $AppName `
        --query "identity" `
        --output json 2>$null

    if (-not $json) { return @() }

    $identity = $json | ConvertFrom-Json -ErrorAction SilentlyContinue
    if (-not $identity) { return @() }

    $ids = @()

    # User-assigned identities — these are used by DefaultAzureCredential via AZURE_CLIENT_ID
    if ($identity.userAssignedIdentities) {
        $uaHash = $identity.userAssignedIdentities
        foreach ($prop in $uaHash.PSObject.Properties) {
            $uaPrincipalId = $prop.Value.principalId
            if (-not [string]::IsNullOrEmpty($uaPrincipalId) -and $uaPrincipalId -ne 'null') {
                $ids += [pscustomobject]@{
                    PrincipalId = $uaPrincipalId
                    Type        = 'UserAssigned'
                    Detail      = $prop.Name.Split('/')[-1]
                }
            }
        }
    }

    # System-assigned identity
    $saPrincipalId = $identity.principalId
    if (-not [string]::IsNullOrEmpty($saPrincipalId) -and $saPrincipalId -ne 'null') {
        $ids += [pscustomobject]@{
            PrincipalId = $saPrincipalId
            Type        = 'SystemAssigned'
            Detail      = 'system'
        }
    }

    return $ids
}

function Get-TargetContainerAppNames {
    [CmdletBinding()]
    param(
        [string]$Rg,
        [string]$Project,
        [string]$Environment
    )

    $names = New-Object System.Collections.Generic.List[string]

    # Keep exact names first when they exist.
    foreach ($exact in @(
        "${Project}-control-plane-api-${Environment}",
        "${Project}-agent-orchestrator-${Environment}"
    )) {
        $exists = az containerapp show `
            --resource-group $Rg `
            --name $exact `
            --query "name" `
            --output tsv 2>$null
        if (-not [string]::IsNullOrWhiteSpace($exists)) {
            $names.Add($exact)
        }
    }

    # Add all matching apps across the resource group regardless of environment suffix.
    $patternMatches = @(az containerapp list `
        --resource-group $Rg `
        --query "[?starts_with(name, '${Project}-control-plane-api-') || starts_with(name, '${Project}-agent-orchestrator-')].name" `
        --output tsv 2>$null)

    foreach ($match in $patternMatches) {
        if (-not [string]::IsNullOrWhiteSpace($match)) {
            $names.Add($match.Trim())
        }
    }

    return @($names | Sort-Object -Unique)
}

$targetApps = @(Get-TargetContainerAppNames -Rg $ResourceGroup -Project $ProjectName -Environment $EnvironmentName)
$principals = @()

foreach ($appName in $targetApps) {
    $ids = @(Get-ManagedIdentityPrincipalIds -AppName $appName -Rg $ResourceGroup)
    foreach ($mi in $ids) {
        $principals += [pscustomobject]@{ Name = "$appName ($($mi.Type): $($mi.Detail))"; PrincipalId = $mi.PrincipalId }
        Write-OK "$appName [$($mi.Type)] : $($mi.PrincipalId)"
    }
}

# Deduplicate — the same user-assigned MI may appear on both apps
$principals = @($principals | Sort-Object PrincipalId -Unique)

if ($principals.Count -eq 0) {
    Write-Warn "No managed identity principal IDs found."
    Write-Warn "Checked all '${ProjectName}-control-plane-api-*' and '${ProjectName}-agent-orchestrator-*' Container Apps in '$ResourceGroup'."
    Write-Host ""
    exit 0
}

# ---------------------------------------------------------------------------
# Step 4: Optional Reader at root management group scope (tenant-wide discovery)
# ---------------------------------------------------------------------------
if (-not $EnableTenantWideReader) {
    Write-Step "Skipping root management group Reader assignment (least-privilege default)."
    Write-Warn "Set -EnableTenantWideReader or NIMBUSIQ_ENABLE_TENANT_WIDE_READER=true to opt in."
}
else {
    Write-Step "Assigning Reader at root management group scope..."

    $mgScope        = "/providers/Microsoft.Management/managementGroups/$tenantId"
    $readerRoleId   = 'acdd72a7-3385-48ef-bd42-f606fba81ae7'  # Built-in Reader

function Grant-RoleAtScope {
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [string]$Scope,
        [string]$RoleDefinitionId,
        [string]$RoleName,
        [string]$PrincipalId,
        [string]$PrincipalLabel
    )

    # Check for an existing assignment via ARM REST
    $listUrl = "https://management.azure.com$Scope/providers/Microsoft.Authorization/roleAssignments" +
               "?api-version=2022-04-01" +
               "&`$filter=assignedTo('$PrincipalId')"

    $existingJson  = az rest --method GET --url $listUrl --output json 2>$null
    $alreadyExists = $false

    if ($existingJson) {
        $existing = $existingJson | ConvertFrom-Json -ErrorAction SilentlyContinue
        if ($existing -and $existing.value) {
            # assignedTo filter returns ALL assignments visible to the principal (including other principals),
            # so we must match BOTH roleDefinitionId AND principalId.
            $match = $existing.value | Where-Object {
                $_.properties.roleDefinitionId -like "*$RoleDefinitionId*" -and
                $_.properties.principalId -eq $PrincipalId
            }
            if ($match) { $alreadyExists = $true }
        }
    }

    if ($alreadyExists) {
        Write-OK "$PrincipalLabel — '$RoleName' already assigned at $Scope"
        return $true
    }

    if ($PSCmdlet.ShouldProcess($PrincipalLabel, "Assign '$RoleName' at $Scope")) {
        # Stable GUID derived from scope + role + principal for idempotency
        $hashBytes = [System.Security.Cryptography.SHA256]::Create().ComputeHash(
            [System.Text.Encoding]::UTF8.GetBytes("$Scope|$RoleDefinitionId|$PrincipalId")
        )
        $guidBytes = [byte[]]::new(16)
        [System.Array]::Copy($hashBytes, $guidBytes, 16)
        $assignmentGuid = ([guid]::new($guidBytes)).ToString()

        $assignmentUrl = "https://management.azure.com$Scope/providers/Microsoft.Authorization/roleAssignments/${assignmentGuid}?api-version=2022-04-01"

        $body = @{
            properties = @{
                roleDefinitionId = "/providers/Microsoft.Authorization/roleDefinitions/$RoleDefinitionId"
                principalId      = $PrincipalId
                principalType    = 'ServicePrincipal'
            }
        } | ConvertTo-Json -Compress

        try {
            $bodyFile = [System.IO.Path]::GetTempFileName()
            Set-Content -Path $bodyFile -Value $body -Encoding utf8NoBOM

            $azResult = az rest `
                --method PUT `
                --url $assignmentUrl `
                --body "@$bodyFile" `
                --headers "Content-Type=application/json" `
                --only-show-errors 2>&1

            if ($LASTEXITCODE -eq 0) {
                Write-OK "$PrincipalLabel — '$RoleName' assigned (ID: $assignmentGuid)"
                return $true
            }
            else {
                $msg = ($azResult -join [Environment]::NewLine)
                throw [System.Exception]::new($msg)
            }
        }
        catch {
            $msg = $_.Exception.Message
            if ($msg -match 'AuthorizationFailed|does not have authorization|RoleAssignmentUpdateNotPermitted') {
                Write-Fail "Authorization failed for '$RoleName' at: $Scope"
                Write-Fail "The current principal needs Owner or User Access Administrator at that scope."
                Write-Fail ""
                Write-Fail "Manual command:"
                Write-Fail "  az role assignment create --role '$RoleName' --assignee-object-id $PrincipalId --assignee-principal-type ServicePrincipal --scope $Scope"
                return $false
            }
            elseif ($msg -match 'RoleAssignmentExists') {
                Write-OK "$PrincipalLabel — '$RoleName' already assigned (concurrent detection)."
                return $true
            }
            else {
                Write-Warn "Unexpected error assigning '$RoleName' to $PrincipalLabel : $msg"
                return $false
            }
        }
        finally {
            if ($bodyFile -and (Test-Path $bodyFile)) { Remove-Item $bodyFile -Force }
        }
    }
    return $true
}

    $mgAssignmentFailed = $false

    foreach ($principal in $principals) {
        Write-Host ""
        Write-Host "  Identity: $($principal.Name)"
        Write-Host "  ObjectId: $($principal.PrincipalId)"

        $result = Grant-RoleAtScope `
            -Scope $mgScope `
            -RoleDefinitionId $readerRoleId `
            -RoleName 'Reader' `
            -PrincipalId $principal.PrincipalId `
            -PrincipalLabel $principal.Name

        if (-not $result) { $mgAssignmentFailed = $true }
    }

    if ($mgAssignmentFailed) {
        Write-Host ""
        Write-Warn "Some root management group Reader assignments failed — see errors above."
        Write-Warn "The app will still start but may only discover the deployment subscription."
    }
}

# ---------------------------------------------------------------------------
# Step 5: Optionally assign Service Group Reader (if feature is registered)
# ---------------------------------------------------------------------------
Write-Step "Checking Azure Service Groups API accessibility..."

$sgScope     = "/providers/Microsoft.Management/serviceGroups/$tenantId"
# Verify SG API availability by GETting the root service group (the LIST endpoint doesn't exist).
$sgApiUrl    = "https://management.azure.com/providers/Microsoft.Management/serviceGroups/${tenantId}?api-version=2024-02-01-preview"

$sgResponse = az rest --method GET --url $sgApiUrl --output json 2>$null
if (-not $sgResponse) {
    Write-Warn "Service Groups API not available — the preview feature is not registered on this tenant."
    Write-Warn "Subscription-based discovery fallback will be used."
    Write-Warn ""
    Write-Warn "To enable Service Groups in the future:"
    Write-Warn "  az feature register --namespace Microsoft.Management --name ServiceGroups"
    Write-Warn "  az provider register --namespace Microsoft.Management"
} else {
    Write-OK "Service Groups API is accessible"

    # Resolve Service Group Reader role; fall back to Reader
    $sgRoleDefinitionId = $null
    $sgRoleName         = $null

    $sgRoleJson = az role definition list --name "Service Group Reader" --output json 2>$null
    if ($sgRoleJson) {
        $sgRoleDefs = $sgRoleJson | ConvertFrom-Json -ErrorAction SilentlyContinue
        if ($sgRoleDefs -and $sgRoleDefs.Count -gt 0) {
            $sgRoleDefinitionId = $sgRoleDefs[0].name
            $sgRoleName         = $sgRoleDefs[0].roleName
        }
    }

    if ([string]::IsNullOrEmpty($sgRoleDefinitionId)) {
        if ($EnableTenantWideReader) {
            $readerRoleId       = 'acdd72a7-3385-48ef-bd42-f606fba81ae7'
            $sgRoleDefinitionId = $readerRoleId
            $sgRoleName         = 'Reader (fallback — Service Group Reader not found)'
            Write-Warn "Service Group Reader role not found; using Reader as fallback (opt-in mode)."
        }
        else {
            Write-Warn "Service Group Reader role not found; skipping tenant-wide fallback Reader assignment."
            Write-Warn "Enable -EnableTenantWideReader only if tenant-wide Service Group discovery is required."
        }
    }

    if (-not [string]::IsNullOrEmpty($sgRoleDefinitionId)) {
        Write-OK "Role : $sgRoleName ($sgRoleDefinitionId)"
        Write-OK "Scope: $sgScope"

        foreach ($principal in $principals) {
            Write-Host ""
            Write-Host "  Identity: $($principal.Name)"

            Grant-RoleAtScope `
                -Scope $sgScope `
                -RoleDefinitionId $sgRoleDefinitionId `
                -RoleName $sgRoleName `
                -PrincipalId $principal.PrincipalId `
                -PrincipalLabel $principal.Name | Out-Null
        }
    }
}

Write-Host ""
Write-OK "Role assignment processing complete."
Write-Host ""
Write-Host "  Next: Call POST /api/v1/service-groups/discover?api-version=2025-02-16" -ForegroundColor Cyan
Write-Host "        to sync Service Groups (or subscriptions) into Atlas." -ForegroundColor Cyan
Write-Host ""
