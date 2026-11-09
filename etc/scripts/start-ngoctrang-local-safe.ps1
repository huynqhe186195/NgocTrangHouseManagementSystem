
# LOCAL FIRST BOOT ONLY: no real payOS, no production DB, no persistent volume.
# Starts its OWN disposable PostgreSQL 16 on 127.0.0.1:15433.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$hostDir = Join-Path $root 'src/NgocTrangHouseManagementSystem.HttpApi.Host'
$migratorDir = Join-Path $root 'src/NgocTrangHouseManagementSystem.DbMigrator'
$containerId = ''
$changed = @('ASPNETCORE_ENVIRONMENT','DOTNET_ENVIRONMENT','ASPNETCORE_URLS',
    'ConnectionStrings__Default','ConnectionStrings__BuildingManagement',
    'NgocTrang__LocalBootstrap__AdminPassword',
    'BuildingManagement__PayOsStaging__Enabled',
    'BuildingManagement__PayOsD04B__Enabled','POSTGRES_PASSWORD')
$old = @{}
foreach ($key in $changed) {
    $old[$key] = [Environment]::GetEnvironmentVariable($key, 'Process')
}
function Set-TestVariable([string]$name,[string]$value) {
    [Environment]::SetEnvironmentVariable($name,$value,'Process')
}
function Assert-Exit([string]$step) {
    if ($LASTEXITCODE -ne 0) {
        throw "$step failed with exit=$LASTEXITCODE. Existing PostgreSQL was not touched."
    }
}
try {
    foreach ($name in @('git','dotnet','docker')) {
        if (-not (Get-Command $name -ErrorAction SilentlyContinue)) {
            throw "Missing command: $name"
        }
    }
    Push-Location $root
    try {
        $branch = (& git branch --show-current).Trim()
        Assert-Exit 'Git inspection'
        if ($branch -cne 'feat/local-safe-startup-20261109') {
            throw "Safety stop: current branch $branch is not feat/local-safe-startup-20261109"
        }
        Write-Host '1/5 Compiling Host and Migrator; no DB changes yet...' -ForegroundColor Cyan
        & dotnet build 'src/NgocTrangHouseManagementSystem.HttpApi.Host/NgocTrangHouseManagementSystem.HttpApi.Host.csproj' -c Release
        Assert-Exit 'Host build'
        & dotnet build 'src/NgocTrangHouseManagementSystem.DbMigrator/NgocTrangHouseManagementSystem.DbMigrator.csproj' -c Release
        Assert-Exit 'Migrator build'
    }
    finally { Pop-Location }

    Write-Host '2/5 Choose a new LOCAL-ONLY admin password. NEVER enter payOS API keys here.' -ForegroundColor Cyan
    $secure = Read-Host 'New LOCAL admin password, 16+ chars' -AsSecureString
    $bstr = [IntPtr]::Zero
    try {
        $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
        $adminPassword = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr)
    }
    finally {
        if ($bstr -ne [IntPtr]::Zero) {
            [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr)
        }
    }
    if ([string]::IsNullOrWhiteSpace($adminPassword) -or $adminPassword.Length -lt 16) {
        throw 'A local admin password with at least 16 chars is required.'
    }
    Set-TestVariable 'NgocTrang__LocalBootstrap__AdminPassword' $adminPassword
    $adminPassword = $null
    $randomBytes = New-Object 'System.Byte[]' 24
    $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($randomBytes) } finally { $rng.Dispose() }
    $dbPassword = [BitConverter]::ToString($randomBytes).Replace('-','')
    [Array]::Clear($randomBytes,0,$randomBytes.Length)
    Set-TestVariable 'POSTGRES_PASSWORD' $dbPassword
    Set-TestVariable 'DOTNET_ENVIRONMENT' 'Development'
    Set-TestVariable 'ASPNETCORE_ENVIRONMENT' 'Development'
    Set-TestVariable 'ASPNETCORE_URLS' 'https://localhost:44311'
    Set-TestVariable 'BuildingManagement__PayOsStaging__Enabled' 'false'
    Set-TestVariable 'BuildingManagement__PayOsD04B__Enabled' 'false'

    Write-Host '3/5 Creating NEW disposable database on loopback port 15433...' -ForegroundColor Cyan
    $name = "nt-safe-firstboot-$PID-$(Get-Random -Minimum 10000 -Maximum 99999)"
    $idText = & docker run --rm --detach --name $name --label 'nt.scope=local-first-boot-20261109' --publish '127.0.0.1:15433:5432' --env POSTGRES_PASSWORD --env 'POSTGRES_DB=nt_firstboot_identity' postgres:16
    Assert-Exit 'Docker isolated start'
    $containerId = "$idText".Trim()
    if ($containerId -notmatch '^[0-9a-f]{64}$') { throw 'No validated disposable container ID.' }

    $ready = $false
    for ($i=0; $i -lt 30; $i++) {
        & docker exec $containerId pg_isready -U postgres -d nt_firstboot_identity *> $null
        if ($LASTEXITCODE -eq 0) { $ready = $true; break }
        Start-Sleep -Seconds 1
    }
    if (-not $ready) { throw 'Disposable PostgreSQL did not become ready.' }
    & docker exec $containerId createdb -U postgres nt_firstboot_building
    Assert-Exit 'Dedicated Building DB creation'
    $identity = "Host=127.0.0.1;Port=15433;Database=nt_firstboot_identity;Username=postgres;Password=$dbPassword;Pooling=false;Timeout=5"
    $building = "Host=127.0.0.1;Port=15433;Database=nt_firstboot_building;Username=postgres;Password=$dbPassword;Pooling=false;Timeout=5"
    Set-TestVariable 'ConnectionStrings__Default' $identity
    Set-TestVariable 'ConnectionStrings__BuildingManagement' $building
    $dbPassword = $null
    $identity = $null
    $building = $null

    Write-Host '4/5 Migrating ONLY freshly created test databases...' -ForegroundColor Cyan
    Push-Location $migratorDir
    try {
        & dotnet run --no-build --no-launch-profile -c Release
        Assert-Exit 'Isolated DB migration'
    }
    finally { Pop-Location }
    Set-TestVariable 'NgocTrang__LocalBootstrap__AdminPassword' $null

    Write-Host '5/5 Starting backend at https://localhost:44311 with payOS OFF.' -ForegroundColor Green
    Write-Host 'Press Ctrl+C to stop and clean up only this disposable database container.'
    Push-Location $hostDir
    try {
        & dotnet run --no-build --no-launch-profile -c Release
        Assert-Exit 'Local HTTP Host'
    }
    finally { Pop-Location }
}
finally {
    if ($containerId -match '^[0-9a-f]{64}$') {
        $label = & docker inspect --format '{{ index .Config.Labels "nt.scope" }}' $containerId 2>$null
        if ($label -eq 'local-first-boot-20261109') {
            Write-Host 'Removing only the newly created first-boot container...'
            & docker rm --force $containerId *> $null
        }
    }
    foreach ($key in $changed) {
        [Environment]::SetEnvironmentVariable($key,$old[$key],'Process')
    }
}
