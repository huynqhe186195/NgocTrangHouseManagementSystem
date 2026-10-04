$ErrorActionPreference = "Stop"
$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$solutionRoot = Join-Path $scriptRoot "../../"

function Write-StudioStep([string]$Message) {
    [Console]::Out.WriteLine("[STUDIO:STEP] $Message")
    [Console]::Out.Flush()
}

Write-StudioStep "Building solution"
Set-Location $solutionRoot
dotnet build

if ($LASTEXITCODE -ne 0) {
    [Console]::Error.WriteLine("dotnet build FAILED with exit code $LASTEXITCODE")
    exit -1
}

$jobs = @()

Write-StudioStep "Installing client-side libraries"
$jobs += Start-Job -Name "InstallLibs" -ScriptBlock {
    $ErrorActionPreference = "Stop"
    Set-Location (Join-Path $using:scriptRoot "../../")
    abp install-libs

    if ($LASTEXITCODE -ne 0) {
        throw "abp install-libs exited with code $LASTEXITCODE"
    }
}

$jobs += Start-Job -Name "DbMigrator" -ScriptBlock {
    $ErrorActionPreference = "Stop"
    Set-Location (Join-Path $using:scriptRoot "../../src/NgocTrangHouseManagementSystem.DbMigrator")
    dotnet run
    dotnet run

    if ($LASTEXITCODE -ne 0) {
        throw "dotnet run (DbMigrator) exited with code $LASTEXITCODE"
    }
}


Write-StudioStep "Waiting for initialization jobs"
while (($jobs | Where-Object { $_.State -eq 'Running' }).Count -gt 0) {
    $running = ($jobs | Where-Object { $_.State -eq 'Running' } | ForEach-Object { $_.Name }) -join ', '
    if (-not [string]::IsNullOrWhiteSpace($running)) {
        Write-StudioStep "Running: $running"
    }
    Start-Sleep -Seconds 2
}

Wait-Job $jobs | Out-Null
# Native tools can write warnings to stderr; keep them visible without failing completed jobs.
$jobs | Receive-Job -ErrorAction Continue

$failed = $jobs | Where-Object { $_.State -eq 'Failed' }
$hasError = $failed.Count -gt 0

if ($hasError) {
    foreach ($job in $failed) {
        [Console]::Error.WriteLine("Job '$($job.Name)' FAILED")
    }

    Remove-Job $jobs | Out-Null
    exit -1
}

Remove-Job $jobs | Out-Null
exit 0
