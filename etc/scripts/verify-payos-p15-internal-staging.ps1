[CmdletBinding()]
param(
    # Explicit pre-push verification: allow running at a local-only commit.
    # The run log records a warning; evidence must be re-confirmed at the
    # pushed SHA before any DONE marking.
    [switch]$AllowUnpushedCommit
)

# P1.5 INTERNAL STAGING Host acceptance matrix (Owner decision 2026-11-11:
# in-process simulated provider, no real payOS traffic, no public callback).
# Real Host, real OpenIddict password-grant tokens, three disposable
# PostgreSQL 16 DBs on 127.0.0.1:15432 only. Never 15433 or 5432.
# QR checkout -> signed webhook -> operator reconcile, all loopback.
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
$hostDir = ''
$pfxPath = ''
$operatorToken = ''
$outsiderToken = ''
$script:checksumKey = ''

$allowedBranches = @('feat/local-safe-startup-20261109', 'main')
$envNames = @(
    'BM_PAYOS_EF_TEST_DB','BM_PAYOS_EF_TEST_PASSWORD','BM_PAYOS_EF_TEST_PORT',
    'BM_PAYOS_P15_INTERNAL',
    'BM_PAYOS_P15_INTERNAL_OPERATOR_PASSWORD',
    'BM_PAYOS_P15_INTERNAL_OUTSIDER_PASSWORD',
    'ConnectionStrings__Default','ConnectionStrings__BuildingManagement',
    'ConnectionStrings__PayOsStaging','ASPNETCORE_ENVIRONMENT',
    'DOTNET_ENVIRONMENT','ASPNETCORE_URLS','AuthServer__CertificatePassPhrase',
    'BuildingManagement__SliceAStaging__Enabled',
    'BuildingManagement__SliceAStaging__Activation',
    'BuildingManagement__Bootstrap__RootUserIds__0',
    'BuildingManagement__PayOsStaging__Enabled',
    'BuildingManagement__PayOsStaging__Activation',
    'BuildingManagement__PayOsStaging__ProviderMode',
    'BuildingManagement__PayOsD04B__Enabled',
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
        $null = Invoke-Capture 'docker' @(
            'cp', $sqlFile, "$($containerId):/tmp/scalar.sql")
        return [string](Invoke-Capture 'docker' @(
            'exec', $containerId, 'psql', '-U', 'postgres', '-d', $database,
            '-At', '-v', 'ON_ERROR_STOP=1', '-f', '/tmp/scalar.sql')).Trim()
    }
    finally {
        Remove-Item -LiteralPath $sqlFile -ErrorAction SilentlyContinue
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

function Assert-StatusAny([string]$step, $actual, [int[]]$expected) {
    if ($expected -notcontains [int]$actual.Status) {
        throw "MATRIX FAILURE: $step expected HTTP $($expected -join '/'), got $($actual.Status). See $($step)-body.txt"
    }
    Write-Host "MATRIX PASS: $step HTTP $($actual.Status)" -ForegroundColor Green
}

function Assert-BodyContains([string]$step, $response, [string]$needle) {
    if (-not $response.Body -or $response.Body -notmatch [regex]::Escape($needle)) {
        throw "MATRIX FAILURE: $step body missing '$needle'. See $($step)-body.txt"
    }
    Write-Host "MATRIX PASS: $step contains $needle" -ForegroundColor Green
}

# Signed payOS confirm-webhook body under the run's synthetic checksum key.
# The signature is HMAC-SHA256 over data fields sorted by name joined
# name=value&..., exactly the payOS documented scheme the Host verifies.
function New-SignedWebhook([long]$orderCode, [long]$amount, [string]$paymentLinkId, [string]$reference) {
    $fields = [ordered]@{
        amount = [string]$amount
        code = '00'
        currency = 'VND'
        orderCode = [string]$orderCode
        paymentLinkId = $paymentLinkId
        reference = $reference
    }
    $pairs = @()
    foreach ($name in ($fields.Keys | Sort-Object)) {
        $pairs += "$name=$($fields[$name])"
    }
    $content = [string]::Join('&', $pairs)
    $hmac = New-Object System.Security.Cryptography.HMACSHA256
    $hmac.Key = [System.Text.Encoding]::UTF8.GetBytes($script:checksumKey)
    try {
        $digest = $hmac.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($content))
        $signature = ([BitConverter]::ToString($digest)).Replace('-', '').ToLowerInvariant()
    }
    finally { $hmac.Dispose() }
    $json = @{
        code = '00'
        success = $true
        data = @{
            amount = $amount
            code = '00'
            currency = 'VND'
            orderCode = $orderCode
            paymentLinkId = $paymentLinkId
            reference = $reference
        }
        signature = $signature
    } | ConvertTo-Json -Depth 5 -Compress
    return [System.Text.Encoding]::UTF8.GetBytes($json)
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
        throw "Safety stop: run only on the reviewed P1.5 branch (current='$branch')."
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
    # The fixture GUID literals must be present in the committed source at
    # this SHA; refuse if the fixture is not part of the tree.
    $fixtureSource = Join-Path $root 'test\PayOs.Staging.Persistence.Tests\P15InternalStagingProvision_Tests.cs'
    if (-not (Test-Path -LiteralPath $fixtureSource)) {
        throw 'Missing fixture source: P15InternalStagingProvision_Tests.cs'
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
        throw 'PORT_15432_BUSY: another disposable container or DB is already using it.'
    }

    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $logRoot = Join-Path (Join-Path $HOME 'NgocTrang-Test-Logs') "payos-p15-internal-$stamp"
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

    $pgTests = 'test/PayOs.Staging.Persistence.Tests/PayOs.Staging.Persistence.Tests.csproj'
    $hostProject = 'src/NgocTrangHouseManagementSystem.HttpApi.Host/NgocTrangHouseManagementSystem.HttpApi.Host.csproj'
    $migratorProject = 'src/NgocTrangHouseManagementSystem.DbMigrator/NgocTrangHouseManagementSystem.DbMigrator.csproj'
    $hostDir = Join-Path $root 'src/NgocTrangHouseManagementSystem.HttpApi.Host'

    Invoke-Logged '01-staging-test-compile' 'dotnet' @('build', $pgTests, '-c', 'Release')
    Invoke-Logged '02-migrator-build' 'dotnet' @('build', $migratorProject, '-c', 'Release')
    Invoke-Logged '03-host-build' 'dotnet' @('build', $hostProject, '-c', 'Release')

    # Disposable PostgreSQL 16, loopback-only, own label, own port.
    Write-Host '=== Launching disposable PostgreSQL 16 on 127.0.0.1:15432 ==='
    $containerName = "bm-payos-p15-internal-$PID"
    $containerId = Invoke-Capture 'docker' @(
        'run', '--detach', '--rm',
        '--name', $containerName,
        '--label', 'bm.scope=temporary-payos-p15-internal-test',
        '--publish', '127.0.0.1:15432:5432',
        '--env', 'POSTGRES_PASSWORD=disposable-only',
        '--env', 'POSTGRES_DB=bm_slicea_staging_p15_identity',
        'postgres:16')
    if ($containerId -notmatch '^[0-9a-f]{64}$') {
        throw "Docker did not return a valid container ID: $containerId"
    }
    $ready = $false
    for ($i = 0; $i -lt 120; $i++) {
        $previous = $ErrorActionPreference
        try {
            $ErrorActionPreference = 'Continue'
            & docker exec $containerId pg_isready -h 127.0.0.1 -p 5432 -U postgres -d bm_slicea_staging_p15_identity *> $null
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
        'bm_slicea_staging_p15_building')
    Invoke-Logged '04b-create-payos-db' 'docker' @(
        'exec', $containerId, 'createdb', '-U', 'postgres',
        'bm_payos_staging_p15')

    # Real DbMigrator against the disposable identity DB. The admin password
    # is generated locally, kept only in script memory, never echoed.
    $adminPassword = 'P15I-' + [Guid]::NewGuid().ToString('N') + '-A'
    [Environment]::SetEnvironmentVariable('NgocTrang__LocalBootstrap__AdminPassword',
        $adminPassword, 'Process')
    [Environment]::SetEnvironmentVariable('ConnectionStrings__Default',
        'Host=127.0.0.1;Port=15432;Database=bm_slicea_staging_p15_identity;Username=postgres;Password=disposable-only', 'Process')
    Invoke-Logged '05-db-migrator-identity' 'dotnet' @(
        'run', '--no-build', '--no-launch-profile', '-c', 'Release',
        '--project', $migratorProject)
    [Environment]::SetEnvironmentVariable('ConnectionStrings__Default', $null, 'Process')
    [Environment]::SetEnvironmentVariable('NgocTrang__LocalBootstrap__AdminPassword', $null, 'Process')

    # Fixture test with generated disposable actor passwords. The fixture
    # clears its own env copies, so keep script-local copies for token calls.
    $operatorPassword = 'P15I-' + [Guid]::NewGuid().ToString('N') + '-O'
    $outsiderPassword = 'P15I-' + [Guid]::NewGuid().ToString('N') + '-X'
    [Environment]::SetEnvironmentVariable('BM_PAYOS_EF_TEST_DB', '1', 'Process')
    [Environment]::SetEnvironmentVariable('BM_PAYOS_EF_TEST_PASSWORD', 'disposable-only', 'Process')
    [Environment]::SetEnvironmentVariable('BM_PAYOS_EF_TEST_PORT', '15432', 'Process')
    [Environment]::SetEnvironmentVariable('BM_PAYOS_P15_INTERNAL',
        'I_ACCEPT_DISPOSABLE_P15_INTERNAL_STAGING_ONLY', 'Process')
    [Environment]::SetEnvironmentVariable('BM_PAYOS_P15_INTERNAL_OPERATOR_PASSWORD',
        $operatorPassword, 'Process')
    [Environment]::SetEnvironmentVariable('BM_PAYOS_P15_INTERNAL_OUTSIDER_PASSWORD',
        $outsiderPassword, 'Process')
    Invoke-Logged '06-p15-internal-provision-fixture' 'dotnet' @(
        'test', $pgTests, '-c', 'Release', '--no-build',
        '--filter', 'FullyQualifiedName~P15InternalStagingProvision_Tests',
        '--logger', 'trx;LogFileName=payos-p15-internal-provision.trx',
        '--results-directory', $logRoot, '--verbosity', 'normal')
    Assert-Trx 'payos-p15-internal-provision.trx' 1
    foreach ($name in @('BM_PAYOS_P15_INTERNAL',
        'BM_PAYOS_P15_INTERNAL_OPERATOR_PASSWORD',
        'BM_PAYOS_P15_INTERNAL_OUTSIDER_PASSWORD')) {
        [Environment]::SetEnvironmentVariable($name, $null, 'Process')
    }

    # Ephemeral self-signed signing certificate for the non-Development Host.
    # Lives only in the isolated worktree Host content root.
    $certPassPhrase = [Guid]::NewGuid().ToString('N')
    $pfxPath = Join-Path $hostDir 'openiddict.pfx'
    Invoke-Logged '07-mint-openiddict-pfx' 'dotnet' @(
        'dev-certs', 'https', '-v', '-ep', $pfxPath, '-p', $certPassPhrase, '--trust')

    # Synthetic payOS merchant credentials + checksum key: required by the
    # module gates (fail-closed without them) but NEVER transmitted anywhere
    # because ProviderMode=InternalSimulated never touches the network.
    $script:checksumKey = 'P15I-' + [Guid]::NewGuid().ToString('N') + '-CK'
    $clientId = 'P15I-' + [Guid]::NewGuid().ToString('N') + '-CID'
    $apiKey = 'P15I-' + [Guid]::NewGuid().ToString('N') + '-AK'

    # Launch the real Host in Staging with BOTH the SliceA gate (grant
    # reader + active account probe so the Contracts.Manage authorization
    # chain resolves real persisted grants) and the PayOsStaging gate with
    # ProviderMode=InternalSimulated. D04B stays OFF: its gate is mutually
    # exclusive with PayOsStaging by design.
    $identityCs = 'Host=127.0.0.1;Port=15432;Database=bm_slicea_staging_p15_identity;Username=postgres;Password=disposable-only'
    $buildingCs = 'Host=127.0.0.1;Port=15432;Database=bm_slicea_staging_p15_building;Username=postgres;Password=disposable-only'
    $payOsCs = 'Host=127.0.0.1;Port=15432;Database=bm_payos_staging_p15;Username=postgres;Password=disposable-only'
    [Environment]::SetEnvironmentVariable('ASPNETCORE_ENVIRONMENT', 'Staging', 'Process')
    [Environment]::SetEnvironmentVariable('DOTNET_ENVIRONMENT', 'Staging', 'Process')
    [Environment]::SetEnvironmentVariable('ASPNETCORE_URLS', 'https://localhost:44311', 'Process')
    [Environment]::SetEnvironmentVariable('ConnectionStrings__Default', $identityCs, 'Process')
    [Environment]::SetEnvironmentVariable('ConnectionStrings__BuildingManagement', $buildingCs, 'Process')
    [Environment]::SetEnvironmentVariable('ConnectionStrings__PayOsStaging', $payOsCs, 'Process')
    [Environment]::SetEnvironmentVariable('BuildingManagement__SliceAStaging__Enabled', 'true', 'Process')
    [Environment]::SetEnvironmentVariable('BuildingManagement__SliceAStaging__Activation',
        'I_ACCEPT_ISOLATED_SLICE_A_STAGING_WRITES', 'Process')
    # Array binding: .NET configuration binds Guid[] from an INDEXED child
    # key (RootUserIds:0), not a comma-scalar, so the env var must be
    # BuildingManagement__Bootstrap__RootUserIds__0.
    [Environment]::SetEnvironmentVariable('BuildingManagement__Bootstrap__RootUserIds__0',
        '0150c000-0000-4000-8000-000000000001', 'Process')
    [Environment]::SetEnvironmentVariable('BuildingManagement__PayOsStaging__Enabled', 'true', 'Process')
    [Environment]::SetEnvironmentVariable('BuildingManagement__PayOsStaging__Activation',
        'I_ACCEPT_ISOLATED_PAYOS_STAGING_ONLY', 'Process')
    [Environment]::SetEnvironmentVariable('BuildingManagement__PayOsStaging__ProviderMode',
        'InternalSimulated', 'Process')
    [Environment]::SetEnvironmentVariable('BuildingManagement__PayOsStaging__ClientId', $clientId, 'Process')
    [Environment]::SetEnvironmentVariable('BuildingManagement__PayOsStaging__ApiKey', $apiKey, 'Process')
    [Environment]::SetEnvironmentVariable('BuildingManagement__PayOsStaging__ChecksumKey', $script:checksumKey, 'Process')
    [Environment]::SetEnvironmentVariable('BuildingManagement__PayOsStaging__ReturnUrl',
        'https://localhost:44311/payos-return', 'Process')
    [Environment]::SetEnvironmentVariable('BuildingManagement__PayOsStaging__CancelUrl',
        'https://localhost:44311/payos-cancel', 'Process')
    [Environment]::SetEnvironmentVariable('BuildingManagement__PayOsD04B__Enabled', 'false', 'Process')
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
    Write-Host 'PASS: real Host healthy on https://localhost:44311/health-status (SliceA + PayOsStaging InternalSimulated, D04B off).' -ForegroundColor Green

    # REAL password-grant tokens. Kept in script memory only.
    $operatorToken = Get-NativeToken 'operator-p15' $operatorPassword
    $outsiderToken = Get-NativeToken 'outsider-p15' $outsiderPassword
    Write-Host 'PASS: two real OpenIddict password-grant tokens obtained (never echoed).' -ForegroundColor Green

    $base = 'https://localhost:44311/api/payments/payos/staging'
    $paymentA = '0150a000-0000-4000-8000-000000000006'
    $paymentB = '0150b000-0000-4000-8000-000000000006'

    # ---------- NEGATIVE MATRIX ----------
    # N1: anonymous checkout -> 401, or 302 redirect-to-login challenge.
    $r = Invoke-MatrixRequest 'N1-anonymous-checkout' 'POST' "$base/checkout/$paymentA" $null $null $null
    if ($r.Status -ne 401 -and $r.Status -ne 302) {
        throw "MATRIX FAILURE: N1-anonymous-checkout expected HTTP 401/302, got $($r.Status)."
    }
    Write-Host 'MATRIX PASS: N1-anonymous-checkout denied (HTTP 401 or 302 login challenge).' -ForegroundColor Green

    # N2: forged bearer -> 401/302.
    $r = Invoke-MatrixRequest 'N2-forged-bearer-checkout' 'POST' "$base/checkout/$paymentA" $null $null 'totally.forged.jwt.value'
    if ($r.Status -ne 401 -and $r.Status -ne 302) {
        throw "MATRIX FAILURE: N2-forged-bearer-checkout expected HTTP 401/302, got $($r.Status)."
    }
    Write-Host 'MATRIX PASS: N2-forged-bearer denied (HTTP 401 or 302 login challenge).' -ForegroundColor Green

    # N3: outsider (no Contracts.Manage permission) -> 403
    $r = Invoke-MatrixRequest 'N3-outsider-checkout' 'POST' "$base/checkout/$paymentA" $null $null $outsiderToken
    Assert-Status 'N3-outsider-checkout' $r 403

    # N4: cross-building checkout (Building B grants nobody) -> 403
    $r = Invoke-MatrixRequest 'N4-cross-building-checkout' 'POST' "$base/checkout/$paymentB" $null $null $operatorToken
    Assert-Status 'N4-cross-building-checkout' $r 403

    # ---------- POSITIVE LANE (PaymentA, Building A) ----------
    # P1: operator checkout -> 200 (Ok, not a POST-201: the controller
    # returns Ok(result) and the ApiResponseFilter only rewrites POST
    # ObjectResults <400 to 201; expect either but assert the view body).
    $r = Invoke-MatrixRequest 'P1-operator-checkout-paymentA' 'POST' "$base/checkout/$paymentA" $null $null $operatorToken
    Assert-StatusAny 'P1-operator-checkout-paymentA' $r @(200, 201)
    $orderCode = [regex]::Match($r.Body, '"orderCode"\s*:\s*([0-9]+)').Groups[1].Value
    if ([string]::IsNullOrWhiteSpace($orderCode)) { throw 'P1: no orderCode extracted.' }
    Assert-BodyContains 'P1-operator-checkout-paymentA' $r 'SIM-P15-'
    Write-Host "MATRIX PASS: P1 checkout issued orderCode $orderCode via simulated provider." -ForegroundColor Green

    # P2: repeat checkout -> SAME orderCode (Recovered same order, never a
    # second intent for one payment).
    $r = Invoke-MatrixRequest 'P2-repeat-checkout-same-order' 'POST' "$base/checkout/$paymentA" $null $null $operatorToken
    Assert-StatusAny 'P2-repeat-checkout-same-order' $r @(200, 201)
    $orderCode2 = [regex]::Match($r.Body, '"orderCode"\s*:\s*([0-9]+)').Groups[1].Value
    if ($orderCode2 -cne $orderCode) {
        throw "MATRIX FAILURE: repeat checkout returned different orderCode ($orderCode2 vs $orderCode)."
    }
    Write-Host 'MATRIX PASS: P2 repeat checkout SAME orderCode (no second intent).' -ForegroundColor Green

    # Durable state after checkout: exactly one intent for paymentA in
    # AwaitingPayment with the SIM link attached, and the attach outbox row
    # Completed (the checkout transaction finished it in the same commit).
    $checkoutState = @"
SELECT
  (SELECT count(*) FROM "BuildingManagementPayOsPaymentIntents" WHERE "PaymentId"='$paymentA'),
  (SELECT "Status" FROM "BuildingManagementPayOsPaymentIntents" WHERE "PaymentId"='$paymentA'),
  (SELECT "PaymentLinkId" LIKE 'SIM-P15-%' FROM "BuildingManagementPayOsPaymentIntents" WHERE "PaymentId"='$paymentA'),
  (SELECT count(*) FROM "BuildingManagementPayOsStagingOutbox" o
     JOIN "BuildingManagementPayOsPaymentIntents" i ON i."Id"=o."PaymentIntentId"
     WHERE i."PaymentId"='$paymentA' AND o."Kind"=1 AND o."Status"=1)
"@
    $checkoutResult = (Invoke-Scalar 'bm_payos_staging_p15' $checkoutState)
    Write-Host "CHECKOUT_STATE: $checkoutResult"
    $cparts = @($checkoutResult -split '\|')
    if ($cparts.Count -ne 4 -or $cparts[0].Trim() -ne '1' -or
        $cparts[1].Trim() -ne '1' -or $cparts[2].Trim() -ne 't' -or
        $cparts[3].Trim() -ne '1') {
        throw "CHECKOUT STATE FAILURE: expected 1|1|t|1 (intents|status|simLink|attachOutboxCompleted), got '$checkoutResult'"
    }
    Write-Host 'PASS: checkout durable state verified (1 intent, AwaitingPayment, SIM link, attach outbox Completed).' -ForegroundColor Green

    # ---------- SIGNED WEBHOOK LANE ----------
    # W1: signed webhook under the synthetic checksum key -> 200 stored.
    $linkId = 'SIM-P15-' + $orderCode
    $webhookBody = New-SignedWebhook ([long]$orderCode) 175000 $linkId 'P15BANK-A-REF'
    $r = Invoke-MatrixRequest 'W1-signed-webhook' 'POST' "$base/webhook" $webhookBody 'application/json' $null
    Assert-StatusAny 'W1-signed-webhook' $r @(200, 201)

    # W2: exact duplicate replay -> 200 duplicate, exactly ONE inbox row.
    $r = Invoke-MatrixRequest 'W2-duplicate-webhook-replay' 'POST' "$base/webhook" $webhookBody 'application/json' $null
    Assert-StatusAny 'W2-duplicate-webhook-replay' $r @(200, 201)
    $inboxCount = (Invoke-Scalar 'bm_payos_staging_p15' @"
SELECT count(*) FROM "BuildingManagementPayOsWebhookInbox"
 WHERE "OrderCode"=$orderCode;
"@)
    if ($inboxCount.Trim() -ne '1') {
        throw "WEBHOOK STATE FAILURE: expected 1 inbox row after duplicate replay, got '$inboxCount'."
    }
    Write-Host 'MATRIX PASS: W2 duplicate replay idempotent (1 inbox row).' -ForegroundColor Green

    # W3: tampered signature -> 400, nothing new stored.
    $tampered = [byte[]]$webhookBody.Clone()
    $tampered[$tampered.Length - 3] = $tampered[$tampered.Length - 3] -bxor 1
    $r = Invoke-MatrixRequest 'W3-tampered-signature-webhook' 'POST' "$base/webhook" $tampered 'application/json' $null
    Assert-Status 'W3-tampered-signature-webhook' $r 400
    $inboxCount2 = (Invoke-Scalar 'bm_payos_staging_p15' @"
SELECT count(*) FROM "BuildingManagementPayOsWebhookInbox"
 WHERE "OrderCode"=$orderCode;
"@)
    if ($inboxCount2.Trim() -ne '1') {
        throw "WEBHOOK STATE FAILURE: tampered webhook changed inbox rows ($inboxCount2)."
    }
    Write-Host 'MATRIX PASS: W3 tampered signature rejected (400), inbox untouched.' -ForegroundColor Green

    # Webhook durable state: inbox row PendingReconciliation(0) plus the
    # ReconcileIntent outbox row queued Pending(0) in the same transaction.
    $webhookState = @"
SELECT
  (SELECT "Status" FROM "BuildingManagementPayOsWebhookInbox" WHERE "OrderCode"=$orderCode),
  (SELECT count(*) FROM "BuildingManagementPayOsStagingOutbox" o
     JOIN "BuildingManagementPayOsPaymentIntents" i ON i."Id"=o."PaymentIntentId"
     WHERE i."PaymentId"='$paymentA' AND o."Kind"=2 AND o."Status"=0)
"@
    $webhookResult = (Invoke-Scalar 'bm_payos_staging_p15' $webhookState)
    Write-Host "WEBHOOK_STATE: $webhookResult"
    $wparts = @($webhookResult -split '\|')
    if ($wparts.Count -ne 2 -or $wparts[0].Trim() -ne '0' -or $wparts[1].Trim() -ne '1') {
        throw "WEBHOOK STATE FAILURE: expected 0|1 (inboxPending|reconcileOutboxPending), got '$webhookResult'"
    }
    Write-Host 'PASS: webhook durable state verified (inbox pending, reconcile outbox queued).' -ForegroundColor Green

    # ---------- OPERATOR RECONCILIATION LANE ----------
    # R1: operator reconcile -> 202 Accepted (AcceptedResult is not rewritten
    # to 201 by the ApiResponse filter: only ObjectResults are), intent
    # ConfirmedPendingPosting, reconcile outbox row Completed.
    $r = Invoke-MatrixRequest 'R1-operator-reconcile-paymentA' 'POST' "$base/reconcile/$paymentA" $null $null $operatorToken
    Assert-StatusAny 'R1-operator-reconcile-paymentA' $r @(200, 201, 202)
    Assert-BodyContains 'R1-operator-reconcile-paymentA' $r 'confirmedPendingFinancialReview'

    # R2: idempotent reconcile replay -> same disposition, no second mutation.
    $r = Invoke-MatrixRequest 'R2-idempotent-reconcile-replay' 'POST' "$base/reconcile/$paymentA" $null $null $operatorToken
    Assert-StatusAny 'R2-idempotent-reconcile-replay' $r @(200, 201, 202)
    Assert-BodyContains 'R2-idempotent-reconcile-replay' $r 'confirmedPendingFinancialReview'

    # R3: outsider reconcile -> 403
    $r = Invoke-MatrixRequest 'R3-outsider-reconcile' 'POST' "$base/reconcile/$paymentA" $null $null $outsiderToken
    Assert-Status 'R3-outsider-reconcile' $r 403

    # Final durable state: intent ConfirmedPendingPosting (2), inbox
    # ReconciledNoPosting (1), BOTH outbox rows Completed (1), and the
    # legacy Payment still Pending (1) — reconciliation is a handoff, not
    # payment completion.
    $finalPayOsState = @"
SELECT
  (SELECT "Status" FROM "BuildingManagementPayOsPaymentIntents" WHERE "PaymentId"='$paymentA'),
  (SELECT "Status" FROM "BuildingManagementPayOsWebhookInbox" WHERE "OrderCode"=$orderCode),
  (SELECT count(*) FROM "BuildingManagementPayOsStagingOutbox" o
     JOIN "BuildingManagementPayOsPaymentIntents" i ON i."Id"=o."PaymentIntentId"
     WHERE i."PaymentId"='$paymentA' AND o."Status"=1),
  (SELECT count(*) FROM "BuildingManagementPayOsStagingOutbox" o
     JOIN "BuildingManagementPayOsPaymentIntents" i ON i."Id"=o."PaymentIntentId"
     WHERE i."PaymentId"='$paymentA')
"@
    $finalResult = (Invoke-Scalar 'bm_payos_staging_p15' $finalPayOsState)
    Write-Host "FINAL_PAYOS_STATE: $finalResult"
    $fparts = @($finalResult -split '\|')
    if ($fparts.Count -ne 4 -or $fparts[0].Trim() -ne '2' -or
        $fparts[1].Trim() -ne '1' -or $fparts[2].Trim() -ne '2' -or
        $fparts[3].Trim() -ne '2') {
        throw "FINAL STATE FAILURE (payos): expected 2|1|2|2 (intentConfirmed|inboxReconciled|outboxCompleted|outboxTotal), got '$finalResult'"
    }
    $legacyStatus = (Invoke-Scalar 'bm_slicea_staging_p15_building' @"
SELECT "Status" FROM "BuildingManagementPayments" WHERE "Id"='$paymentA';
"@)
    if ($legacyStatus.Trim() -ne '1') {
        throw "FINAL STATE FAILURE (legacy): Payment must stay Pending(1) after reconciliation, got '$legacyStatus'."
    }
    Write-Host 'PASS: final durable state verified - intent ConfirmedPendingPosting, inbox Reconciled, both outbox rows Completed, Payment still Pending.' -ForegroundColor Green

    # ---------- CLEANUP ----------
    Write-Host '=== PASS: P1.5 INTERNAL STAGING MATRIX COMPLETE ===' -ForegroundColor Green
    Write-Host "SOURCE_COMMIT=$sha"
    Write-Host "LOG_DIRECTORY=$logRoot"
    Write-Host 'No real payOS payment, no real bank transfer; 15433/5432 untouched; zero real payOS network traffic.'
}
catch {
    Write-Host "=== P1.5 INTERNAL STAGING MATRIX FAILED: $($_.Exception.Message) ===" -ForegroundColor Red
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
    $operatorToken = ''
    $outsiderToken = ''
    $script:checksumKey = ''
    foreach ($key in $envNames) {
        [Environment]::SetEnvironmentVariable($key, $oldEnv[$key], 'Process')
    }
    foreach ($extra in @(
        'BuildingManagement__PayOsStaging__ClientId',
        'BuildingManagement__PayOsStaging__ApiKey',
        'BuildingManagement__PayOsStaging__ChecksumKey',
        'BuildingManagement__PayOsStaging__ReturnUrl',
        'BuildingManagement__PayOsStaging__CancelUrl',
        'BuildingManagement__Bootstrap__RootUserIds__0')) {
        [Environment]::SetEnvironmentVariable($extra, $null, 'Process')
    }
    if ($hostProcess -and -not $hostProcess.HasExited) {
        Write-Host 'Stopping only this run staging Host ...' -ForegroundColor Cyan
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
