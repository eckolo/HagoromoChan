[CmdletBinding()]
param([Parameter(Mandatory=$true)][ValidateSet('Stop','Restart','RestartDesktop','RestartTunnel','Status')][string]$Action)
. (Join-Path $PSScriptRoot 'Runtime.ps1')
$root = Get-HagoromoRoot
if (-not (Test-Path -LiteralPath $root)) {
    if ($Action -eq 'Status') { Write-Output 'Not installed.'; return }
    throw 'Managed runtime is missing.'
}
$probe = Open-HagoromoLock (Join-Path $root 'launcher.lock')
$running = $null -eq $probe
if ($null -ne $probe) { $probe.Dispose() }
if ($Action -eq 'Status') {
    $file = Join-Path $root 'runtime-status.json'
    Write-Host "Manager lock held: $running"
    if (Test-Path -LiteralPath $file) {
        $snapshot = Get-Content -LiteralPath $file -Raw | ConvertFrom-Json
        if ($running -and ([DateTimeOffset]::UtcNow - [DateTimeOffset]$snapshot.ObservedAt).TotalSeconds -lt 12) {
            $snapshot
        } else {
            Write-Host 'Last recorded snapshot (not live):'
            $snapshot
        }
    }
    return
}
if (-not $running) {
    if ($Action -eq 'Stop') { Write-Host 'Managed application is already stopped.'; return }
    if ($Action -eq 'Restart') { Start-HagoromoInstalled $root; return }
    throw 'Managed application is not running.'
}
$argument = switch ($Action) {
    Stop { '--stop' }
    Restart { '--restart' }
    RestartDesktop { '--restart-desktop' }
    RestartTunnel { '--restart-tunnel' }
}
Invoke-HagoromoControl $root $argument
if ($Action -eq 'Stop') {
    $lock = Wait-HagoromoStopped $root
    try { Assert-HagoromoChildrenStopped $root } finally { $lock.Dispose() }
    Write-Host 'Managed application stopped.'
} else {
    Write-Host 'Restart accepted. Inspect Control.ps1 -Action Status or the connection status panel for completion.'
}
