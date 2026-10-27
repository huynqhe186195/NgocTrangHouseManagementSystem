param(
    [ValidateSet('Quick', 'Full')]
    [string]$Mode = 'Quick'
)

# ONE-COMMAND, SAFE, LOCAL QUALITY GATE.
# Only .NET test projects that run with isolated SQLite/in-memory test
# servers are included. No PostgreSQL test project, DbMigrator, EF migration,
# production credentials, role changes, or HTTP production host is invoked.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
Push-Location $root
try {
    $branch = (& git branch --show-current | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or $branch -ne 'wip/phase-2g2') {
        throw "Safety stop: expected wip/phase-2g2, got '$branch'."
    }
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        throw 'dotnet SDK is required.'
    }

    Write-Host "=== SECURITY REGRESSION / $Mode / $branch ==="

    $scripts = @(
        '.\etc\scripts\check-building-scope-design-only-gate.ps1',
        '.\etc\scripts\check-invoice-revision-no-write-gate.ps1',
        '.\etc\scripts\check-move-out-lifecycle-gate.ps1'
    )
    foreach ($script in $scripts) {
        Write-Host "== SOURCE GATE: $script =="
        & powershell -NoProfile -ExecutionPolicy Bypass -File $script
        if ($LASTEXITCODE -ne 0) {
            throw "Safety source gate failed: $script (exit $LASTEXITCODE)"
        }
    }

    # Compile PostgreSQL-specific test code without ever connecting to PG.
    # PostgreSQL tests are opt-in and NEVER executed by this Full batch.
    Write-Host '== COMPILE ONLY: PostgreSQL isolated test project (no DB) =='
    & dotnet build '.\modules\buildingmanagement\test\BuildingManagement.PostgreSql.Tests\BuildingManagement.PostgreSql.Tests.csproj' --nologo
    if ($LASTEXITCODE -ne 0) {
        throw 'PostgreSQL test project failed to compile. No database was accessed.'
    }

    $projects = @(
        @{
            Name = 'Domain'
            Path = '.\modules\buildingmanagement\test\BuildingManagement.Domain.Tests\BuildingManagement.Domain.Tests.csproj'
            QuickFilter = $null
        },
        @{
            Name = 'Application'
            Path = '.\modules\buildingmanagement\test\BuildingManagement.Application.Tests\BuildingManagement.Application.Tests.csproj'
            QuickFilter = 'FullyQualifiedName~BuildingBusinessScopeDesignGate_Tests|FullyQualifiedName~BuildingOperatorChangeDesignGate_Tests|FullyQualifiedName~BuildingScopedReadDesignGate_Tests|FullyQualifiedName~BuildingOperatorAccessGrant_Tests|FullyQualifiedName~InvoiceDraftRevisionServerAccessGate_Tests|FullyQualifiedName~MoveOutCompletionReadinessEvaluator_Tests|FullyQualifiedName~Should_Refuse_Direct_End_Of_Active_Contract_Without_MoveOut_Settlement'
        },
        @{
            Name = 'SQLite EF'
            Path = '.\modules\buildingmanagement\test\BuildingManagement.EntityFrameworkCore.Tests\BuildingManagement.EntityFrameworkCore.Tests.csproj'
            QuickFilter = 'FullyQualifiedName~BuildingBusinessScopeEfIntegration_Tests|FullyQualifiedName~BuildingOperatorAccessGrantDesignSqlite_Tests|FullyQualifiedName~BuildingOperatorGrantCandidateQuery_Tests|FullyQualifiedName~BuildingBusinessScopedCollectionQuery_Tests|FullyQualifiedName~BuildingScopedCollectionStore_Tests|FullyQualifiedName~BuildingScopedReadSqliteComposition_Tests|FullyQualifiedName~InvoiceDraftRevisionServerGraphIntegration_Tests|FullyQualifiedName~MoveOutCompletionReadinessSqlite_Tests'
        },
        @{
            Name = 'HTTP Host (isolated)'
            Path = '.\test\NgocTrangHouseManagementSystem.HttpApi.Host.Tests\NgocTrangHouseManagementSystem.HttpApi.Host.Tests.csproj'
            QuickFilter = $null
        }
    )

    # Fail fast before hundreds of EF tests report the SAME container
    # initialization error. This old, previously passing SQLite case is
    # intentionally used as the smoke baseline. A new app-service DI /
    # ABP proxy regression will surface here with one readable stack.
    $sqliteProject = '.\\modules\\buildingmanagement\\test\\BuildingManagement.EntityFrameworkCore.Tests\\BuildingManagement.EntityFrameworkCore.Tests.csproj'
    Write-Host '== SQLITE MODULE STARTUP SMOKE: legacy scope integration =='
    & dotnet test $sqliteProject --filter 'FullyQualifiedName~BuildingBusinessScopedCollectionQuery_Tests.LISTSCOPE01' --logger 'console;verbosity=normal'
    if ($LASTEXITCODE -ne 0) {
        throw 'SQLite ABP startup smoke FAILED. Inspect this one test exception before the full EF suite.'
    }

    foreach ($project in $projects) {
        Write-Host "== DOTNET TEST: $($project.Name) =="
        $testArguments = @(
            'test', $project.Path, '--logger', 'console;verbosity=minimal'
        )
        if ($Mode -eq 'Quick' -and $project.QuickFilter) {
            $testArguments += @('--filter', $project.QuickFilter)
        }
        & dotnet @testArguments
        if ($LASTEXITCODE -ne 0) {
            throw "$($project.Name) FAILED. Stop before integration/deployment."
        }
    }

    Write-Host '=== QUALITY GATE PASS: ALL REQUESTED TEST RUNS SUCCEEDED ==='
    Write-Host 'NO DATABASE MIGRATION, NO POSTGRESQL TEST (COMPILE ONLY), NO LIVE APPLICATION STARTED.'
    Write-Host 'Source-only checks are NOT proof of authenticated cross-building CRUD security.'

    # Shows any unexpected changes without modifying them. In particular the
    # operator's untracked revision-ledger review SQL is NEVER touched.
    & git status --short
    if ($LASTEXITCODE -ne 0) {
        throw 'Cannot read Git status.'
    }
}
finally {
    Pop-Location
}
