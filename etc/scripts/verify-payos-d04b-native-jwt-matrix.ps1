[CmdletBinding()]
param()

# P1.1 D04-B NATIVE OpenIddict JWT dual-actor HTTP acceptance matrix.
# Real Host, real password-grant tokens, three disposable PostgreSQL 16 DBs
# on 127.0.0.1:15432 only. Never 15433 or 5432. No real payOS, no real bank.
# Tokens/passwords are never echoed or written to disk.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
# Windows PowerShell 5.1 defaults may not negotiate TLS 1.2.
[System.Net.ServicePointManager]::SecurityProtocol = [System.Net.SecurityProtocolType]::Tls12

# Self-signed ephemeral dev cert: skip validation for this run's requests.
$script:certCallback = { $true }

function Enable-CertSkip() {
    $script:originalCallback = [System.Net.ServicePointManager]::ServerCertificateValidationCallback
    [System.Net.ServicePointManager]::ServerCertificateValidationCallback = $script:certCallback
}

function Disable-CertSkip() {
    [System.Net.ServicePointManager]::ServerCertificateValidationCallback = $script:originalCallback
}

function Test-HostHealthy() {
    try {
        Enable-CertSkip
        try {
            $request = [System.Net.HttpWebRequest]::Create('https://localhost:44311/health-status')
            $request.Method = 'GET'
            $request.Proxy = $null
            $request.Timeout = 5000
            $response = $request.GetResponse()
            try {
                return ([int]$response.StatusCode -eq 200)
            }
            finally { $response.Dispose() }
        }
        catch { return $false }
        finally { Disable-CertSkip }
    }
    catch { return $false }
}

$root = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$originalRoot = $root
$isolatedRoot = ''
$isolatedCreated = $false
$enteredIsolated = $false
$containerId = ''
$hostProcess = $null
$logRoot = ''
$archiveRoot = ''
$pfxPath = ''
$reviewerToken = ''
$ownerToken = ''
$outsiderToken = ''

$allowedBranches = @('feat/local-safe-startup-20261109')
$envNames = @(
    'BM_PAYOS_EF_TEST_DB','BM_PAYOS_EF_TEST_PASSWORD','BM_PAYOS_EF_TEST_PORT',
    'BM_PAYOS_D04_REAL_WRITER_TEST_DB','BM_PAYOS_D04_NATIVE_JWT',
    'BM_PAYOS_D04_NATIVE_JWT_REVIEWER_PASSWORD',
    'BM_PAYOS_D04_NATIVE_JWT_OWNER_PASSWORD',
    'BM_PAYOS_D04_NATIVE_JWT_OUTSIDER_PASSWORD',
    'ConnectionStrings__Default','ConnectionStrings__BuildingManagement',
    'ConnectionStrings__PayOsD04BStaging','ASPNETCORE_ENVIRONMENT',
    'DOTNET_ENVIRONMENT','ASPNETCORE_URLS','AuthServer__CertificatePassPhrase',
    'BuildingManagement__PayOsStaging__Enabled',
    'BuildingManagement__PayOsD04B__Enabled',
    'BuildingManagement__PayOsD04B__Activation',
    'BuildingManagement__PayOsD04B__DisposableArchive__Enabled',
    'BuildingManagement__PayOsD04B__DisposableArchive__Root',
    'BuildingManagement__PayOsD04B__DisposableArchive__KeyBase64',
    'NgocTrang__LocalBootstrap__AdminPassword'
)
$oldEnv = @{}
foreach ($name in $envNames) {
    $oldEnv[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}

function Invoke-Capture([string]$command, [string[]]$arguments) {
    $previous = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $output = (& $command @arguments 2>&1 | Out-String).Trim()
        $exitCode = $LASTEXITCODE
    }
    finally { $ErrorActionPreference = $previous }
    if ($exitCode -ne 0) {
        throw "$command $($arguments -join ' ') failed (exit=$exitCode): $output"
    }
    return $output
}

function Invoke-Logged([string]$step, [string]$command, [string[]]$arguments) {
    $log = Join-Path $logRoot "$step.log"
    Write-Host "=== $step ===" -ForegroundColor Cyan
    $previous = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        & $command @arguments *>&1 |
            ForEach-Object { $_.ToString() } |
            Tee-Object -FilePath $log |
            Out-Host
        $exitCode = $LASTEXITCODE
    }
    finally { $ErrorActionPreference = $previous }
    if ($exitCode -ne 0) {
        Write-Host "=== $step original error and stack ===" -ForegroundColor Red
        if (Test-Path -LiteralPath $log) {
            $logLines = @([System.IO.File]::ReadAllLines($log))
            $start = -1
            for ($i = 0; $i -lt $logLines.Length; $i++) {
                if ($logLines[$i] -match '^\s*Failed\s+.*\[\d+\s*(ms|s)\]' -or
                    $logLines[$i] -match '^\s*(Error Message:|\[xUnit.net.*\[FAIL\])') {
                    $start = $i
                    break
                }
            }
            if ($start -ge 0) {
                $end = [Math]::Min($start + 55, $logLines.Length - 1)
                for ($i = $start; $i -le $end; $i++) {
                    Write-Host $logLines[$i] -ForegroundColor Red
                }
            }
            else {
                $diagnostics = @($logLines | Where-Object {
                    $_ -match '(?i)(error (CS|NU|MSB)[0-9]+|ERROR:|FATAL:|Exception:|Npgsql\.PostgresException|\[FAIL\])'
                } | Select-Object -First 20)
                if ($diagnostics.Count -gt 0) {
                    $diagnostics | ForEach-Object { Write-Host $_ -ForegroundColor Red }
                }
                else {
                    $logLines | Select-Object -Last 22 |
                        ForEach-Object { Write-Host $_ -ForegroundColor Red }
                }
            }
        }
        throw "$step FAILED (exit=$exitCode). Log: $log"
    }
    Write-Host "PASS: $step" -ForegroundColor Green
}

