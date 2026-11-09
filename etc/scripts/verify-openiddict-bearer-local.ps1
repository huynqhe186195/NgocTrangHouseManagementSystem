
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

    # Accept "Authorization: Bearer JWT", "Bearer JWT" or just JWT.
    $value = $inputText.Trim().Trim([char]0xFEFF)
    $value = $value.Trim([char[]]@('"', "'"))
    $value = [regex]::Replace(
        $value, '^\s*Authorization\s*:\s*Bearer\s+', '',
        [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
    $value = [regex]::Replace(
        $value, '^\s*Bearer\s+', '',
        [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
    $value = [regex]::Replace($value, '\s+', '')
    $value = $value.Trim([char[]]@('"', "'"))

    $parts = $value.Split('.')
    if ($parts.Length -ne 3) {
        throw 'TOKEN_FORMAT_INVALID: expected three JWT sections separated by dots.'
    }
    foreach ($part in $parts) {
        if ($part -notmatch '^[A-Za-z0-9_-]+$') {
            throw 'TOKEN_FORMAT_INVALID: invalid Base64URL characters.'
        }
    }
    return $value
}

function Assert-LocalJwtClaims([string]$jwt) {
    # Local syntax checks only. The Host is responsible for signature validation.
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
        throw 'TOKEN_EXPIRED_OR_NO_EXP: authorize again on the current running Host.'
    }
    if ([string]$claims.iss -cne 'https://localhost:44311/') {
        throw 'TOKEN_ISSUER_MISMATCH: token was not issued for localhost:44311.'
    }
    if (@($claims.aud) -cnotcontains 'NgocTrangHouseManagementSystem') {
        throw 'TOKEN_AUDIENCE_MISMATCH: token is not for this API.'
    }
    Write-Host 'TOKEN_LOCAL_FORMAT_PASS: JWT syntax, expiry, issuer, audience checked locally (not cryptographic validation).' -ForegroundColor Green
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
            switch ($valid.Code) {
                401 { throw 'AUTH_BEARER_REJECTED_401: request rejected; generate NEW token after current Host starts.' }
                302 { throw 'AUTH_BEARER_REDIRECT_302: Host redirected to cookie login; investigate authentication scheme.' }
                403 { throw 'AUTH_BEARER_FORBIDDEN_403: bearer might be valid but lacks identity admin permission.' }
                default { throw ("AUTH_BEARER_NOT_PROVED: HTTP={0} TYPE={1}." -f $valid.Code, $valid.Type) }
            }
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
