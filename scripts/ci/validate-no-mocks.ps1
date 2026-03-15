#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Anti-mock policy enforcement for runtime, integration, E2E, and release paths.

.DESCRIPTION
    Validates that no mocks/stubs/fakes/simulators are configured in production paths.
    Fails CI if mock configuration is detected outside unit/component test directories.

.PARAMETER Path
    Root path to scan (defaults to repository root).

.PARAMETER Strict
    Enable strict mode (fails on any suspicious patterns).

.EXAMPLE
    .\validate-no-mocks.ps1
    
.EXAMPLE
    .\validate-no-mocks.ps1 -Path ./apps -Strict
#>

[CmdletBinding()]
param(
    [Parameter()]
    [string]$Path = ".",
    
    [Parameter()]
    [switch]$Strict
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Patterns that indicate mock/stub/fake/simulator usage
$suspiciousPatterns = @(
    # Moq patterns
    '\.UseMock',
    'Mock<',
    'new Mock<',
    '\.Setup\(',
    '\.SetupGet\(',
    '\.SetupSet\(',
    '\.Returns\(',
    '\.Callback\(',
    '\.Verifiable\(',
    '\.Verify\(',
    
    # NSubstitute patterns
    'Substitute\.For',
    'Substitute\.ForPartsOf',
    '\.Received\(',
    '\.DidNotReceive\(',
    '\.ReceivedWithAnyArgs\(',
    
    # FakeItEasy patterns
    'A\.Fake',
    'A\.CallTo',
    'A\.Dummy',
    
    # Generic patterns
    'Simulator',
    'TestDouble',
    'InMemory.*Provider',
    'Fake.*Client',
    'Mock.*Service',
    'Stub.*Repository',
    'Test.*Double'
)

# Allowed directories (unit/component tests)
$allowedDirs = @(
    'tests/Unit',
    'tests/Component',
    'test/unit',
    'test/component',
    '__tests__/unit',
    '__tests__/component',
    '/Unit/',
    '/Component/'
)

# Integration test directories (must NOT contain mocks)
$integrationDirs = @(
    'tests/Integration',
    'test/integration',
    '__tests__/integration',
    '/Integration/'
)

# E2E test directories (must NOT contain mocks)
$e2eDirs = @(
    'tests/E2E',
    'tests/e2e',
    'test/e2e',
    '__tests__/e2e',
    '/E2E/',
    '/e2e/'
)

# Exclude patterns
$excludePatterns = @(
    '*/node_modules/*',
    '*/bin/*',
    '*/obj/*',
    '*/dist/*',
    '*/.git/*'
)

Write-Host "🔍 Scanning for mock/stub/fake usage outside unit/component tests..." -ForegroundColor Cyan
Write-Host "Path: $Path" -ForegroundColor Gray
Write-Host ""
Write-Host "Allowed: Unit and Component tests only" -ForegroundColor Green
Write-Host "Prohibited: Integration tests, E2E tests, src/, runtime code" -ForegroundColor Red
Write-Host ""

$violations = @()
$integrationViolations = @()
$filesScanned = 0

# Get all source files (C#, TypeScript, JavaScript)
$sourceFiles = Get-ChildItem -Path $Path -Recurse -Include *.cs,*.ts,*.tsx,*.js,*.jsx,*.json -File | Where-Object {
    $file = $_
    $relativePath = $file.FullName.Replace((Get-Location).Path, "").TrimStart('\', '/')
    
    # Skip excluded paths
    $excluded = $false
    foreach ($exclude in $excludePatterns) {
        if ($relativePath -like $exclude) {
            $excluded = $true
            break
        }
    }
    
    # Skip allowed test directories
    $inAllowedDir = $false
    foreach ($allowedDir in $allowedDirs) {
        if ($relativePath -like "*$allowedDir*") {
            $inAllowedDir = $true
            break
        }
    }
    
    -not $excluded -and -not $inAllowedDir
}

foreach ($file in $sourceFiles) {
    $filesScanned++
    $relativePath = $file.FullName.Replace((Get-Location).Path, "").TrimStart('\', '/')
    $content = Get-Content -Path $file.FullName -Raw -ErrorAction SilentlyContinue
    
    if (-not $content) { continue }
    
    # Check if file is in integration or E2E test directory
    $inIntegrationDir = $false
    foreach ($integrationDir in $integrationDirs) {
        if ($relativePath -like "*$integrationDir*") {
            $inIntegrationDir = $true
            break
        }
    }
    
    $inE2EDir = $false
    foreach ($e2eDir in $e2eDirs) {
        if ($relativePath -like "*$e2eDir*") {
            $inE2EDir = $true
            break
        }
    }
    
    foreach ($pattern in $suspiciousPatterns) {
        if ($content -match $pattern) {
            $lineMatch = $content -split "`n" | Select-String -Pattern $pattern | Select-Object -First 1
            $lineNumber = if ($lineMatch) { $lineMatch.LineNumber } else { 1 }
            
            $violation = [PSCustomObject]@{
                File = $relativePath
                Pattern = $pattern
                LineNumber = $lineNumber
                Context = if ($inIntegrationDir) { "Integration Test" } elseif ($inE2EDir) { "E2E Test" } else { "Runtime/Src" }
            }
            
            if ($inIntegrationDir -or $inE2EDir) {
                $integrationViolations += $violation
            } else {
                $violations += $violation
            }
        }
    }
}

Write-Host "✅ Scanned $filesScanned files" -ForegroundColor Green

$hasFailures = $false

if ($integrationViolations.Count -gt 0) {
    Write-Host ""
    Write-Host "❌ CRITICAL: Found $($integrationViolations.Count) mock usages in Integration/E2E tests:" -ForegroundColor Red
    Write-Host ""
    
    foreach ($violation in $integrationViolations) {
        Write-Host "  File: $($violation.File):$($violation.LineNumber)" -ForegroundColor Yellow
        Write-Host "  Pattern: $($violation.Pattern)" -ForegroundColor Gray
        Write-Host "  Context: $($violation.Context)" -ForegroundColor Magenta
        Write-Host ""
    }
    $hasFailures = $true
}

if ($violations.Count -gt 0) {
    Write-Host ""
    Write-Host "⚠️  WARNING: Found $($violations.Count) potential mock usages in non-integration code:" -ForegroundColor Yellow
    Write-Host ""
    
    foreach ($violation in $violations) {
        Write-Host "  File: $($violation.File):$($violation.LineNumber)" -ForegroundColor Yellow
        Write-Host "  Pattern: $($violation.Pattern)" -ForegroundColor Gray
        Write-Host "  Context: $($violation.Context)" -ForegroundColor Magenta
        Write-Host ""
    }
    $hasFailures = $true
}

if ($hasFailures) {
    Write-Host ""
    Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor Red
    Write-Host "Policy: Mock/stub/fake/simulator usage is PROHIBITED outside unit tests" -ForegroundColor Red
    Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor Red
    Write-Host ""
    Write-Host "✅ Allowed: Unit tests and Component tests only" -ForegroundColor Green
    Write-Host "❌ Prohibited: Integration tests, E2E tests, src/, runtime code" -ForegroundColor Red
    Write-Host ""
    Write-Host "Integration tests MUST use real dependencies (PostgreSQL, Redis, Azure services)" -ForegroundColor Yellow
    Write-Host "E2E tests MUST validate against production-like environments" -ForegroundColor Yellow
    Write-Host ""
    exit 1
}

Write-Host "✅ PASS: No mock/stub/fake usage detected outside unit/component tests" -ForegroundColor Green
Write-Host ""
Write-Host "Summary:" -ForegroundColor Cyan
Write-Host "  Files scanned: $filesScanned" -ForegroundColor Gray
Write-Host "  Violations: 0" -ForegroundColor Green
Write-Host "  Integration/E2E tests: Clean (no mocks detected)" -ForegroundColor Green
Write-Host ""
exit 0
