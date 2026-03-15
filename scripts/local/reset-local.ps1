# T004 - Local Reset Script
<#
.SYNOPSIS
    Reset Atlas local development environment

.DESCRIPTION
    Stops and removes local containers and optionally cleans database data
    
.PARAMETER CleanData
    Remove all data volumes
    
.EXAMPLE
    .\reset-local.ps1
    .\reset-local.ps1 -CleanData
#>

[CmdletBinding()]
param(
    [switch]$CleanData
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot | Split-Path -Parent
Write-Host "Repository root: $repoRoot" -ForegroundColor Cyan

# Stop containers
Write-Host "`n==> Stopping local containers..." -ForegroundColor Yellow

if (Test-Path "$repoRoot/infra/docker/compose.local.yml") {
    Push-Location "$repoRoot/infra/docker"
    docker-compose -f compose.local.yml down
    
    if ($CleanData) {
        Write-Host "Removing data volumes..." -ForegroundColor Red
        docker-compose -f compose.local.yml down -v
    }
    
    Pop-Location
} else {
    Write-Host "Docker Compose file not found." -ForegroundColor Yellow
}

Write-Host "`n==> Local environment reset complete!" -ForegroundColor Green
