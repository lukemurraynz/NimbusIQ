$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$api = 'https://nimbusiq-control-plane-api-prod.mangomeadow-c84634f4.australiaeast.azurecontainerapps.io'
$front = 'https://nimbusiq-frontend-prod.mangomeadow-c84634f4.australiaeast.azurecontainerapps.io'
$results = @()

function Add-Result {
    param(
        [string]$Name,
        [bool]$Ok,
        [string]$Detail
    )

    $script:results += [pscustomobject]@{
        check  = $Name
        ok     = $Ok
        detail = $Detail
    }
}

function Safe-Invoke {
    param(
        [string]$Name,
        [scriptblock]$Action
    )

    try {
        & $Action
    }
    catch {
        Add-Result -Name $Name -Ok $false -Detail $_.Exception.Message
    }
}

Safe-Invoke 'health-live' {
    $r = Invoke-WebRequest "$api/health/live" -TimeoutSec 60 -SkipHttpErrorCheck
    Add-Result 'health-live' ($r.StatusCode -eq 200) "status=$($r.StatusCode)"
}

Safe-Invoke 'health-ready' {
    $r = Invoke-WebRequest "$api/health/ready" -TimeoutSec 60 -SkipHttpErrorCheck
    Add-Result 'health-ready' ($r.StatusCode -eq 200) "status=$($r.StatusCode)"
}

Safe-Invoke 'health-foundry' {
    $r = Invoke-WebRequest "$api/health/foundry" -TimeoutSec 60 -SkipHttpErrorCheck
    $preview = if ($r.Content.Length -gt 180) { $r.Content.Substring(0, 180) } else { $r.Content }
    Add-Result 'health-foundry' ($r.StatusCode -eq 200) "status=$($r.StatusCode); body=$preview"
}

$script:sgList = @()
Safe-Invoke 'service-groups-list' {
    $sgResp = Invoke-WebRequest "$front/api/v1/service-groups?api-version=2025-02-16" -TimeoutSec 90 -SkipHttpErrorCheck
    $script:sgList = @((($sgResp.Content | ConvertFrom-Json).value))
    Add-Result 'service-groups-list' ($sgResp.StatusCode -eq 200 -and $script:sgList.Count -ge 1) "status=$($sgResp.StatusCode); count=$($script:sgList.Count)"
}

if ($script:sgList.Count -eq 0) {
    Safe-Invoke 'service-groups-discover' {
        $d = Invoke-WebRequest "$front/api/v1/service-groups/discover?api-version=2025-02-16" -Method Post -TimeoutSec 180 -SkipHttpErrorCheck
        $dBody = $d.Content | ConvertFrom-Json
        Add-Result 'service-groups-discover' ($d.StatusCode -in 200, 202) "status=$($d.StatusCode); discovered=$($dBody.discovered)"

        $sgResp = Invoke-WebRequest "$front/api/v1/service-groups?api-version=2025-02-16" -TimeoutSec 90 -SkipHttpErrorCheck
        $script:sgList = @((($sgResp.Content | ConvertFrom-Json).value))
        Add-Result 'service-groups-list-after-discover' ($sgResp.StatusCode -eq 200 -and $script:sgList.Count -ge 1) "status=$($sgResp.StatusCode); count=$($script:sgList.Count)"
    }
}

$sg = $null
$script:runId = $null

