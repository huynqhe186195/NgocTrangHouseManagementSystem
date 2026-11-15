[CmdletBinding()]
param()

# P4.5 MOCKUP SCREENS (PHASE 1) E2E - disposable local staging lane only.
#
# Phase 1 extends the backend so the five Figma screens (Docs/front-end/
# building_rooms/) have real data behind them: rent, equipment, photos,
# room geometry and floor-plan objects. This harness proves that surface
# end-to-end through the Vite proxy, exactly as the browser will use it:
#
#   PUT  rent            -> GET shows the saved amount
#   POST 2 equipment     -> GET shows both
#   PUT  1 equipment     -> GET shows the change
#   DELETE 1 equipment   -> DB IsDeleted = t, GET no longer lists it
#   POST a real 1x1 PNG  -> GET .../photo-content returns the SAME bytes
#   POST text pretending to be .png  -> REJECTED, proving the magic-byte check
#   PUT  room layout     -> GET shows the geometry
#   POST a floor layout item -> GET shows it
#   negative control: a random building id is still denied
#   cleanup back to empty, verifying IsDeleted in PostgreSQL (not just HTTP)
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
$logDir = Join-Path $logRoot "p45-mockup-screens-$stamp"
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
    Write-Step "=== P4.5 MOCKUP SCREENS (PHASE 1) E2E / branch=$branch / sha=$sha ==="
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
    $adminPassword = 'P45E2E-' + [Guid]::NewGuid().ToString('N') + '-A'
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
    Write-Step 'PASS: BuildingManagement EF migrations (including P45) applied to the disposable building database.'

    # Prove the P45 migration actually landed instead of trusting exit code 0.
    $p45Tables = (Invoke-Db -Database $buildingDb -Sql (
        "SELECT string_agg(table_name, ',' ORDER BY table_name) FROM information_schema.tables " +
        "WHERE table_schema='public' AND table_name IN " +
        "('BuildingManagementRoomEquipment','BuildingManagementRoomPhotos'," +
        "'BuildingManagementRoomPhotoContents','BuildingManagementFloorLayoutItems')") | Out-String).Trim()
    if ($p45Tables -ne 'BuildingManagementFloorLayoutItems,BuildingManagementRoomEquipment,BuildingManagementRoomPhotoContents,BuildingManagementRoomPhotos') {
        throw "P45 tables missing from the migrated schema (found '$p45Tables')."
    }
    $p45Columns = (Invoke-Db -Database $buildingDb -Sql (
        "SELECT count(*) FROM information_schema.columns WHERE table_name='BuildingManagementRooms' " +
        "AND column_name IN ('MonthlyRent','LayoutX','LayoutY','LayoutWidth','LayoutHeight'," +
        "'LayoutShape','LayoutRotationDegrees','LayoutPoints')") | Out-String).Trim()
    if ($p45Columns -ne '8') { throw "Expected 8 P45 columns on BuildingManagementRooms, found $p45Columns." }
    Write-Step 'PASS: P45 schema verified (4 tables + 8 Room columns) in PostgreSQL.'

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

    # --- 8. Fixtures: building -> floor -> 2 rooms ---------------------------
    $r = Invoke-Web -Method 'POST' -Url "$api/building" -Bearer $token `
        -JsonBody '{"name":"P45 Mockup Building","address":"2 Kiem Thu","description":"Harness-created"}'
    Assert-Status $r @(200,201) 'POST building'
    $buildingId = (Get-EnvelopeData $r.Body).id
    if (-not $buildingId) { throw "POST building returned no id: $($r.Body)" }
    Write-Step "PASS: POST building -> $($r.Status) (id $buildingId)."

    $r = Invoke-Web -Method 'POST' -Url "$api/floor" -Bearer $token `
        -JsonBody ('{{"buildingId":"{0}","floorNumber":1,"name":"Tang 1","description":null}}' -f $buildingId)
    Assert-Status $r @(200,201) 'POST floor'
    $floorId = (Get-EnvelopeData $r.Body).id
    if (-not $floorId) { throw "POST floor returned no id: $($r.Body)" }
    Write-Step "PASS: POST floor -> $($r.Status) (id $floorId)."

    $roomIds = @()
    foreach ($number in '101','102') {
        $r = Invoke-Web -Method 'POST' -Url "$api/room" -Bearer $token `
            -JsonBody ('{{"floorId":"{0}","roomNumber":"{1}","name":"Phong {1}","area":25,"capacity":2,"status":1,"description":null}}' -f $floorId, $number)
        Assert-Status $r @(200,201) "POST room $number"
        $id = (Get-EnvelopeData $r.Body).id
        if (-not $id) { throw "POST room $number returned no id: $($r.Body)" }
        $roomIds += $id
    }
    $roomId = $roomIds[0]
    Write-Step "PASS: POST 2 rooms -> $($roomIds -join ', ')."

    # --- 9. Rent round-trip ---------------------------------------------------
    $r = Invoke-Web -Method 'PUT' -Url "$api/room/$roomId/rent" -Bearer $token `
        -JsonBody '{"monthlyRent":3500000}'
    Assert-Status $r @(200) 'PUT room rent'
    $r = Invoke-Web -Method 'GET' -Url "$api/room/$roomId" -Bearer $token
    Assert-Status $r @(200) 'GET room after rent'
    $rent = (Get-EnvelopeData $r.Body).monthlyRent
    if ($rent -ne 3500000) { throw "Room rent did not round-trip: got '$rent', expected 3500000." }
    $dbRent = (Invoke-Db $buildingDb "SELECT ""MonthlyRent""::text FROM ""BuildingManagementRooms"" WHERE ""Id""='$roomId'" | Out-String).Trim()
    if ($dbRent -ne '3500000') { throw "MonthlyRent in PostgreSQL is '$dbRent', expected 3500000." }
    Write-Step "PASS: rent round-trip 3500000 (API and DB agree)."

    # --- 10. Equipment: add 2, update 1, delete 1 ----------------------------
    $equipmentIds = @()
    $equipmentBodies = @(
        '{"name":"Dieu hoa Daikin","category":"Lanh","quantity":1,"condition":1,"installedDate":"2025-06-01","notes":null}',
        '{"name":"Tu lanh Samsung","category":"Dien","quantity":2,"condition":2,"installedDate":null,"notes":"Can sua"}'
    )
    foreach ($body in $equipmentBodies) {
        $r = Invoke-Web -Method 'POST' -Url "$api/room/$roomId/equipment" -Bearer $token -JsonBody $body
        Assert-Status $r @(200,201) 'POST equipment'
        $id = (Get-EnvelopeData $r.Body).id
        if (-not $id) { throw "POST equipment returned no id: $($r.Body)" }
        $equipmentIds += $id
    }
    $r = Invoke-Web -Method 'GET' -Url "$api/room/$roomId/equipment" -Bearer $token
    Assert-Status $r @(200) 'GET equipment'
    $equipmentList = @(Get-EnvelopeData $r.Body)
    if ($equipmentList.Count -ne 2) { throw "Expected 2 equipment rows, got $($equipmentList.Count)." }
    Write-Step 'PASS: 2 equipment rows created and listed.'

    $r = Invoke-Web -Method 'PUT' -Url "$api/room/$roomId/equipment/$($equipmentIds[1])" -Bearer $token `
        -JsonBody '{"name":"Tu lanh Samsung","category":"Dien","quantity":3,"condition":1,"installedDate":null,"notes":"Da sua xong"}'
    Assert-Status $r @(200) 'PUT equipment'
    $r = Invoke-Web -Method 'GET' -Url "$api/room/$roomId/equipment" -Bearer $token
    Assert-Status $r @(200) 'GET equipment after update'
    $updated = @(Get-EnvelopeData $r.Body) | Where-Object { $_.id -eq $equipmentIds[1] }
    if ($updated.quantity -ne 3 -or [int]$updated.condition -ne 1) {
        throw "Equipment update did not stick: quantity=$($updated.quantity) condition=$($updated.condition)."
    }
    Write-Step 'PASS: equipment update -> quantity 3, condition 1.'

    $r = Invoke-Web -Method 'DELETE' -Url "$api/room/$roomId/equipment/$($equipmentIds[0])" -Bearer $token
    Assert-Status $r @(200) 'DELETE equipment'
    $dbDeleted = (Invoke-Db $buildingDb "SELECT ""IsDeleted""::text FROM ""BuildingManagementRoomEquipment"" WHERE ""Id""='$($equipmentIds[0])'" | Out-String).Trim()
    # psql renders an uncast boolean as 't'; an explicit ::text cast yields
    # 'true'. Accept either spelling so the assertion tests the flag, not psql.
    if ($dbDeleted -notin @('t', 'true')) { throw "Equipment IsDeleted in PostgreSQL is '$dbDeleted', expected true." }
    $r = Invoke-Web -Method 'GET' -Url "$api/room/$roomId/equipment" -Bearer $token
    Assert-Status $r @(200) 'GET equipment after delete'
    $remaining = @(Get-EnvelopeData $r.Body)
    if ($remaining.Count -ne 1) { throw "Expected 1 equipment row after delete, got $($remaining.Count)." }
    Write-Step 'PASS: DELETE equipment -> DB IsDeleted=t and only 1 row remains.'

    # --- 11. Photo: upload a real PNG, fetch it back byte-for-byte -----------
    # A genuine 1x1 PNG, built in-process so no binary asset enters the repo.
    $pngBase64 = 'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg=='
    $pngBytes = [Convert]::FromBase64String($pngBase64)
    if ($pngBytes.Length -lt 8 -or $pngBytes[1] -ne 0x50) { throw 'The in-process PNG fixture is not a PNG.' }

    $r = Invoke-Web -Method 'POST' -Url "$api/room/$roomId/upload-photo" -Bearer $token `
        -JsonBody ('{{"base64Content":"{0}","fileName":"../../etc/evil.png"}}' -f $pngBase64)
    Assert-Status $r @(200,201) 'POST photo'
    $photo = Get-EnvelopeData $r.Body
    $photoId = $photo.id
    if (-not $photoId) { throw "POST photo returned no id: $($r.Body)" }
    if ($photo.contentType -ne 'image/png') { throw "Stored content type is '$($photo.contentType)', expected image/png." }
    # The client sent a traversal-shaped name; the server must have rewritten it.
    if ($photo.fileName -match '[\\/]' -or $photo.fileName -ne 'evil.png') {
        throw "FileName was not sanitised (got '$($photo.fileName)')."
    }
    if ([long]$photo.sizeBytes -ne $pngBytes.LongLength) {
        throw "Stored SizeBytes is $($photo.sizeBytes), expected $($pngBytes.LongLength)."
    }
    Write-Step "PASS: POST photo -> $($r.Status); path in FileName stripped, type sniffed as image/png."

    $r = Invoke-Web -Method 'GET' -Url "$api/room/$roomId/photo-content/$photoId" -Bearer $token
    Assert-Status $r @(200) 'GET photo content'
    $file = Get-EnvelopeData $r.Body
    if ($file.contentType -ne 'image/png') { throw "Served content type is '$($file.contentType)'." }
    if ($file.content -ne $pngBase64) {
        throw 'The downloaded bytes differ from the uploaded bytes.'
    }
    Write-Step 'PASS: photo content round-trip is byte-for-byte identical.'

    $r = Invoke-Web -Method 'GET' -Url "$api/room/$roomId/photos" -Bearer $token
    Assert-Status $r @(200) 'GET photos'
    $photos = @(Get-EnvelopeData $r.Body)
    if ($photos.Count -ne 1) { throw "Expected 1 photo, got $($photos.Count)." }

    # --- 12. Magic-byte rejection: text disguised as a .png ------------------
    $fakeBase64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes('this is definitely not a png'))
    $r = Invoke-Web -Method 'POST' -Url "$api/room/$roomId/upload-photo" -Bearer $token `
        -JsonBody ('{{"base64Content":"{0}","fileName":"fake.png"}}' -f $fakeBase64)
    if ($r.Status -ne 400) {
        throw "A text payload named fake.png was expected to be rejected with 400, got $($r.Status): $($r.Body)"
    }
    $errorCode = Get-ErrorCode $r.Body
    if ($errorCode -notmatch 'RoomPhoto') {
        throw "Rejection did not carry a photo error code (got '$errorCode'): $($r.Body)"
    }
    $photoCount = (Invoke-Db $buildingDb "SELECT count(*)::text FROM ""BuildingManagementRoomPhotos"" WHERE ""RoomId""='$roomId'" | Out-String).Trim()
    if ($photoCount -ne '1') { throw "A rejected upload still wrote a row (count=$photoCount)." }
    Write-Step "PASS: text payload named fake.png rejected with 400 ($errorCode) and no row written."

    # --- 13. Room layout round-trip ------------------------------------------
    $r = Invoke-Web -Method 'PUT' -Url "$api/room/$roomId/layout" -Bearer $token `
        -JsonBody '{"x":2.5,"y":3,"width":6,"height":4.5,"shape":1,"rotationDegrees":0,"points":null}'
    Assert-Status $r @(200) 'PUT room layout'
    $r = Invoke-Web -Method 'GET' -Url "$api/room/$roomId" -Bearer $token
    Assert-Status $r @(200) 'GET room after layout'
    $room = Get-EnvelopeData $r.Body
    if ([decimal]$room.layoutX -ne 2.5 -or [decimal]$room.layoutY -ne 3 -or
        [decimal]$room.layoutWidth -ne 6 -or [decimal]$room.layoutHeight -ne 4.5) {
        throw "Room layout did not round-trip: x=$($room.layoutX) y=$($room.layoutY) w=$($room.layoutWidth) h=$($room.layoutHeight)."
    }
    if ([int]$room.equipmentCount -ne 1 -or [int]$room.photoCount -ne 1) {
        throw "Room counts are wrong: equipment=$($room.equipmentCount) photo=$($room.photoCount)."
    }
    Write-Step 'PASS: room layout round-trip and EquipmentCount/PhotoCount are accurate.'

    # --- 14. Floor layout item ----------------------------------------------
    $r = Invoke-Web -Method 'POST' -Url "$api/floor/$floorId/layout-item" -Bearer $token `
        -JsonBody '{"kind":2,"label":"Cau thang bo","x":1,"y":1,"width":2.5,"height":4}'
    Assert-Status $r @(200,201) 'POST floor layout item'
    $layoutItemId = (Get-EnvelopeData $r.Body).id
    if (-not $layoutItemId) { throw "POST layout item returned no id: $($r.Body)" }

    $r = Invoke-Web -Method 'GET' -Url "$api/floor/$floorId/layout-items" -Bearer $token
    Assert-Status $r @(200) 'GET floor layout items'
    $items = @(Get-EnvelopeData $r.Body)
    if ($items.Count -ne 1 -or $items[0].id -ne $layoutItemId) {
        throw "Expected exactly the created layout item, got $($r.Body)"
    }
    Write-Step 'PASS: floor layout item create + list round-trip.'

    # --- 14b. The widened kinds (8..11) actually persist -------------------
    # CK_BM_FloorLayoutItem_Kind used to pin Kind to 1..7, so the four kinds
    # mockup 3 draws but P4.5 Phase 1 omitted would be rejected by PostgreSQL
    # even though the C# enum accepted them. Proving one of them round-trips
    # is what shows the companion migration moved the constraint.
    $r = Invoke-Web -Method 'POST' -Url "$api/floor/$floorId/layout-item" -Bearer $token `
        -JsonBody '{"kind":9,"label":"Kho sau","x":2,"y":2,"width":1.5,"height":1.5}'
    Assert-Status $r @(200,201) 'POST floor layout item of the new kind 9 (Storage)'
    $storageItemId = (Get-EnvelopeData $r.Body).id
    if (-not $storageItemId) { throw "POST kind-9 item returned no id: $($r.Body)" }

    $kindInDb = (Invoke-Db $buildingDb `
        "SELECT ""Kind""::text FROM ""BuildingManagementFloorLayoutItems"" WHERE ""Id"" = '$storageItemId'" | Out-String).Trim()
    if ($kindInDb -ne '9') {
        throw "Kind 9 was not stored (row Kind='$kindInDb'). Did the widen migration run?"
    }
    Write-Step 'PASS: layout item kind 9 (Storage) accepted and stored - the widened CHECK is in force.'

    # --- 15. Negative control: an unknown building id is denied -------------
    $randomId = [Guid]::NewGuid().ToString()
    $r = Invoke-Web -Method 'GET' -Url "$api/building/$randomId" -Bearer $token
    if ($r.Status -in @(200, 201)) {
        throw "Negative control FAILED: a random building id was readable ($($r.Status))."
    }
    Write-Step "PASS: negative control - random building id denied (HTTP $($r.Status))."

    # --- 16. Cleanup, verifying PostgreSQL rather than the HTTP status ------
    # A delete can answer 200 while the row survives. Record the DB truth.
    function Get-DeletedFlag([string]$Table, [string]$Id) {
        $sql = "SELECT ""IsDeleted"" FROM ""$Table"" WHERE ""Id"" = '$Id'"
        $value = Invoke-Db $buildingDb $sql
        # psql -t -A prints nothing for zero rows, which PowerShell surfaces
        # as $null or an empty string depending on the call path.
        if ($null -eq $value -or ([string]$value).Trim() -eq '') { return 'gone' }
        return ([string]$value).Trim()
    }

    foreach ($id in @($equipmentIds[0])) {
        $flag = Get-DeletedFlag 'BuildingManagementRoomEquipment' $id
        if ($flag -ne 't') { throw "Equipment $id IsDeleted='$flag', expected 't'." }
    }

    $r = Invoke-Web -Method 'DELETE' -Url "$api/room/$roomId/photo/$photoId" -Bearer $token
    Assert-Status $r @(200) 'DELETE photo'
    $photoFlag = Get-DeletedFlag 'BuildingManagementRoomPhotos' $photoId
    if ($photoFlag -ne 't') { throw "Photo IsDeleted='$photoFlag', expected 't'." }
    $contentLeft = (Invoke-Db $buildingDb "SELECT count(*)::text FROM ""BuildingManagementRoomPhotoContents"" WHERE ""PhotoId""='$photoId'" | Out-String).Trim()
    if ($contentLeft -ne '0') { throw "Photo bytes were not removed (rows=$contentLeft)." }
    Write-Step 'PASS: DELETE photo -> photo row soft-deleted and the bytes table row is gone.'

    $r = Invoke-Web -Method 'DELETE' -Url "$api/floor/$floorId/layout-item/$layoutItemId" -Bearer $token
    Assert-Status $r @(200) 'DELETE floor layout item'
    $itemFlag = Get-DeletedFlag 'BuildingManagementFloorLayoutItems' $layoutItemId
    if ($itemFlag -notin @('t', 'gone')) { throw "Layout item IsDeleted='$itemFlag', expected 't' or gone." }

    $r = Invoke-Web -Method 'DELETE' -Url "$api/floor/$floorId/layout-item/$storageItemId" -Bearer $token
    Assert-Status $r @(200) 'DELETE the kind-9 layout item'
    $storageFlag = Get-DeletedFlag 'BuildingManagementFloorLayoutItems' $storageItemId
    if ($storageFlag -notin @('t', 'gone')) { throw "Kind-9 item IsDeleted='$storageFlag', expected 't' or gone." }
    Write-Step 'PASS: DELETE floor layout item (including the kind-9 item).'

    # The remaining equipment row must go too, or the room delete would leave
    # a live child behind.
    $r = Invoke-Web -Method 'DELETE' -Url "$api/room/$roomId/equipment/$($equipmentIds[1])" -Bearer $token
    Assert-Status $r @(200) 'DELETE remaining equipment'

    foreach ($id in $roomIds) {
        $r = Invoke-Web -Method 'DELETE' -Url "$api/room/$id" -Bearer $token
        Assert-Status $r @(200) "DELETE room $id"
        $flag = Get-DeletedFlag 'BuildingManagementRooms' $id
        if ($flag -notin @('t', 'gone')) { throw "Room $id IsDeleted='$flag' after DELETE." }
    }
    Write-Step 'PASS: DELETE rooms -> every room is soft-deleted in PostgreSQL.'

    $r = Invoke-Web -Method 'DELETE' -Url "$api/floor/$floorId" -Bearer $token
    Assert-Status $r @(200) 'DELETE floor'
    $floorFlag = Get-DeletedFlag 'BuildingManagementFloors' $floorId
    if ($floorFlag -notin @('t', 'gone')) { throw "Floor IsDeleted='$floorFlag' after DELETE." }

    $r = Invoke-Web -Method 'DELETE' -Url "$api/building/$buildingId" -Bearer $token
    Assert-Status $r @(200) 'DELETE building'
    $buildingFlag = Get-DeletedFlag 'Buildings' $buildingId
    if ($buildingFlag -notin @('t', 'gone')) { throw "Building IsDeleted='$buildingFlag' after DELETE." }
    Write-Step 'PASS: DELETE floor + building -> soft-deleted in PostgreSQL.'

    foreach ($resource in 'building','floor','room') {
        $r = Invoke-Web -Method 'GET' -Url "$api/$resource" -Bearer $token
        # Deleting the last building also removes the actor's only grant, and
        # the deny-all policy answers such a read with its scope denial rather
        # than an empty list. Both outcomes prove the rows are gone; only a
        # 200 that still carries rows would be a failure.
        if ($r.Status -eq 200) {
            $data = @(Get-EnvelopeData $r.Body)
            if ($data.Count -ne 0) {
                throw "GET $resource expected no rows after cleanup, got $($data.Count): $($r.Body)"
            }
            Write-Step "PASS: GET $resource -> 200 with 0 rows after cleanup."
        } elseif ($r.Status -in @(302, 403)) {
            Write-Step "PASS: GET $resource -> $($r.Status) (no grant left after the last delete; scope denial)."
        } else {
            throw "GET $resource after cleanup expected 200 or a scope denial, got $($r.Status): $($r.Body)"
        }
    }

    $summaryPath = Join-Path $logDir 'summary.json'
    [pscustomobject]@{
        Commit          = $sha
        Branch          = $branch
        Phase           = 'P4.5 Phase 1 (backend data layer)'
        Timestamp       = (Get-Date -Format 'o')
        HostPort        = $hostPort
        VitePort        = $vitePort
        IdentityDb      = $identityDb
        BuildingDb      = $buildingDb
        KnownLimitation = 'RoomPhotoFileDto returns the image bytes base64-encoded inside the JSON envelope; a real binary stream endpoint is deferred to the frontend phase.'
    } | ConvertTo-Json -Depth 4 | Set-Content -Path $summaryPath -Encoding utf8

    Write-Step '=== P4.5 MOCKUP SCREENS PHASE 1 E2E: ALL STEPS PASSED ==='
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
