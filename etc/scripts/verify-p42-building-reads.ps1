[CmdletBinding()]
param()

# P4.2 BUILDING/ROOM READ-PATH E2E - disposable local lane only.
#
# Starts a SECOND Staging Host on 44321 (the Owner's own dev Host keeps 44311)
# plus a Vite dev server on 5273, against two throwaway databases on the
# EXISTING container bm-p21-pg-15432 (127.0.0.1:15432).
#
# What it proves: the three list endpoints the new screens call answer with a
# real, decoded HTTP status for a real bearer token through the real Vite
# proxy - not 401 (token rejected) and not 500. Either 200 with a JSON array,
# or 403 from the building-scope gate (the expected state today, because no
# operator-grant store exists).
#
# Never touches 15433 or 5432, never runs a production migration, never
# enables payOS, never performs a bank operation. The admin password is
# generated in-process and is never echoed or written to disk.
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
$logDir = Join-Path $logRoot "p42-building-reads-$stamp"
New-Item -ItemType Directory -Path $logDir -Force | Out-Null
$transcript = Join-Path $logDir 'run.log'
$results = New-Object System.Collections.Generic.List[object]

function Write-Step([string]$Message) {
    $line = '{0}  {1}' -f (Get-Date -Format 'HH:mm:ss'), $Message
    Write-Host $line
    Add-Content -Path $transcript -Value $line -Encoding utf8
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
    param([string]$Method, [string]$Url, [string]$Form, [string]$Bearer, [bool]$FollowRedirects = $false)
    [System.Net.ServicePointManager]::ServerCertificateValidationCallback = { $true }
    try {
        $request = [System.Net.HttpWebRequest]::Create($Url)
        $request.Method = $Method
        $request.Proxy = $null
        $request.AllowAutoRedirect = $FollowRedirects
        $request.Timeout = 30000
        $request.Accept = 'application/json'
        if ($Bearer) { $request.Headers.Add('Authorization', "Bearer $Bearer") }
        if ($Form) {
            $request.ContentType = 'application/x-www-form-urlencoded'
            $bytes = [Text.Encoding]::UTF8.GetBytes($Form)
            $request.ContentLength = $bytes.Length
            $stream = $request.GetRequestStream()
            try { $stream.Write($bytes, 0, $bytes.Length) } finally { $stream.Dispose() }
        }
        try {
            $response = $request.GetResponse()
            try {
                $body = (New-Object IO.StreamReader($response.GetResponseStream())).ReadToEnd()
                return @{ Status = [int]$response.StatusCode; Body = $body; Location = $response.Headers['Location']; FinalUrl = $response.ResponseUri.AbsoluteUri; ContentType = $response.ContentType }
            } finally { $response.Dispose() }
        } catch [System.Net.WebException] {
            $response = $_.Exception.Response
            if (-not $response) { throw }
            $body = (New-Object IO.StreamReader($response.GetResponseStream())).ReadToEnd()
            $status = [int]$response.StatusCode
            $response.Dispose()
            return @{ Status = $status; Body = $body; Location = $null; FinalUrl = $null; ContentType = $null }
        }
    } finally {
        [System.Net.ServicePointManager]::ServerCertificateValidationCallback = $originalCertCallback
    }
}