function Assert-Trx([string]$filename, [int]$expectedCount = 0) {
    $path = Join-Path $logRoot $filename
    if (-not (Test-Path -LiteralPath $path)) {
        throw "Missing TRX evidence: $path"
    }
    [xml]$trx = Get-Content -LiteralPath $path -Raw
    $counters = $trx.SelectSingleNode('//*[local-name()="Counters"]')
    if ($null -eq $counters) {
        throw "Missing TRX counters: $path"
    }
    $total = [int]$counters.GetAttribute('total')
    $passed = [int]$counters.GetAttribute('passed')
    $failed = [int]$counters.GetAttribute('failed')
    $skipped = [int]$counters.GetAttribute('notExecuted')
    if ($total -lt 1 -or $passed -lt 1 -or $failed -ne 0 -or $skipped -ne 0 -or
        ($expectedCount -gt 0 -and ($total -ne $expectedCount -or $passed -ne $expectedCount))) {
        throw "TRX failed/empty/skipped/wrong test count: total=$total passed=$passed failed=$failed notExecuted=$skipped expected=$expectedCount ($path)"
    }
    Write-Host "TRX VERIFIED: $filename passed=$passed total=$total" -ForegroundColor Green
}

# SQL through a file to keep PS5.1 + docker.exe quoting exact.
function Invoke-SqlFile([string]$database, [string]$sql, [string]$step) {
    $sqlFile = Join-Path $logRoot "$step.sql"
    Set-Content -LiteralPath $sqlFile -Encoding Ascii -Value $sql
    Invoke-Logged $step 'docker' @(
        'cp', $sqlFile, "$($containerId):/tmp/$step.sql")
    Invoke-Logged "${step}-exec" 'docker' @(
        'exec', $containerId, 'psql', '-U', 'postgres', '-d', $database,
        '-At', '-v', 'ON_ERROR_STOP=1', '-f', "/tmp/$step.sql")
}

function Invoke-Scalar([string]$database, [string]$sql) {
    $sqlFile = Join-Path $logRoot ('scalar-' + [Guid]::NewGuid().ToString('N') + '.sql')
    Set-Content -LiteralPath $sqlFile -Encoding Ascii -Value $sql
    try {
        Invoke-Capture 'docker' @(
            'cp', $sqlFile, "$($containerId):/tmp/scalar.sql")
        return (Invoke-Capture 'docker' @(
            'exec', $containerId, 'psql', '-U', 'postgres', '-d', $database,
            '-At', '-v', 'ON_ERROR_STOP=1', '-f', '/tmp/scalar.sql')).Trim()
    }
    finally {
        Remove-Item -LiteralPath $sqlFile -ErrorAction SilentlyContinue
        # scalar files never contain secrets: only GUIDs and statuses.
    }
}

# Password-grant token from the real OpenIddict token endpoint. The token is
# kept ONLY in script memory and is never echoed, logged or saved to disk.
function Get-NativeToken([string]$username, [string]$password) {
    $pair = "username=$username&password=$password&grant_type=password" +
        "&client_id=NgocTrangHouseManagementSystem_App" +
        "&scope=NgocTrangHouseManagementSystem"
    $bodyBytes = [System.Text.Encoding]::UTF8.GetBytes($pair)
    $request = [System.Net.HttpWebRequest]::Create('https://localhost:44311/connect/token')
    $request.Method = 'POST'
    $request.ContentType = 'application/x-www-form-urlencoded'
    $request.ContentLength = $bodyBytes.Length
    $request.Proxy = $null
    $request.Timeout = 30000
    # Self-signed ephemeral dev cert: validation is skipped; the HTTPS channel
    # itself is not the object under test (identity of the CA is).
    Enable-CertSkip
    try {
        $stream = $request.GetRequestStream()
        try { $stream.Write($bodyBytes, 0, $bodyBytes.Length) } finally { $stream.Dispose() }
        $response = $null
        try {
            $response = $request.GetResponse()
            $reader = New-Object System.IO.StreamReader($response.GetResponseStream())
            try {
                $json = $reader.ReadToEnd()
                $token = [regex]::Match($json, '"access_token"\s*:\s*"([^"]+)"').Groups[1].Value
                if ([string]::IsNullOrWhiteSpace($token)) {
                    throw "No access_token in token response for $username."
                }
                return $token
            } finally { $reader.Dispose() }
        }
        finally { if ($response) { $response.Dispose() } }
    }
    finally { Disable-CertSkip }
}

# A single HTTP request carrying an optional bearer token. Response body is
# logged minus any token material; status is returned.
function Invoke-MatrixRequest(
    [string]$step, [string]$method, [string]$url,
    [byte[]]$bodyBytes, [string]$contentType, [string]$token
) {
    Write-Host "=== $step ===" -ForegroundColor Cyan
    $request = [System.Net.HttpWebRequest]::Create($url)
    $request.Method = $method
    $request.Proxy = $null
    $request.AllowAutoRedirect = $false
    $request.Timeout = 120000
    if ($token) {
        $request.Headers.Add('Authorization', "Bearer $token")
    }
    if ($bodyBytes -and $bodyBytes.Length -gt 0) {
        if ($contentType) { $request.ContentType = $contentType }
        $request.ContentLength = $bodyBytes.Length
        $stream = $request.GetRequestStream()
        try { $stream.Write($bodyBytes, 0, $bodyBytes.Length) } finally { $stream.Dispose() }
    }
    Enable-CertSkip
    try {
        try {
            $response = $request.GetResponse()
        }
        catch [System.Net.WebException] {
            $response = $_.Exception.Response
            if ($null -eq $response) { throw }
        }
        try {
            $status = [int]$response.StatusCode
            $reader = New-Object System.IO.StreamReader($response.GetResponseStream())
            try { $bodyText = $reader.ReadToEnd() } finally { $reader.Dispose() }
            # Only ids and status words are logged; never token material.
            $safeBody = $bodyText -replace '"access_token"\s*:\s*"[^"]+"', '"access_token":"[REDACTED]"'
            Write-Host "MATRIX $step => HTTP $status"
            $safeBody | Set-Content -LiteralPath (Join-Path $logRoot "$step-body.txt")
            return @{ Status = $status; Body = $bodyText }
        }
        finally { $response.Dispose() }
    }
    finally { Disable-CertSkip }
}

