
# Read-only OpenIddict test against localhost ONLY.
# NO cookies, NO redirects, NO Windows auth; token never saved or printed.
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.Net.Http

$uri = 'https://localhost:44311/api/identity/users?maxResultCount=1'
$handler = [System.Net.Http.HttpClientHandler]::new()
$handler.UseCookies = $false
$handler.AllowAutoRedirect = $false
$handler.UseDefaultCredentials = $false
$handler.PreAuthenticate = $false
$client = [System.Net.Http.HttpClient]::new($handler)
$client.Timeout = [TimeSpan]::FromSeconds(15)

function Invoke-ReadOnlyGet([string]$name, [string]$bearer) {
    $request = [System.Net.Http.HttpRequestMessage]::new(
        [System.Net.Http.HttpMethod]::Get, $uri)
    try {
        $request.Headers.Accept.ParseAdd('application/json')
        if (-not [string]::IsNullOrWhiteSpace($bearer)) {
            $request.Headers.Authorization =
                [System.Net.Http.Headers.AuthenticationHeaderValue]::new(
                    'Bearer', $bearer)
        }
        $response = $client.SendAsync($request).GetAwaiter().GetResult()
        try {
            $code = [int]$response.StatusCode
            $type = if ($response.Content.Headers.ContentType) {
                $response.Content.Headers.ContentType.MediaType
            } else { 'none' }
            Write-Host ("{0}: HTTP={1} TYPE={2}" -f $name, $code, $type)
            return [pscustomobject]@{ Code=$code; Type=$type }
        }
        finally { $response.Dispose() }
    }
    finally { $request.Dispose() }
}

try {
    Write-Host 'Checking local Identity API. All requests are GET; no cookie jar; no redirect follow.'
    $anon = Invoke-ReadOnlyGet -name 'NO_COOKIE_NO_TOKEN' -bearer $null
    $invalid = Invoke-ReadOnlyGet -name 'INVALID_BEARER' -bearer 'intentionally.invalid.token'
    if ($anon.Code -eq 200 -or $invalid.Code -eq 200) {
        throw 'AUTH_NEGATIVE_FAILED: unauthenticated/invalid bearer request returned HTTP 200.'
    }
    Write-Host 'Negative checks passed: neither anonymous nor malformed bearer returned HTTP 200.' -ForegroundColor Green

    Write-Host 'Get the ACCESS TOKEN from your own Swagger DevTools Network request.'
    Write-Host 'Paste ONLY the access token at the hidden prompt; never paste it in chat or into commands.'
    $secret = Read-Host 'OAuth access token (hidden input)' -AsSecureString
    $bstr = [IntPtr]::Zero
    $token = $null
    try {
        $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secret)
        $token = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr)
    }
    finally {
        if ($bstr -ne [IntPtr]::Zero) {
            [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr)
        }
    }
    $secret = $null
    if ([string]::IsNullOrWhiteSpace($token)) { throw 'Missing access token.' }
    $token = $token.Trim()
    if ($token.StartsWith('Bearer ', [StringComparison]::OrdinalIgnoreCase)) {
        $token = $token.Substring(7).Trim()
    }
    if ([string]::IsNullOrWhiteSpace($token)) {
        throw 'Missing access token.'
    }
    # Windows PowerShell 5.1 throws on Set-Clipboard -Value ''.
    # Replace clipboard with a single space. Failure to clear it must NOT
    # interrupt the independent bearer verification. Never echo the token.
    if (Get-Command Set-Clipboard -ErrorAction SilentlyContinue) {
        try {
            Set-Clipboard -Value ' ' -ErrorAction Stop
        }
        catch {
            Write-Warning 'Clipboard could not be cleared. Copy harmless text over it manually after testing.'
        }
    }
    try {
        $valid = Invoke-ReadOnlyGet -name 'ADMIN_BEARER_ONLY' -bearer $token
        if ($valid.Code -ne 200 -or $valid.Type -notmatch 'json') {
            throw 'AUTH_BEARER_NOT_PROVED: expected HTTP 200 JSON for the valid admin bearer alone.'
        }
        Write-Host 'AUTH_BEARER_ONLY_PASS: local API accepted independent admin bearer without cookies or redirects.' -ForegroundColor Green
    }
    finally {
        $token = $null
    }
}
finally {
    $client.Dispose()
    $handler.Dispose()
}
