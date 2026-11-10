# LOCAL HOST-ONLY RECOVERY for the dedicated disposable first-boot DB.
# Never creates/removes any container, never migrates or resets a database.
# payOS OFF. All secrets remain local in memory; never print docker environment.
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$hostDir = Join-Path $root 'src/NgocTrangHouseManagementSystem.HttpApi.Host'
$hostDll = Join-Path $hostDir 'bin/Release/net10.0/NgocTrangHouseManagementSystem.HttpApi.Host.dll'
$envNames = @(
    'DOTNET_ENVIRONMENT', 'ASPNETCORE_ENVIRONMENT', 'ASPNETCORE_URLS',
    'ConnectionStrings__Default', 'ConnectionStrings__BuildingManagement',
    'NgocTrang__LocalBootstrap__AdminPassword',
    'BuildingManagement__PayOsStaging__Enabled',
    'BuildingManagement__PayOsD04B__Enabled',
    'App__DisablePII'
)
$before = @{}
foreach ($key in $envNames) {
    $before[$key] = [Environment]::GetEnvironmentVariable($key, 'Process')
}
function Set-OnlyForChild([string]$key, [string]$value) {
    [Environment]::SetEnvironmentVariable($key, $value, 'Process')
}
function Assert-Exit([string]$task) {
    if ($LASTEXITCODE -ne 0) {
        throw ("{0} FAILED; exit={1}. Existing database not modified by this script." -f $task, $LASTEXITCODE)
    }
}

try {
    foreach ($command in @('docker', 'dotnet', 'git')) {
        if (-not (Get-Command $command -ErrorAction SilentlyContinue)) {
            throw ("MISSING_COMMAND: {0}" -f $command)
        }
    }
    Push-Location $root
    try {
        $branch = (& git branch --show-current | Out-String).Trim()
        Assert-Exit 'GIT_BRANCH_READ'
        if ($branch -cne 'feat/local-safe-startup-20261109') {
            throw 'LOCAL_HOST_DENIED: wrong Git branch.'
        }
    }
    finally { Pop-Location }

    # Only reuse one running container explicitly created by the local runner.
    $containers = @(& docker ps --filter 'label=nt.scope=local-first-boot-20261109' --format '{{.ID}}')
    Assert-Exit 'DOCKER_TEST_CONTAINER_LOOKUP'
    if ($containers.Count -ne 1 -or
        [string]::IsNullOrWhiteSpace([string]$containers[0])) {
        throw 'LOCAL_HOST_DENIED: exactly one labelled running disposable test container required.'
    }
    $id = [string]$containers[0]

    $port = (& docker port $id '5432/tcp' | Out-String).Trim()
    Assert-Exit 'DOCKER_TEST_PORT_LOOKUP'
    if ($port -cne '127.0.0.1:15433') {
        throw 'LOCAL_HOST_DENIED: container PostgreSQL is not bound exclusively to 127.0.0.1:15433.'
    }

    $configJson = (& docker inspect --format '{{json .Config}}' $id | Out-String).Trim()
    Assert-Exit 'DOCKER_TEST_CONFIG_INSPECT'
    if ([string]::IsNullOrWhiteSpace($configJson)) {
        throw 'LOCAL_HOST_DENIED: no Docker container configuration.'
    }
    $config = ConvertFrom-Json -InputObject $configJson
    $configJson = $null
    if ([string]$config.Labels.'nt.scope' -cne 'local-first-boot-20261109' -or
        [string]$config.Image -cne 'postgres:16') {
        throw 'LOCAL_HOST_DENIED: test container label/image mismatch.'
    }
    $dbPasswordEntries = @($config.Env | Where-Object {
        $_ -like 'POSTGRES_PASSWORD=*'
    })
    $dbNameEntries = @($config.Env | Where-Object {
        $_ -ceq 'POSTGRES_DB=nt_firstboot_identity'
    })
    if ($dbPasswordEntries.Count -ne 1 -or
        $dbNameEntries.Count -ne 1) {
        throw 'LOCAL_HOST_DENIED: expected disposable DB initialization values absent.'
    }
    $password = [string]$dbPasswordEntries[0]
    $password = $password.Substring('POSTGRES_PASSWORD='.Length)
    if ([string]::IsNullOrWhiteSpace($password)) {
        throw 'LOCAL_HOST_DENIED: disposable DB password is empty.'
    }
    $dbPasswordEntries = $null
    $config = $null

    foreach ($dbname in @('nt_firstboot_identity', 'nt_firstboot_building')) {
        $returned = (& docker exec $id psql -U postgres -d $dbname -Atqc 'SELECT current_database()' 2>$null | Out-String).Trim()
        if ($LASTEXITCODE -ne 0 -or $returned -cne $dbname) {
            throw ("LOCAL_HOST_DENIED: missing/unreachable isolated database {0}." -f $dbname)
        }
    }
    if (-not (Test-Path -LiteralPath $hostDll -PathType Leaf)) {
        throw 'LOCAL_HOST_DENIED: compiled Release Host DLL not found. Do NOT start the first-boot script while port 15433 is occupied.'
    }
    # An existing HTTPS listener means another Host might already be running.
    $existing = @(Get-NetTCPConnection -LocalPort 44311 -State Listen -ErrorAction SilentlyContinue)
    if ($existing.Count -gt 0) {
        throw 'LOCAL_HOST_DENIED: HTTPS port 44311 is already listening; do not start a second Host.'
    }

    $csIdentity = "Host=127.0.0.1;Port=15433;Database=nt_firstboot_identity;Username=postgres;Password=$password;Pooling=false;Timeout=5"
    $csBuilding = "Host=127.0.0.1;Port=15433;Database=nt_firstboot_building;Username=postgres;Password=$password;Pooling=false;Timeout=5"
    $password = $null

    Set-OnlyForChild 'DOTNET_ENVIRONMENT' 'Development'
    Set-OnlyForChild 'ASPNETCORE_ENVIRONMENT' 'Development'
    Set-OnlyForChild 'ASPNETCORE_URLS' 'https://localhost:44311'
    Set-OnlyForChild 'ConnectionStrings__Default' $csIdentity
    Set-OnlyForChild 'ConnectionStrings__BuildingManagement' $csBuilding
    Set-OnlyForChild 'NgocTrang__LocalBootstrap__AdminPassword' $null
    Set-OnlyForChild 'BuildingManagement__PayOsStaging__Enabled' 'false'
    Set-OnlyForChild 'BuildingManagement__PayOsD04B__Enabled' 'false'
    Set-OnlyForChild 'App__DisablePII' 'true'
    $csIdentity = $null
    $csBuilding = $null

    Write-Host 'LOCAL_HOST_RECOVERY_GUARDS_PASS: reusing original disposable identity and building DB.' -ForegroundColor Green
    Write-Host 'NO migrations, NO new container, NO database deletion. payOS OFF.'
    Write-Host 'Starting existing built NgocTrang Host at https://localhost:44311'
    Write-Host 'Keep this PowerShell window OPEN while running role acceptance.'
    Push-Location $hostDir
    try {
        & dotnet run --no-build --no-launch-profile -c Release
        Assert-Exit 'LOCAL_HOST_PROCESS'
    }
    finally { Pop-Location }
}
finally {
    # Restore caller process environment. No Docker container cleanup here.
    foreach ($key in $envNames) {
        [Environment]::SetEnvironmentVariable($key, $before[$key], 'Process')
    }
}
