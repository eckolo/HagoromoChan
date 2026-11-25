[CmdletBinding()]
param([ValidateSet('All','Desktop')][string]$Target = 'All')
. (Join-Path $PSScriptRoot 'Runtime.ps1')
$repo = Split-Path $PSScriptRoot -Parent
$root = Get-HagoromoRoot
[IO.Directory]::CreateDirectory($root) | Out-Null
$dotnet = (Get-Command dotnet -CommandType Application -ErrorAction Stop).Source
$stage = Join-Path $root ('app-stage-' + [Guid]::NewGuid().ToString('N'))
$fingerprint = Get-HagoromoSharedFingerprint $repo
[IO.Directory]::CreateDirectory($stage) | Out-Null

function Publish-Project([string]$Name, [string]$Output) {
    $project = Join-Path $repo ('src\' + $Name + '\' + $Name + '.csproj')
    & $dotnet publish $project --configuration Release --self-contained false --nologo --output $Output | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Publish failed: $Name" }
}

Push-Location $repo
try {
    if ($Target -eq 'Desktop') {
        $app = Join-Path $root 'app'
        $manifest = Join-Path $app 'build-info.json'
        if (-not (Test-Path -LiteralPath $manifest)) { throw 'Desktop-only build requires an installed full build.' }
        $old = Get-Content -LiteralPath $manifest -Raw | ConvertFrom-Json
        if ($old.SharedFingerprint -ne $fingerprint) {
            throw 'Launcher/Relay/Contracts or shared build inputs changed. Run a full Update.ps1 instead.'
        }
        Get-ChildItem -LiteralPath $app -Force | Where-Object Name -ne 'Desktop' |
            ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $stage -Recurse -Force }
    } else {
        Publish-Project 'HagoromoChan.Launcher' $stage
        Publish-Project 'HagoromoChan.Relay' (Join-Path $stage 'Relay')
    }
    Publish-Project 'HagoromoChan.Desktop' (Join-Path $stage 'Desktop')
    if ((Get-HagoromoSharedFingerprint $repo) -ne $fingerprint) {
        throw 'Shared build inputs changed during the build; discard this candidate.'
    }
    $revision = 'unknown'
    $git = Get-Command git -CommandType Application -ErrorAction SilentlyContinue
    if ($null -ne $git) {
        $value = & $git.Source rev-parse HEAD 2>$null
        if ($LASTEXITCODE -eq 0) { $revision = [string]$value }
    }
    [ordered]@{
        SharedFingerprint = $fingerprint
        Revision = $revision
        Target = $Target
        BuiltAt = [DateTimeOffset]::UtcNow.ToString('O')
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $stage 'build-info.json') -Encoding UTF8
    $null = Assert-HagoromoStage $root $stage
    Write-Host "Build staged: $stage"
    Write-Output $stage
} catch {
    # Only remove the unique directory this invocation created. Never touch app or the data store.
    Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue
    throw
} finally { Pop-Location }
