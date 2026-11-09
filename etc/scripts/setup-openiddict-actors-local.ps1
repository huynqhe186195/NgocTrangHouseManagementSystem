# SYNTHETIC LOCAL ONLY: create two least-privilege ABP Identity accounts
# in the existing labelled disposable first-boot PostgreSQL environment.
# No payOS or D04 controls are enabled or modified.
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.Net.Http

# Refuse to create users against an unverified/production-like environment.
$containers = @(& docker ps --filter 'label=nt.scope=local-first-boot-20261109' --format '{{.ID}}')
if ($LASTEXITCODE -ne 0 -or $containers.Count -ne 1 -or
    [string]::IsNullOrWhiteSpace([string]$containers[0])) {
    throw 'UAT_ENV_DENIED: exactly one running labelled disposable first-boot container required.'
}
$dockerPort = (& docker port $containers[0] '5432/tcp' 2>$null | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or $dockerPort -cne '127.0.0.1:15433') {
    throw 'UAT_ENV_DENIED: dedicated disposable PostgreSQL must bind 127.0.0.1:15433.'
}
$base = 'https://localhost:44311'
$handler = [System.Net.Http.HttpClientHandler]::new()
$handler.AllowAutoRedirect = $false
$handler.UseCookies = $false
$handler.UseDefaultCredentials = $false
$handler.PreAuthenticate = $false
$client = [System.Net.Http.HttpClient]::new($handler)
$client.Timeout = [TimeSpan]::FromSeconds(15)
$adminToken = $null

