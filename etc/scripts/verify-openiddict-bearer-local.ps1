
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


function Get-NormalizedJwt([string]$inputText) {
    if ([string]::IsNullOrWhiteSpace($inputText)) {
        throw 'TOKEN_FORMAT_INVALID: no token was pasted.'
    }

    # Accept Chrome's header value, "Bearer <JWT>", or a plain JWT.
    # Whitespace added by line wrapping or clipboard copying is removed.
    $value = $inputText.Trim().Trim([char]0xFEFF).Trim('"', "'")
    $value = [regex]::Replace(
        $value, '^\s*Authorization\s*:\s*Bearer\s+', '',
        [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
    $value = [regex]::Replace(
        $value, '^\s*Bearer\s+', '',
        [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
    $value = [regex]::Replace($value, '\s+', '')
    $value = $value.Trim('"', "'")

    $parts = $value.Split('.')
    if ($parts.Length -ne 3) {
        throw 'TOKEN_FORMAT_INVALID: expected exactly three JWT segments.'
    }
    foreach ($part in $parts) {
        if ($part -notmatch '^[A-Za-z0-9_-]+, [string]$bearer) {
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
    $token = Get-NormalizedJwt -inputText $token
    Assert-LocalJwtClaims -jwt $token
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
            if ($valid.Code -eq 401) {
                throw 'AUTH_BEARER_REJECTED_401: wrong/stale signing key, expired or invalid JWT; re-authorize AFTER latest Host startup.'
            }
            if ($valid.Code -eq 302) {
                throw 'AUTH_BEARER_REDIRECT_302: Host is challenging with cookie login instead of accepting Bearer; inspect auth scheme configuration.'
            }
            if ($valid.Code -eq 403) {
                throw 'AUTH_BEARER_FORBIDDEN_403: JWT may be valid but this user lacks Identity.Users permission.'
            }
            throw ("AUTH_BEARER_NOT_PROVED: expected HTTP 200 JSON, got HTTP={0} TYPE={1}." -f $valid.Code, $valid.Type)
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
) {
            throw 'TOKEN_FORMAT_INVALID: JWT contains characters outside Base64URL.'
        }
    }
    return $value
}

function Assert-LocalJwtClaims([string]$jwt) {
    # This only checks local token syntax/expiry/target; it does NOT
    # cryptographically validate its signature. The Host does that.
    try {
        $bodyPart = $jwt.Split('.')[1].Replace('-', '+').Replace('_', '/')
        switch ($bodyPart.Length % 4) {
            0 { break }
            2 { $bodyPart += '==' ; break }
            3 { $bodyPart += '=' ; break }
            default { throw 'bad padding' }
        }
        $json = [System.Text.Encoding]::UTF8.GetString(
            [Convert]::FromBase64String($bodyPart))
        $claims = ConvertFrom-Json -InputObject $json
    }
    catch {
        throw 'TOKEN_FORMAT_INVALID: JWT payload is not valid Base64URL JSON.'
    }

    if ($null -eq $claims.exp -or
        [long]$claims.exp -le [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()) {
        throw 'TOKEN_EXPIRED_OR_NO_EXP: authorize again on the CURRENT running Host.'
    }
    if ([string]$claims.iss -cne 'https://localhost:44311/') {
        throw 'TOKEN_ISSUER_MISMATCH: token is not for local OpenIddict Host.'
    }
    if (@($claims.aud) -cnotcontains 'NgocTrangHouseManagementSystem') {
        throw 'TOKEN_AUDIENCE_MISMATCH: token is not for this API.'
    }
    Write-Host 'TOKEN_LOCAL_FORMAT_PASS: JWT syntax, expiry, issuer and audience are plausible; signature is checked by Host.' -ForegroundColor Green
}

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
