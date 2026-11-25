param([switch]$SkipBuild)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$dotnet = (Get-Command dotnet -ErrorAction Stop).Source

if (-not $SkipBuild) {
    & $dotnet build (Join-Path $repoRoot 'HagoromoChan.sln') --configuration Release --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
}

$relayDll = Join-Path $repoRoot 'src/HagoromoChan.Relay/bin/Release/net10.0/HagoromoChan.Relay.dll'
$desktopExe = Join-Path $repoRoot 'src/HagoromoChan.Desktop/bin/Release/net10.0-windows/HagoromoChan.Desktop.exe'
if (-not (Test-Path $relayDll) -or -not (Test-Path $desktopExe)) {
    throw 'Build outputs are missing. Run this script without -SkipBuild.'
}

$health = $null
try { $health = Invoke-RestMethod 'http://127.0.0.1:48732/health' -TimeoutSec 2 } catch { }
if ($health -and $health.service -ne 'HagoromoChan') {
    throw 'Port 48732 is being used by another service.'
}
if (-not $health) {
    $relay = Start-Process -FilePath $dotnet -ArgumentList ('"' + $relayDll + '"') `
        -WorkingDirectory $repoRoot -WindowStyle Minimized -PassThru
    for ($attempt = 0; $attempt -lt 50; $attempt++) {
        if ($relay.HasExited) { throw 'The relay exited during startup. Run the relay project to inspect the error.' }
        try {
            $health = Invoke-RestMethod 'http://127.0.0.1:48732/health' -TimeoutSec 2
            if ($health.service -eq 'HagoromoChan') { break }
        } catch { }
        Start-Sleep -Milliseconds 200
    }
    if (-not $health -or $health.service -ne 'HagoromoChan') {
        throw 'The relay did not become ready. Check its console window.'
    }
}

Start-Process -FilePath $desktopExe -WorkingDirectory $repoRoot
