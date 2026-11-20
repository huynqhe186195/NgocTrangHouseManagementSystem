# P61 launches the existing roster-only disposable lane, then captures the
# local-only invoice design route with an authenticated session. No invoice API
# or production database is used.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$here = $PSScriptRoot
$lane = Join-Path $here 'start-p60-roster-lane.ps1'
$capture = Join-Path $here 'capture-p61-invoice-preview.mjs'
$container = 'bm-p21-pg-15432'
$containerInfo = docker inspect $container | ConvertFrom-Json
$passwordEntry = $containerInfo[0].Config.Env | Where-Object { $_ -like 'POSTGRES_PASSWORD=*' } | Select-Object -First 1
if ([string]::IsNullOrWhiteSpace($passwordEntry)) { throw 'Disposable PostgreSQL password is unavailable.' }
$pgPassword = $passwordEntry.Substring('POSTGRES_PASSWORD='.Length)
[Environment]::SetEnvironmentVariable('BM_DISPOSABLE_PG_PASSWORD', $pgPassword, 'Process')
$pw = 'P61Capture-' + [Guid]::NewGuid().ToString('N').Substring(0, 12) + '-9x'
$runId = [Guid]::NewGuid().ToString('N').Substring(0, 12)
$identityDb = "bm_slicea_staging_identity_$runId"
$buildingDb = "bm_slicea_staging_building_$runId"
$rs = [runspacefactory]::CreateRunspace(); $rs.Open()
$lanePs = [powershell]::Create(); $lanePs.Runspace = $rs
$null = $lanePs.AddCommand($lane).AddParameter('AdminPassword', $pw).AddParameter('RunId', $runId)
$handle = $lanePs.BeginInvoke()

function Wait-Port([int]$Port, [int]$Seconds, [string]$What) {
    for ($i = 0; $i -lt $Seconds; $i++) {
        if ($lanePs.InvocationStateInfo.State -eq 'Failed' -or $lanePs.InvocationStateInfo.State -eq 'Completed') {
            throw "$What ended before readiness: $($lanePs.Streams.Error | Out-String)"
        }
        if (Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue) { return }
        Start-Sleep -Seconds 1
    }
    throw "$What did not start within $Seconds seconds."
}

try {
    Wait-Port 44321 420 'roster staging Host'
    Wait-Port 5273 120 'Vite'
    [Environment]::SetEnvironmentVariable('LANE_ADMIN_PASSWORD', $pw, 'Process')
    [Environment]::SetEnvironmentVariable('LANE_BASE', 'http://localhost:5273', 'Process')
    & node $capture
    if ($LASTEXITCODE -ne 0) { throw "P61 capture failed with exit code $LASTEXITCODE." }
}
finally {
    Remove-Item Env:LANE_ADMIN_PASSWORD -ErrorAction SilentlyContinue
    Remove-Item Env:LANE_BASE -ErrorAction SilentlyContinue
    Remove-Item Env:BM_DISPOSABLE_PG_PASSWORD -ErrorAction SilentlyContinue
    try { $lanePs.Stop() } catch { }
    try { $lanePs.Dispose() } catch { }
    try { $rs.Close(); $rs.Dispose() } catch { }
    foreach ($port in 5273, 44321) {
        Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue |
            ForEach-Object { Stop-Process -Id $_.OwningProcess -Force -ErrorAction SilentlyContinue }
    }
    $cleanupErrorAction = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    & docker exec $container dropdb -U postgres --if-exists $identityDb 2>$null | Out-Null
    & docker exec $container dropdb -U postgres --if-exists $buildingDb 2>$null | Out-Null
    $ErrorActionPreference = $cleanupErrorAction
}
