param(
    [Parameter(Mandatory = $true)][string]$Path,
    [Parameter(Mandatory = $true)][ValidateRange(1, 100000)][int]$ExpectedTests
)
# Read the machine-readable VSTest/TRX summary. Do not infer success from
# console phrases, historical test counts, or a previous run's output.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
    throw "Missing current-run TRX result: $Path"
}

try {
    [xml]$result = Get-Content -LiteralPath $Path -Raw -ErrorAction Stop
}
catch {
    throw "Unreadable TRX result '$Path': $($_.Exception.Message)"
}

$counters = $result.SelectSingleNode(
    "/*[local-name()='TestRun']/*[local-name()='ResultSummary']/*[local-name()='Counters']")
if ($null -eq $counters) {
    throw "TRX ResultSummary/Counters is missing: $Path"
}

function Read-Counter([string]$name, [bool]$required = $true) {
    $attribute = $counters.Attributes[$name]
    if ($null -eq $attribute) {
        if ($required) { throw "TRX missing required counter '$name': $Path" }
        return 0
    }
    $value = 0
    if (-not [int]::TryParse($attribute.Value, [ref]$value) -or $value -lt 0) {
        throw "TRX has invalid counter '$name': $Path"
    }
    return $value
}

$total = Read-Counter 'total'
$executed = Read-Counter 'executed'
$passed = Read-Counter 'passed'
$failed = Read-Counter 'failed'
$skipped = Read-Counter 'skipped' $false
$notExecuted = Read-Counter 'notExecuted' $false

if ($total -ne $ExpectedTests -or $executed -ne $ExpectedTests -or
    $passed -ne $ExpectedTests -or $failed -ne 0 -or
    $skipped -ne 0 -or $notExecuted -ne 0) {
    throw "TRX mismatch: expected=$ExpectedTests, total=$total, executed=$executed, passed=$passed, failed=$failed, skipped=$skipped, notExecuted=$notExecuted. File=$Path"
}

"TRX VERIFIED: passed=$passed/$total; failed=$failed; skipped=$skipped; file=$Path"
