[CmdletBinding()]
param(
    [string]$AdminPassword,
    [string]$RunId
)

# P60 disposable roster-only lane. It deliberately never enables the legacy
# SliceAStaging write profile. The password is process-scoped and is cleared
# in finally; disposable databases are dropped only by this run.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$hostProject = Join-Path $root 'src\NgocTrangHouseManagementSystem.HttpApi.Host\NgocTrangHouseManagementSystem.HttpApi.Host.csproj'
$migratorProject = Join-Path $root 'src\NgocTrangHouseManagementSystem.DbMigrator\NgocTrangHouseManagementSystem.DbMigrator.csproj'
$migratorDir = Join-Path $root 'src\NgocTrangHouseManagementSystem.DbMigrator'
$bmEfProject = Join-Path $root 'modules\buildingmanagement\src\BuildingManagement.EntityFrameworkCore\BuildingManagement.EntityFrameworkCore.csproj'
$frontendDir = Join-Path $root 'frontend'
$container = 'bm-p21-pg-15432'
$pgPassword = [Environment]::GetEnvironmentVariable('BM_DISPOSABLE_PG_PASSWORD', 'Process')
if ([string]::IsNullOrWhiteSpace($pgPassword)) { throw 'BM_DISPOSABLE_PG_PASSWORD must be supplied in process memory.' }
$hostPort = 44321
$vitePort = 5273
if (-not $RunId) { $RunId = [Guid]::NewGuid().ToString('N') }
if ($RunId -notmatch '^[0-9a-f]{12}$') { throw 'RunId must be exactly 12 lowercase hexadecimal characters.' }
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$logDir = Join-Path (Join-Path $env:USERPROFILE 'NgocTrang-Test-Logs') "p60-roster-lane-$stamp-$RunId"
New-Item -ItemType Directory -Path $logDir -Force | Out-Null

function Say([string]$Message) {
    $line = '{0}  {1}' -f (Get-Date -Format 'HH:mm:ss'), $Message
    Write-Host $line
    Add-Content -Path (Join-Path $logDir 'lane.log') -Value $line -Encoding utf8
}
function Invoke-Db([string]$Database, [string]$Sql) {
    $out = $Sql | & docker exec -i $container psql -U postgres -d $Database -v ON_ERROR_STOP=1 -t -A
    if ($LASTEXITCODE -ne 0) { throw "psql failed on $Database" }
    return @($out)
}
function Quote-Sql([string]$Value) { return "'" + $Value.Replace("'", "''") + "'" }

