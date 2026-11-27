#requires -Version 5.1
[CmdletBinding()]
param([switch]$List, [ValidateNotNullOrEmpty()][string]$TestName)
if ($List -and $TestName) {
    throw 'Choose either a listing mode or one exact test name.'
}

$ErrorActionPreference = 'Stop'
# Windows PowerShell 5.1 ignores this; PS7 must leave native exits to the runner.
$PSNativeCommandUseErrorActionPreference = $false
$repo = Split-Path -Parent $PSScriptRoot
$manifest = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot 'TestSuites.json') | ConvertFrom-Json
$checks = @($manifest)
if ($checks.Count -eq 0) { throw 'The automatic test registry is empty.' }
$registered = @{}
foreach ($check in $checks) {
    if ($check.name -notmatch '^[A-Za-z][A-Za-z0-9.-]*$' -or $registered.ContainsKey($check.name) -or
        $check.project -notmatch '^[A-Za-z][A-Za-z0-9.-]*$' -or
        $check.framework -notmatch '^net[0-9.]+(-windows[0-9.]*)?$' -or
        $check.platform -notin @('Any', 'Windows')) {
        throw 'The automatic test registry has an invalid or duplicated entry.'
    }
    $registered[$check.name] = $check
}
$requested = if ($TestName) { $TestName } else { 'All' }
if ($TestName) {
    if ($TestName -cnotmatch '^[A-Za-z][A-Za-z0-9.-]+$') {
        Write-Host "Unknown test: $TestName. Use -List."
        exit 2
    }
    $checks = @($checks | Where-Object {
        $_.cases -ccontains $TestName -or
            ($_.selectionPrefix -and $TestName.StartsWith($_.selectionPrefix, [StringComparison]::Ordinal))
    })
    if ($checks.Count -ne 1) {
        Write-Host "Unknown or ambiguous test: $TestName. Use -List."
        exit 2
    }
}
$dotnet = (Get-Command dotnet -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
$git = Get-Command git -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
$artifactsRoot = Join-Path ([IO.Path]::GetTempPath()) ('HagoromoChan-Tests-' + [Guid]::NewGuid().ToString('N'))
function Get-TestAssembly($Check, [string]$Artifacts) {
    # Manifest projects target one framework; the SDK artifacts pivot for a
    # single-target Release build is "release", not the normal TFM directory.
    return Join-Path $Artifacts "bin/$($Check.project)/release/$($Check.project).dll"
}
Push-Location $repo
try {
    $revision = 'unknown'
    if ($git) {
        try {
            $value = & $git.Source rev-parse --short HEAD 2>$null
            if ($LASTEXITCODE -eq 0 -and $value) { $revision = $value }
        } catch {
            # Revision display is optional and must not block any check.
        }
    }
    if ($List) {
        $listingFailed = $false
        foreach ($check in $checks) {
            foreach ($case in $check.cases) {
                Write-Output "$case [$($check.platform)]"
            }
            if (-not $check.listArguments) { continue }
            $project = Join-Path 'tests' $check.project
            $artifacts = Join-Path $artifactsRoot $check.name
            try {
                & $dotnet build $project -c Release --no-incremental --artifacts-path $artifacts | Out-Host
                if ($LASTEXITCODE -ne 0) { $listingFailed = $true; continue }
                $dll = Get-TestAssembly $check $artifacts
                if (-not (Test-Path -LiteralPath $dll -PathType Leaf)) {
                    throw "Fresh test assembly not found: $dll"
                }
                $listingArguments = @($dll) + @($check.listArguments)
                & $dotnet @listingArguments
                if ($LASTEXITCODE -ne 0) { $listingFailed = $true }
            } catch {
                Write-Host "ERROR $($check.name) list: $($_.Exception.Message)"
                $listingFailed = $true
            }
        }
        if ($listingFailed) { exit 1 }
        exit 0
    }
    Write-Host "HagoromoChan tests: $requested (HEAD $revision)"
    $results = @()
    $failedTestIds = @()
    $failureDetails = @{}
    $failed = $false
    $unsupported = $false
    foreach ($check in $checks) {
        $name = $check.name
        if ($check.platform -eq 'Windows' -and $env:OS -ne 'Windows_NT') {
            $results += "SKIP $name (requires an interactive Windows desktop)"
            $unsupported = $true
            if ($TestName) { $failed = $true }
            continue
        }
        $project = Join-Path 'tests' $check.project
        $artifacts = Join-Path $artifactsRoot $name

        Write-Host "RUN  $name build"
        $code = $null
        try {
            & $dotnet build $project -c Release --no-incremental --artifacts-path $artifacts
            $code = $LASTEXITCODE
        } catch {
            Write-Host "ERROR ${name} build: $($_.Exception.Message)"
        }
        if ($null -eq $code -or $code -ne 0) {
            $failed = $true
            $detail = if ($null -eq $code) { 'not started' } else { "exit=$code" }
            $results += "FAIL $name build ($detail)"
            $results += "SKIP $name run (fresh build failed)"
            continue
        }
        $results += "PASS $name build (exit=0)"

        $dll = Get-TestAssembly $check $artifacts
        # Invoke the fresh binary directly so there is no second build.
        $testArguments = @($dll)
        if ($check.arguments) { $testArguments += $check.arguments }
        if ($TestName -and $check.selectArgument) { $testArguments += @($check.selectArgument, $TestName) }
        Write-Host "RUN  $name run"
        $code = $null
        $reportedTestIds = @()
        try {
            if (-not (Test-Path -LiteralPath $dll -PathType Leaf)) {
                throw "Fresh test assembly not found: $dll"
            }
            # Only stdout is parsed. Native stderr remains visible and does
            # not become a terminating PowerShell error on 5.1.
            & $dotnet @testArguments | ForEach-Object {
                Write-Host $_
                if ($_ -cmatch '^TEST-FAIL ([A-Za-z][A-Za-z0-9.-]*): (.*)$') {
                    $id = $Matches[1]
                    if (($check.selectionPrefix -and $id.StartsWith($check.selectionPrefix, [StringComparison]::Ordinal)) -or
                        $check.cases -ccontains $id) {
                        $reportedTestIds += $id
                        $failureDetails[$id] = $Matches[2]
                    }
                }
            }
            $code = $LASTEXITCODE
        } catch {
            Write-Host "ERROR ${name} run: $($_.Exception.Message)"
        }
        if ($null -eq $code -or $code -ne 0) {
            $failed = $true
            $detail = if ($null -eq $code) { 'not started' } else { "exit=$code" }
            $results += "FAIL $name run ($detail)"
            if ($reportedTestIds.Count -gt 0) {
                $failedTestIds += $reportedTestIds
            } elseif ($null -ne $code -and $check.cases -and @($check.cases).Count -eq 1) {
                # This executable owns exactly one registered test. Report
                # its ID even if it exited before writing a result line.
                $failedTestIds += $check.cases[0]
            }
            continue
        }
        $results += "PASS $name run (exit=0)"
    }
    Write-Host "HagoromoChan test summary: $requested"
    foreach ($result in $results) { Write-Host $result }
    if ($failed) {
        foreach ($id in ($failedTestIds | Sort-Object -Unique)) {
            if ($failureDetails.ContainsKey($id)) {
                Write-Host "FAILED TEST ${id}: $($failureDetails[$id])"
            } else {
                Write-Host "FAILED TEST $id"
            }
        }
        Write-Host "FAIL $requested (exit=1)"
        exit 1
    }
    if ($unsupported) {
        Write-Host "PARTIAL $requested (unsupported checks skipped; exit=0)"
        exit 0
    }
    Write-Host "PASS $requested (exit=0)"
    exit 0
} finally {
    Pop-Location
    if (Test-Path -LiteralPath $artifactsRoot -PathType Container) {
        try {
            # Remove only this GUID-named test directory, never existing bin/obj.
            Remove-Item -LiteralPath $artifactsRoot -Recurse -Force
        } catch {
            Write-Warning "Could not remove this run's temporary artifacts: $artifactsRoot"
        }
    }
}
