#requires -Version 5.1
<#
.SYNOPSIS
  Run the pinned M14 fixture on an isolated Windows Desktop and loopback Relay.
.PARAMETER PackageSource
  An existing local NuGet package folder. Only this package source is used;
  no dependency download or change to the installed SSP is performed.
.PARAMETER NoDesktop
  Validate the same synthetic Relay fixtures without WPF (Linux PS 7+ supported).
#>
[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$PackageSource, [switch]$NoDesktop)

$ErrorActionPreference = 'Stop'
$windowsHost = $env:OS -eq 'Windows_NT'
if (-not $windowsHost -and -not $NoDesktop) { throw 'Windows is required for WPF. Use -NoDesktop for the fixture check.' }
$pin = '53dffc8e5f530565ef22205358bd3c43296e72cb'
$repository = Split-Path -Parent $PSScriptRoot
$resolved = git -C $repository rev-parse --verify "$pin^{commit}"
if ($LASTEXITCODE -ne 0 -or $resolved -cne $pin) { throw 'Fetch milestone/M14-new-message-clearing to obtain the pinned commit.' }
$sourceItem = Get-Item -LiteralPath $PackageSource
if (-not $sourceItem.PSIsContainer -or $sourceItem.FullName.StartsWith('\\') -or
    ($windowsHost -and [IO.DriveInfo]::new([IO.Path]::GetPathRoot($sourceItem.FullName)).DriveType -eq 'Network')) {
    throw 'PackageSource must be a local filesystem directory, not a network location.'
}
$source = $sourceItem.FullName
$dot = (Get-Command dotnet -CommandType Application).Source
$trial = Join-Path ([IO.Path]::GetTempPath()) ('HagoromoM14-' + [Guid]::NewGuid().ToString('N'))
$work = Join-Path $trial 'source'
$data = Join-Path $trial 'private-data'
$prefs = Join-Path $data 'desktop-ui.json'
$environmentNames = @('HAGOROMO_DATA_DIR','HAGOROMO_MANAGED_SESSION','HAGOROMO_STOP_EVENT','HAGOROMO_CONTROL_PIPE',
    'DOTNET_CLI_HOME','NUGET_PACKAGES','DOTNET_GENERATE_ASPNET_CERTIFICATE','DOTNET_CLI_TELEMETRY_OPTOUT','DOTNET_CLI_USE_MSBUILD_SERVER')
$previous = @{}
foreach ($name in $environmentNames) { $previous[$name] = [Environment]::GetEnvironmentVariable($name,'Process') }
Add-Type -AssemblyName System.Net.Http
$handler = [Net.Http.HttpClientHandler]::new()
$handler.UseProxy = $false
$handler.AllowAutoRedirect = $false
$http = [Net.Http.HttpClient]::new($handler)
$http.Timeout = [TimeSpan]::FromSeconds(2)
$relay = $null
$desktop = $null
$token = $null
$listener = $null

