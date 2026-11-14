[CmdletBinding()]
param()

# P4.4 CRUD SCREENS E2E - disposable local staging lane only.
#
# The P4.4 screens (Buildings / Floors / Rooms create-edit-delete dialogs)
# talk to the SAME nine REST endpoints the P4.3 gate harness proved readable.
# This harness drives those endpoints through the Vite proxy exactly as the
# browser does, and asserts the full lifecycle plus every guard the UI
# surfaces to the Owner:
#
#   create building -> floor -> room        (201)
#   update building name / room status      (200, then GET shows the change)
#   delete room                             (200, then GET no longer lists it)
#   delete floor while it still has a room  (409 FloorHasRooms)
#   delete building while it still has floors (409 BuildingHasFloors)
#   create a room whose number contradicts its floor (400 RoomNumberDoesNotMatchFloor)
#   create a duplicate room number in the same floor  (409 RoomNumberAlreadyExists)
#   negative control: a random building id is still denied
#   then delete back down to empty and confirm all three lists are empty
#
# Safe-testing rules honored: 127.0.0.1:15432 disposable databases only,
# 15433/5432 untouched, the Owner's dev instance on 44311/5173 untouched
# (this lane uses 44321/5273 and its own host-bin output folder), no
# payOS, no real money, no production migration. The admin password is
# generated in-process and never echoed or written to disk.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
[System.Net.ServicePointManager]::SecurityProtocol = [System.Net.SecurityProtocolType]::Tls12

$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$hostDir = Join-Path $root 'src\NgocTrangHouseManagementSystem.HttpApi.Host'
$frontendDir = Join-Path $root 'frontend'
$container = 'bm-p21-pg-15432'
$pgPassword = 'postgres'
$hostPort = 44321
$vitePort = 5273

$logRoot = Join-Path $env:USERPROFILE 'NgocTrang-Test-Logs'
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$logDir = Join-Path $logRoot "p44-crud-screens-$stamp"
New-Item -ItemType Directory -Path $logDir -Force | Out-Null
$transcript = Join-Path $logDir 'run.log'

function Write-Step([string]$Message) {
    $line = '{0}  {1}' -f (Get-Date -Format 'HH:mm:ss'), $Message
    Write-Host $line
    Add-Content -Path $transcript -Value $line -Encoding utf8
}

# Invoke SQL on a database inside the disposable container, piping the SQL
# through stdin: Windows PowerShell 5.1 strips double quotes from native
# arguments, so -c "..." would corrupt any quoted identifier. stdin keeps
# the SQL verbatim. Returns the scalar/rows as text.
function Invoke-Db {
    param([string]$Database, [string]$Sql)
    $out = $Sql | & docker exec -i $container psql -U postgres -d $Database -t -A
    if ($LASTEXITCODE -ne 0) { throw "psql failed on $Database" }
    return $out
}

