[CmdletBinding()]
param(
    [string]$TunnelExecutable,
    [string]$ProfileFile,
    [ValidateSet('info','debug','warn')][string]$LogLevel = 'info',
    [switch]$ReuseKey,
    [switch]$AllowOtherVersion
)
. (Join-Path $PSScriptRoot 'Runtime.ps1')
$root = Get-HagoromoRoot
[IO.Directory]::CreateDirectory($root) | Out-Null
$deploy = Open-HagoromoLock (Join-Path $root 'deployment.lock')
if ($null -eq $deploy) { throw 'A launch/deployment is in progress.' }
$instance = $null
try {
    $instance = Open-HagoromoLock (Join-Path $root 'launcher.lock')
    if ($null -eq $instance) { throw 'Exit the managed application before changing its tunnel configuration.' }
    if ([string]::IsNullOrWhiteSpace($TunnelExecutable)) {
        $TunnelExecutable = (Get-Command tunnel-client -CommandType Application -ErrorAction Stop).Source
    }
    if ([string]::IsNullOrWhiteSpace($ProfileFile)) {
        $profileDir = if ($env:TUNNEL_CLIENT_PROFILE_DIR) { $env:TUNNEL_CLIENT_PROFILE_DIR }
            elseif ($env:XDG_CONFIG_HOME) { Join-Path $env:XDG_CONFIG_HOME 'tunnel-client' }
            else { Join-Path $HOME '.config\tunnel-client' }
        $ProfileFile = Join-Path $profileDir 'hagoromo.yaml'
    }
    $exe = (Resolve-Path -LiteralPath $TunnelExecutable).ProviderPath
    $profile = (Resolve-Path -LiteralPath $ProfileFile).ProviderPath
    if (-not (Test-Path -LiteralPath $exe -PathType Leaf) -or -not (Test-Path -LiteralPath $profile -PathType Leaf)) {
        throw 'Tunnel executable or profile file is missing.'
    }
    if ([IO.Path]::GetExtension($exe) -ne '.exe') { throw 'Select a Windows tunnel-client executable.' }
    $version = (& $exe --version | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($version)) {
        throw 'Could not identify tunnel-client version.'
    }
    if (-not $AllowOtherVersion -and $version -notmatch '^0\.0\.21(?:[+\s]|$)') {
        throw 'The verified configuration uses tunnel-client 0.0.21. Use -AllowOtherVersion only after checking compatibility.'
    }
    $exeHash = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash
    $config = Join-Path $root 'launcher.json'
    if (-not $ReuseKey) {
        $key = Read-Host 'Tunnel runtime API key (not admin key)' -AsSecureString
        try {
            if ($key.Length -eq 0) { throw 'Empty runtime key.' }
            # With no explicit key, Windows PowerShell uses current-user DPAPI.
            $protected = ConvertFrom-SecureString -SecureString $key
        } finally { $key.Dispose() }
    } else {
        if (-not (Test-Path -LiteralPath $config -PathType Leaf)) { throw 'No existing configuration.' }
        $previous = Get-Content -LiteralPath $config -Raw | ConvertFrom-Json
        $protected = [string]$previous.ProtectedRuntimeKey
        if ([string]::IsNullOrWhiteSpace($protected)) { throw 'No existing DPAPI-protected key.' }
    }

    # Profile, binary identity and protected key are replaced together.
    [ordered]@{
        Executable = $exe; ProfileFile = $profile; LogLevel = $LogLevel
        ExecutableSha256 = $exeHash; Version = $version; ProtectedRuntimeKey = $protected
    } | ConvertTo-Json | Set-Content -LiteralPath ($config + '.tmp') -Encoding UTF8
    if (Test-Path -LiteralPath $config) {
        [IO.File]::Replace($config + '.tmp', $config, [NullString]::Value)
    } else {
        [IO.File]::Move($config + '.tmp', $config)
    }
    Write-Host "Configured tunnel-client: $version"
    Write-Host 'Tunnel configuration saved for this Windows user. Existing profile and subscription were not changed.'
} finally {
    if ($null -ne $instance) { $instance.Dispose() }
    $deploy.Dispose()
}
