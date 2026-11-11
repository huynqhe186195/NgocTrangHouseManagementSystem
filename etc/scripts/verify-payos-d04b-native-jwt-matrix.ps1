[CmdletBinding()]
param(
    # Explicit pre-push verification: allow running at a local-only commit.
    # The run log records a warning; evidence must be re-confirmed at the
    # pushed SHA before any DONE marking.
    [switch]$AllowUnpushedCommit
)

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
    'BuildingManagement__PayOsD4B__DisposableArchive__PreviousKeyBase64',
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
        # Suppress the cp call's return value: it would otherwise join the
        # psql result in this function's output pipeline (array, not string).
        $null = Invoke-Capture 'docker' @(
            'cp', $sqlFile, "$($containerId):/tmp/scalar.sql")
        return [string](Invoke-Capture 'docker' @(
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
    if ($sha -ne $remoteSha -and -not $AllowUnpushedCommit) {
        throw "Local SHA $sha differs from origin $remoteSha. Push first, or pass -AllowUnpushedCommit for an explicit pre-push verification run (evidence must be re-verified after push)."
    }
    if ($sha -ne $remoteSha) {
        Write-Warning "PRE-PUSH VERIFICATION: local SHA $sha is NOT on origin yet. Re-run after pushing to confirm at the pushed SHA."
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

    # P1.3 step 06c: create the restricted HOST runtime roles on the legacy
    # and staging databases (superuser provisioning). The Host below then
    # runs the whole native JWT matrix WITHOUT the disposable superuser on
    # those two slots; the identity slot stays superuser for the ABP /
    # OpenIddict stack, and migrations/fixture already ran as superuser on
    # separate connections. Grants match the canonical Host D04-B SQL
    # (review + approval evidence INSERT included, by design distinct from
    # the P1.2 d04_money_writer_* rehearsal roles whose gate denies those).
    Invoke-SqlFile 'payos_d04_legacy_test' @'
DO $d04hr$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_roles
                 WHERE rolname='d04_d04b_host_legacy_test') THEN
    CREATE ROLE d04_d04b_host_legacy_test LOGIN
      PASSWORD 'disposable-only' NOSUPERUSER NOCREATEDB
      NOCREATEROLE NOREPLICATION NOINHERIT;
  END IF;
END
$d04hr$;
GRANT CONNECT ON DATABASE payos_d04_legacy_test TO d04_d04b_host_legacy_test;
GRANT USAGE ON SCHEMA public TO d04_d04b_host_legacy_test;
-- Read scope: ALL tables in the legacy schema. The integrity triggers on
-- the writable tables (BM_GuardClosedContractSource, BM_Freeze*, the
-- D04 validate trigger...) legitimately query cross-domain tables under
-- the INVOKING role privileges (no SECURITY DEFINER), so an enumerated
-- read list cannot work. Reading is not the privilege boundary here:
-- INSERT/UPDATE below stay narrow and DDL/role-creation is denied.
GRANT SELECT ON ALL TABLES IN SCHEMA public
TO d04_d04b_host_legacy_test;
GRANT INSERT ON
  public."BuildingManagementPayOsD04BankReviews",
  public."BuildingManagementPayOsD04OwnerApprovals",
  public."BuildingManagementDepositSettlements",
  public."BuildingManagementPayOsD04ReceiptJournal",
  public."BuildingManagementPayOsD04Outbox"
TO d04_d04b_host_legacy_test;
GRANT UPDATE ON
  public."BuildingManagementPayments",
  public."BuildingManagementDepositSettlements"
TO d04_d04b_host_legacy_test;
-- SELECT FOR SHARE/FOR UPDATE requires the UPDATE privilege in PG16
-- (probed empirically). The Host locks BankReviews/OwnerApprovals
-- (review), OperatorBuildingGrants/Buildings (DualAuthority), and
-- BM_PayOsD04ValidateReceipt FOR SHAREs Contracts/Rooms/Floors during
-- journal INSERT. Actual mutation of immutable evidence stays blocked
-- by the 23514 reject-triggers.
GRANT UPDATE ON
  public."BuildingManagementPayOsD04BankReviews",
  public."BuildingManagementPayOsD04OwnerApprovals",
  public."BuildingManagementOperatorBuildingGrants",
  public."Buildings",
  public."BuildingManagementContracts",
  public."BuildingManagementRooms",
  public."BuildingManagementFloors"
TO d04_d04b_host_legacy_test;
GRANT UPDATE ("AcknowledgedAtUtc","AttemptCount")
  ON public."BuildingManagementPayOsD04Outbox"
  TO d04_d04b_host_legacy_test;
REVOKE ALL ON DATABASE payos_d04_identity_test FROM d04_d04b_host_legacy_test;
REVOKE ALL ON DATABASE payos_ef_migration_test FROM d04_d04b_host_legacy_test;
'@ '06c-create-host-legacy-runtime-role'

    Invoke-SqlFile 'payos_ef_migration_test' @'
DO $d04hs$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_roles
                 WHERE rolname='d04_d04b_host_staging_test') THEN
    CREATE ROLE d04_d04b_host_staging_test LOGIN
      PASSWORD 'disposable-only' NOSUPERUSER NOCREATEDB
      NOCREATEROLE NOREPLICATION NOINHERIT;
  END IF;
END
$d04hs$;
GRANT CONNECT ON DATABASE payos_ef_migration_test TO d04_d04b_host_staging_test;
GRANT USAGE ON SCHEMA public TO d04_d04b_host_staging_test;
GRANT SELECT ON
  public."BuildingManagementPayOsPaymentIntents",
  public."BuildingManagementPayOsWebhookInbox"
TO d04_d04b_host_staging_test;
GRANT UPDATE ("Status","ConcurrencyStamp","ExtraProperties")
  ON public."BuildingManagementPayOsPaymentIntents"
  TO d04_d04b_host_staging_test;
REVOKE ALL ON DATABASE payos_d04_legacy_test FROM d04_d04b_host_staging_test;
REVOKE ALL ON DATABASE payos_d04_identity_test FROM d04_d04b_host_staging_test;
'@ '06c-create-host-staging-runtime-role'

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

    # P1.3: previous key generation for rotation rehearsal. The run has no
    # pre-rotation files, so the ring proves only that the Host accepts the
    # optional PreviousKeyBase64 configuration and stays fail-closed on a
    # missing current key. (Rotation behavior itself is covered by
    # D04B_HOSTARCHIVE08.)
    $prevKeyBytes = New-Object byte[] 32
    $rng2 = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng2.GetBytes($prevKeyBytes) } finally { $rng2.Dispose() }
    $archivePreviousKey = [Convert]::ToBase64String($prevKeyBytes)
    [Array]::Clear($prevKeyBytes, 0, $prevKeyBytes.Length)

    # Launch the real Host in Staging with the full D04-B gate configuration.
    # P1.3: legacy + staging slots run as the restricted HOST runtime roles
    # (created in step 06c); only the identity slot stays superuser because
    # the whole ABP/OpenIddict stack (sign-in, tokens, permission checks)
    # operates on it in this disposable lane.
    $identityCs = 'Host=127.0.0.1;Port=15432;Database=payos_d04_identity_test;Username=postgres;Password=disposable-only'
    $legacyCs = 'Host=127.0.0.1;Port=15432;Database=payos_d04_legacy_test;Username=d04_d04b_host_legacy_test;Password=disposable-only'
    $stagingCs = 'Host=127.0.0.1;Port=15432;Database=payos_ef_migration_test;Username=d04_d04b_host_staging_test;Password=disposable-only'
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
    [Environment]::SetEnvironmentVariable('BuildingManagement__PayOsD4B__DisposableArchive__PreviousKeyBase64', $archivePreviousKey, 'Process')
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
    # IntentA2 carries its own fixture reference (D04NATIVEJWT-A2-REF):
    # the A-lane form would be denied as provider-reference mismatch.
    $pngA2 = New-ReviewMultipart 'D04NATIVEJWT-A2-REF' 175000 $boundary
    $r = Invoke-MatrixRequest 'R1-reviewer-review-intentA2' 'POST' "$base/review/$intentA2" $pngA2 'multipart/form-data; boundary=d04nativeboundary1234567890' $reviewerToken
    Assert-Status 'R1-reviewer-review-intentA2' $r 201
    $reviewIdA2 = [regex]::Match($r.Body, '"reviewId":"([^"]+)"').Groups[1].Value

    # ---------- ARCHIVE TAMPER (must precede R3: with the owner grant
    # revoked, approve would 403 for the wrong reason and hide the tamper
    # check; also tamper the EXACT vault file bound to reviewIdA2, read
    # from the committed SQL review row, not an arbitrary *.d04vault). ----------
    # PowerShell here-strings keep " literal (no "" collapsing), so these
    # identifiers must be written with single quotes; $vars still expand.
    $reviewRefA2 = (Invoke-Scalar 'payos_d04_legacy_test' @"
SELECT "ExternalBankProofReference"::text
  FROM "BuildingManagementPayOsD04BankReviews"
 WHERE "Id"='$reviewIdA2';
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
DELETE FROM "AbpPermissionGrants"
 WHERE "ProviderName"='U' AND "ProviderKey"='$ownerId'
   AND "Name"='BuildingManagement.Payments.ApproveD04Posting';
"@ 'R3-revoke-owner-grant'

    $r = Invoke-MatrixRequest 'R4-owner-post-intentA2-after-revoke' 'POST' "$base/post/$intentA2" $null $null $ownerToken
    Assert-Status 'R4-owner-post-intentA2-after-revoke' $r 403
    Write-Host 'MATRIX PASS: revoked owner grant blocks posting mid-run.' -ForegroundColor Green

    # R5: re-grant the owner's ApproveD04Posting (the crash lane below needs
    # a full review -> approve -> post chain; the grant row is restored the
    # same way R3 revoked it — direct psql on the disposable identity DB).
    Invoke-SqlFile 'payos_d04_identity_test' @"
INSERT INTO "AbpPermissionGrants" ("Id","TenantId","Name","ProviderName","ProviderKey")
VALUES (gen_random_uuid(),NULL,'BuildingManagement.Payments.ApproveD04Posting',
        'U','$ownerId')
ON CONFLICT DO NOTHING;
"@ 'R5-re-grant-owner-grant'

    # ---------- P1.4 CRASH/RECOVERY LANE (IntentB, Building B) ----------
    # N7 above already proved Building B grants nobody. The crash lane now
    # grants both actors to Building B mid-run (the same psql technique R3
    # uses to revoke), runs the full review -> approve -> post chain, and
    # kills the Host exactly BETWEEN the legacy financial commit and the
    # staging ACK. Two independent crash mechanisms are combined:
    #   C1:  a PostgreSQL trigger on the staging intent UPDATE 2->3
    #        (the ACK's MarkPosted) terminates the ACK backend, so the
    #        coordinator returns the retriable AckPending outcome while
    #        Payment+settlement+journal+outbox are already durable.
    #   C1b: the whole Host process tree is force-killed AFTER that
    #        response, leaving a genuinely dead process with a committed
    #        receipt and an unacknowledged outbox.
    # C2 restarts the real Host, replays the SAME intent and proves the
    # replay acknowledges the SAME receipt with no second financial row.
    # C3 replays once more (duplicate) and proves counts never grow.
    Invoke-SqlFile 'payos_d04_legacy_test' @'
INSERT INTO "BuildingManagementOperatorBuildingGrants"
  ("Id","OperatorUserId","BuildingId","GrantedByUserId",
   "GrantedAtUtc","RevisionNumber","ExtraProperties","ConcurrencyStamp")
VALUES (gen_random_uuid(),'0d04c000-0000-4000-8000-000000000001',
          '0d04b000-0000-4000-8000-000000000001',
          '0d04c000-0000-4000-8000-000000000002',
        NOW() - interval '10 minutes',1,'{}',md5(random()::text)),
       (gen_random_uuid(),'0d04c000-0000-4000-8000-000000000002',
          '0d04b000-0000-4000-8000-000000000001',
          '0d04c000-0000-4000-8000-000000000002',
        NOW() - interval '10 minutes',1,'{}',md5(random()::text));
'@ 'C0-grant-actors-building-b'

    $r = Invoke-MatrixRequest 'C1-reviewer-review-intentB' 'POST' "$base/review/$intentB" $pngB 'multipart/form-data; boundary=d04nativeboundary1234567890' $reviewerToken
    Assert-Status 'C1-reviewer-review-intentB' $r 201
    $reviewIdB = [regex]::Match($r.Body, '"reviewId":"([^"]+)"').Groups[1].Value
    if ([string]::IsNullOrWhiteSpace($reviewIdB)) { throw 'C1: no reviewId extracted for IntentB.' }

    $r = Invoke-MatrixRequest 'C1-owner-approve-intentB' 'POST' "$base/approve/$reviewIdB" $null $null $ownerToken
    Assert-Status 'C1-owner-approve-intentB' $r 201

    # Arm the precise crash point: the ACK's MarkPosted UPDATE (2 -> 3) for
    # IntentB terminates its own backend BEFORE the staging commit, so the
    # coordinator must classify the already-committed receipt as AckPending.
    # Single-quoted here-string: SQL dollar-quoting must NOT be expanded.
    Invoke-SqlFile 'payos_ef_migration_test' @'
CREATE OR REPLACE FUNCTION "BM_D04P14TerminateBeforeAck"()
RETURNS trigger LANGUAGE plpgsql AS $d04p14$
BEGIN
  PERFORM pg_terminate_backend(pg_backend_pid());
  RETURN NEW;
END;
$d04p14$;
DROP TRIGGER IF EXISTS "TR_D04P14CrashBeforeAck"
  ON "BuildingManagementPayOsPaymentIntents";
CREATE TRIGGER "TR_D04P14CrashBeforeAck"
  BEFORE UPDATE ON "BuildingManagementPayOsPaymentIntents"
  FOR EACH ROW
  WHEN (OLD."Id" = '0d04b000-0000-4000-8000-000000000007'
        AND OLD."Status" = 2 AND NEW."Status" = 3)
  EXECUTE FUNCTION "BM_D04P14TerminateBeforeAck"();
'@ 'C1-arm-crash-trigger'

    $r = Invoke-MatrixRequest 'C1-owner-post-intentB-crash-before-ack' 'POST' "$base/post/$intentB" $null $null $ownerToken
    # Accepted(202) may be rewritten to 201 by the same global response
    # filter that forced P4's Ok() to 201; both are "success, ACK pending".
    if ($r.Status -ne 201 -and $r.Status -ne 202) {
        throw "MATRIX FAILURE: C1 post expected HTTP 201/202 (AckPending), got $($r.Status). See C1-owner-post-intentB-crash-before-ack-body.txt"
    }
    Assert-BodyContains 'C1-owner-post-intentB-crash-before-ack' $r 'LegacyCommittedAckPendingRetrySameIntent'
    $receiptIdB = [regex]::Match($r.Body, '"receiptId":"([^"]+)"').Groups[1].Value
    if ([string]::IsNullOrWhiteSpace($receiptIdB)) { throw 'C1: no receiptId extracted for IntentB.' }
    Write-Host 'MATRIX PASS: C1 legacy committed, ACK pending (retriable, no second financial row).' -ForegroundColor Green

    # Durable state at the crash point: the irreversible legacy rows exist,
    # but the outbox is NOT acknowledged and the staging intent is still
    # ConfirmedPendingPosting (MarkPosted never committed).
    $crashLegacy = @"
SELECT
  (SELECT "Status" FROM "BuildingManagementPayments" WHERE "Id"='0d04b000-0000-4000-8000-000000000006'),
  (SELECT count(*) FROM "BuildingManagementDepositSettlements" WHERE "PaymentId"='0d04b000-0000-4000-8000-000000000006' AND "Type"=3 AND "Status"=2),
  (SELECT count(*) FROM "BuildingManagementPayOsD04ReceiptJournal" WHERE "PayOsIntentId"='$intentB'),
  (SELECT count(*) FROM "BuildingManagementPayOsD04Outbox" WHERE "ReceiptId"='$receiptIdB' AND "AcknowledgedAtUtc" IS NOT NULL);
"@
    $crashLegacyResult = (Invoke-Scalar 'payos_d04_legacy_test' $crashLegacy)
    Write-Host "CRASH_LEGACY_STATE: $crashLegacyResult"
    $cparts = @($crashLegacyResult -split '\|')
    if ($cparts.Count -ne 4 -or $cparts[0].Trim() -ne '2' -or
        $cparts[1].Trim() -ne '1' -or $cparts[2].Trim() -ne '1' -or
        $cparts[3].Trim() -ne '0') {
        throw "CRASH STATE FAILURE (legacy): expected 2|1|1|0, got '$crashLegacyResult'"
    }
    $crashStaging = "SELECT `"Status`" FROM `"BuildingManagementPayOsPaymentIntents`" WHERE `"Id`"='$intentB';"
    $crashStagingStatus = (Invoke-Scalar 'payos_ef_migration_test' $crashStaging)
    if ($crashStagingStatus.Trim() -ne '2') {
        throw "CRASH STATE FAILURE (staging): intentB expected Status 2 (ConfirmedPendingPosting), got '$crashStagingStatus'"
    }
    Write-Host 'PASS: crash durable state = committed receipt, unacknowledged outbox, intent still ConfirmedPendingPosting.' -ForegroundColor Green

    # ---------- C1b: FORCE-KILL the real Host process tree ----------
    # The response above already proves the exact crash state; killing the
    # process now makes the restart a genuine cold recovery, not a retry
    # inside one process.
    Write-Host '=== C1b-kill-host-after-legacy-commit-before-ack ===' -ForegroundColor Cyan
    if ($hostProcess -and -not $hostProcess.HasExited) {
        $null = Invoke-Capture 'taskkill' @('/PID', "$($hostProcess.Id)", '/T', '/F')
    }
    if ($hostProcess) { $hostProcess.WaitForExit(15000) | Out-Null }
    $hostDown = $false
    for ($i = 0; $i -lt 30; $i++) {
        if (-not (Test-HostHealthy)) { $hostDown = $true; break }
        Start-Sleep -Milliseconds 500
    }
    if (-not $hostDown) { throw 'C1b: Host still healthy after forced process-tree kill.' }
    Write-Host 'PASS: real Host process tree killed (committed receipt, outbox unacknowledged).' -ForegroundColor Green

    # ---------- C2: RESTART the real Host, disarm the trigger, replay ----------
    Write-Host '=== C2-restart-real-staging-host ===' -ForegroundColor Cyan
    $hostLog2 = Join-Path $logRoot '08-host-restart.log'
    Push-Location $hostDir
    try {
        $hostProcess = Start-Process -FilePath 'dotnet' `
            -ArgumentList @('run','--no-build','--no-launch-profile','-c','Release') `
            -WorkingDirectory $hostDir -PassThru `
            -RedirectStandardOutput $hostLog2 -RedirectStandardError (Join-Path $logRoot '08-host-restart-stderr.log')
        $ready = $false
        for ($i = 0; $i -lt 120; $i++) {
            if ($hostProcess.HasExited) {
                throw "Restarted Host exited early with code $($hostProcess.ExitCode). See 08-host-restart.log / 08-host-restart-stderr.log"
            }
            if (Test-HostHealthy) { $ready = $true; break }
            Start-Sleep -Milliseconds 1000
        }
        if (-not $ready) {
            throw 'Restarted Host did not become healthy on https://localhost:44311/health-status within 120s. See 08-host-restart.log'
        }
    }
    finally { Pop-Location }
    Write-Host 'PASS: real Host restarted and healthy after the crash.' -ForegroundColor Green

    # The crash trigger is test scaffolding, not recovery behavior: disarm
    # it so the replay's MarkPosted can commit normally.
    Invoke-SqlFile 'payos_ef_migration_test' @'
DROP TRIGGER IF EXISTS "TR_D04P14CrashBeforeAck"
  ON "BuildingManagementPayOsPaymentIntents";
DROP FUNCTION IF EXISTS "BM_D04P14TerminateBeforeAck"();
'@ 'C2-disarm-crash-trigger'

    # Same bearer tokens remain valid: the Host re-reads the same identity
    # DB and re-verifies the same signing certificate from the worktree.
    $r = Invoke-MatrixRequest 'C2-replay-post-intentB-after-restart' 'POST' "$base/post/$intentB" $null $null $ownerToken
    Assert-Status 'C2-replay-post-intentB-after-restart' $r 201
    Assert-BodyContains 'C2-replay-post-intentB-after-restart' $r 'LegacyCommittedAndStagingAcknowledged'
    $receiptIdB2 = [regex]::Match($r.Body, '"receiptId":"([^"]+)"').Groups[1].Value
    if ($receiptIdB2 -cne $receiptIdB) {
        throw "MATRIX FAILURE: post-crash replay returned different receiptId ($receiptIdB2 vs $receiptIdB)."
    }
    Write-Host 'MATRIX PASS: C2 post-restart replay acknowledged the SAME receipt (no second financial row).' -ForegroundColor Green

    # ---------- C3: duplicate replay after recovery ----------
    $r = Invoke-MatrixRequest 'C3-duplicate-replay-post-intentB' 'POST' "$base/post/$intentB" $null $null $ownerToken
    Assert-Status 'C3-duplicate-replay-post-intentB' $r 201
    $receiptIdB3 = [regex]::Match($r.Body, '"receiptId":"([^"]+)"').Groups[1].Value
    if ($receiptIdB3 -cne $receiptIdB) {
        throw "MATRIX FAILURE: duplicate replay returned different receiptId ($receiptIdB3 vs $receiptIdB)."
    }
    Write-Host 'MATRIX PASS: C3 duplicate replay same receiptId (idempotent).' -ForegroundColor Green

    # Recovered durable state after C2/C3: exactly one journal, one
    # settlement, one acknowledged outbox row, intent Posted, and the
    # webhook inbox untouched (still exactly one reconciled row).
    $recoveredLegacy = @"
SELECT
  (SELECT "Status" FROM "BuildingManagementPayments" WHERE "Id"='0d04b000-0000-4000-8000-000000000006'),
  (SELECT count(*) FROM "BuildingManagementDepositSettlements" WHERE "PaymentId"='0d04b000-0000-4000-8000-000000000006' AND "Type"=3 AND "Status"=2),
  (SELECT count(*) FROM "BuildingManagementPayOsD04ReceiptJournal" WHERE "PayOsIntentId"='$intentB'),
  (SELECT count(*) FROM "BuildingManagementPayOsD04Outbox" WHERE "ReceiptId"='$receiptIdB' AND "AcknowledgedAtUtc" IS NOT NULL),
  (SELECT count(*) FROM "BuildingManagementPayOsD04Outbox" WHERE "ReceiptId"='$receiptIdB');
"@
    $recoveredLegacyResult = (Invoke-Scalar 'payos_d04_legacy_test' $recoveredLegacy)
    Write-Host "RECOVERED_LEGACY_STATE: $recoveredLegacyResult"
    $rparts = @($recoveredLegacyResult -split '\|')
    if ($rparts.Count -ne 5 -or $rparts[0].Trim() -ne '2' -or
        $rparts[1].Trim() -ne '1' -or $rparts[2].Trim() -ne '1' -or
        $rparts[3].Trim() -ne '1' -or $rparts[4].Trim() -ne '1') {
        throw "RECOVERY STATE FAILURE (legacy): expected 2|1|1|1|1 (payment|settlement|journal|ackedOutbox|outboxRows), got '$recoveredLegacyResult'"
    }
    $recoveredStaging = @"
SELECT
  (SELECT "Status" FROM "BuildingManagementPayOsPaymentIntents" WHERE "Id"='$intentB'),
  (SELECT "Status" FROM "BuildingManagementPayOsWebhookInbox" WHERE "Id"='0d04b000-0000-4000-8000-000000000008'),
  (SELECT count(*) FROM "BuildingManagementPayOsWebhookInbox" WHERE "PaymentIntentId"='$intentB');
"@
    $recoveredStagingResult = (Invoke-Scalar 'payos_ef_migration_test' $recoveredStaging)
    Write-Host "RECOVERED_STAGING_STATE: $recoveredStagingResult"
    $rsparts = @($recoveredStagingResult -split '\|')
    if ($rsparts.Count -ne 3 -or $rsparts[0].Trim() -ne '3' -or
        $rsparts[1].Trim() -ne '1' -or $rsparts[2].Trim() -ne '1') {
        throw "RECOVERY STATE FAILURE (staging): expected 3|1|1 (intentPosted|inboxStatus|inboxRows), got '$recoveredStagingResult'"
    }
    Write-Host 'PASS: crash/recovery durable state verified — no double Payment/Settlement/Receipt/Outbox; webhook inbox untouched.' -ForegroundColor Green

    # ---------- DURABLE STATE ASSERTIONS (psql) ----------
    Write-Host '=== Durable state assertions ===' -ForegroundColor Cyan
    $legacyCheck = @"
SELECT
  (SELECT "Status" FROM "BuildingManagementPayments" WHERE "Id"='0d04a000-0000-4000-8000-000000000006'),
  (SELECT count(*) FROM "BuildingManagementDepositSettlements" WHERE "PaymentId"='0d04a000-0000-4000-8000-000000000006' AND "Type"=3 AND "Status"=2),
  (SELECT count(*) FROM "BuildingManagementPayOsD04ReceiptJournal" WHERE "PayOsIntentId"='$intentA'),
  (SELECT count(*) FROM "BuildingManagementPayOsD04Outbox" WHERE "ReceiptId"='$receiptId' AND "AcknowledgedAtUtc" IS NOT NULL);
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
  (SELECT "Status" FROM "BuildingManagementPayOsPaymentIntents" WHERE "Id"='$intentA'),
  (SELECT "Status" FROM "BuildingManagementPayOsWebhookInbox" WHERE "Id"='0d04a000-0000-4000-8000-000000000008'),
  (SELECT "Status" FROM "BuildingManagementPayOsPaymentIntents" WHERE "Id"='$intentA2');
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