$envNames = @(
    'ASPNETCORE_ENVIRONMENT','DOTNET_ENVIRONMENT','ASPNETCORE_URLS',
    'ConnectionStrings__Default','ConnectionStrings__BuildingManagement',
    'AuthServer__CertificatePassPhrase','AuthServer__Authority','App__SelfUrl',
    'NgocTrang__LocalBootstrap__AdminPassword','VITE_PROXY_TARGET',
    'BuildingManagement__SliceARosterStaging__Enabled',
    'BuildingManagement__SliceARosterStaging__Activation',
    'BuildingManagement__SliceAStaging__Enabled',
    'BuildingManagement__SliceAStaging__Activation',
    'BuildingManagement__OwnerRecovery__Enabled'
)
$oldEnv = @{}
foreach ($name in $envNames) { $oldEnv[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
$hostProcess = $null; $viteProcess = $null; $identityDb = $null; $buildingDb = $null

try {
    foreach ($port in $hostPort, $vitePort) {
        if (Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue) {
            throw "Port $port is already in use."
        }
    }
    if (-not (docker ps --format '{{.Names}}' | Select-String -SimpleMatch $container)) {
        throw "Container $container is not running."
    }
    if (-not $AdminPassword) { $AdminPassword = 'RosterLane-' + [Guid]::NewGuid().ToString('N').Substring(0, 12) + '-9x' }
    if ($AdminPassword.Length -lt 16) { throw 'The disposable admin password must be at least 16 characters.' }

    $identityDb = "bm_slicea_staging_identity_$RunId"
    $buildingDb = "bm_slicea_staging_building_$RunId"
    foreach ($db in $identityDb, $buildingDb) {
        & docker exec $container createdb -U postgres $db
        if ($LASTEXITCODE -ne 0) { throw "createdb $db failed" }
    }
    Say "Created roster-only databases $identityDb / $buildingDb on 15432."

    [Environment]::SetEnvironmentVariable('ConnectionStrings__Default', "Host=127.0.0.1;Port=15432;Database=$identityDb;Username=postgres;Password=$pgPassword", 'Process')
    [Environment]::SetEnvironmentVariable('ConnectionStrings__BuildingManagement', "Host=127.0.0.1;Port=15432;Database=$buildingDb;Username=postgres;Password=$pgPassword", 'Process')
    [Environment]::SetEnvironmentVariable('NgocTrang__LocalBootstrap__AdminPassword', $AdminPassword, 'Process')

    & dotnet build -c Release $migratorProject *> (Join-Path $logDir 'migrator-build.log')
    if ($LASTEXITCODE -ne 0) { throw "Migrator build failed. See $logDir\migrator-build.log" }
    & dotnet run --no-build --no-launch-profile -c Release --project $migratorProject *> (Join-Path $logDir 'migrator.log')
    if ($LASTEXITCODE -ne 0) { throw "Identity migration failed. See $logDir\migrator.log" }
    Push-Location $migratorDir
    try {
        & dotnet ef database update --project $bmEfProject --startup-project $migratorProject --context BuildingManagementDbContext *> (Join-Path $logDir 'bm-migration.log')
    } finally { Pop-Location }
    if ($LASTEXITCODE -ne 0) { throw "BuildingManagement migration failed. See $logDir\bm-migration.log" }

    $adminId = ((Invoke-Db $identityDb 'SELECT "Id"::text FROM "AbpUsers" WHERE "UserName" = ''admin'' LIMIT 1') | Out-String).Trim()
    if (-not $adminId -or $adminId -notmatch '^[0-9a-f-]{36}$') { throw 'Could not read seeded admin id.' }
    $permissionSql = 'INSERT INTO "AbpPermissionGrants" ("Id", "Name", "ProviderName", "ProviderKey", "TenantId") VALUES (gen_random_uuid()::uuid, ''BuildingManagement.Buildings.ReadOperators'', ''U'', ''' + $adminId + ''', NULL);'
    Invoke-Db $identityDb $permissionSql | Out-Null

    $buildingA = [Guid]::NewGuid().ToString('D'); $buildingB = [Guid]::NewGuid().ToString('D')
    $operatorA = [Guid]::NewGuid().ToString('D'); $operatorB = [Guid]::NewGuid().ToString('D'); $operatorC = [Guid]::NewGuid().ToString('D')
    $grantAdminA = [Guid]::NewGuid().ToString('D'); $grantAdminB = [Guid]::NewGuid().ToString('D')
    $now = [DateTime]::UtcNow
    $created = $now.ToString('yyyy-MM-dd HH:mm:ss.ffffff+00')
    $past = $now.AddHours(-3).ToString('yyyy-MM-dd HH:mm:ss.ffffff+00')
    $expiredAt = $now.AddHours(-2).ToString('yyyy-MM-dd HH:mm:ss.ffffff+00')
    $future = $now.AddHours(1).ToString('yyyy-MM-dd HH:mm:ss.ffffff+00')
    $revokedAt = $now.AddHours(-1).ToString('yyyy-MM-dd HH:mm:ss.ffffff+00')
    $seedSql = @"
INSERT INTO `"Buildings`" (`"Id`",`"Name`",`"Address`",`"Description`",`"ExtraProperties`",`"ConcurrencyStamp`",`"CreationTime`",`"IsDeleted`") VALUES
  ('$buildingA'::uuid, U&'T\00F2a nh\00E0 A', '12 Nguy\1EC5n Tr\00E3i', 'Roster capture A', '{}', gen_random_uuid()::text, now()::timestamp, false),
  ('$buildingB'::uuid, U&'T\00F2a nh\00E0 B', '45 Tr\00E2n H\01B0ng \0110\1EA1o', 'Roster capture B', '{}', gen_random_uuid()::text, now()::timestamp, false);
INSERT INTO `"BuildingManagementOperatorBuildingGrants`" (`"Id`",`"OperatorUserId`",`"BuildingId`",`"GrantedByUserId`",`"GrantedAtUtc`",`"ExpiresAtUtc`",`"RevokedAtUtc`",`"RevokedByUserId`",`"RevisionNumber`",`"ExtraProperties`",`"ConcurrencyStamp`") VALUES
  ('$grantAdminA'::uuid, '$adminId'::uuid, '$buildingA'::uuid, '$adminId'::uuid, '$created'::timestamptz, NULL, NULL, NULL, 1, '{}', gen_random_uuid()::text),
  ('$grantAdminB'::uuid, '$adminId'::uuid, '$buildingB'::uuid, '$adminId'::uuid, '$created'::timestamptz, NULL, NULL, NULL, 1, '{}', gen_random_uuid()::text),
  (gen_random_uuid(), '$operatorA'::uuid, '$buildingA'::uuid, '$adminId'::uuid, '$created'::timestamptz, NULL, NULL, NULL, 1, '{}', gen_random_uuid()::text),
  (gen_random_uuid(), '$operatorB'::uuid, '$buildingB'::uuid, '$adminId'::uuid, '$created'::timestamptz, NULL, NULL, NULL, 1, '{}', gen_random_uuid()::text),
  (gen_random_uuid(), '$operatorB'::uuid, '$buildingA'::uuid, '$adminId'::uuid, '$past'::timestamptz, '$expiredAt'::timestamptz, NULL, NULL, 1, '{}', gen_random_uuid()::text),
  (gen_random_uuid(), '$operatorC'::uuid, '$buildingB'::uuid, '$adminId'::uuid, '$past'::timestamptz, NULL, '$revokedAt'::timestamptz, '$adminId'::uuid, 2, '{}', gen_random_uuid()::text),
  (gen_random_uuid(), '$operatorC'::uuid, '$buildingA'::uuid, '$adminId'::uuid, '$future'::timestamptz, NULL, NULL, NULL, 1, '{}', gen_random_uuid()::text);
"@
    Invoke-Db $buildingDb $seedSql | Out-Null
    Say 'Seeded persisted ReadOperators permission, live issuer scope, and membership history.'

    $hostOut = Join-Path $logDir 'host-bin'
    & dotnet build -c Release $hostProject -o $hostOut *> (Join-Path $logDir 'host-build.log')
    if ($LASTEXITCODE -ne 0) { throw "Host build failed. See $logDir\host-build.log" }
    $pfxPath = Join-Path $hostOut 'openiddict.pfx'; $certPassPhrase = [Guid]::NewGuid().ToString('N')
    & dotnet dev-certs https -v -ep $pfxPath -p $certPassPhrase --trust *> $null
    if ($LASTEXITCODE -ne 0) { throw 'dotnet dev-certs failed.' }
    $url = "https://localhost:$hostPort"
    [Environment]::SetEnvironmentVariable('ASPNETCORE_ENVIRONMENT', 'Staging', 'Process')
    [Environment]::SetEnvironmentVariable('DOTNET_ENVIRONMENT', 'Staging', 'Process')
    [Environment]::SetEnvironmentVariable('ASPNETCORE_URLS', $url, 'Process')
    [Environment]::SetEnvironmentVariable('AuthServer__Authority', $url, 'Process')
    [Environment]::SetEnvironmentVariable('App__SelfUrl', $url, 'Process')
    [Environment]::SetEnvironmentVariable('AuthServer__CertificatePassPhrase', $certPassPhrase, 'Process')
    [Environment]::SetEnvironmentVariable('BuildingManagement__SliceARosterStaging__Enabled', 'true', 'Process')
    [Environment]::SetEnvironmentVariable('BuildingManagement__SliceARosterStaging__Activation', 'I_ACCEPT_ISOLATED_SLICE_A_ROSTER_READS', 'Process')
    [Environment]::SetEnvironmentVariable('BuildingManagement__SliceAStaging__Enabled', 'false', 'Process')
    [Environment]::SetEnvironmentVariable('BuildingManagement__OwnerRecovery__Enabled', 'false', 'Process')
    $hostProcess = Start-Process -FilePath 'dotnet' -ArgumentList @((Join-Path $hostOut 'NgocTrangHouseManagementSystem.HttpApi.Host.dll')) -WorkingDirectory $hostOut -PassThru -RedirectStandardOutput (Join-Path $logDir 'host.log') -RedirectStandardError (Join-Path $logDir 'host-stderr.log')

    Add-Type -AssemblyName System.Net.Http -ErrorAction SilentlyContinue
    $handler = New-Object System.Net.Http.HttpClientHandler
    $handler.ServerCertificateCustomValidationCallback = [System.Net.Http.HttpClientHandler]::DangerousAcceptAnyServerCertificateValidator
    $handler.UseProxy = $false; $client = New-Object System.Net.Http.HttpClient($handler); $client.Timeout = [TimeSpan]::FromSeconds(5)
    try {
        $ready = $false
        for ($i = 0; $i -lt 180; $i++) {
            if ($hostProcess.HasExited) { throw "Host exited early. See $logDir\host.log" }
            try { $resp = $client.GetAsync("$url/health-status").GetAwaiter().GetResult(); if ([int]$resp.StatusCode -eq 200) { $ready = $true; break } } catch { }
            Start-Sleep -Seconds 1
        }
    } finally { $client.Dispose(); $handler.Dispose() }
    if (-not $ready) { throw "Roster Host not healthy within 180s." }
    [Environment]::SetEnvironmentVariable('VITE_PROXY_TARGET', $url, 'Process')
    $viteProcess = Start-Process -FilePath 'npm.cmd' -ArgumentList @('run','dev','--','--port',"$vitePort",'--strictPort') -WorkingDirectory $frontendDir -PassThru -RedirectStandardOutput (Join-Path $logDir 'vite.log') -RedirectStandardError (Join-Path $logDir 'vite-stderr.log')
    for ($i = 0; $i -lt 90; $i++) { if ($viteProcess.HasExited) { throw "Vite exited early." }; if (Get-NetTCPConnection -LocalPort $vitePort -State Listen -ErrorAction SilentlyContinue) { break }; Start-Sleep -Seconds 1 }
    if (-not (Get-NetTCPConnection -LocalPort $vitePort -State Listen -ErrorAction SilentlyContinue)) { throw 'Vite did not start.' }
    Say "Roster-only lane ready at http://localhost:$vitePort; capture can now authenticate as admin."
    while ($true) { if ($hostProcess.HasExited) { throw 'Roster Host stopped.' }; if ($viteProcess.HasExited) { throw 'Vite stopped.' }; Start-Sleep -Seconds 2 }
}
finally {
    Say 'Shutting roster-only lane down.'
    foreach ($proc in $viteProcess, $hostProcess) { if ($proc -and -not $proc.HasExited) { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue } }
    foreach ($port in $vitePort, $hostPort) { Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue | ForEach-Object { Stop-Process -Id $_.OwningProcess -Force -ErrorAction SilentlyContinue } }
    foreach ($name in $envNames) { [Environment]::SetEnvironmentVariable($name, $oldEnv[$name], 'Process') }
    foreach ($db in $identityDb, $buildingDb) { if ($db) { & docker exec $container dropdb -U postgres --if-exists $db 2>$null | Out-Null; Say "Dropped disposable database $db" } }
    $AdminPassword = $null
    Say "Roster-only lane stopped. 15433 and 5432 were not touched. Logs: $logDir"
}
