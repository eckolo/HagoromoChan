# Shared Windows development/deployment helpers. Dot-source from entry scripts.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-HagoromoRoot {
    if (-not [string]::IsNullOrWhiteSpace($env:HAGOROMO_DATA_DIR)) {
        return [IO.Path]::GetFullPath($env:HAGOROMO_DATA_DIR)
    }
    return (Join-Path $env:LOCALAPPDATA 'HagoromoChan')
}

function Open-HagoromoLock([string]$Path) {
    try {
        return [IO.File]::Open($Path, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    } catch [IO.IOException] {
        if (($_.Exception.HResult -band 0xFFFF) -in 32,33) { return $null }
        throw
    }
}

function Get-HagoromoLauncher([string]$Root) {
    return (Join-Path $Root 'app\HagoromoChan.exe')
}

function Invoke-HagoromoControl([string]$Root, [string]$Argument) {
    $launcher = Get-HagoromoLauncher $Root
    if (-not (Test-Path -LiteralPath $launcher -PathType Leaf)) {
        throw 'Managed runtime is missing. Run Update.ps1.'
    }
    $process = Start-Process -FilePath $launcher -ArgumentList $Argument -WorkingDirectory (Split-Path $launcher -Parent) -PassThru -Wait
    if ($process.ExitCode -ne 0) {
        throw "Manager rejected or could not receive $Argument (exit $($process.ExitCode)). No unrelated process was stopped."
    }
}

function Wait-HagoromoStopped([string]$Root, [int]$TimeoutSeconds = 70) {
    $timer = [Diagnostics.Stopwatch]::StartNew()
    do {
        $lock = Open-HagoromoLock (Join-Path $Root 'launcher.lock')
        if ($null -ne $lock) { return $lock }
        Start-Sleep -Milliseconds 250
    } while ($timer.Elapsed.TotalSeconds -lt $TimeoutSeconds)
    throw 'Managed application did not stop within the limit. Check logs\launcher.log. Deployment aborted.'
}

function Assert-HagoromoChildrenStopped([string]$Root) {
    foreach ($name in 'desktop.lock','relay.lock') {
        $probe = Open-HagoromoLock (Join-Path $Root $name)
        if ($null -eq $probe) { throw "An old/independent process still holds $name. Stop it normally first." }
        $probe.Dispose()
    }
    $names = @('tunnel-client','tunnel-client-runtime','tunnel-client-runtime-cloudflared')
    $config = Join-Path $Root 'launcher.json'
    if (Test-Path -LiteralPath $config) {
        $settings = Get-Content -LiteralPath $config -Raw | ConvertFrom-Json
        $names += [IO.Path]::GetFileNameWithoutExtension([string]$settings.Executable)
    }
    if (@(Get-Process -Name $names -ErrorAction SilentlyContinue).Count -gt 0) {
        throw 'A tunnel client is still running. Stop the old/manual tunnel normally before proceeding; no process was killed.'
    }
}

function Start-HagoromoInstalled([string]$Root) {
    $launcher = Get-HagoromoLauncher $Root
    if (-not (Test-Path -LiteralPath $launcher -PathType Leaf)) {
        throw 'Managed runtime is missing. Run Update.ps1 first.'
    }
    Start-Process -FilePath $launcher -WorkingDirectory (Split-Path $launcher -Parent)
}

function Get-HagoromoSharedFingerprint([string]$RepoRoot) {
    $files = @()
    foreach ($project in 'HagoromoChan.Contracts','HagoromoChan.Relay','HagoromoChan.Launcher') {
        $files += @(Get-ChildItem -LiteralPath (Join-Path $RepoRoot ('src\' + $project)) -File -Recurse |
            Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' })
    }
    foreach ($name in 'global.json','Directory.Build.props','Directory.Build.targets','Directory.Packages.props','NuGet.Config','HagoromoChan.sln') {
        $file = Join-Path $RepoRoot $name
        if (Test-Path -LiteralPath $file -PathType Leaf) { $files += Get-Item -LiteralPath $file }
    }
    $lines = @($files | Sort-Object FullName | ForEach-Object {
        $_.FullName.Substring($RepoRoot.Length + 1).Replace('\','/') + ':' +
            (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
    })
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        return [BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes(($lines -join [Environment]::NewLine)))).Replace('-','')
    } finally { $sha.Dispose() }
}

function Assert-HagoromoStage([string]$Root, [string]$Stage) {
    $full = [IO.Path]::GetFullPath($Stage)
    $parent = [IO.Path]::GetFullPath((Split-Path $full -Parent))
    if (-not [string]::Equals($parent, [IO.Path]::GetFullPath($Root), [StringComparison]::OrdinalIgnoreCase) -or
        (Split-Path $full -Leaf) -notmatch '^app-stage-[0-9a-f]{32}$') {
        throw 'Only an app-stage directory created under the HagoromoChan data directory is accepted.'
    }
    if (-not (Test-Path -LiteralPath $full -PathType Container)) { throw 'Stage directory does not exist.' }
    if ((Get-Item -LiteralPath $full).Attributes -band [IO.FileAttributes]::ReparsePoint) {
        throw 'A stage directory cannot be a junction or symlink.'
    }
    foreach ($relative in 'HagoromoChan.exe','Desktop\HagoromoChan.Desktop.exe','Relay\HagoromoChan.Relay.exe','build-info.json') {
        if (-not (Test-Path -LiteralPath (Join-Path $full $relative) -PathType Leaf)) {
            throw "Incomplete stage: $relative"
        }
    }
    return $full
}
