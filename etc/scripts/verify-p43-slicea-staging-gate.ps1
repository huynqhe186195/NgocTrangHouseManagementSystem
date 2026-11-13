[CmdletBinding()]
param()

# P4.3 SLICE-A STAGING GATE E2E - disposable local lane only.
#
# Proves the building-scope gate opens through the SERVER'S OWN opt-in
# mechanism (no backend code change): the Host starts in Staging with
# BuildingManagement:SliceAStaging enabled + the activation token + two
# dedicated bm_slicea_staging_* databases + one designated root admin.
# The root then bootstraps a Building (which atomically issues the first
# operator grant to itself), creates a Floor and a Room, and the three
# list endpoints the P4.2 screens call return 200 with the REAL data
# through the same Vite proxy the browser uses.
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
$logDir = Join-Path $logRoot "p43-slicea-staging-gate-$stamp"
New-Item -ItemType Directory -Path $logDir -Force | Out-Null
$transcript = Join-Path $logDir 'run.log'
$results = New-Object System.Collections.Generic.List[object]

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
    'VITE_PROXY_TARGET'
)
$oldEnv = @{}
foreach ($name in $envNames) { $oldEnv[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }

$hostProcess = $null
$viteProcess = $null
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
        } catch [System.Net.WebException] {
            $response = $_.Exception.Response
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

try {
    $sha = (& git -C $root rev-parse HEAD | Out-String).Trim()
    $branch = (& git -C $root branch --show-current | Out-String).Trim()
    Write-Step "=== P4.3 SLICE-A STAGING GATE E2E / branch=$branch / sha=$sha ==="
    Write-Step "LOG_DIRECTORY=$logDir"

    foreach ($port in $hostPort, $vitePort) {
        if (Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue) {
            throw "Port $port already has a listener; this lane uses $hostPort/$vitePort to stay clear of the owner's dev instance."
        }
    }

    # --- 1. Two dedicated disposable Slice A staging databases --------------
    # The host module REQUIRES both database names to start with
    # bm_slicea_staging_ and to differ from each other.
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
    $adminPassword = 'P43E2E-' + [Guid]::NewGuid().ToString('N') + '-A'
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

    # The BuildingManagement schema is a SEPARATE DbContext (its own
    # migrations): DbMigrator only migrates the Identity/Default context.
    # Apply the module's real EF migrations to the building database the
    # same way the design-time factory resolves the connection string.
    # Run from the DbMigrator directory: the factory's BuildConfiguration
    # requires appsettings.json in the CURRENT directory.
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
    # The seed uses the GuidGenerator, so the admin id differs per database.
    # Read it back, then insert user-level permission grants exactly like
    # IPermissionManager.SetForUserAsync does (ProviderName 'U').
    $adminId = (Invoke-Db -Database $identityDb `
        -Sql 'SELECT "Id"::text FROM "AbpUsers" WHERE "UserName" = ''admin'' LIMIT 1' | Out-String).Trim()
    if (-not $adminId -or $adminId -notmatch '^[0-9a-f-]{36}$') {
        throw "Could not read the seeded admin user id (got '$adminId')."
    }
    Write-Step "PASS: seeded admin id read (id not echoed in full)."

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
    Write-Step 'PASS: ephemeral OpenIddict signing certificate minted.'

    # --- 5. Staging Host with the Slice A opt-in -----------------------------
    [Environment]::SetEnvironmentVariable('ASPNETCORE_ENVIRONMENT', 'Staging', 'Process')
    [Environment]::SetEnvironmentVariable('DOTNET_ENVIRONMENT', 'Staging', 'Process')
    [Environment]::SetEnvironmentVariable('ASPNETCORE_URLS', "https://localhost:$hostPort", 'Process')
    [Environment]::SetEnvironmentVariable('AuthServer__CertificatePassPhrase', $certPassPhrase, 'Process')
    [Environment]::SetEnvironmentVariable('BuildingManagement__SliceAStaging__Enabled', 'true', 'Process')
    [Environment]::SetEnvironmentVariable('BuildingManagement__SliceAStaging__Activation', 'I_ACCEPT_ISOLATED_SLICE_A_STAGING_WRITES', 'Process')
    # A Guid[] binds from an INDEXED configuration key (the tests use
    # "BuildingManagement:Bootstrap:RootUserIds:0"), not from a scalar value:
    # a scalar binds to string and Get<Guid[]>() returns null, which the host
    # module rejects as "no designated root".
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
        $probe = Invoke-Web -Method 'GET' -Url "https://localhost:$hostPort/health-status"
        if ($probe.Status -eq 200) { $ready = $true; break }
        Start-Sleep -Milliseconds 1000
    }
    if (-not $ready) { throw "Host not healthy within 150s. See $hostLog" }
    Write-Step "PASS: Host healthy on https://localhost:$hostPort/health-status with Slice A staging adapters registered."

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
    if ($tokenResponse.Status -ne 200) {
        throw "TOKEN FAILED: HTTP $($tokenResponse.Status)"
    }
    $token = ($tokenResponse.Body | ConvertFrom-Json).access_token
    if (-not $token) { throw 'Grant returned no access_token.' }
    Write-Step 'PASS: password grant through the Vite proxy returned an access token (token not echoed).'

    # --- 8. Bootstrap a building (root) - the atomic first-owner grant ------
    $api = "http://localhost:$vitePort/api/building-management"
    $buildingBody = '{"name":"P43 Staging Lane Building","address":"1 Duong Thu Nghiem","description":"Harness-created building"}'
    $buildingResponse = Invoke-Web -Method 'POST' -Url "$api/building" -Bearer $token -JsonBody $buildingBody
    # ABP emits 201 Created for create endpoints: success = 200 or 201.
    if ($buildingResponse.Status -ne 200 -and $buildingResponse.Status -ne 201) {
        throw "POST building failed: HTTP $($buildingResponse.Status) - $($buildingResponse.Body)"
    }
    $buildingData = Get-EnvelopeData $buildingResponse.Body
    $buildingId = $buildingData.id
    if (-not $buildingId) { throw "POST building returned no id: $($buildingResponse.Body)" }
    Write-Step "PASS: building bootstrapped over HTTP (id: $buildingId) with the atomic first-owner grant."

    # --- 9. Create a floor and a room through the real endpoints -----------
    $floorBody = ('{{"buildingId":"{0}","floorNumber":1,"name":"Tang 1","description":null}}' -f $buildingId)
    $floorResponse = Invoke-Web -Method 'POST' -Url "$api/floor" -Bearer $token -JsonBody $floorBody
    if ($floorResponse.Status -ne 200 -and $floorResponse.Status -ne 201) {
        throw "POST floor failed: HTTP $($floorResponse.Status) - $($floorResponse.Body)"
    }
    $floorData = Get-EnvelopeData $floorResponse.Body
    $floorId = $floorData.id
    Write-Step "PASS: floor created over HTTP (id: $floorId)."

    $roomBody = ('{{"floorId":"{0}","roomNumber":"101","name":"Phong 101","area":25,"capacity":2,"status":1,"description":null}}' -f $floorId)
    $roomResponse = Invoke-Web -Method 'POST' -Url "$api/room" -Bearer $token -JsonBody $roomBody
    if ($roomResponse.Status -ne 200 -and $roomResponse.Status -ne 201) {
        throw "POST room failed: HTTP $($roomResponse.Status) - $($roomResponse.Body)"
    }
    Write-Step 'PASS: room created over HTTP.'

    # --- 10. The three list endpoints now return 200 with REAL data ---------
    foreach ($resource in 'building','floor','room') {
        $response = Invoke-Web -Method 'GET' -Url "$api/$resource" -Bearer $token
        $body = $response.Body

        if ($response.Status -ne 200) {
            throw "GET $resource expected 200 with data, got HTTP $($response.Status): $body"
        }
        $data = @(Get-EnvelopeData $body)
        # PS 5.1 ConvertFrom-Json unwraps single-element arrays into a bare
        # object; @() re-wraps it so .Count and indexing behave like arrays.
        if ($data.Count -lt 1) {
            throw "GET $resource returned 200 but no array: $body"
        }

        $marker = if ($resource -eq 'building') { 'P43 Staging Lane Building' }
        elseif ($resource -eq 'floor') { 'Tang 1' }
        else { 'Phong 101' }
        $found = ($body -like "*$marker*")
        if (-not $found) {
            throw "GET $resource returned 200 but the created row ('$marker') is missing: $body"
        }

        $count = $data.Count
        Write-Step ("PASS: GET {0} -> HTTP 200, {1} row(s), includes the created '{2}'." -f $resource, $count, $marker)
        $results.Add([pscustomobject]@{
            Resource = $resource
            Status   = $response.Status
            Rows     = $count
            Created  = $marker
        })
    }

    # --- 11. Negative control: an ungranted random building id is denied ---
    $randomId = [Guid]::NewGuid().ToString('D')
    $negResponse = Invoke-Web -Method 'GET' -Url "$api/building/$randomId" -Bearer $token
    if ($negResponse.Status -eq 200) {
        throw "Negative control failed: GET random building id unexpectedly returned 200."
    }
    Write-Step "PASS: negative control - GET a random building id is still denied (HTTP $($negResponse.Status))."

    $summaryPath = Join-Path $logDir 'summary.json'
    $payload = [pscustomobject]@{
        Commit      = $sha
        Branch      = $branch
        Timestamp   = (Get-Date -Format 'o')
        HostPort    = $hostPort
        VitePort    = $vitePort
        IdentityDb  = $identityDb
        BuildingDb  = $buildingDb
        BuildingId  = $buildingId
        Endpoints   = $results
    }
    $payload | ConvertTo-Json -Depth 6 | Set-Content -Path $summaryPath -Encoding utf8

    Write-Step '=== P4.3 SLICE-A STAGING GATE E2E: ALL STEPS PASSED ==='
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
    foreach ($name in ($envNames + @(
        'BuildingManagement__SliceAStaging__Enabled',
        'BuildingManagement__SliceAStaging__Activation',
        'BuildingManagement__Bootstrap__RootUserIds__0'))) {
        $previous = if ($oldEnv.ContainsKey($name)) { $oldEnv[$name] } else { $null }
        [Environment]::SetEnvironmentVariable($name, $previous, 'Process')
    }
    [System.Net.ServicePointManager]::ServerCertificateValidationCallback = $originalCertCallback
}
