# Native OpenIddict + D04-B disposable financial Host. No migrations or real payOS.
# PRECONDITIONS: three disposable DBs already exist on 127.0.0.1:15432,
# with migrated schemas, two independent test actors and synthetic financial intent.
# Stop the other localhost:44311 Host first. NEVER run against real bank evidence.
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$ArchiveRoot
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$hostDir = Join-Path $root 'src/NgocTrangHouseManagementSystem.HttpApi.Host'
$names = @(
 'ASPNETCORE_ENVIRONMENT','DOTNET_ENVIRONMENT','ASPNETCORE_URLS',
 'ConnectionStrings__Default','ConnectionStrings__BuildingManagement',
 'ConnectionStrings__PayOsD04BStaging',
 'BuildingManagement__PayOsStaging__Enabled',
 'BuildingManagement__PayOsD04B__Enabled',
 'BuildingManagement__PayOsD04B__Activation',
 'BuildingManagement__PayOsD04B__DisposableArchive__Enabled',
 'BuildingManagement__PayOsD04B__DisposableArchive__Root',
 'BuildingManagement__PayOsD04B__DisposableArchive__KeyBase64'
)
$prior = @{}
foreach ($name in $names) { $prior[$name] = [Environment]::GetEnvironmentVariable($name,'Process') }
function Set-Local([string]$name,[string]$value) {
 [Environment]::SetEnvironmentVariable($name,$value,'Process')
}
try {
 $branch = (& git -C $root branch --show-current).Trim()
 if ($LASTEXITCODE -ne 0 -or $branch -cne 'feat/local-safe-startup-20261109') {
    throw 'D04B_BRANCH_DENIED: expected dedicated local-safe-startup branch.'
 }
 if (-not [IO.Path]::IsPathFullyQualified($ArchiveRoot)) {
    throw 'D04B_ARCHIVE_DENIED: absolute directory required.'
 }
 $fullArchive = [IO.Path]::GetFullPath($ArchiveRoot)
 if ($fullArchive.StartsWith($root + [IO.Path]::DirectorySeparatorChar,
      [StringComparison]::OrdinalIgnoreCase)) {
    throw 'D04B_ARCHIVE_DENIED: archive must be outside the source repository.'
 }
 if (-not (Test-Path -LiteralPath $fullArchive -PathType Container)) {
    throw 'D04B_ARCHIVE_DENIED: create a dedicated private disposable archive directory first.'
 }
 # Never create a key silently: restarting with a different key would make
 # previously committed evidence unreadable and block Owner/posting.
 $key = [Environment]::GetEnvironmentVariable('D04B_UAT_ARCHIVE_KEY_BASE64','Process')
 if ([string]::IsNullOrWhiteSpace($key)) {
    throw 'D04B_KEY_MISSING: set D04B_UAT_ARCHIVE_KEY_BASE64 locally; never paste it into chat.'
 }
 try { $bytes = [Convert]::FromBase64String($key) }
 catch { throw 'D04B_KEY_INVALID: expected base64-encoded 32-byte AES key.' }
 if ($bytes.Length -ne 32) { throw 'D04B_KEY_INVALID: expected exactly 32 bytes.' }
 [Array]::Clear($bytes,0,$bytes.Length)
 $portBusy = Get-NetTCPConnection -LocalPort 44311 -State Listen -ErrorAction SilentlyContinue
 if ($portBusy) { throw 'D04B_HOST_PORT_BUSY: stop the existing 44311 Host; do not kill it automatically.' }
 $dbs = @('payos_d04_identity_test','payos_d04_legacy_test','payos_ef_migration_test')
 $dbConn = @{}
 foreach ($db in $dbs) {
    $dbConn[$db] = "Host=127.0.0.1;Port=15432;Database=$db;Username=postgres;Password=disposable-only;Pooling=false;Timeout=5"
 }
 # Verify database connectivity without creating/migrating/modifying anything.
 foreach ($db in $dbs) {
    $result = & docker ps --filter 'publish=15432' --format '{{.ID}}'
    if ($LASTEXITCODE -ne 0 -or -not $result) {
        throw 'D04B_DB_UNAVAILABLE: disposable PostgreSQL on port 15432 is not running.'
    }
 }
 Set-Local 'ASPNETCORE_ENVIRONMENT' 'Staging'
 Set-Local 'DOTNET_ENVIRONMENT' 'Staging'
 Set-Local 'ASPNETCORE_URLS' 'https://localhost:44311'
 Set-Local 'ConnectionStrings__Default' $dbConn['payos_d04_identity_test']
 Set-Local 'ConnectionStrings__BuildingManagement' $dbConn['payos_d04_legacy_test']
 Set-Local 'ConnectionStrings__PayOsD04BStaging' $dbConn['payos_ef_migration_test']
 Set-Local 'BuildingManagement__PayOsStaging__Enabled' 'false'
 Set-Local 'BuildingManagement__PayOsD04B__Enabled' 'true'
 Set-Local 'BuildingManagement__PayOsD04B__Activation' 'I_ACCEPT_DISPOSABLE_D04B_DUAL_AUTH_ONLY'
 Set-Local 'BuildingManagement__PayOsD04B__DisposableArchive__Enabled' 'true'
 Set-Local 'BuildingManagement__PayOsD04B__DisposableArchive__Root' $fullArchive
 Set-Local 'BuildingManagement__PayOsD04B__DisposableArchive__KeyBase64' $key
 $key = $null
 Write-Host 'D04B_NATIVE_UAT_START: exact three disposable databases, staging, payOS merchant OFF.' -ForegroundColor Cyan
 Write-Host 'No migration, financial posting, token creation or live transfer is initiated by this script.'
 Push-Location $hostDir
 try {
    & dotnet run --no-build --no-launch-profile
    if ($LASTEXITCODE -ne 0) { throw "D04B_NATIVE_HOST_FAILED: exit=$LASTEXITCODE" }
 } finally { Pop-Location }
}
finally {
 foreach ($name in $names) {
    [Environment]::SetEnvironmentVariable($name,$prior[$name],'Process')
 }
}
