# Local-only identity actor probe: Owner vs independent bank reviewer.
# READ-ONLY, HTTPS localhost:44311, no cookies, no redirects, no login credentials.
# Do not send tokens or API responses to a chat, a GitHub issue or CI logs.
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.Net.Http

$base = 'https://localhost:44311'
$handler = [System.Net.Http.HttpClientHandler]::new()
$handler.AllowAutoRedirect = $false
$handler.UseCookies = $false
$handler.UseDefaultCredentials = $false
$handler.PreAuthenticate = $false
$client = [System.Net.Http.HttpClient]::new($handler)
$client.Timeout = [TimeSpan]::FromSeconds(15)

function Read-ActorToken([string]$label) {
    Write-Host ("Paste NEW {0} access token locally. Input stays hidden." -f $label)
    $secret = Read-Host ("{0} token (hidden input)" -f $label) -AsSecureString
    $ptr = [IntPtr]::Zero
    $plain = $null
    try {
        $ptr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secret)
        $plain = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($ptr)
    }
    finally {
        if ($ptr -ne [IntPtr]::Zero) {
            [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($ptr)
        }
        $secret = $null
    }
    if ([string]::IsNullOrWhiteSpace($plain)) {
        throw 'ACTOR_TOKEN_MISSING'
    }
    $plain = $plain.Trim().Trim([char]0xFEFF)
    $plain = [regex]::Replace($plain, '^\s*Authorization\s*:\s*Bearer\s+', '',
        [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
    $plain = [regex]::Replace($plain, '^\s*Bearer\s+', '',
        [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
    $plain = [regex]::Replace($plain, '\s+', '')
    $plain = $plain.Trim([char[]]@('"', "'"))
    $pieces = $plain.Split('.')
    if ($pieces.Length -ne 3) {
        throw 'ACTOR_TOKEN_FORMAT_INVALID'
    }
    foreach ($piece in $pieces) {
        if ($piece -notmatch '^[A-Za-z0-9_-]+$') {
            throw 'ACTOR_TOKEN_FORMAT_INVALID'
        }
    }
    try {
        $payload = $pieces[1].Replace('-', '+').Replace('_', '/')
        switch ($payload.Length % 4) {
            0 { break }
            2 { $payload += '==' ; break }
            3 { $payload += '=' ; break }
            default { throw 'invalid padding' }
        }
        $json = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($payload))
        $claims = ConvertFrom-Json -InputObject $json
    }
    catch {
        throw 'ACTOR_TOKEN_FORMAT_INVALID'
    }
    if ($null -eq $claims.exp -or
        [long]$claims.exp -le [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()) {
        throw 'ACTOR_TOKEN_EXPIRED'
    }
    if ([string]$claims.iss -cne 'https://localhost:44311/' -or
        @($claims.aud) -cnotcontains 'NgocTrangHouseManagementSystem') {
        throw 'ACTOR_TOKEN_ISSUER_AUDIENCE_MISMATCH'
    }
    if ([string]::IsNullOrWhiteSpace([string]$claims.sub)) {
        throw 'ACTOR_TOKEN_SUBJECT_MISSING'
    }
    if (Get-Command Set-Clipboard -ErrorAction SilentlyContinue) {
        try { Set-Clipboard -Value ' ' -ErrorAction Stop }
        catch { Write-Warning 'Could not clear clipboard; overwrite it with harmless text manually.' }
    }
    return [pscustomobject]@{
        Token = $plain
        Subject = [string]$claims.sub
        UserName = [string]$claims.preferred_username
        Roles = @($claims.role)
    }
}

function Invoke-ActorGet([string]$path, [string]$token) {
    $request = [System.Net.Http.HttpRequestMessage]::new(
        [System.Net.Http.HttpMethod]::Get, ($base + $path))
    try {
        $request.Headers.Accept.ParseAdd('application/json')
        $request.Headers.Authorization =
            [System.Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer', $token)
        try {
            $response = $client.SendAsync($request).GetAwaiter().GetResult()
        }
        catch {
            throw 'ACTOR_HTTP_TRANSPORT_FAILED: localhost:44311 not reachable over trusted HTTPS'
        }
        try {
            $type = if ($response.Content.Headers.ContentType) {
                $response.Content.Headers.ContentType.MediaType
            } else { 'none' }
            return [pscustomobject]@{
                Code = [int]$response.StatusCode
                Type = $type
            }
        }
        finally { $response.Dispose() }
    }
    finally { $request.Dispose() }
}

try {
    # Positive proof before any negative authorization assertion.
    # The JWT claims are untrusted until the Host successfully validates them.
    $owner = Read-ActorToken 'owner-uat'
    try {
        if ($owner.UserName -cne 'owner-uat') {
            throw 'OWNER_IDENTITY_MISMATCH: Owner token does not belong to owner-uat'
        }
        if ($owner.Roles -cnotcontains 'Owner') {
            throw 'OWNER_ROLE_CLAIM_MISSING: Owner role must be granted before login'
        }
        $ownerProfile = Invoke-ActorGet '/api/account/my-profile' $owner.Token
        Write-Host ("OWNER_PROFILE HTTP={0} TYPE={1}" -f $ownerProfile.Code, $ownerProfile.Type)
        if ($ownerProfile.Code -ne 200 -or $ownerProfile.Type -notmatch 'json') {
            throw 'OWNER_AUTH_NOT_PROVED: profile GET must be HTTP 200 JSON'
        }
        $ownerUsers = Invoke-ActorGet '/api/identity/users?maxResultCount=1' $owner.Token
        Write-Host ("OWNER_USERS HTTP={0} TYPE={1}" -f $ownerUsers.Code, $ownerUsers.Type)
        if ($ownerUsers.Code -ne 403) {
            throw 'OWNER_NEGATIVE_NOT_PROVED: identity users list must deny Owner with HTTP 403'
        }
        $ownerSubject = $owner.Subject
    }
    finally { $owner = $null }

    $reviewer = Read-ActorToken 'reviewer-uat'
    try {
        if ($reviewer.UserName -cne 'reviewer-uat') {
            throw 'REVIEWER_IDENTITY_MISMATCH: Reviewer token does not belong to reviewer-uat'
        }
        if ($reviewer.Roles -ccontains 'Owner' -or $reviewer.Roles -ccontains 'admin') {
            throw 'REVIEWER_ROLE_ESCALATION: reviewer must not have Owner or admin role'
        }
        if ($reviewer.Subject -ceq $ownerSubject) {
            throw 'ACTOR_SEPARATION_FAILED: Owner and reviewer use the same subject'
        }
        $reviewerProfile = Invoke-ActorGet '/api/account/my-profile' $reviewer.Token
        Write-Host ("REVIEWER_PROFILE HTTP={0} TYPE={1}" -f $reviewerProfile.Code, $reviewerProfile.Type)
        if ($reviewerProfile.Code -ne 200 -or $reviewerProfile.Type -notmatch 'json') {
            throw 'REVIEWER_AUTH_NOT_PROVED: profile GET must be HTTP 200 JSON'
        }
        $reviewerUsers = Invoke-ActorGet '/api/identity/users?maxResultCount=1' $reviewer.Token
        Write-Host ("REVIEWER_USERS HTTP={0} TYPE={1}" -f $reviewerUsers.Code, $reviewerUsers.Type)
        if ($reviewerUsers.Code -ne 403) {
            throw 'REVIEWER_NEGATIVE_NOT_PROVED: identity users list must deny Reviewer with HTTP 403'
        }
    }
    finally { $reviewer = $null; $ownerSubject = $null }
    Write-Host 'IDENTITY_TWO_ACTOR_PASS: two distinct active bearer identities, profile access and identity admin denial.' -ForegroundColor Green
    Write-Host 'D04 FINANCIAL DUAL AUTHORITY REMAINS HOLD: no reviewer/Owner financial grants or posting has been tested.' -ForegroundColor Yellow
}
finally {
    $client.Dispose()
    $handler.Dispose()
}
