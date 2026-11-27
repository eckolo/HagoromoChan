#requires -Version 5.1
[CmdletBinding()]
param([ValidateSet('Core', 'WindowsLayout', 'All')][string]$Suite = 'Core')

$ErrorActionPreference = 'Stop'
if ($Suite -ne 'Core' -and $env:OS -ne 'Windows_NT') {
    throw 'WindowsLayout requires an interactive Windows desktop. Use -Suite Core on other systems.'
}
$repo = Split-Path -Parent $PSScriptRoot
$dotnet = (Get-Command dotnet -CommandType Application -ErrorAction Stop).Source
$git = Get-Command git -CommandType Application -ErrorAction SilentlyContinue
$checks = switch ($Suite) {
    'Core'          { @{ Name = 'HagoromoChan.Tests'; Framework = 'net10.0' } }
    'WindowsLayout' { @{ Name = 'HagoromoChan.WindowsLayoutChecks'; Framework = 'net10.0-windows' } }
    'All'          {
        @{ Name = 'HagoromoChan.Tests'; Framework = 'net10.0' }
        @{ Name = 'HagoromoChan.WindowsLayoutChecks'; Framework = 'net10.0-windows' }
    }
}

Push-Location $repo
try {
    $revision = 'unknown'
    if ($git) {
        $value = & $git.Source rev-parse --short HEAD 2>$null
        if ($LASTEXITCODE -eq 0) { $revision = $value }
    }
    Write-Host "HagoromoChan tests: $Suite (HEAD $revision)"
    foreach ($check in $checks) {
        $name = $check.Name
        $project = Join-Path 'tests' $name
        & $dotnet build $project -c Release --no-incremental
        $code = $LASTEXITCODE
        if ($code -ne 0) {
            Write-Host "FAIL $name build (exit=$code)"
            exit $code
        }
        $dll = Join-Path $project "bin/Release/$($check.Framework)/${name}.dll"
        # Invoke the fresh binary directly so runtime diagnostics and exit code
        # come from the actual test, not from a second build or dotnet run.
        & $dotnet $dll
        $code = $LASTEXITCODE
        if ($code -ne 0) {
            Write-Host "FAIL $name (exit=$code)"
            exit $code
        }
    }
    Write-Host "PASS $Suite (exit=0)"
    exit 0
} finally {
    Pop-Location
}
