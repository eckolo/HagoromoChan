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
    $exe = (Resolve-Path -LiteralPath $TunnelExecutable).ProviderPath
    if (-not (Test-Path -LiteralPath $exe -PathType Leaf) -or [IO.Path]::GetExtension($exe) -ne '.exe') {
        throw 'Select a Windows tunnel-client executable.'
    }
    $version = (& $exe --version | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($version)) {
        throw 'Could not identify tunnel-client version.'
    }
    if (-not $AllowOtherVersion -and $version -notmatch '^0\.0\.21(?:[+\s]|$)') {
        throw 'The verified configuration uses tunnel-client 0.0.21. Use -AllowOtherVersion only after checking compatibility.'
    }
    if ([string]::IsNullOrWhiteSpace($ProfileFile)) {
        # Let the installed client resolve its own profile directory, including
        # TUNNEL_CLIENT_PROFILE_DIR, XDG_CONFIG_HOME, HOME and Windows APPDATA.
        # Do not confuse PowerShell's automatic $HOME with the HOME environment variable.
        $previousEncoding = [Console]::OutputEncoding
        try {
            [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
            $profileJson = (& $exe profiles list --json | Out-String).Trim()
            $profileExit = $LASTEXITCODE
        } finally {
            [Console]::OutputEncoding = $previousEncoding
        }
        if ($profileExit -ne 0 -or [string]::IsNullOrWhiteSpace($profileJson)) {
            throw 'Cannot list existing profiles. Run tunnel-client profiles list, then pass -ProfileFile explicitly.'
        }
        try {
            $profiles = @($profileJson | ConvertFrom-Json -ErrorAction Stop)
            $matches = @($profiles | Where-Object { $null -ne $_ -and $_.Name -ceq 'hagoromo' })
        } catch {
            throw 'Cannot parse profile listing. Run tunnel-client profiles list, then pass -ProfileFile explicitly.'
        }
        if ($matches.Count -ne 1 -or [string]::IsNullOrWhiteSpace([string]$matches[0].Path)) {
            throw 'The hagoromo profile was not uniquely found. Run tunnel-client profiles list (with --profile-dir if needed), then pass -ProfileFile explicitly. No profile was created or changed.'
        }
        $ProfileFile = [string]$matches[0].Path
    }
    if (-not (Test-Path -LiteralPath $ProfileFile -PathType Leaf)) {
        throw 'Profile file does not exist. Check tunnel-client profiles list or pass the existing -ProfileFile explicitly.'
    }
    $profile = (Resolve-Path -LiteralPath $ProfileFile).ProviderPath
    if ([IO.Path]::GetExtension($profile) -cne '.yaml') {
        throw 'The official tunnel-client profile file must have a .yaml extension.'
    }
    Write-Host "Using existing profile: $profile"
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