$envNames = @(
    'ASPNETCORE_ENVIRONMENT','DOTNET_ENVIRONMENT','ASPNETCORE_URLS',
    'ConnectionStrings__Default','ConnectionStrings__BuildingManagement',
    'AuthServer__CertificatePassPhrase','NgocTrang__LocalBootstrap__AdminPassword',
    'VITE_PROXY_TARGET',
    'BuildingManagement__SliceAStaging__Enabled',
    'BuildingManagement__SliceAStaging__Activation',
    'BuildingManagement__Bootstrap__RootUserIds__0'
)
$oldEnv = @{}
foreach ($name in $envNames) { $oldEnv[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }

$hostProcess = $null
$viteProcess = $null
$identityDb = $null
$buildingDb = $null
$originalCertCallback = [System.Net.ServicePointManager]::ServerCertificateValidationCallback

function Invoke-Web {
    param([string]$Method, [string]$Url, [string]$Form, [string]$Bearer, [string]$JsonBody)
    [System.Net.ServicePointManager]::ServerCertificateValidationCallback = { $true }
    try {
        $request = [System.Net.HttpWebRequest]::Create($Url)
        $request.Method = $Method
        $request.Proxy = $null
        $request.AllowAutoRedirect = $false
        $request.Timeout = 30000
        $request.Accept = 'application/json'
        if ($Bearer) { $request.Headers.Add('Authorization', "Bearer $Bearer") }
        $payload = $null
        if ($Form) {
            $request.ContentType = 'application/x-www-form-urlencoded'
            $payload = [Text.Encoding]::UTF8.GetBytes($Form)
        } elseif ($JsonBody) {
            $request.ContentType = 'application/json'
            $payload = [Text.Encoding]::UTF8.GetBytes($JsonBody)
        }
        if ($payload) {
            $request.ContentLength = $payload.Length
            $stream = $request.GetRequestStream()
            try { $stream.Write($payload, 0, $payload.Length) } finally { $stream.Dispose() }
        }
        try {
            $response = $request.GetResponse()
            try {
                $body = (New-Object IO.StreamReader($response.GetResponseStream())).ReadToEnd()
                return @{ Status = [int]$response.StatusCode; Body = $body }
            } finally { $response.Dispose() }
        } catch {
            # A .NET method that throws inside PowerShell surfaces as
            # MethodInvocationException, so catching WebException alone
            # misses every non-2xx response. Unwrap first.
            $ex = $_.Exception
            if ($ex -is [System.Management.Automation.MethodInvocationException]) {
                $ex = $ex.InnerException
            }
            if ($ex -isnot [System.Net.WebException]) { throw }
            $response = $ex.Response
            if (-not $response) { throw }
            $body = (New-Object IO.StreamReader($response.GetResponseStream())).ReadToEnd()
            $status = [int]$response.StatusCode
            $response.Dispose()
            return @{ Status = $status; Body = $body }
        }
    } finally {
        [System.Net.ServicePointManager]::ServerCertificateValidationCallback = $originalCertCallback
    }
}

# Extract the innermost JSON from the ABP envelope; $null when unparseable.
function Get-EnvelopeData([string]$Body) {
    try {
        $parsed = $Body | ConvertFrom-Json
    } catch { return $null }
    if ($null -ne $parsed -and $null -ne $parsed.PSObject.Properties['data']) {
        return $parsed.data
    }
    return $parsed
}

function Assert-Status {
    param([hashtable]$Response, [int[]]$Expected, [string]$Label)
    if ($Expected -notcontains $Response.Status) {
        throw "$Label expected HTTP $($Expected -join '/'), got $($Response.Status): $($Response.Body)"
    }
}

function Get-ErrorCode([string]$Body) {
    try {
        $parsed = $Body | ConvertFrom-Json
        if ($null -ne $parsed -and $null -ne $parsed.PSObject.Properties['error'] -and
            $null -ne $parsed.error -and $null -ne $parsed.error.PSObject.Properties['code']) {
            return [string]$parsed.error.code
        }
    } catch { }
    return ''
}

try {
    $sha = (& git -C $root rev-parse HEAD | Out-String).Trim()
    $branch = (& git -C $root branch --show-current | Out-String).Trim()
    Write-Step "=== P4.4 CRUD SCREENS E2E / branch=$branch / sha=$sha ==="
    Write-Step "LOG_DIRECTORY=$logDir"

    foreach ($port in $hostPort, $vitePort) {
        if (Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue) {
            throw "Port $port already has a listener; this lane uses $hostPort/$vitePort to stay clear of the owner's dev instance."
        }
    }
    if (-not (docker ps --format '{{.Names}}' | Select-String -SimpleMatch $container)) {
        throw "Container $container is not running. Start it with: docker start $container"
    }

    # --- 1. Two dedicated disposable Slice A staging databases --------------
    $identityDb = "bm_slicea_staging_identity_$stamp"
    $buildingDb = "bm_slicea_staging_building_$stamp"
    foreach ($db in $identityDb, $buildingDb) {
        & docker exec $container dropdb -U postgres --if-exists $db | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "dropdb $db failed." }
        & docker exec $container createdb -U postgres $db | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "createdb $db failed." }
    }
    Write-Step "PASS: disposable databases $identityDb / $buildingDb created on 15432."

    # --- 2. Migrate + seed the bootstrap admin ------------------------------
    $adminPassword = 'P44E2E-' + [Guid]::NewGuid().ToString('N') + '-A'
    [Environment]::SetEnvironmentVariable('ConnectionStrings__Default',
        "Host=127.0.0.1;Port=15432;Database=$identityDb;Username=postgres;Password=$pgPassword", 'Process')
    [Environment]::SetEnvironmentVariable('ConnectionStrings__BuildingManagement',
        "Host=127.0.0.1;Port=15432;Database=$buildingDb;Username=postgres;Password=$pgPassword", 'Process')
    [Environment]::SetEnvironmentVariable('NgocTrang__LocalBootstrap__AdminPassword', $adminPassword, 'Process')

    & dotnet build -c Release (Join-Path $root 'src\NgocTrangHouseManagementSystem.DbMigrator\NgocTrangHouseManagementSystem.DbMigrator.csproj') *> (Join-Path $logDir 'migrator-build.log')
    if ($LASTEXITCODE -ne 0) { throw 'DbMigrator build failed.' }

    $hostOut = Join-Path $logDir 'host-bin'
    & dotnet build -c Release (Join-Path $hostDir 'NgocTrangHouseManagementSystem.HttpApi.Host.csproj') -o $hostOut *> (Join-Path $logDir 'host-build.log')
    if ($LASTEXITCODE -ne 0) { throw 'Host build failed.' }

    & dotnet run --no-build --no-launch-profile -c Release `
        --project (Join-Path $root 'src\NgocTrangHouseManagementSystem.DbMigrator\NgocTrangHouseManagementSystem.DbMigrator.csproj') *> (Join-Path $logDir 'migrator.log')
    if ($LASTEXITCODE -ne 0) { throw "DbMigrator failed. See $logDir\migrator.log" }
    Write-Step 'PASS: DbMigrator completed the Identity database (password never echoed).'

    $bmEfProject = Join-Path $root 'modules\buildingmanagement\src\BuildingManagement.EntityFrameworkCore\BuildingManagement.EntityFrameworkCore.csproj'
    $migratorProject = Join-Path $root 'src\NgocTrangHouseManagementSystem.DbMigrator\NgocTrangHouseManagementSystem.DbMigrator.csproj'
    Push-Location (Join-Path $root 'src\NgocTrangHouseManagementSystem.DbMigrator')
    try {
        & dotnet ef database update --project $bmEfProject `
            --startup-project $migratorProject `
            --context BuildingManagementDbContext *> (Join-Path $logDir 'bm-migration.log')
    } finally { Pop-Location }
    if ($LASTEXITCODE -ne 0) { throw "BuildingManagement EF migration failed. See $logDir\bm-migration.log" }
    Write-Step 'PASS: BuildingManagement EF migrations applied to the disposable building database.'

    # --- 3. Read the seeded admin id and grant the BM permissions -----------
    $adminId = (Invoke-Db -Database $identityDb `
        -Sql 'SELECT "Id"::text FROM "AbpUsers" WHERE "UserName" = ''admin'' LIMIT 1' | Out-String).Trim()
    if (-not $adminId -or $adminId -notmatch '^[0-9a-f-]{36}$') {
        throw "Could not read the seeded admin user id (got '$adminId')."
    }

    $permissions = @(
        'BuildingManagement.Buildings.Read',
        'BuildingManagement.Buildings.Manage',
        'BuildingManagement.Buildings.BootstrapOwner',
        'BuildingManagement.Buildings.AssignOperators',
        'BuildingManagement.Buildings.RevokeOperators',
        'BuildingManagement.Floors.Read',
        'BuildingManagement.Floors.Manage',
        'BuildingManagement.Rooms.Read',
        'BuildingManagement.Rooms.Manage'
    )
    foreach ($name in $permissions) {
        $null = Invoke-Db -Database $identityDb -Sql (
            "INSERT INTO ""AbpPermissionGrants"" (""Id"", ""Name"", ""ProviderName"", ""ProviderKey"", ""TenantId"") " +
            "VALUES (gen_random_uuid()::uuid, '$name', 'U', '$adminId', NULL)")
    }
    $grantCount = (Invoke-Db -Database $identityDb `
        -Sql "SELECT count(*) FROM ""AbpPermissionGrants"" WHERE ""ProviderName""='U' AND ""ProviderKey""='$adminId'" | Out-String).Trim()
    if ($grantCount -ne '9') { throw "Expected 9 user-level permission grants, found $grantCount." }
    Write-Step 'PASS: 9 BuildingManagement permissions granted to the admin user.'

    # --- 4. Ephemeral signing certificate ------------------------------------
    $pfxPath = Join-Path $hostOut 'openiddict.pfx'
    $certPassPhrase = [Guid]::NewGuid().ToString('N')
    & dotnet dev-certs https -v -ep $pfxPath -p $certPassPhrase --trust *> $null
    if ($LASTEXITCODE -ne 0) { throw 'dotnet dev-certs failed.' }

    # --- 5. Staging Host with the Slice A opt-in -----------------------------
    [Environment]::SetEnvironmentVariable('ASPNETCORE_ENVIRONMENT', 'Staging', 'Process')
    [Environment]::SetEnvironmentVariable('DOTNET_ENVIRONMENT', 'Staging', 'Process')
    [Environment]::SetEnvironmentVariable('ASPNETCORE_URLS', "https://localhost:$hostPort", 'Process')
    [Environment]::SetEnvironmentVariable('AuthServer__CertificatePassPhrase', $certPassPhrase, 'Process')
    [Environment]::SetEnvironmentVariable('BuildingManagement__SliceAStaging__Enabled', 'true', 'Process')
    [Environment]::SetEnvironmentVariable('BuildingManagement__SliceAStaging__Activation', 'I_ACCEPT_ISOLATED_SLICE_A_STAGING_WRITES', 'Process')
    [Environment]::SetEnvironmentVariable('BuildingManagement__Bootstrap__RootUserIds__0', $adminId, 'Process')

    $hostLog = Join-Path $logDir 'host.log'
    $hostDll = Join-Path $hostOut 'NgocTrangHouseManagementSystem.HttpApi.Host.dll'
    $hostProcess = Start-Process -FilePath 'dotnet' `
        -ArgumentList @($hostDll) `
        -WorkingDirectory $hostOut -PassThru `
        -RedirectStandardOutput $hostLog -RedirectStandardError (Join-Path $logDir 'host-stderr.log')

    $ready = $false
    for ($i = 0; $i -lt 150; $i++) {
        if ($hostProcess.HasExited) { throw "Host exited early with code $($hostProcess.ExitCode). See $hostLog" }
        # Kestrel logs "Now listening" before it accepts connections, so the
        # first probes can fail to connect: that is expected, not a failure.
        try {
            $probe = Invoke-Web -Method 'GET' -Url "https://localhost:$hostPort/health-status"
            if ($probe.Status -eq 200) { $ready = $true; break }
        } catch { }
        Start-Sleep -Milliseconds 1000
    }
    if (-not $ready) { throw "Host not healthy within 150s. See $hostLog" }
    Write-Step "PASS: Host healthy on https://localhost:$hostPort/health-status."

    # --- 6. Vite proxy on its own port ---------------------------------------
    [Environment]::SetEnvironmentVariable('VITE_PROXY_TARGET', "https://localhost:$hostPort", 'Process')
    $viteLog = Join-Path $logDir 'vite.log'
    $viteProcess = Start-Process -FilePath 'npm.cmd' `
        -ArgumentList @('run','dev','--','--port',"$vitePort",'--strictPort') `
        -WorkingDirectory $frontendDir -PassThru `
        -RedirectStandardOutput $viteLog -RedirectStandardError (Join-Path $logDir 'vite-stderr.log')
    $viteReady = $false
    for ($i = 0; $i -lt 90; $i++) {
        if ($viteProcess.HasExited) { throw "Vite exited early. See $viteLog" }
        if (Get-NetTCPConnection -LocalPort $vitePort -State Listen -ErrorAction SilentlyContinue) { $viteReady = $true; break }
        Start-Sleep -Milliseconds 1000
    }
    if (-not $viteReady) { throw "Vite not listening on $vitePort within 90s. See $viteLog" }
    Write-Step "PASS: Vite dev server listening on http://localhost:$vitePort (proxy -> https://localhost:$hostPort)."

    # --- 7. Token through the proxy ------------------------------------------
    $grantForm = 'grant_type=password&client_id=NgocTrangHouseManagementSystem_App' +
        '&username=admin' +
        '&password=' + [Uri]::EscapeDataString($adminPassword) +
        '&scope=openid+profile+email+roles+NgocTrangHouseManagementSystem'
    $tokenResponse = Invoke-Web -Method 'POST' -Url "http://localhost:$vitePort/connect/token" -Form $grantForm
    if ($tokenResponse.Status -ne 200) { throw "TOKEN FAILED: HTTP $($tokenResponse.Status)" }
    $token = ($tokenResponse.Body | ConvertFrom-Json).access_token
    if (-not $token) { throw 'Grant returned no access_token.' }
    Write-Step 'PASS: password grant through the Vite proxy returned an access token (token not echoed).'

    $api = "http://localhost:$vitePort/api/building-management"

    # --- 8. CREATE building -> floor -> room --------------------------------
    $buildingBody = '{"name":"P44 CRUD Building","address":"1 Duong Kiem Thu","description":"Harness-created"}'
    $r = Invoke-Web -Method 'POST' -Url "$api/building" -Bearer $token -JsonBody $buildingBody
    Assert-Status $r @(200,201) 'POST building'
    $buildingId = (Get-EnvelopeData $r.Body).id
    if (-not $buildingId) { throw "POST building returned no id: $($r.Body)" }
    Write-Step "PASS: POST building -> $($r.Status) (id $buildingId)."

    $floorBody = ('{{"buildingId":"{0}","floorNumber":1,"name":"Tang 1","description":null}}' -f $buildingId)
    $r = Invoke-Web -Method 'POST' -Url "$api/floor" -Bearer $token -JsonBody $floorBody
    Assert-Status $r @(200,201) 'POST floor'
    $floorId = (Get-EnvelopeData $r.Body).id
    if (-not $floorId) { throw "POST floor returned no id: $($r.Body)" }
    Write-Step "PASS: POST floor -> $($r.Status) (id $floorId)."

    $roomBody = ('{{"floorId":"{0}","roomNumber":"101","name":"Phong 101","area":25,"capacity":2,"status":1,"description":null}}' -f $floorId)
    $r = Invoke-Web -Method 'POST' -Url "$api/room" -Bearer $token -JsonBody $roomBody
    Assert-Status $r @(200,201) 'POST room'
    $roomId = (Get-EnvelopeData $r.Body).id
    if (-not $roomId) { throw "POST room returned no id: $($r.Body)" }
    Write-Step "PASS: POST room -> $($r.Status) (id $roomId)."

    # --- 9. UPDATE building name ---------------------------------------------
    $updatedName = 'P44 CRUD Building (renamed)'
    $updateBody = ('{{"name":"{0}","address":"1 Duong Kiem Thu","description":"Harness-created"}}' -f $updatedName)
    $r = Invoke-Web -Method 'PUT' -Url "$api/building/$buildingId" -Bearer $token -JsonBody $updateBody
    Assert-Status $r @(200) 'PUT building'
    $r = Invoke-Web -Method 'GET' -Url "$api/building" -Bearer $token
    Assert-Status $r @(200) 'GET buildings after update'
    if ($r.Body -notlike "*$updatedName*") { throw "Building rename did not persist: $($r.Body)" }
    Write-Step 'PASS: PUT building -> 200 and GET shows the new name.'

    # --- 10. UPDATE room status ----------------------------------------------
    $roomUpdate = '{"roomNumber":"101","name":"Phong 101","area":25,"capacity":2,"status":4,"description":null}'
    $r = Invoke-Web -Method 'PUT' -Url "$api/room/$roomId" -Bearer $token -JsonBody $roomUpdate
    Assert-Status $r @(200) 'PUT room'
    $r = Invoke-Web -Method 'GET' -Url "$api/room/$roomId" -Bearer $token
    Assert-Status $r @(200) 'GET room after update'
    if ((Get-EnvelopeData $r.Body).status -ne 4) { throw "Room status did not change to 4: $($r.Body)" }
    Write-Step 'PASS: PUT room -> 200 and GET shows status 4 (Bao tri).'

    # --- 11. Guard: room number contradicting its floor is rejected ----------
    $badRoom = ('{{"floorId":"{0}","roomNumber":"901","name":"Sai tang","area":20,"capacity":2,"status":1,"description":null}}' -f $floorId)
    $r = Invoke-Web -Method 'POST' -Url "$api/room" -Bearer $token -JsonBody $badRoom
    Assert-Status $r @(400) 'POST room with a floor-mismatched number'
    $code = Get-ErrorCode $r.Body
    if ($code -notlike '*RoomNumberDoesNotMatchFloor*') {
        throw "Expected RoomNumberDoesNotMatchFloor, got '$code': $($r.Body)"
    }
    Write-Step "PASS: POST room 901 on tang 1 -> 400 $code."

    # --- 12. Guard: duplicate room number in the same floor ------------------
    $dupRoom = ('{{"floorId":"{0}","roomNumber":"101","name":"Phong trung","area":20,"capacity":2,"status":1,"description":null}}' -f $floorId)
    $r = Invoke-Web -Method 'POST' -Url "$api/room" -Bearer $token -JsonBody $dupRoom
    Assert-Status $r @(409) 'POST duplicate room number'
    $code = Get-ErrorCode $r.Body
    if ($code -notlike '*RoomNumberAlreadyExists*') {
        throw "Expected RoomNumberAlreadyExists, got '$code': $($r.Body)"
    }
    Write-Step "PASS: POST duplicate room 101 -> 409 $code."

    # --- 13. Guard: cannot delete a floor that still has rooms ---------------
    $r = Invoke-Web -Method 'DELETE' -Url "$api/floor/$floorId" -Bearer $token
    Assert-Status $r @(409) 'DELETE floor with rooms'
    $code = Get-ErrorCode $r.Body
    if ($code -notlike '*FloorHasRooms*') {
        throw "Expected FloorHasRooms, got '$code': $($r.Body)"
    }
    Write-Step "PASS: DELETE floor with a room -> 409 $code."

    # --- 14. Guard: cannot delete a building that still has floors -----------
    $r = Invoke-Web -Method 'DELETE' -Url "$api/building/$buildingId" -Bearer $token
    Assert-Status $r @(409) 'DELETE building with floors'
    $code = Get-ErrorCode $r.Body
    if ($code -notlike '*BuildingHasFloors*') {
        throw "Expected BuildingHasFloors, got '$code': $($r.Body)"
    }
    Write-Step "PASS: DELETE building with a floor -> 409 $code."

    # --- 15. Negative control: a random building id is still denied ----------
    $randomId = [Guid]::NewGuid().ToString('D')
    $r = Invoke-Web -Method 'GET' -Url "$api/building/$randomId" -Bearer $token
    if ($r.Status -eq 200) { throw 'Negative control failed: GET random building id returned 200.' }
    Write-Step "PASS: negative control - GET a random building id is still denied (HTTP $($r.Status))."

    # --- 16. DELETE back down to empty, in dependency order ------------------
    $r = Invoke-Web -Method 'DELETE' -Url "$api/room/$roomId" -Bearer $token
    Assert-Status $r @(200) 'DELETE room'
    $r = Invoke-Web -Method 'GET' -Url "$api/room/$roomId" -Bearer $token
    if ($r.Status -eq 200) {
        throw "Deleted room is still readable: $($r.Body)"
    }
    Write-Step "PASS: DELETE room -> 200 and the room is no longer readable (HTTP $($r.Status))."

    $r = Invoke-Web -Method 'DELETE' -Url "$api/floor/$floorId" -Bearer $token
    Assert-Status $r @(200) 'DELETE floor'
    Write-Step 'PASS: DELETE floor -> 200.'

    $r = Invoke-Web -Method 'DELETE' -Url "$api/building/$buildingId" -Bearer $token
    Assert-Status $r @(200) 'DELETE building'
    Write-Step 'PASS: DELETE building -> 200.'

    foreach ($resource in 'building','floor','room') {
        $r = Invoke-Web -Method 'GET' -Url "$api/$resource" -Bearer $token
        Assert-Status $r @(200) "GET $resource after cleanup"
        $data = @(Get-EnvelopeData $r.Body)
        if ($data.Count -ne 0) {
            throw "GET $resource expected an empty list after cleanup, got $($data.Count) row(s): $($r.Body)"
        }
        Write-Step "PASS: GET $resource -> 200 with 0 rows after cleanup."
    }

    $summaryPath = Join-Path $logDir 'summary.json'
    [pscustomobject]@{
        Commit         = $sha
        Branch         = $branch
        Timestamp      = (Get-Date -Format 'o')
        HostPort       = $hostPort
        VitePort       = $vitePort
        IdentityDb     = $identityDb
        BuildingDb     = $buildingDb
        KnownLimitation = 'A soft-deleted floor/room number still occupies the unfiltered unique index, so re-creating it returns 500 rather than 409. A room with contract/meter history is only flipped to Inactive on delete.'
    } | ConvertTo-Json -Depth 4 | Set-Content -Path $summaryPath -Encoding utf8

    Write-Step '=== P4.4 CRUD SCREENS E2E: ALL STEPS PASSED ==='
    Write-Step "COMMIT=$sha"
    Write-Step "SUMMARY_JSON=$summaryPath"
    Write-Step "LOG_DIRECTORY=$logDir"
    Write-Step 'Production and live application DB: NOT TOUCHED by this script.'
}
finally {
    if ($viteProcess -and -not $viteProcess.HasExited) {
        Stop-Process -Id $viteProcess.Id -Force -ErrorAction SilentlyContinue
    }
    Get-NetTCPConnection -LocalPort $vitePort -State Listen -ErrorAction SilentlyContinue |
        ForEach-Object { Stop-Process -Id $_.OwningProcess -Force -ErrorAction SilentlyContinue }
    if ($hostProcess -and -not $hostProcess.HasExited) {
        Stop-Process -Id $hostProcess.Id -Force -ErrorAction SilentlyContinue
    }
    Get-NetTCPConnection -LocalPort $hostPort -State Listen -ErrorAction SilentlyContinue |
        ForEach-Object { Stop-Process -Id $_.OwningProcess -Force -ErrorAction SilentlyContinue }
    foreach ($name in $envNames) {
        [Environment]::SetEnvironmentVariable($name, $oldEnv[$name], 'Process')
    }
    # The host process is killed above, so its own cleanup never runs. Drop
    # only the two databases this run created.
    foreach ($db in $identityDb, $buildingDb) {
        if ($db) {
            & docker exec $container dropdb -U postgres --if-exists $db 2>$null | Out-Null
            Write-Step "Dropped disposable database $db"
        }
    }
    [System.Net.ServicePointManager]::ServerCertificateValidationCallback = $originalCertCallback
}