function Read-SecretText([string]$prompt) {
    $s = Read-Host $prompt -AsSecureString
    $p = [IntPtr]::Zero
    try {
        $p = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($s)
        return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($p)
    }
    finally {
        if ($p -ne [IntPtr]::Zero) {
            [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($p)
        }
        $s = $null
    }
}
function Check-Password([string]$candidate, [string]$label) {
    if ($candidate.Length -lt 16 -or $candidate -cnotmatch '[A-Z]' -or
        $candidate -cnotmatch '[a-z]' -or $candidate -notmatch '[0-9]' -or
        $candidate -notmatch '[^A-Za-z0-9]') {
        throw ("{0}_PASSWORD_POLICY: use 16+ chars including upper/lower/digit/symbol." -f $label)
    }
}
function Unwrap-Data($reply) {
    if ($null -eq $reply) { throw 'UAT_API_RESPONSE_EMPTY' }
    if ($null -ne $reply.PSObject.Properties['data']) { return $reply.data }
    if ($null -ne $reply.PSObject.Properties['result']) { return $reply.result }
    return $reply
}
function Invoke-LocalIdentity([string]$method, [string]$path, $payload) {
    $request = [System.Net.Http.HttpRequestMessage]::new(
        [System.Net.Http.HttpMethod]::new($method), ($base + $path))
    try {
        $request.Headers.Accept.ParseAdd('application/json')
        $request.Headers.Authorization =
            [System.Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer', $adminToken)
        if ($null -ne $payload) {
            $json = ConvertTo-Json -InputObject $payload -Depth 8 -Compress
            $request.Content = [System.Net.Http.StringContent]::new(
                $json, [Text.Encoding]::UTF8, 'application/json')
            $json = $null
        }
        try {
            $response = $client.SendAsync($request).GetAwaiter().GetResult()
        }
        catch {
            throw 'UAT_TRANSPORT_FAILED: could not reach trusted localhost HTTPS backend.'
        }
        try {
            $code = [int]$response.StatusCode
            if ($code -lt 200 -or $code -ge 300) {
                throw ("UAT_API_DENIED: {0} {1} HTTP={2}. No response body printed." -f $method,$path,$code)
            }
            $body = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
            if ([string]::IsNullOrWhiteSpace($body)) { throw 'UAT_API_RESPONSE_EMPTY' }
            $parsed = ConvertFrom-Json -InputObject $body
            $body = $null
            return (Unwrap-Data $parsed)
        }
        finally { $response.Dispose() }
    }
    finally { $request.Dispose() }
}
function Find-Role([string]$name) {
    $roles = Invoke-LocalIdentity 'GET' '/api/identity/roles?maxResultCount=1000' $null
    $items = @($roles.items)
    return @($items | Where-Object { $_.name -ceq $name })
}
function Assert-UserRoles([string]$userId, [string]$expected, [string]$label) {
    $roles = Invoke-LocalIdentity 'GET' ("/api/identity/users/{0}/roles" -f $userId) $null
    $names = @($roles.items | ForEach-Object { $_.name })
    if ($names -cnotcontains $expected -or $names -ccontains 'admin' -or
        ($label -eq 'REVIEWER' -and $names -ccontains 'Owner')) {
        throw ("{0}_ROLE_PERSISTENCE_FAILED: expected only assigned nonadmin duties." -f $label)
    }
    if ($label -eq 'OWNER' -and $names -ccontains 'BankReviewer') {
        throw 'OWNER_ROLE_SEPARATION_FAILED: Owner must not also be reviewer.'
    }
    Write-Host ("{0}_ROLE_PERSISTED: {1}" -f $label,$expected) -ForegroundColor Green
}
function Ensure-Role([string]$name) {
    $existing = @(Find-Role $name)
    if ($existing.Count -gt 1) { throw 'UAT_ROLE_DUPLICATE' }
    if ($existing.Count -eq 1) {
        if ([bool]$existing[0].isDefault) {
            throw 'UAT_ROLE_UNSAFE_DEFAULT: role automatically grants to new users.'
        }
        Write-Host ("ROLE_ALREADY_PRESENT: {0}" -f $name)
    }
    else {
        $role = Invoke-LocalIdentity 'POST' '/api/identity/roles' @{
            name=$name; isDefault=$false; isPublic=$false
        }
        if ([string]$role.name -cne $name) { throw 'UAT_ROLE_CREATE_MISMATCH' }
        Write-Host ("ROLE_CREATED: {0}" -f $name) -ForegroundColor Green
    }
}
function Ensure-User([string]$name,[string]$role,[string]$label) {
    $list = Invoke-LocalIdentity 'GET' ("/api/identity/users?filter={0}&maxResultCount=100" -f $name) $null
    $existing = @($list.items | Where-Object { $_.userName -ceq $name })
    if ($existing.Count -gt 1) { throw ("{0}_DUPLICATE_USER" -f $label) }
    if ($existing.Count -eq 1) {
        if (-not [bool]$existing[0].isActive) { throw ("{0}_USER_INACTIVE" -f $label) }
        $id = [string]$existing[0].id
        Assert-UserRoles $id $role $label
        Write-Host ("{0}_USER_ALREADY_PRESENT: {1}" -f $label,$name)
        return $id
    }

    $password = Read-SecretText ("New disposable {0} password (hidden, 16+ chars)" -f $name)
    Check-Password $password $label
    try {
        $newUser = Invoke-LocalIdentity 'POST' '/api/identity/users' @{
            userName=$name; name=$label; surname='LocalUAT'
            email=("{0}@example.invalid" -f $name)
            phoneNumber=''; password=$password; isActive=$true
            lockoutEnabled=$true; roleNames=@($role)
        }
    }
    finally { $password = $null }
    if ([string]$newUser.userName -cne $name -or
        [string]::IsNullOrWhiteSpace([string]$newUser.id)) {
        throw ("{0}_USER_CREATE_MISMATCH" -f $label)
    }
    $id = [string]$newUser.id
    Assert-UserRoles $id $role $label
    Write-Host ("{0}_USER_CREATED: {1}" -f $label,$name) -ForegroundColor Green
    return $id
}

try {
    Write-Host 'LOCAL IDENTITY UAT SETUP ONLY. No financial permissions, no building grants, no payOS.'
    Write-Host 'Enter a CURRENT admin Swagger access token; never put it in chat or a command line.'
    $adminToken = Read-SecretText 'admin Bearer token (hidden input)'
    if ([string]::IsNullOrWhiteSpace($adminToken)) { throw 'UAT_ADMIN_TOKEN_MISSING' }
    $adminToken = $adminToken.Trim()
    $adminToken = [regex]::Replace($adminToken, '^\s*Bearer\s+', '',
        [Text.RegularExpressions.RegexOptions]::IgnoreCase)
    $adminToken = [regex]::Replace($adminToken, '\s+', '')
    if ($adminToken.Split('.').Length -ne 3) { throw 'UAT_ADMIN_TOKEN_FORMAT_INVALID' }

    # Guard: token must have real ABP management permissions, not just an active session.
    $check = Invoke-LocalIdentity 'GET' '/api/identity/users?maxResultCount=1' $null
    if ($null -eq $check.PSObject.Properties['items']) { throw 'UAT_ADMIN_USERS_UNEXPECTED_SHAPE' }
    Write-Host 'ADMIN_IDENTITY_MANAGEMENT_API_PASS' -ForegroundColor Green

    Ensure-Role 'Owner'
    Ensure-Role 'BankReviewer'
    $ownerId = Ensure-User 'owner-uat' 'Owner' 'OWNER'
    $reviewerId = Ensure-User 'reviewer-uat' 'BankReviewer' 'REVIEWER'
    if ($ownerId -ceq $reviewerId) { throw 'UAT_ACTOR_ID_COLLISION' }
    Write-Host 'IDENTITY_UAT_FIXTURES_PASS: two distinct active real ABP Identity accounts with persisted separate roles.' -ForegroundColor Green
    Write-Host 'Keep each temporary password LOCAL. Next, get each users own OpenIddict token and run verify-openiddict-actors-local.ps1.'
}
finally {
    $adminToken = $null
    $client.Dispose()
    $handler.Dispose()
}
