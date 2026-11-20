# Runs the disposable dev lane and the P59 capture in one process, so the
# lane password never reaches a command line or a file: it is generated here,
# handed to the lane through a runspace parameter (memory only), and put into
# LANE_ADMIN_PASSWORD for the capture child process.
#
# Scratch harness. Not part of any slice.

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$here = $PSScriptRoot
$lane = Join-Path $here 'start-p43-dev-lane.ps1'
$capture = Join-Path $here 'capture-p59-tenants.mjs'
$container = 'bm-p21-pg-15432'

$pw = 'DevLane-' + [Guid]::NewGuid().ToString('N').Substring(0, 10) + '-9x'

# The caller tees the output into this folder before the capture runs, so it
# has to exist first or the pipeline dies at start and orphans the lane.
$out = Join-Path (Join-Path (Join-Path $here '..') 'captures') 'p59'
$null = New-Item -ItemType Directory -Force -Path $out

$rs = [runspacefactory]::CreateRunspace()
$rs.Open()
$lanePs = [powershell]::Create()
$lanePs.Runspace = $rs
$null = $lanePs.AddCommand($lane).AddParameter('AdminPassword', $pw)
$handle = $lanePs.BeginInvoke()

function Wait-Port([int]$Port, [int]$Seconds, [string]$What) {
    for ($i = 0; $i -lt $Seconds; $i++) {
        if ($lanePs.InvocationStateInfo.State -eq 'Failed' -or
            $lanePs.InvocationStateInfo.State -eq 'Completed') {
            throw "$What never came up; the lane ended early: " +
                $lanePs.Streams.Error | Out-String
        }
        if (Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue) {
            return
        }
        Start-Sleep -Seconds 1
    }
    throw "$What did not start within $Seconds seconds."
}

try {
    Wait-Port -Port 44321 -Seconds 420 -What 'The staging Host'
    Wait-Port -Port 5273 -Seconds 120 -What 'Vite'

    # The lane grants nine permissions. The tenant screens need
    # BuildingManagement.Tenants.Read on top of those; the per-building grant
    # itself is issued by POST /building (the first-owner bootstrap).
    # PS 5.1 strips quotes from native-command arguments, so every statement
    # goes in over stdin instead of as a -c argument.
    function Invoke-Sql([string]$Database, [string]$Sql) {
        $out = $Sql | & docker exec -i $container psql -U postgres -d $Database -t -A
        if ($LASTEXITCODE -ne 0) { throw "psql failed on $Database" }
        return (($out | Out-String).Trim())
    }

    $identityDb = Invoke-Sql 'postgres' `
        "SELECT datname FROM pg_database WHERE datname LIKE 'bm_slicea_staging_identity_%' ORDER BY datname DESC LIMIT 1"
    if (-not $identityDb) { throw 'Could not find the lane identity database.' }

    $buildingDb = Invoke-Sql 'postgres' `
        "SELECT datname FROM pg_database WHERE datname LIKE 'bm_slicea_staging_building_%' ORDER BY datname DESC LIMIT 1"
    if (-not $buildingDb) { throw 'Could not find the lane building database.' }

    $adminId = Invoke-Sql $identityDb `
        'SELECT "Id"::text FROM "AbpUsers" WHERE "UserName" = ''admin'' LIMIT 1'
    if (-not $adminId -or $adminId -notmatch '^[0-9a-f-]{36}$') {
        throw "Could not read the seeded admin id (got '$adminId')."
    }

    foreach ($name in 'BuildingManagement.Tenants.Read', 'BuildingManagement.Contracts.Read') {
        $null = Invoke-Sql $identityDb (
            "INSERT INTO ""AbpPermissionGrants"" (""Id"", ""Name"", ""ProviderName"", ""ProviderKey"", ""TenantId"") " +
            "VALUES (gen_random_uuid()::uuid, '$name', 'U', '$adminId', NULL)")
    }
    Write-Host "granted tenant permissions to the lane admin in $identityDb"

    $env:LANE_ADMIN_PASSWORD = $pw
    $env:LANE_BASE = 'http://localhost:5273'
    $env:LANE_DB_CONTAINER = $container
    $env:LANE_BUILDING_DB = $buildingDb
    & node $capture
    if ($LASTEXITCODE -ne 0) { throw "Capture failed with exit code $LASTEXITCODE." }
}
finally {
    $env:LANE_ADMIN_PASSWORD = $null
    try { $lanePs.Stop() } catch { }
    try { $lanePs.Dispose() } catch { }
    try { $rs.Close(); $rs.Dispose() } catch { }

    # The lane's own finally may not run once its runspace is stopped, so the
    # listeners and the two throwaway databases are cleaned up here too.
    foreach ($port in 5273, 44321) {
        Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue |
            ForEach-Object { Stop-Process -Id $_.OwningProcess -Force -ErrorAction SilentlyContinue }
    }
    $stale = & docker exec $container psql -U postgres -t -A -c `
        "SELECT datname FROM pg_database WHERE datname LIKE 'bm_slicea_staging_%'"
    foreach ($db in ($stale -split "`n" | ForEach-Object { $_.Trim() } | Where-Object { $_ })) {
        & docker exec $container dropdb -U postgres --if-exists $db 2>$null | Out-Null
        Write-Host "dropped disposable database $db"
    }
}