function Request([string]$Method,[string]$Path,$Payload) {
    $request = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::new($Method), "$endpoint$Path")
    try {
        if ($token) { $request.Headers.Authorization = [Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer',$token) }
        if ($null -ne $Payload) {
            $json = ConvertTo-Json -Depth 8 -Compress -InputObject $Payload
            $request.Content = [Net.Http.StringContent]::new($json,[Text.Encoding]::UTF8,'application/json')
        }
        $response = $http.SendAsync($request).GetAwaiter().GetResult()
        try {
            $null = $response.EnsureSuccessStatusCode()
            $response.Content.ReadAsStringAsync().GetAwaiter().GetResult() | ConvertFrom-Json
        } finally { $response.Dispose() }
    } finally { $request.Dispose() }
}

function Replace-Exactly([string]$Relative,[string]$Pattern,[string]$Value,[int]$Count) {
    $path = Join-Path $work $Relative
    $text = [IO.File]::ReadAllText($path)
    if ([regex]::Matches($text,[regex]::Escape($Pattern)).Count -ne $Count) { throw "Pinned source changed: $Relative" }
    [IO.File]::WriteAllText($path,$text.Replace($Pattern,$Value),[Text.UTF8Encoding]::new($false))
}

function Wait-Unread([string]$Phase,[long]$Sequence,[string[]]$Expected) {
    for ($n=0; $n -lt 30; $n++) {
        $snapshot = $null
        try { $snapshot = Get-Content -LiteralPath $prefs -Raw | ConvertFrom-Json } catch { Start-Sleep -Milliseconds 300; continue }
        $ids = @($snapshot.unreadIds | Where-Object { $_ })
        if ($snapshot.observedSequence -eq $Sequence -and $ids.Count -eq $Expected.Count -and
            @($ids | Where-Object { $_ -cnotin $Expected }).Count -eq 0) {
            Write-Host "PASS $Phase ($($ids.Count) new)"
            return
        }
        Start-Sleep -Milliseconds 300
    }
    throw "Expected new-state mismatch: $Phase. Inspect the test-only desktop-ui.json at $trial."
}

function Seed-Request([string]$Text) {
    $id = [Guid]::NewGuid().ToString('D')
    $null = Request POST '/api/messages' @{requestId=$id; text=$Text; inReplyTo=$null}
    $id
}

function Seed-Reply([string]$RequestId,[string]$Label) {
    $body = (1..120 | ForEach-Object { "$Label - display test line $_" }) -join "`n"
    $r = Request POST '/mcp' @{jsonrpc='2.0'; id=$Label; method='tools/call'; params=@{name='reply'; arguments=@{request_id=$RequestId;text=$body}}}
    if ($r.error -or $r.result.isError) { throw "Synthetic reply failed: $Label" }
    $reply = $r.result.content[0].text | ConvertFrom-Json
    if ($reply.reply_id -cne ('reply_' + $RequestId)) { throw "Synthetic reply ID mismatch: $Label" }
    $reply.reply_id
}

function Read-Continue([string]$Message) {
    $inputValue = Read-Host "$Message (Enter=continue, Q=stop)"
    if ($inputValue -match '^[qQ]$') { throw 'Stopped at the operator request.' }
}

try {
    [IO.Directory]::CreateDirectory($trial) | Out-Null
    Write-Host "Trial folder: $trial"
    $archive = Join-Path $trial 'source.zip'
    git -C $repository archive --format=zip "--output=$archive" $pin
    if ($LASTEXITCODE -ne 0) { throw 'Pinned source export failed.' }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::ExtractToDirectory($archive,$work)
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,0)
    $listener.Start()
    $port = $listener.LocalEndpoint.Port
    $listener.Stop()
    if ($port -eq 48732) { throw 'Cannot use the installed Relay port.' }
    $endpoint = "http://127.0.0.1:$port"
    Replace-Exactly 'src/HagoromoChan.Relay/RelaySettings.cs' '48732' "$port" 2
    Replace-Exactly 'src/HagoromoChan.Desktop/RelayClient.cs' '48732' "$port" 1
    Replace-Exactly 'src/HagoromoChan.Desktop/MainWindow.xaml' '    </Grid>' @'
        <TextBlock Text="[M14 TEST]" FontWeight="Bold" Background="White"
            Foreground="Black" VerticalAlignment="Bottom" HorizontalAlignment="Center"
            IsHitTestVisible="False" />
    </Grid>
'@ 1
    Replace-Exactly 'src/HagoromoChan.Desktop/DesktopPanels.cs' 'Title = "羽衣ちゃん - "' 'Title = "[M14 TEST] 羽衣ちゃん - "' 1

    foreach ($name in @('HAGOROMO_MANAGED_SESSION','HAGOROMO_STOP_EVENT','HAGOROMO_CONTROL_PIPE')) {
        [Environment]::SetEnvironmentVariable($name,$null,'Process')
    }
    $env:HAGOROMO_DATA_DIR = $data
    $env:DOTNET_CLI_HOME = Join-Path $trial 'dotnet-home'
    $env:NUGET_PACKAGES = Join-Path $trial 'packages'
    $env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_CLI_USE_MSBUILD_SERVER = '0'
    $xmlSource = [System.Security.SecurityElement]::Escape(([uri]::new($source)).AbsoluteUri)
    $nuget = Join-Path $trial 'NuGet.Config'
    [IO.File]::WriteAllText($nuget,"<configuration><packageSources><clear/><add key='local-only' value='$xmlSource'/></packageSources><fallbackPackageFolders><clear/></fallbackPackageFolders></configuration>")
    Push-Location $work
    try {
        $version = & $dot --version | Select-Object -Last 1
        if ($LASTEXITCODE -ne 0 -or $version -notmatch '^10\.0\.\d+$') { throw 'The pinned candidate requires .NET 10 SDK.' }
        $projects = @('Relay')
        if (-not $NoDesktop) { $projects += 'Desktop' }
        foreach ($project in $projects) {
            $file = "src/HagoromoChan.$project/HagoromoChan.$project.csproj"
            & $dot restore $file --configfile $nuget -p:NuGetAudit=false
            if ($LASTEXITCODE -ne 0) { throw "Offline restore failed for $project; supply a populated, local NuGet package cache." }
            & $dot build $file -c Release --no-restore -m:1 --disable-build-servers
            if ($LASTEXITCODE -ne 0) { throw "Build failed for $project." }
        }
    } finally { Pop-Location }

    $relayDll = Join-Path $work 'src/HagoromoChan.Relay/bin/Release/net10.0/HagoromoChan.Relay.dll'
    $relay = Start-Process -FilePath $dot -ArgumentList @('"' + $relayDll + '"') -PassThru -RedirectStandardOutput (Join-Path $trial 'relay.log') -RedirectStandardError (Join-Path $trial 'relay.err')
    for ($n=0; $n -lt 30; $n++) {
        if ($relay.HasExited) { throw 'The isolated Relay exited before readiness.' }
        if (Test-Path (Join-Path $data 'relay.token')) { break }
        Start-Sleep -Milliseconds 200
    }
    $token = [IO.File]::ReadAllText((Join-Path $data 'relay.token')).Trim()
    $ready = $false
    for ($n=0; $n -lt 30; $n++) {
        if ($relay.HasExited) { throw 'The isolated Relay was not able to bind its port.' }
        try { $status = Request GET '/api/status' $null; $ready = -not $status.subscribed; break } catch { Start-Sleep -Milliseconds 200 }
    }
    if (-not $ready) { throw 'The isolated authenticated Relay did not respond without a subscription.' }

    $desktopExe = Join-Path $work 'src/HagoromoChan.Desktop/bin/Release/net10.0-windows/HagoromoChan.Desktop.exe'
    if (-not $NoDesktop) {
        $desktop = Start-Process -FilePath $desktopExe -PassThru
        Wait-Unread 'empty starting checkpoint' 0 @()
    }
    $qa = Seed-Request 'Synthetic question A'
    $a = Seed-Reply $qa 'M14-A'
    $qb = Seed-Request 'Synthetic question B'
    $b = Seed-Reply $qb 'M14-B'
    $null = Seed-Request 'M14 pending question; not to be answered'
    $page = Request GET '/api/messages' $null
    $status = Request GET '/api/status' $null
    $answerIds = @($page.messages | Where-Object role -eq 'assistant' | ForEach-Object id)
    if ($answerIds.Count -ne 2 -or $answerIds[0] -cne $a -or $answerIds[1] -cne $b -or
        $status.subscribed -or $status.queuedCount -ne 1 -or $status.awaitingReplyCount -ne 0) {
        throw 'The isolated fixture shape was not two replies and one pending request.'
    }
    Write-Host 'M14_FIXTURE_OK'
    if ($NoDesktop) { return }

    Wait-Unread 'passive receipt before any gesture' $page.nextCursor @($a,$b)
    Read-Continue 'On the labeled [M14 TEST] mascot only: right-click, choose 話しかける, and put focus in the input. Do not navigate history.'
    Wait-Unread 'input-only focus' $page.nextCursor @($a,$b)
    Read-Continue 'Close only the test input via its mascot menu. Confirm M14-A is visible and M14-B is NOT visible; then click inside the M14-A history card to activate. If both are visible, stop and report the display case.'
    Wait-Unread 'activation clears displayed A, not hidden B' $page.nextCursor @($b)
    Read-Continue 'In that history, wheel down or press history ↓ until the first M14-B page is visible.'
    Wait-Unread 'navigation clears B' $page.nextCursor @()
    Read-Continue 'In the labeled test mascot context menu choose 終了 to close only the test app.'
    if (-not $desktop.WaitForExit(10000)) { throw 'The labeled test Desktop was not closed.' }
    $desktop.Dispose()
    $desktop = Start-Process -FilePath $desktopExe -PassThru
    Start-Sleep -Seconds 4
    if ($desktop.HasExited) { throw 'The test Desktop failed to restart.' }
    Wait-Unread 'no revived new IDs after restart and replay' $page.nextCursor @()
    $status = Request GET '/api/status' $null
    if ($status.queuedCount -ne 1 -or $status.awaitingReplyCount -ne 0 -or $status.subscribed) { throw 'Pending request was affected.' }
    Write-Host 'M14_WINDOWS_HISTORY_OK'
} finally {
    if ($listener) { $listener.Stop() }
    foreach ($p in @($desktop,$relay)) {
        if ($p) {
            try { if (-not $p.HasExited) { $p.Kill(); $null = $p.WaitForExit(5000) } } finally { $p.Dispose() }
        }
    }
    foreach ($name in $environmentNames) { [Environment]::SetEnvironmentVariable($name,$previous[$name],'Process') }
    $http.Dispose()
    $handler.Dispose()
}
