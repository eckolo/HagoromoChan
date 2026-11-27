#requires -Version 5.1
[CmdletBinding()]
param([ValidateSet('Core', 'WindowsLayout', 'M35LongDiagnostics', 'All')][string]$Suite = 'Core')

$ErrorActionPreference = 'Stop'
# In PowerShell 7, honor native exit codes here rather than the caller's
# native-error preference. Windows PowerShell 5.1 ignores this variable.
$PSNativeCommandUseErrorActionPreference = $false
if ($Suite -ne 'Core' -and $env:OS -ne 'Windows_NT') {
    throw 'The requested suite requires an interactive Windows desktop. Use -Suite Core on other systems.'
}
$repo = Split-Path -Parent $PSScriptRoot
$dotnet = (Get-Command dotnet -CommandType Application -ErrorAction Stop).Source
$git = Get-Command git -CommandType Application -ErrorAction SilentlyContinue
$checks = switch ($Suite) {
    'Core'          { @{ Name = 'HagoromoChan.Tests'; Framework = 'net10.0' } }
    'WindowsLayout' { @{ Name = 'HagoromoChan.WindowsLayoutChecks'; Framework = 'net10.0-windows'; Mode = 'layout-all' } }
    'M35LongDiagnostics' { @{ Name = 'HagoromoChan.WindowsLayoutChecks'; Framework = 'net10.0-windows'; Mode = 'long-all'; Label = 'M35LongDiagnostics' } }
    'All'          {
        @{ Name = 'HagoromoChan.Tests'; Framework = 'net10.0' }
        @{ Name = 'HagoromoChan.WindowsLayoutChecks'; Framework = 'net10.0-windows'; Mode = 'layout-all' }
    }
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
    Write-Host "HagoromoChan tests: $Suite (HEAD $revision)"
    $results = @()
    $failed = $false
    foreach ($check in $checks) {
        $name = $check.Name
        $label = if ($check.Label) { $check.Label } else { $name }
        $project = Join-Path 'tests' $name

        Write-Host "RUN  $label build"
        $code = $null
        try {
            & $dotnet build $project -c Release --no-incremental
            $code = $LASTEXITCODE
        } catch {
            Write-Host "ERROR ${label} build: $($_.Exception.Message)"
        }
        if ($null -eq $code -or $code -ne 0) {
            $failed = $true
            $detail = if ($null -eq $code) { 'not started' } else { "exit=$code" }
            $results += "FAIL $label build ($detail)"
            $results += "SKIP $label run (fresh build failed)"
            continue
        }
        $results += "PASS $label build (exit=0)"

        $dll = Join-Path $project "bin/Release/$($check.Framework)/${name}.dll"
        $testArguments = @($dll)
        if ($check.Mode) { $testArguments += $check.Mode }
        # Invoke the fresh binary directly so there is no second build. The
        # M35 supervisor handles independent children and their raw exit codes.
        Write-Host "RUN  $label run"
        $code = $null
        try {
            & $dotnet @testArguments
            $code = $LASTEXITCODE
        } catch {
            Write-Host "ERROR ${label} run: $($_.Exception.Message)"
        }
        if ($null -eq $code -or $code -ne 0) {
            $failed = $true
            $detail = if ($null -eq $code) { 'not started' } else { "exit=$code" }
            $results += "FAIL $label run ($detail)"
            continue
        }
        $results += "PASS $label run (exit=0)"
    }
    Write-Host "HagoromoChan test summary: $Suite"
    foreach ($result in $results) { Write-Host $result }
    if ($failed) {
        Write-Host "FAIL $Suite (exit=1)"
        exit 1
    }
    Write-Host "PASS $Suite (exit=0)"
    exit 0
} finally {
    Pop-Location
}