function Assert-Status([string]$step, $actual, [int]$expected) {
    if ($actual.Status -ne $expected) {
        throw "MATRIX FAILURE: $step expected HTTP $expected, got $($actual.Status). See $($step)-body.txt"
    }
    Write-Host "MATRIX PASS: $step HTTP $expected" -ForegroundColor Green
}

function Assert-BodyContains([string]$step, $response, [string]$needle) {
    if (-not $response.Body -or $response.Body -notmatch [regex]::Escape($needle)) {
        throw "MATRIX FAILURE: $step body missing '$needle'. See $($step)-body.txt"
    }
    Write-Host "MATRIX PASS: $step contains $needle" -ForegroundColor Green
}

# Multipart form with one synthetic PNG bank-statement file (magic bytes only).
function New-ReviewMultipart([string]$bankReference, [long]$amountVnd, [string]$boundary) {
    $prefix = [byte[]](0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A)
    $payload = New-Object System.Collections.Generic.List[byte]
    $enc = [System.Text.Encoding]::UTF8
    $fields = [ordered]@{
        'BankReference' = $bankReference
        'AmountVnd' = [string]$amountVnd
        'ReviewerAttestsBankStatementMatched' = 'true'
        # Non-nullable form string: model validation requires a value; the
        # server-side facade ignores this client-supplied reference.
        'ProtectedArchiveReference' = 'client-supplied-ignored'
    }
    foreach ($field in $fields.GetEnumerator()) {
        $part = "--$boundary`r`nContent-Disposition: form-data; name=`"$($field.Key)`"`r`n`r`n$($field.Value)`r`n"
        $payload.AddRange($enc.GetBytes($part))
    }
    $fileHeader = "--$boundary`r`nContent-Disposition: form-data; name=`"Document`"; filename=`"statement.png`"`r`nContent-Type: image/png`r`n`r`n"
    $payload.AddRange($enc.GetBytes($fileHeader))
    $payload.AddRange($prefix)
    $payload.AddRange($enc.GetBytes((('D04NATIVE' * 80))))
    $payload.AddRange($enc.GetBytes("`r`n--$boundary--`r`n"))
    return $payload.ToArray()
}

Push-Location $root
try {
    foreach ($command in @('git','dotnet','docker')) {
        if (-not (Get-Command $command -ErrorAction SilentlyContinue)) {
            throw "Missing required tool: $command"
        }
    }
    $branch = Invoke-Capture 'git' @('branch', '--show-current')
    if ($branch -cnotin $allowedBranches) {
        throw "Safety stop: run only on the reviewed D04-B branch (current='$branch')."
    }
    $sha = Invoke-Capture 'git' @('rev-parse', 'HEAD')
    $remoteLine = Invoke-Capture 'git' @('ls-remote', '--exit-code',
        'origin', "refs/heads/$branch")
    if ($remoteLine -match '^([0-9a-fA-F]{40})\s+') {
        $remoteSha = $Matches[1]
    }
    else {
        throw 'Cannot validate current origin HEAD. Do not run stale tests.'
    }
    if ($sha -ne $remoteSha) {
        throw "Local SHA $sha differs from origin $remoteSha."
    }
    $tracked = Invoke-Capture 'git' @('status', '--porcelain', '--untracked-files=no')
    if (-not [string]::IsNullOrWhiteSpace($tracked)) {
        throw "Tracked working tree has edits. Review 'git status' first."
    }
    $sdkList = Invoke-Capture 'dotnet' @('--list-sdks')
    if ($sdkList -notmatch '(?m)^10\.') {
        throw '.NET SDK 10 is required (net10.0 test projects).'
    }

    $dockerServer = Invoke-Capture 'docker' @('info', '--format', '{{.ServerVersion}}')
    Write-Host "SOURCE_COMMIT: $sha"
    Write-Host "DOCKER SERVER: $dockerServer"

    # Never touch the local firstboot identity DB (15433) or 5432.
    $busy = Get-NetTCPConnection -LocalPort 15433 -State Listen -ErrorAction SilentlyContinue
    if ($busy) { Write-Host 'NOTE: 15433 identity Host is listening (untouched by this run).' }
    $portBusy = Get-NetTCPConnection -LocalPort 44311 -State Listen -ErrorAction SilentlyContinue
    if ($portBusy) {
        throw 'HOST_PORT_BUSY: stop the existing 44311 Host; never killed automatically.'
    }
    $conflict = Get-NetTCPConnection -LocalPort 15432 -State Listen -ErrorAction SilentlyContinue
    if ($conflict) {
        throw 'PORT_15432_BUSY: another disposable D04 container or DB is already using it.'
    }

    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $logRoot = Join-Path (Join-Path $HOME 'NgocTrang-Test-Logs') "payos-native-jwt-$stamp"
    New-Item -ItemType Directory -Path $logRoot -Force | Out-Null
    Set-Content -Path (Join-Path $logRoot 'source-commit.txt') -Value $sha

    # Isolated worktree from the exact origin-validated SHA. The ephemeral
    # openiddict.pfx lives inside the worktree Host content root, never in
    # the real repository, and is deleted with the worktree.
    $isolatedRoot = Join-Path $logRoot 'isolated-source'
    Write-Host 'Creating temporary source worktree ...' -ForegroundColor Cyan
    Invoke-Capture 'git' @('worktree','add','--detach','--',$isolatedRoot,$sha) | Out-Null
    $isolatedCreated = $true
    Push-Location $isolatedRoot
    $enteredIsolated = $true
    $isolatedSha = Invoke-Capture 'git' @('rev-parse','HEAD')
    if ($isolatedSha -cne $sha) {
        throw 'ISOLATED_SOURCE_SHA_MISMATCH.'
    }
    $root = $isolatedRoot
    Write-Host "ISOLATED_SOURCE_READY: $sha" -ForegroundColor Green

    # The P1.1 fixture test is not yet committed: copy the exact working-tree
    # file into the isolated worktree so the built test assembly contains it.
    # Any tracked edit still aborts earlier, so this file is the only addition.
    $fixtureSource = Join-Path $originalRoot 'test\PayOs.Staging.Persistence.Tests\D04BNativeJwtProvision_Tests.cs'
    $fixtureTarget = Join-Path $isolatedRoot 'test\PayOs.Staging.Persistence.Tests\D04BNativeJwtProvision_Tests.cs'
    if (-not (Test-Path -LiteralPath $fixtureSource)) {
        throw 'Missing fixture source: D04BNativeJwtProvision_Tests.cs'
    }
    Copy-Item -LiteralPath $fixtureSource -Destination $fixtureTarget -Force
    Write-Host 'FIXTURE_COPIED: D04BNativeJwtProvision_Tests.cs (untracked P1.1 fixture).'

    $pgTests = 'test/PayOs.Staging.Persistence.Tests/PayOs.Staging.Persistence.Tests.csproj'
    $hostProject = 'src/NgocTrangHouseManagementSystem.HttpApi.Host/NgocTrangHouseManagementSystem.HttpApi.Host.csproj'
    $migratorProject = 'src/NgocTrangHouseManagementSystem.DbMigrator/NgocTrangHouseManagementSystem.DbMigrator.csproj'
    $hostDir = Join-Path $root 'src/NgocTrangHouseManagementSystem.HttpApi.Host'

    Invoke-Logged '01-staging-test-compile' 'dotnet' @('build', $pgTests, '-c', 'Release')
    Invoke-Logged '02-migrator-build' 'dotnet' @('build', $migratorProject, '-c', 'Release')
    Invoke-Logged '03-host-build' 'dotnet' @('build', $hostProject, '-c', 'Release')

    # Disposable PostgreSQL 16, loopback-only, own label, own port.
    Write-Host '=== Launching disposable PostgreSQL 16 on 127.0.0.1:15432 ==='
    $containerName = "bm-payos-d04-native-$PID"
    $containerId = Invoke-Capture 'docker' @(
        'run', '--detach', '--rm',
        '--name', $containerName,
        '--label', 'bm.scope=temporary-payos-d04-native-jwt-test',
        '--publish', '127.0.0.1:15432:5432',
        '--env', 'POSTGRES_PASSWORD=disposable-only',
        '--env', 'POSTGRES_DB=payos_d04_identity_test',
        'postgres:16')
    if ($containerId -notmatch '^[0-9a-f]{64}$') {
        throw "Docker did not return a valid container ID: $containerId"
    }
    $ready = $false
    for ($i = 0; $i -lt 120; $i++) {
        $previous = $ErrorActionPreference
        try {
            $ErrorActionPreference = 'Continue'
            & docker exec $containerId pg_isready -h 127.0.0.1 -p 5432 -U postgres -d payos_d04_identity_test *> $null
            $result = $LASTEXITCODE
        }
        finally { $ErrorActionPreference = $previous }
        if ($result -eq 0) { $ready = $true; break }
        Start-Sleep -Milliseconds 500
    }
    if (-not $ready) {
        throw 'Disposable PostgreSQL TCP server did not become ready.'
    }
    Write-Host 'PASS: disposable PostgreSQL ready.' -ForegroundColor Green

    Invoke-Logged '04-create-databases' 'docker' @(
        'exec', $containerId, 'createdb', '-U', 'postgres',
        'payos_d04_legacy_test')
    Invoke-Logged '04b-create-staging-db' 'docker' @(
        'exec', $containerId, 'createdb', '-U', 'postgres',
        'payos_ef_migration_test')

    # Real DbMigrator against the disposable identity DB. The admin password
    # is generated locally, kept only in script memory, never echoed.
    $adminPassword = 'D04N-' + [Guid]::NewGuid().ToString('N') + '-A'
    [Environment]::SetEnvironmentVariable('NgocTrang__LocalBootstrap__AdminPassword',
        $adminPassword, 'Process')
    [Environment]::SetEnvironmentVariable('ConnectionStrings__Default',
        'Host=127.0.0.1;Port=15432;Database=payos_d04_identity_test;Username=postgres;Password=disposable-only', 'Process')
    Invoke-Logged '05-db-migrator-identity' 'dotnet' @(
        'run', '--no-build', '--no-launch-profile', '-c', 'Release',
        '--project', $migratorProject)
    [Environment]::SetEnvironmentVariable('ConnectionStrings__Default', $null, 'Process')
    [Environment]::SetEnvironmentVariable('NgocTrang__LocalBootstrap__AdminPassword', $null, 'Process')

    # Fixture test with generated disposable actor passwords. The fixture
    # clears its own env copies, so keep script-local copies for token calls.
    $reviewerPassword = 'D04N-' + [Guid]::NewGuid().ToString('N') + '-R'
    $ownerPassword = 'D04N-' + [Guid]::NewGuid().ToString('N') + '-O'
    $outsiderPassword = 'D04N-' + [Guid]::NewGuid().ToString('N') + '-X'
    [Environment]::SetEnvironmentVariable('BM_PAYOS_EF_TEST_DB', '1', 'Process')
    [Environment]::SetEnvironmentVariable('BM_PAYOS_EF_TEST_PASSWORD', 'disposable-only', 'Process')
    [Environment]::SetEnvironmentVariable('BM_PAYOS_EF_TEST_PORT', '15432', 'Process')
    [Environment]::SetEnvironmentVariable('BM_PAYOS_D04_REAL_WRITER_TEST_DB',
        'I_ACCEPT_ONLY_DISPOSABLE_PAYOS_D04_DB', 'Process')
    [Environment]::SetEnvironmentVariable('BM_PAYOS_D04_NATIVE_JWT',
        'I_ACCEPT_DISPOSABLE_D04B_NATIVE_JWT_ONLY', 'Process')
    [Environment]::SetEnvironmentVariable('BM_PAYOS_D04_NATIVE_JWT_REVIEWER_PASSWORD',
        $reviewerPassword, 'Process')
    [Environment]::SetEnvironmentVariable('BM_PAYOS_D04_NATIVE_JWT_OWNER_PASSWORD',
        $ownerPassword, 'Process')
    [Environment]::SetEnvironmentVariable('BM_PAYOS_D04_NATIVE_JWT_OUTSIDER_PASSWORD',
        $outsiderPassword, 'Process')
    Invoke-Logged '06-native-jwt-provision-fixture' 'dotnet' @(
        'test', $pgTests, '-c', 'Release', '--no-build',
        '--filter', 'FullyQualifiedName~D04BNativeJwtProvision_Tests',
        '--logger', 'trx;LogFileName=payos-native-jwt-provision.trx',
        '--results-directory', $logRoot, '--verbosity', 'normal')
    Assert-Trx 'payos-native-jwt-provision.trx' 1
    foreach ($name in @('BM_PAYOS_D04_NATIVE_JWT_REVIEWER_PASSWORD',
        'BM_PAYOS_D04_NATIVE_JWT_OWNER_PASSWORD',
        'BM_PAYOS_D04_NATIVE_JWT_OUTSIDER_PASSWORD')) {
        [Environment]::SetEnvironmentVariable($name, $null, 'Process')
    }

    # Ephemeral self-signed signing certificate for the non-Development Host.
    # Lives only in the isolated worktree Host content root.
    $certPassPhrase = [Guid]::NewGuid().ToString('N')
    $pfxPath = Join-Path $hostDir 'openiddict.pfx'
    Invoke-Logged '07-mint-openiddict-pfx' 'dotnet' @(
        'dev-certs', 'https', '-v', '-ep', $pfxPath, '-p', $certPassPhrase, '--trust')

    # Disposable archive root outside both the repo and the worktree.
    $archiveRoot = Join-Path $logRoot 'd04b-archive-root'
    New-Item -ItemType Directory -Path $archiveRoot -Force | Out-Null
    $keyBytes = New-Object byte[] 32
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($keyBytes) } finally { $rng.Dispose() }
    $archiveKey = [Convert]::ToBase64String($keyBytes)
    [Array]::Clear($keyBytes, 0, $keyBytes.Length)

    # Launch the real Host in Staging with the full D04-B gate configuration.
    $identityCs = 'Host=127.0.0.1;Port=15432;Database=payos_d04_identity_test;Username=postgres;Password=disposable-only'
    $legacyCs = 'Host=127.0.0.1;Port=15432;Database=payos_d04_legacy_test;Username=postgres;Password=disposable-only'
    $stagingCs = 'Host=127.0.0.1;Port=15432;Database=payos_ef_migration_test;Username=postgres;Password=disposable-only'
    [Environment]::SetEnvironmentVariable('ASPNETCORE_ENVIRONMENT', 'Staging', 'Process')
    [Environment]::SetEnvironmentVariable('DOTNET_ENVIRONMENT', 'Staging', 'Process')
    [Environment]::SetEnvironmentVariable('ASPNETCORE_URLS', 'https://localhost:44311', 'Process')
    [Environment]::SetEnvironmentVariable('ConnectionStrings__Default', $identityCs, 'Process')
    [Environment]::SetEnvironmentVariable('ConnectionStrings__BuildingManagement', $legacyCs, 'Process')
    [Environment]::SetEnvironmentVariable('ConnectionStrings__PayOsD04BStaging', $stagingCs, 'Process')
    [Environment]::SetEnvironmentVariable('BuildingManagement__PayOsStaging__Enabled', 'false', 'Process')
    [Environment]::SetEnvironmentVariable('BuildingManagement__PayOsD04B__Enabled', 'true', 'Process')
    [Environment]::SetEnvironmentVariable('BuildingManagement__PayOsD04B__Activation',
        'I_ACCEPT_DISPOSABLE_D04B_DUAL_AUTH_ONLY', 'Process')
    [Environment]::SetEnvironmentVariable('BuildingManagement__PayOsD04B__DisposableArchive__Enabled', 'true', 'Process')
    [Environment]::SetEnvironmentVariable('BuildingManagement__PayOsD04B__DisposableArchive__Root', $archiveRoot, 'Process')
    [Environment]::SetEnvironmentVariable('BuildingManagement__PayOsD04B__DisposableArchive__KeyBase64', $archiveKey, 'Process')
    [Environment]::SetEnvironmentVariable('AuthServer__CertificatePassPhrase', $certPassPhrase, 'Process')

    Write-Host '=== 08-start-real-staging-host ===' -ForegroundColor Cyan
    $hostLog = Join-Path $logRoot '08-host.log'
    Push-Location $hostDir
    $hostStarted = $false
    try {
        # dotnet run reuses the Release build from step 03 (--no-build -c Release).
        $hostProcess = Start-Process -FilePath 'dotnet' `
            -ArgumentList @('run','--no-build','--no-launch-profile','-c','Release') `
            -WorkingDirectory $hostDir -PassThru `
            -RedirectStandardOutput $hostLog -RedirectStandardError (Join-Path $logRoot '08-host-stderr.log')
        $ready = $false
        for ($i = 0; $i -lt 120; $i++) {
            if ($hostProcess.HasExited) {
                throw "Host process exited early with code $($hostProcess.ExitCode). See 08-host.log / 08-host-stderr.log"
            }
            if (Test-HostHealthy) { $ready = $true; break }
            Start-Sleep -Milliseconds 1000
        }
        if (-not $ready) {
            throw 'Host did not become healthy on https://localhost:44311/health-status within 120s. See 08-host.log'
        }
        $hostStarted = $true
    }
    finally {
        if (-not $hostStarted -and $hostProcess -and -not $hostProcess.HasExited) {
            Stop-Process -Id $hostProcess.Id -Force -ErrorAction SilentlyContinue
        }
        Pop-Location
    }
    Write-Host 'PASS: real Host healthy on https://localhost:44311/health-status.' -ForegroundColor Green

    # REAL password-grant tokens. Kept in script memory only.
    $reviewerToken = Get-NativeToken 'reviewer-uat' $reviewerPassword
    $ownerToken = Get-NativeToken 'owner-uat' $ownerPassword
    $outsiderToken = Get-NativeToken 'outsider-uat' $outsiderPassword
    Write-Host 'PASS: three real OpenIddict password-grant tokens obtained (never echoed).' -ForegroundColor Green

    $base = 'https://localhost:44311/api/payments/payos/d04/staging'
    $intentA = '0d04a000-0000-4000-8000-000000000007'
    $intentA2 = '0d04a000-0000-4000-8000-00000000000c'
    $intentB = '0d04b000-0000-4000-8000-000000000007'
    $reviewerId = '0d04c000-0000-4000-8000-000000000001'
    $ownerId = '0d04c000-0000-4000-8000-000000000002'

    # ---------- NEGATIVE MATRIX ----------
    $boundary = 'd04nativeboundary1234567890'
    $png = New-ReviewMultipart 'D04NATIVEJWT-A-REF' 175000 $boundary

    # N1: anonymous -> 401, or 302 redirect-to-login challenge (both deny
    # access before any controller code runs; the Host's default challenge
    # scheme is the interactive login redirect, not a bare 401).
    $r = Invoke-MatrixRequest 'N1-anonymous-review' 'POST' "$base/review/$intentA" $png 'multipart/form-data; boundary=d04nativeboundary1234567890' $null
    if ($r.Status -ne 401 -and $r.Status -ne 302) {
        throw "MATRIX FAILURE: N1-anonymous-review expected HTTP 401/302, got $($r.Status)."
    }
    Write-Host 'MATRIX PASS: N1-anonymous-review denied (HTTP 401 or 302 login challenge).' -ForegroundColor Green

    # N2: forged bearer -> 401, or 302 redirect-to-login challenge (the
    # bearer validation middleware rejects the forged token before any
    # controller code runs; the default challenge is a login redirect).
    $r = Invoke-MatrixRequest 'N2-forged-bearer-review' 'POST' "$base/review/$intentA" $png 'multipart/form-data; boundary=d04nativeboundary1234567890' 'totally.forged.jwt.value'
    if ($r.Status -ne 401 -and $r.Status -ne 302) {
        throw "MATRIX FAILURE: N2-forged-bearer-review expected HTTP 401/302, got $($r.Status)."
    }
    Write-Host 'MATRIX PASS: N2-forged-bearer denied (HTTP 401 or 302 login challenge).' -ForegroundColor Green

    # N3: outsider (no permissions) -> 403
    $r = Invoke-MatrixRequest 'N3-outsider-review' 'POST' "$base/review/$intentA" $png 'multipart/form-data; boundary=d04nativeboundary1234567890' $outsiderToken
    Assert-Status 'N3-outsider-review' $r 403

    # N4: owner-as-reviewer (no ReviewBankEvidence permission) -> 403
    $r = Invoke-MatrixRequest 'N4-owner-as-reviewer' 'POST' "$base/review/$intentA" $png 'multipart/form-data; boundary=d04nativeboundary1234567890' $ownerToken
    Assert-Status 'N4-owner-as-reviewer' $r 403

    # N5: reviewer-as-owner (no ApproveD04Posting permission) -> 403 on approve
    # (unknown review id: permission check fires before validation).
    $r = Invoke-MatrixRequest 'N5-reviewer-as-owner-approve' 'POST' "$base/approve/00000000-0000-0000-0000-0000000000aa" $null $null $reviewerToken
    Assert-Status 'N5-reviewer-as-owner-approve' $r 403

    # N6: reviewer-as-owner on post -> 403
    $r = Invoke-MatrixRequest 'N6-reviewer-as-owner-post' 'POST' "$base/post/$intentA" $null $null $reviewerToken
    Assert-Status 'N6-reviewer-as-owner-post' $r 403

    # N7: cross-building review (IntentB grants nobody) -> 403
    $pngB = New-ReviewMultipart 'D04NATIVEJWT-B-REF' 185000 $boundary
    $r = Invoke-MatrixRequest 'N7-cross-building-review' 'POST' "$base/review/$intentB" $pngB 'multipart/form-data; boundary=d04nativeboundary1234567890' $reviewerToken
    Assert-Status 'N7-cross-building-review' $r 403

    # ---------- POSITIVE LANE (IntentA) ----------
    # P1: reviewer review -> 201 (ApiResponseFilter) + reviewId
    $r = Invoke-MatrixRequest 'P1-reviewer-review-intentA' 'POST' "$base/review/$intentA" $png 'multipart/form-data; boundary=d04nativeboundary1234567890' $reviewerToken
    Assert-Status 'P1-reviewer-review-intentA' $r 201
    Assert-BodyContains 'P1-reviewer-review-intentA' $r 'reviewId'
    $reviewId = [regex]::Match($r.Body, '"reviewId":"([^"]+)"').Groups[1].Value
    if ([string]::IsNullOrWhiteSpace($reviewId)) { throw 'P1: no reviewId extracted.' }

    # P2: idempotent re-review (same reviewer, same document content) -> same reviewId
    $r = Invoke-MatrixRequest 'P2-idempotent-re-review' 'POST' "$base/review/$intentA" $png 'multipart/form-data; boundary=d04nativeboundary1234567890' $reviewerToken
    Assert-Status 'P2-idempotent-re-review' $r 201
    $reviewId2 = [regex]::Match($r.Body, '"reviewId":"([^"]+)"').Groups[1].Value
    if ($reviewId2 -cne $reviewId) {
        throw "MATRIX FAILURE: idempotent re-review returned different reviewId ($reviewId2 vs $reviewId)."
    }
    Write-Host 'MATRIX PASS: idempotent re-review same reviewId.' -ForegroundColor Green

    # P3: owner approve -> 201 + approvalId
    $r = Invoke-MatrixRequest 'P3-owner-approve' 'POST' "$base/approve/$reviewId" $null $null $ownerToken
    Assert-Status 'P3-owner-approve' $r 201
    Assert-BodyContains 'P3-owner-approve' $r 'approvalId'
    $approvalId = [regex]::Match($r.Body, '"approvalId":"([^"]+)"').Groups[1].Value
    if ([string]::IsNullOrWhiteSpace($approvalId)) { throw 'P3: no approvalId extracted.' }

    # P4: owner post -> 201 + receiptId (ApiResponseFilter forces POST success to 201;
    # the controller returns Ok() here but the global filter rewrites it).
    $r = Invoke-MatrixRequest 'P4-owner-post-intentA' 'POST' "$base/post/$intentA" $null $null $ownerToken
    Assert-Status 'P4-owner-post-intentA' $r 201
    Assert-BodyContains 'P4-owner-post-intentA' $r 'receiptId'
    $receiptId = [regex]::Match($r.Body, '"receiptId":"([^"]+)"').Groups[1].Value
    if ([string]::IsNullOrWhiteSpace($receiptId)) { throw 'P4: no receiptId extracted.' }
    Assert-BodyContains 'P4-owner-post-intentA' $r 'LegacyCommittedAndStagingAcknowledged'

    # P5: replay post -> same receiptId, no new money
    $r = Invoke-MatrixRequest 'P5-replay-post-intentA' 'POST' "$base/post/$intentA" $null $null $ownerToken
    Assert-Status 'P5-replay-post-intentA' $r 201
    $receiptId2 = [regex]::Match($r.Body, '"receiptId":"([^"]+)"').Groups[1].Value
    if ($receiptId2 -cne $receiptId) {
        throw "MATRIX FAILURE: post replay returned different receiptId ($receiptId2 vs $receiptId)."
    }
    Write-Host 'MATRIX PASS: post replay same receiptId (no double money).' -ForegroundColor Green

    # ---------- POST-COMMIT NEGATIVES (needs a committed positive lane first) ----------
    # N8: self-approval — the reviewer now tries to approve his own review
    # (owner permission was already proven missing for reviewer in N5/N6;
    # this asserts the distinct-actor invariant directly).
    $r = Invoke-MatrixRequest 'N8-reviewer-self-approve' 'POST' "$base/approve/$reviewId" $null $null $reviewerToken
    Assert-Status 'N8-reviewer-self-approve' $r 403

    # ---------- MID-RUN GRANT REVOCATION (IntentA2 second lane) ----------
    # Review + approve first, then revoke Owner's ApproveD04Posting grant
    # via psql in the identity DB and prove post -> 403.
    $r = Invoke-MatrixRequest 'R1-reviewer-review-intentA2' 'POST' "$base/review/$intentA2" $png 'multipart/form-data; boundary=d04nativeboundary1234567890' $reviewerToken
    Assert-Status 'R1-reviewer-review-intentA2' $r 201
    $reviewIdA2 = [regex]::Match($r.Body, '"reviewId":"([^"]+)"').Groups[1].Value

    # ---------- ARCHIVE TAMPER (must precede R3: with the owner grant
    # revoked, approve would 403 for the wrong reason and hide the tamper
    # check; also tamper the EXACT vault file bound to reviewIdA2, read
    # from the committed SQL review row, not an arbitrary *.d04vault). ----------
    $reviewRefA2 = (Invoke-Scalar 'payos_d04_legacy_test' @"
SELECT ""ExternalBankProofReference""::text
  FROM ""BuildingManagementPayOsD04BankReviews""
 WHERE ""Id""='$reviewIdA2';
"@).Trim()
    if ([string]::IsNullOrWhiteSpace($reviewRefA2)) {
        throw 'T1: no ExternalBankProofReference for reviewIdA2.'
    }
    $vaultFile = Get-Item -LiteralPath (Join-Path $archiveRoot ($reviewRefA2 + '.d04vault'))
    $backup = [System.IO.File]::ReadAllBytes($vaultFile.FullName)
    try {
        $tampered = [byte[]]$backup.Clone()
        $tampered[$tampered.Length - 1] = $tampered[$tampered.Length - 1] -bxor 1
        [System.IO.File]::WriteAllBytes($vaultFile.FullName, $tampered)

        $r = Invoke-MatrixRequest 'T1-approve-after-tamper' 'POST' "$base/approve/$reviewIdA2" $null $null $ownerToken
        Assert-Status 'T1-approve-after-tamper' $r 403
        Write-Host 'MATRIX PASS: tampered archive blocks approval (fail-closed).' -ForegroundColor Green
    }
    finally {
        [System.IO.File]::WriteAllBytes($vaultFile.FullName, $backup)
    }

    $r = Invoke-MatrixRequest 'R2-owner-approve-intentA2' 'POST' "$base/approve/$reviewIdA2" $null $null $ownerToken
    Assert-Status 'R2-owner-approve-intentA2' $r 201

    # Revoke the owner's permission grant row.
    Invoke-SqlFile 'payos_d04_identity_test' @"
DELETE FROM ""AbpPermissionGrants""
 WHERE ""ProviderName""='U' AND ""ProviderKey""='$ownerId'
   AND ""Name""='BuildingManagement.Payments.ApproveD04Posting';
"@ 'R3-revoke-owner-grant'

    $r = Invoke-MatrixRequest 'R4-owner-post-intentA2-after-revoke' 'POST' "$base/post/$intentA2" $null $null $ownerToken
    Assert-Status 'R4-owner-post-intentA2-after-revoke' $r 403
    Write-Host 'MATRIX PASS: revoked owner grant blocks posting mid-run.' -ForegroundColor Green

    # ---------- DURABLE STATE ASSERTIONS (psql) ----------
    Write-Host '=== Durable state assertions ===' -ForegroundColor Cyan
    $legacyCheck = @"
SELECT
  (SELECT ""Status"" FROM ""BuildingManagementPayments"" WHERE ""Id""='0d04a000-0000-4000-8000-000000000006'),
  (SELECT count(*) FROM ""DepositSettlements"" WHERE ""PaymentId""='0d04a000-0000-4000-8000-000000000006' AND ""Type""=3 AND ""Status""=2),
  (SELECT count(*) FROM ""BuildingManagementPayOsD04ReceiptJournal"" WHERE ""PayOsIntentId""='$intentA'),
  (SELECT count(*) FROM ""BuildingManagementPayOsD04Outbox"" WHERE ""ReceiptId""='$receiptId' AND ""AcknowledgedAtUtc"" IS NOT NULL);
"@
    $legacyResult = (Invoke-Scalar 'payos_d04_legacy_test' $legacyCheck)
    Write-Host "LEGACY_STATE: $legacyResult"
    $parts = @($legacyResult -split '\|')
    if ($parts.Count -ne 4 -or $parts[0].Trim() -ne '2' -or
        $parts[1].Trim() -ne '1' -or $parts[2].Trim() -ne '1' -or
        $parts[3].Trim() -ne '1') {
        throw "DURABLE STATE FAILURE (legacy): expected 2|1|1|1, got '$legacyResult'"
    }

    $stagingCheck = @"
SELECT
  (SELECT ""Status"" FROM ""BuildingManagementPayOsPaymentIntents"" WHERE ""Id""='$intentA'),
  (SELECT ""Status"" FROM ""BuildingManagementPayOsWebhookInbox"" WHERE ""Id""='0d04a000-0000-4000-8000-000000000008'),
  (SELECT ""Status"" FROM ""BuildingManagementPayOsPaymentIntents"" WHERE ""Id""='$intentA2');
"@
    $stagingResult = (Invoke-Scalar 'payos_ef_migration_test' $stagingCheck)
    Write-Host "STAGING_STATE: $stagingResult"
    $sparts = @($stagingResult -split '\|')
    if ($sparts[0].Trim() -ne '3' -or $sparts[1].Trim() -ne '1') {
        throw "DURABLE STATE FAILURE (staging): $stagingResult"
    }
    Write-Host 'PASS: durable legacy/staging state verified via psql.' -ForegroundColor Green

    # ---------- CLEANUP ----------
    Write-Host '=== PASS: D04-B NATIVE JWT MATRIX COMPLETE ===' -ForegroundColor Green
    Write-Host "SOURCE_COMMIT=$sha"
    Write-Host "LOG_DIRECTORY=$logRoot"
    Write-Host 'No real payOS payment, no real bank transfer; 15433/5432 untouched.'
}
catch {
    Write-Host "=== D04 NATIVE JWT MATRIX FAILED: $($_.Exception.Message) ===" -ForegroundColor Red
    if ($logRoot) {
        Write-Host "LOG_DIRECTORY=$logRoot" -ForegroundColor Yellow
        if ($containerId) {
            $diagnosticPath = Join-Path $logRoot 'docker-postgres-tail.log'
            $previous = $ErrorActionPreference
            try {
                $ErrorActionPreference = 'Continue'
                & docker logs --tail 150 $containerId *>&1 |
                    ForEach-Object { $_.ToString() } |
                    Set-Content -LiteralPath $diagnosticPath
            }
            catch { }
            finally { $ErrorActionPreference = $previous }
        }
    }
    throw
}
finally {
    $reviewerToken = ''
    $ownerToken = ''
    $outsiderToken = ''
    foreach ($key in $envNames) {
        [Environment]::SetEnvironmentVariable($key, $oldEnv[$key], 'Process')
    }
    if ($hostProcess -and -not $hostProcess.HasExited) {
        Write-Host 'Stopping this run staging Host only ...' -ForegroundColor Cyan
        try { Stop-Process -Id $hostProcess.Id -Force -ErrorAction SilentlyContinue } catch { }
        $hostProcess.WaitForExit(10000) | Out-Null
    }
    if ($containerId) {
        Write-Host 'Removing only this run disposable PostgreSQL container ...'
        $previous = $ErrorActionPreference
        try {
            $ErrorActionPreference = 'Continue'
            & docker rm -f $containerId *> $null
        }
        finally { $ErrorActionPreference = $previous }
    }
    if ($pfxPath -and (Test-Path -LiteralPath $pfxPath)) {
        Remove-Item -LiteralPath $pfxPath -Force -ErrorAction SilentlyContinue
    }
    if ($archiveRoot -and (Test-Path -LiteralPath $archiveRoot)) {
        # Contains only synthetic disposable evidence; lives under this run log root.
        Remove-Item -LiteralPath $archiveRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
    if ($enteredIsolated) { Pop-Location }
    if ($isolatedCreated) {
        # Preserve the Host's Serilog file log (it lived under the worktree
        # content root and would otherwise be deleted with it) so failures
        # can be diagnosed after the run.
        if ($hostDir) {
            $serilogSource = Join-Path $hostDir 'Logs\logs.txt'
            if (Test-Path -LiteralPath $serilogSource) {
                Copy-Item -LiteralPath $serilogSource -Destination (Join-Path $logRoot 'serilog-logs.txt') -Force -ErrorAction SilentlyContinue
            }
        }
        Write-Host 'Removing only this run temporary Git worktree...' -ForegroundColor Cyan
        $previous = $ErrorActionPreference
        try {
            $ErrorActionPreference = 'Continue'
            & git -C $originalRoot worktree remove --force -- $isolatedRoot *> $null
            if ($LASTEXITCODE -ne 0) {
                Write-Warning 'Temporary worktree cleanup failed; inspect only the reported log directory.'
            }
        }
        catch { }
        finally { $ErrorActionPreference = $previous }
    }
    Pop-Location
}