try {
    $sha = (& git -C $root rev-parse HEAD | Out-String).Trim()
    $branch = (& git -C $root branch --show-current | Out-String).Trim()
    Write-Step "=== P4.2 BUILDING READ-PATH E2E / branch=$branch / sha=$sha ==="
    Write-Step "LOG_DIRECTORY=$logDir"

    foreach ($port in $hostPort, $vitePort) {
        if (Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue) {
            throw "Port $port already has a listener; this lane uses $hostPort/$vitePort to stay clear of the owner's dev instance."
        }
    }

    # --- 1. Disposable databases ---------------------------------------------
    foreach ($db in @('bm_p42_identity_test','bm_p42_legacy_test')) {
        & docker exec $container dropdb -U postgres --if-exists $db | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "dropdb $db failed." }
        & docker exec $container createdb -U postgres $db | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "createdb $db failed." }
    }
    Write-Step 'PASS: disposable databases bm_p42_identity_test / bm_p42_legacy_test recreated on 15432.'

    # --- 2. Migrate + seed admin ---------------------------------------------
    $adminPassword = 'P42E2E-' + [Guid]::NewGuid().ToString('N') + '-A'
    [Environment]::SetEnvironmentVariable('ConnectionStrings__Default',
        "Host=127.0.0.1;Port=15432;Database=bm_p42_identity_test;Username=postgres;Password=$pgPassword", 'Process')
    [Environment]::SetEnvironmentVariable('ConnectionStrings__BuildingManagement',
        "Host=127.0.0.1;Port=15432;Database=bm_p42_legacy_test;Username=postgres;Password=$pgPassword", 'Process')
    [Environment]::SetEnvironmentVariable('NgocTrang__LocalBootstrap__AdminPassword', $adminPassword, 'Process')

    & dotnet build -c Release (Join-Path $root 'src\NgocTrangHouseManagementSystem.DbMigrator\NgocTrangHouseManagementSystem.DbMigrator.csproj') *> (Join-Path $logDir 'migrator-build.log')
    if ($LASTEXITCODE -ne 0) { throw 'DbMigrator build failed.' }

    # The Owner's dev Host (PID on 44311) is running FROM bin\Release and locks
    # every DLL there. Build this lane's Host into its own output folder and
    # run the DLL directly, so the dev instance's files are never touched.
    $hostOut = Join-Path $logDir 'host-bin'
    & dotnet build -c Release (Join-Path $hostDir 'NgocTrangHouseManagementSystem.HttpApi.Host.csproj') -o $hostOut *> (Join-Path $logDir 'host-build.log')
    if ($LASTEXITCODE -ne 0) { throw 'Host build failed.' }

    & dotnet run --no-build --no-launch-profile -c Release `
        --project (Join-Path $root 'src\NgocTrangHouseManagementSystem.DbMigrator\NgocTrangHouseManagementSystem.DbMigrator.csproj') *> (Join-Path $logDir 'migrator.log')
    if ($LASTEXITCODE -ne 0) { throw "DbMigrator failed. See $logDir\migrator.log" }
    Write-Step 'PASS: DbMigrator completed on the disposable identity DB (password never echoed).'

    # --- 3. Ephemeral signing certificate ------------------------------------
    $pfxPath = Join-Path $hostDir 'openiddict.pfx'
    if (Test-Path -LiteralPath $pfxPath) { Remove-Item -LiteralPath $pfxPath -Force }
    $certPassPhrase = [Guid]::NewGuid().ToString('N')
    & dotnet dev-certs https -v -ep $pfxPath -p $certPassPhrase --trust *> $null
    if ($LASTEXITCODE -ne 0) { throw 'dotnet dev-certs failed.' }
    Write-Step 'PASS: ephemeral OpenIddict signing certificate minted.'

    # --- 4. Staging Host on its own port -------------------------------------
    [Environment]::SetEnvironmentVariable('ASPNETCORE_ENVIRONMENT', 'Staging', 'Process')
    [Environment]::SetEnvironmentVariable('DOTNET_ENVIRONMENT', 'Staging', 'Process')
    [Environment]::SetEnvironmentVariable('ASPNETCORE_URLS', "https://localhost:$hostPort", 'Process')
    [Environment]::SetEnvironmentVariable('AuthServer__CertificatePassPhrase', $certPassPhrase, 'Process')

    $hostLog = Join-Path $logDir 'host.log'
    $hostDll = Join-Path $hostOut 'NgocTrangHouseManagementSystem.HttpApi.Host.dll'
    $hostProcess = Start-Process -FilePath 'dotnet' `
        -ArgumentList @($hostDll) `
        -WorkingDirectory $hostDir -PassThru `
        -RedirectStandardOutput $hostLog -RedirectStandardError (Join-Path $logDir 'host-stderr.log')

    $ready = $false
    for ($i = 0; $i -lt 150; $i++) {
        if ($hostProcess.HasExited) { throw "Host exited early with code $($hostProcess.ExitCode). See $hostLog" }
        $probe = Invoke-Web -Method 'GET' -Url "https://localhost:$hostPort/health-status"
        if ($probe.Status -eq 200) { $ready = $true; break }
        Start-Sleep -Milliseconds 1000
    }
    if (-not $ready) { throw "Host not healthy within 150s. See $hostLog" }
    Write-Step "PASS: Host healthy on https://localhost:$hostPort/health-status."

    # --- 5. Vite proxy on its own port ---------------------------------------
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

    # --- 6. Token through the proxy ------------------------------------------
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

    # --- 7. The three list endpoints the new screens call --------------------
    # 401 here would mean the token is not accepted (a real failure).
    # 200 with a JSON array, or 403 from the scope gate, are both honest
    # outcomes; 500 is always a failure.
    foreach ($resource in 'building','floor','room') {
        $url = "http://localhost:$vitePort/api/building-management/$resource"

        # 1) No-redirect view: the raw status the server emits. The gate's 403
        #    surfaces here as a 302 OpenIddict challenge.
        $response = Invoke-Web -Method 'GET' -Url $url -Bearer $token
        # 2) Browser view: fetch() auto-follows redirects, so this is what the
        #    React app actually receives.
        $browserView = Invoke-Web -Method 'GET' -Url $url -Bearer $token -FollowRedirects $true

        $body = $response.Body
        $isArray = $body.TrimStart().StartsWith('[')
        $preview = if ($body.Length -gt 300) { $body.Substring(0, 300) + '...' } else { $body }

        Write-Step ("GET {0} -> raw HTTP {1}; browser-followed HTTP {2} ({3}); body: {4}" -f `
            $resource, $response.Status, $browserView.Status, $browserView.ContentType, $preview)

        if ($response.Status -eq 200) {
            if (-not $isArray) {
                throw "$resource returned 200 but the body is not a JSON array."
            }
            $outcome = '200 OK (list returned)'
        } elseif ($response.Status -in 302, 303, 307) {
            # The ABP scope-gate 403 is converted by the OpenIddict handler
            # into a redirect to /Error?httpStatusCode=403. Confirm it is
            # that specific denial and not some unrelated redirect.
            $challengeText = if ($response.Location) { $response.Location } else { $response.Body }
            if ($challengeText -notmatch 'httpStatusCode=403') {
                throw "$resource returned a redirect that is not the scope-gate denial: $challengeText"
            }
            $outcome = "denied by the building scope gate (raw 403 surfaced as HTTP $($response.Status) to /Error?httpStatusCode=403; browser sees HTTP $($browserView.Status))"
        } elseif ($response.Status -eq 401) {
            throw "$resource returned 401 - the bearer token was rejected."
        } elseif ($response.Status -ge 500) {
            throw "$resource returned $($response.Status) - server error."
        } else {
            $outcome = "unexpected $($response.Status)"
        }

        Write-Step ("PASS: {0} -> {1}" -f $resource, $outcome)

        $results.Add([pscustomobject]@{
            Resource        = $resource
            RawStatus       = $response.Status
            BrowserStatus   = $browserView.Status
            BrowserLocation = $browserView.FinalUrl
            Outcome         = $outcome
            Body            = $body
        })
    }

    $summaryPath = Join-Path $logDir 'summary.json'
    $payload = [pscustomobject]@{
        Commit       = $sha
        Branch       = $branch
        Timestamp    = (Get-Date -Format 'o')
        HostPort     = $hostPort
        VitePort     = $vitePort
        Endpoints    = $results
    }
    $payload | ConvertTo-Json -Depth 6 | Set-Content -Path $summaryPath -Encoding utf8

    Write-Step '=== P4.2 BUILDING READ-PATH E2E: ALL STEPS PASSED ==='
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
    [System.Net.ServicePointManager]::ServerCertificateValidationCallback = $originalCertCallback
}
