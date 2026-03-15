# T004 - Local Deployment Script
<#
.SYNOPSIS
    Deploy Atlas platform locally using Docker Compose

.DESCRIPTION
    Provisions local PostgreSQL, OTLP collector, APIs, and frontend for local development

.PARAMETER SkipBuild
    Skip building .NET and frontend projects

.PARAMETER SkipInfra
    Skip infrastructure provisioning (use existing containers)

.EXAMPLE
    .\deploy-local.ps1
    .\deploy-local.ps1 -SkipBuild
#>

[CmdletBinding()]
param(
    [switch]$SkipBuild,
    [switch]$SkipInfra
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot | Split-Path -Parent
Write-Host "Repository root: $repoRoot" -ForegroundColor Cyan

# T004 - Build projects
if (-not $SkipBuild) {
    Write-Host "`n==> Building .NET projects..." -ForegroundColor Green

    Push-Location "$repoRoot/apps/control-plane-api"
    dotnet build ControlPlaneApi.slnx --configuration Debug
    Pop-Location

    Push-Location "$repoRoot/apps/agent-orchestrator"
    dotnet build AgentOrchestrator.slnx --configuration Debug
    Pop-Location

    Write-Host "`n==> Building frontend..." -ForegroundColor Green
    Push-Location "$repoRoot/apps/frontend"
    npm install
    npm run build
    Pop-Location
}

# T004 - Start infrastructure
if (-not $SkipInfra) {
    Write-Host "`n==> Starting local infrastructure..." -ForegroundColor Green

    if (Test-Path "$repoRoot/infra/docker/compose.local.yml") {
        Push-Location "$repoRoot/infra/docker"
        docker-compose -f compose.local.yml up -d
        Pop-Location

        Write-Host "Waiting for PostgreSQL to be ready..." -ForegroundColor Yellow
        Start-Sleep -Seconds 10
    } else {
        Write-Host "Docker Compose file not found. Skipping infrastructure." -ForegroundColor Yellow
    }
}

Write-Host "`n==> Local deployment complete!" -ForegroundColor Green
Write-Host "Control Plane API: http://localhost:5000" -ForegroundColor Cyan
Write-Host "Frontend: http://localhost:3000" -ForegroundColor Cyan
Write-Host "PostgreSQL: localhost:5432 (user: nimbusiq, db: nimbusiq_dev)" -ForegroundColor Cyan
