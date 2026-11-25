[CmdletBinding(DefaultParameterSetName='Build')]
param(
    [Parameter(ParameterSetName='Build')][ValidateSet('All','Desktop')][string]$Target = 'All',
    [Parameter(ParameterSetName='Stage', Mandatory=$true)][string]$StageDirectory,
    [switch]$NoStart
)
. (Join-Path $PSScriptRoot 'Runtime.ps1')
$root = Get-HagoromoRoot
[IO.Directory]::CreateDirectory($root) | Out-Null
$deploy = Open-HagoromoLock (Join-Path $root 'deployment.lock')
if ($null -eq $deploy) { throw 'Another deployment or launch is in progress.' }
$instance = $null
$backup = $null
try {
    # Build before stopping the working version. Compiler output never targets app.
    if ($PSCmdlet.ParameterSetName -eq 'Stage') {
        $stage = Assert-HagoromoStage $root $StageDirectory
    } else {
        $stage = & (Join-Path $PSScriptRoot 'Build.ps1') -Target $Target
        $stage = Assert-HagoromoStage $root $stage
    }

    $instance = Open-HagoromoLock (Join-Path $root 'launcher.lock')
    if ($null -eq $instance) {
        Invoke-HagoromoControl $root '--stop'
        $instance = Wait-HagoromoStopped $root
    }
    Assert-HagoromoChildrenStopped $root

    $app = Join-Path $root 'app'
    if (Test-Path -LiteralPath $app) {
        if (-not (Test-Path -LiteralPath (Join-Path $app 'build-info.json') -PathType Leaf) -or
            ((Get-Item -LiteralPath $app).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw 'Existing app directory is not a recognized managed deployment. No files were replaced.'
        }
        $backup = Join-Path $root ('app-previous-' + [Guid]::NewGuid().ToString('N'))
        [IO.Directory]::Move($app, $backup)
    }
    try {
        [IO.Directory]::Move($stage, $app)
    } catch {
        if ($null -ne $backup -and -not (Test-Path -LiteralPath $app)) {
            [IO.Directory]::Move($backup, $app)
        }
        throw
    }
    Write-Host "Installed: $(Get-HagoromoLauncher $root)"
    if ($null -ne $backup) { Write-Host "Previous runtime retained: $backup" }
} finally {
    if ($null -ne $instance) { $instance.Dispose() }
    $deploy.Dispose()
}
if (-not $NoStart) { Start-HagoromoInstalled $root }
