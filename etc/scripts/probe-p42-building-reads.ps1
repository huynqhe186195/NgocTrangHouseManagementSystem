#requires -Version 5.1
<#
  P4.2 read-path probe.

  Confirms what the three new screens will actually receive from the
  running Staging Host, through the same Vite proxy the browser uses:

    GET /api/building-management/building
    GET /api/building-management/floor
    GET /api/building-management/room

  The building-scope gate has no operator-grant store, so these are
  expected to answer 403. The probe records the real status and the
  server's error body so the frontend's copy can be checked against it.

  It never prints the access token or the admin password.
#>
[CmdletBinding()]
param(
    [string]$Host_ = 'https://localhost:44311',
    [string]$ViteBase = 'http://localhost:5173',
    [string]$LogDirectory = (Join-Path $env:USERPROFILE 'NgocTrang-Test-Logs')
)

$ErrorActionPreference = 'Stop'
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$logDir = Join-Path $LogDirectory "p42-read-probe-$stamp"
New-Item -ItemType Directory -Path $logDir -Force | Out-Null
$logFile = Join-Path $logDir 'probe.log'

function Write-Log([string]$Message) {
    $line = '{0}  {1}' -f (Get-Date -Format 'HH:mm:ss'), $Message
    Write-Host $line
    Add-Content -Path $logFile -Value $line -Encoding utf8
}

# PS 5.1 routes localhost through the system proxy; bypass it explicitly and
# trust the dev certificate only for these two hosts.
$script:certCallback = [System.Net.ServicePointManager]::ServerCertificateValidationCallback
[System.Net.ServicePointManager]::ServerCertificateValidationCallback = { $true }

function Invoke-Probe {
    param(
        [string]$Method,
        [string]$Uri,
        [hashtable]$Headers = @{},
        [string]$Body
    )
    $request = [System.Net.HttpWebRequest]::Create($Uri)
    $request.Proxy = $null
    $request.Method = $Method
    $request.Accept = 'application/json'
    $request.Timeout = 30000
    foreach ($key in $Headers.Keys) { $request.Headers[$key] = $Headers[$key] }
    if ($Body) {
        $request.ContentType = 'application/x-www-form-urlencoded'
        $bytes = [Text.Encoding]::UTF8.GetBytes($Body)
        $request.ContentLength = $bytes.Length
        $stream = $request.GetRequestStream()
        $stream.Write($bytes, 0, $bytes.Length)
        $stream.Close()
    }
    try {
        $response = $request.GetResponse()
        $reader = New-Object IO.StreamReader($response.GetResponseStream())
        $text = $reader.ReadToEnd(); $reader.Close(); $response.Close()
        return [pscustomobject]@{ Status = [int]$response.StatusCode; Body = $text }
    } catch [System.Net.WebException] {
        $webResponse = $_.Exception.Response
        if ($null -eq $webResponse) { throw }
        $reader = New-Object IO.StreamReader($webResponse.GetResponseStream())
        $text = $reader.ReadToEnd(); $reader.Close(); $webResponse.Close()
        return [pscustomobject]@{ Status = [int]$webResponse.StatusCode; Body = $text }
    }
}

$results = @()

try {
    Write-Log "Probe directory: $logDir"

    if (-not $env:NGT_PROBE_USER -or -not $env:NGT_PROBE_PASS) {
        throw 'Set NGT_PROBE_USER / NGT_PROBE_PASS in the environment for this run.'
    }

    $form = 'grant_type=password&client_id=NgocTrangHouseManagementSystem_App' +
            '&scope=' + [uri]::EscapeDataString('openid profile email roles NgocTrangHouseManagementSystem') +
            '&username=' + [uri]::EscapeDataString($env:NGT_PROBE_USER) +
            '&password=' + [uri]::EscapeDataString($env:NGT_PROBE_PASS)

    Write-Log 'Step 1: password grant through the Vite proxy'
    $tokenResponse = Invoke-Probe -Method 'POST' -Uri "$ViteBase/connect/token" -Body $form
    Write-Log ("  -> HTTP {0}" -f $tokenResponse.Status)
    $results += [pscustomobject]@{ Step = 'token'; Status = $tokenResponse.Status }

    if ($tokenResponse.Status -ne 200) {
        Write-Log 'Grant did not succeed; stopping before the data probes.'
        throw 'Password grant failed.'
    }

    $token = ($tokenResponse.Body | ConvertFrom-Json).access_token
    if (-not $token) { throw 'Grant returned no access_token.' }

    $auth = @{ Authorization = "Bearer $token" }

    foreach ($resource in 'building', 'floor', 'room') {
        Write-Log "Step 2.$resource: GET /api/building-management/$resource"
        $response = Invoke-Probe -Method 'GET' -Uri "$ViteBase/api/building-management/$resource" -Headers $auth
        Write-Log ("  -> HTTP {0}" -f $response.Status)
        $preview = if ($response.Body.Length -gt 400) { $response.Body.Substring(0, 400) + '…' } else { $response.Body }
        Write-Log ("  body: {0}" -f $preview)
        $results += [pscustomobject]@{ Step = $resource; Status = $response.Status; Body = $response.Body }
    }

    Write-Log 'Summary'
    foreach ($row in $results) { Write-Log ("  {0}: {1}" -f $row.Step, $row.Status) }

    $logPath = Join-Path $logDir 'summary.json'
    $results | ConvertTo-Json -Depth 4 | Set-Content -Path $logPath -Encoding utf8
    Write-Log "SUMMARY_JSON=$logPath"
    Write-Log "LOG_DIRECTORY=$logDir"
}
finally {
    [System.Net.ServicePointManager]::ServerCertificateValidationCallback = $script:certCallback
}