if ($script:sgList.Count -gt 0) {
    Safe-Invoke 'analysis-run' {
        $state = 'unknown'
        foreach ($candidate in $script:sgList) {
            $candidateSg = $candidate.id
            $start = Invoke-WebRequest "$front/api/v1/service-groups/$candidateSg/analysis?api-version=2025-02-16" -Method Post -TimeoutSec 180 -SkipHttpErrorCheck
            $op = ($start.Headers['operation-location'] | Select-Object -First 1)
            if ($op.StartsWith('/')) {
                $op = "$front$op"
            }

            for ($i = 0; $i -lt 24; $i++) {
                Start-Sleep -Seconds 10
                $poll = Invoke-WebRequest $op -TimeoutSec 120 -SkipHttpErrorCheck
                $pb = $poll.Content | ConvertFrom-Json
                $state = $pb.status
                if ($state -in @('completed', 'partial', 'failed', 'cancelled')) {
                    if ($pb.runId) {
                        $script:runId = $pb.runId
                    }

                    break
                }
            }

            $recsResp = Invoke-WebRequest "$front/api/v1/recommendations?serviceGroupId=$candidateSg&limit=50" -TimeoutSec 120 -SkipHttpErrorCheck
            $recs = @((($recsResp.Content | ConvertFrom-Json).value))
            if ($recs.Count -gt 0) {
                $script:sg = $candidateSg
                break
            }
        }

        Add-Result 'analysis-run' ($state -in @('completed', 'partial')) "state=$state; runId=$script:runId; selectedServiceGroup=$script:sg"
    }

    Safe-Invoke 'recommendations-flow' {
        if ($null -eq $script:sg) {
            throw 'No service group produced recommendations after analysis.'
        }

        $recsResp = Invoke-WebRequest "$front/api/v1/recommendations?serviceGroupId=$script:sg&limit=50" -TimeoutSec 120 -SkipHttpErrorCheck
        $recs = @((($recsResp.Content | ConvertFrom-Json).value))
        Add-Result 'recommendations-list' ($recsResp.StatusCode -eq 200 -and $recs.Count -ge 1) "status=$($recsResp.StatusCode); count=$($recs.Count)"

        if ($recs.Count -gt 0) {
            $rid = $recs[0].id

            $detail = Invoke-WebRequest "$front/api/v1/recommendations/$rid" -TimeoutSec 120 -SkipHttpErrorCheck
            $d = $detail.Content | ConvertFrom-Json
            Add-Result 'recommendation-detail' ($detail.StatusCode -eq 200 -and -not [string]::IsNullOrWhiteSpace($d.title)) "status=$($detail.StatusCode); title=$($d.title)"

            $wf = Invoke-WebRequest "$front/api/v1/recommendations/$rid/workflow" -TimeoutSec 120 -SkipHttpErrorCheck
            Add-Result 'recommendation-workflow' ($wf.StatusCode -eq 200) "status=$($wf.StatusCode)"

            $lin = Invoke-WebRequest "$front/api/v1/recommendations/$rid/lineage" -TimeoutSec 120 -SkipHttpErrorCheck
            $lb = $lin.Content | ConvertFrom-Json
            Add-Result 'recommendation-lineage' ($lin.StatusCode -eq 200 -and $lb.steps.Count -ge 1) "status=$($lin.StatusCode); steps=$($lb.steps.Count)"

            $conf = Invoke-WebRequest "$front/api/v1/recommendations/$rid/confidence-explainer" -TimeoutSec 120 -SkipHttpErrorCheck
            $cb = $conf.Content | ConvertFrom-Json
            Add-Result 'confidence-explainer' ($conf.StatusCode -eq 200 -and $cb.factors.Count -ge 1) "status=$($conf.StatusCode); trustLevel=$($cb.trustLevel)"

            $iac = Invoke-WebRequest "$front/api/v1/recommendations/$rid/iac-examples" -TimeoutSec 120 -SkipHttpErrorCheck
            $ib = $iac.Content | ConvertFrom-Json
            $iacOk = ($iac.StatusCode -eq 200 -and -not [string]::IsNullOrWhiteSpace($ib.bicepExample) -and -not [string]::IsNullOrWhiteSpace($ib.terraformExample))
            Add-Result 'iac-examples' $iacOk "status=$($iac.StatusCode); mode=$($ib.groundingMode); mcpStatus=$($ib.azureMcpContextStatus); bicep=$($ib.bicepModulePath):$($ib.bicepVersion)"

            $sim = Invoke-WebRequest "$front/api/v1/recommendations/$rid/policy-impact-simulation" -Method Post -Body '{}' -ContentType 'application/json' -TimeoutSec 120 -SkipHttpErrorCheck
            $sb = $sim.Content | ConvertFrom-Json
            Add-Result 'policy-simulation' ($sim.StatusCode -eq 200 -and $sb.policyDecision) "status=$($sim.StatusCode); decision=$($sb.policyDecision)"

            $taskBody = @{ provider = 'azure-devops'; notes = 'demo smoke task' } | ConvertTo-Json
            $task = Invoke-WebRequest "$front/api/v1/recommendations/$rid/tasks" -Method Post -Body $taskBody -ContentType 'application/json' -TimeoutSec 120 -SkipHttpErrorCheck
            $tb = $task.Content | ConvertFrom-Json
            Add-Result 'task-create' ($task.StatusCode -eq 200 -and $tb.taskId) "status=$($task.StatusCode); taskId=$($tb.taskId)"
        }
    }

    Safe-Invoke 'value-dashboard' {
        if ($null -eq $script:sg) {
            throw 'No service group selected for value dashboard check.'
        }

        $val = Invoke-WebRequest "$front/api/v1/value-tracking/dashboard?serviceGroupId=$script:sg&api-version=2025-01-23" -TimeoutSec 120 -SkipHttpErrorCheck
        $vb = $val.Content | ConvertFrom-Json
        $ok = ($val.StatusCode -eq 200 -and $null -ne $vb.totalEstimatedAnnualSavings)
        Add-Result 'value-dashboard' $ok "status=$($val.StatusCode); estimatedAnnual=$($vb.totalEstimatedAnnualSavings); evidenceStatus=$($vb.billingEvidenceStatus)"
    }
}

if ($null -ne $script:runId) {
    Safe-Invoke 'agent-messages' {
        $msgs = Invoke-WebRequest "$front/api/v1/analysis/$script:runId/messages?api-version=2025-02-16" -TimeoutSec 120 -SkipHttpErrorCheck
        $mb = @((($msgs.Content | ConvertFrom-Json).value))
        Add-Result 'agent-messages' ($msgs.StatusCode -eq 200 -and $mb.Count -ge 1) "status=$($msgs.StatusCode); count=$($mb.Count)"
    }
}

$results | Format-Table -AutoSize

$failed = @($results | Where-Object { -not $_.ok })
if ($failed.Count -gt 0) {
    Write-Host ""
    Write-Host "FAILED CHECKS:" -ForegroundColor Red
    $failed | Format-Table -AutoSize
    exit 2
}

Write-Host ""
Write-Host "All smoke checks passed." -ForegroundColor Green
