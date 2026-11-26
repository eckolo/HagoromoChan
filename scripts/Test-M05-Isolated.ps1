#requires -Version 5.1
<#
.SYNOPSIS
  Run the M05 Windows UI preview with isolated data while the installed app keeps running.
.DESCRIPTION
  Requires a local HagoromoChan Git checkout and the Windows .NET 10 SDK.
  Only the local Git objects, NuGet, and two loopback endpoints are used. Does not use the production launcher,
  tunnel, connector or database. Test files are left in a uniquely named temp folder.
  Press Enter after inspection to stop only the test processes.
.PARAMETER NoDesktop
  Run the isolated relay and synthetic MCP smoke test, with no window launch.
#>
[CmdletBinding()]
param([switch]$NoDesktop)

$ErrorActionPreference='Stop'
$vars=@('GIT_TERMINAL_PROMPT','DOTNET_CLI_HOME','NUGET_PACKAGES','DOTNET_CLI_TELEMETRY_OPTOUT','MSBuildEnableWorkloadResolver','HAGOROMO_DATA_DIR','HAGOROMO_ENABLE_M05_IMPORT','HAGOROMO_MANAGED_SESSION','HAGOROMO_STOP_EVENT','HAGOROMO_CONTROL_PIPE')
$savedEnv=@{}
foreach($name in $vars){$savedEnv[$name]=[Environment]::GetEnvironmentVariable($name,'Process')}
$prod='http://127.0.0.1:48732'
$pin='4244fe7b5fd1ecef48f20f9287545c6b26a1abce'
# Compute the checkout root natively; git archive from scripts/ exports only that subtree.
# Git for Windows emits UTF-8, while Windows PowerShell 5.1 may decode stdout
# using a legacy code page. The values captured below are ASCII only.
$localRepo=Split-Path -Parent $PSScriptRoot
$inside=$null
try { $inside=git -C $localRepo rev-parse --is-inside-work-tree 2>$null } catch {}
if($LASTEXITCODE -ne 0 -or $inside -cne 'true'){
  throw 'Run this script from scripts\ inside your local HagoromoChan Git checkout.'
}
$resolvedPin=$null
try { $resolvedPin=git -C $localRepo rev-parse --verify --quiet "$pin^{commit}" 2>$null } catch {}
if($LASTEXITCODE -ne 0 -or $resolvedPin -cne $pin){
  throw 'The M05 source commit is missing. Run git fetch origin milestone/M05-other-conversations and retry.'
}
Add-Type -AssemblyName System.Net.Http
$handler=[Net.Http.HttpClientHandler]::new()
$handler.UseProxy=$false
$handler.AllowAutoRedirect=$false
$http=[Net.Http.HttpClient]::new($handler)
$http.Timeout=[TimeSpan]::FromSeconds(5)
function LocalJson([string]$method,[string]$uri,[string]$key,$data){
  $request=[Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::new($method),$uri)
  try {
    if($key){$request.Headers.Authorization=[Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer',$key)}
    if($null -ne $data){
      $json=ConvertTo-Json -Depth 12 -Compress -InputObject $data
      $request.Content=[Net.Http.StringContent]::new($json,[Text.Encoding]::UTF8,'application/json')
    }
    $res=$http.SendAsync($request).GetAwaiter().GetResult()
    try {
      if(-not $res.IsSuccessStatusCode){throw "Local HTTP response: $([int]$res.StatusCode)"}
      $res.Content.ReadAsStringAsync().GetAwaiter().GetResult() | ConvertFrom-Json
    } finally {$res.Dispose()}
  } finally {$request.Dispose()}
}
function Prod {
  try { $h=LocalJson GET "$prod/health" '' $null; "$($h.service)|$($h.status)|$($h.managedSession)" }
  catch { 'unavailable' }
}
$before=Prod
$base=Join-Path ([IO.Path]::GetTempPath()) ('M05ui-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($base) | Out-Null
$repo=Join-Path $base 'src'
$relay=$null;$desk=$null;$wire=$null
$dot=(Get-Command dotnet -CommandType Application).Source
try {
  $env:GIT_TERMINAL_PROMPT='0'
  foreach($name in @('HAGOROMO_MANAGED_SESSION','HAGOROMO_STOP_EVENT','HAGOROMO_CONTROL_PIPE')){[Environment]::SetEnvironmentVariable($name,$null,'Process')}
  Write-Host "Trial folder: $base"
  $archive=Join-Path $base 'pinned-source.zip'
  git -C $localRepo archive --format=zip "--output=$archive" $pin
  if($LASTEXITCODE -ne 0){throw 'Pinned source export from the local Git checkout failed.'}
  Add-Type -AssemblyName System.IO.Compression.FileSystem
  $required=@('src/HagoromoChan.Relay/RelaySettings.cs','src/HagoromoChan.Desktop/RelayClient.cs',
    'src/HagoromoChan.Relay/HagoromoChan.Relay.csproj','src/HagoromoChan.Desktop/HagoromoChan.Desktop.csproj','HagoromoChan.sln')
  $zip=[IO.Compression.ZipFile]::OpenRead($archive)
  try {
    foreach($relative in $required){
      if($null -eq $zip.GetEntry($relative)){throw "Pinned source ZIP is missing: $relative"}
    }
  } finally {$zip.Dispose()}
  [IO.Compression.ZipFile]::ExtractToDirectory($archive,$repo)
  foreach($relative in $required){
    if(-not (Test-Path -LiteralPath (Join-Path $repo $relative) -PathType Leaf)){
      throw "Extracted pinned source is missing: $relative"
    }
  }
  Write-Host "Source: $pin (local Git objects)"
  $socket=[Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,0); $socket.Start(); $port=$socket.LocalEndpoint.Port; $socket.Stop()
  if($port -eq 48732){throw 'port collision'}
  $ep="http://127.0.0.1:$port"
  $paths=@{'src/HagoromoChan.Relay/RelaySettings.cs'=2;'src/HagoromoChan.Desktop/RelayClient.cs'=1}
  foreach($r in $paths.Keys){
    $p=Join-Path $repo $r; $old=[IO.File]::ReadAllText($p)
    if(([regex]::Matches($old,'48732')).Count -ne $paths[$r]){throw 'port literal guard'}
    [IO.File]::WriteAllText($p,$old.Replace('48732',"$port"),[Text.UTF8Encoding]::new($false))
  }
  Write-Host "Trial loopback port: $port"
  $env:DOTNET_CLI_HOME=Join-Path $base 'dotnet-home'
  $env:NUGET_PACKAGES=Join-Path $base 'packages'
  $env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
  $env:MSBuildEnableWorkloadResolver='false'
  $env:HAGOROMO_DATA_DIR=Join-Path $base 'isolated-data'
  $env:HAGOROMO_ENABLE_M05_IMPORT='1'
  $version=[string](& $dot --version | Select-Object -Last 1)
  $version=$version.Trim()
  if($LASTEXITCODE -ne 0 -or $version -notmatch '^10\.0\.\d+$'){throw 'Install a stable Windows .NET 10 SDK and retry.'}
  $sdk=Join-Path (Split-Path -Parent $dot) "sdk/$version/MSBuild.dll"
  if(-not (Test-Path -LiteralPath $sdk)){throw 'MSBuild.dll not found in the Windows .NET 10 SDK.'}

  foreach($project in @('HagoromoChan.Relay','HagoromoChan.Desktop')){
    & $dot $sdk (Join-Path $repo "src/$project/$project.csproj") -restore -p:Configuration=Release -m:1 -nodeReuse:false -verbosity:minimal
    if($LASTEXITCODE -ne 0){throw "compile failed $project"}
  }
  $rd=Get-ChildItem -LiteralPath (Join-Path $repo 'src/HagoromoChan.Relay/bin/Release') -Recurse -Filter HagoromoChan.Relay.dll | Select-Object -First 1
  $de=Get-ChildItem -LiteralPath (Join-Path $repo 'src/HagoromoChan.Desktop/bin/Release') -Recurse -Filter HagoromoChan.Desktop.exe | Select-Object -First 1
  $relay=Start-Process -FilePath $dot -ArgumentList @('"' + $rd.FullName + '"') -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $base 'relay.log') -RedirectStandardError (Join-Path $base 'relay.err')
  $health=$null
  for($n=0;$n -lt 24;$n++){
    if($relay.HasExited){ Get-Content -LiteralPath (Join-Path $base 'relay.err') -ErrorAction SilentlyContinue; throw "test Relay exited $($relay.ExitCode)"}
    try {$health=LocalJson GET "$ep/health" '' $null;break} catch {Start-Sleep -Milliseconds 250}
  }
  if($health.status -ne 'ok' -or $health.managedSession){throw 'isolated health failed'}
  $token=[IO.File]::ReadAllText((Join-Path $env:HAGOROMO_DATA_DIR 'relay.token')).Trim()
  function Request($method,$path,$data){LocalJson $method "$ep$path" $token $data}
  $s1=Request POST /api/m05/sources @{kind='chat';url="https://chatgpt.com/c/$([Guid]::NewGuid())";title='M05 Win Chat alpha'}
  $s2=Request POST /api/m05/sources @{kind='work';url="https://chatgpt.com/c/$([Guid]::NewGuid())";title='M05 Win Work beta'}
  $pi=[Diagnostics.ProcessStartInfo]::new($dot)
  $pi.Arguments='"' + $rd.FullName + '" --mcp-stdio'
  $pi.UseShellExecute=$false;$pi.CreateNoWindow=$true;$pi.RedirectStandardInput=$true;$pi.RedirectStandardOutput=$true;$wire=[Diagnostics.Process]::Start($pi)
  function Rpc([int]$id,[string]$method,$p) {
    $wire.StandardInput.WriteLine((ConvertTo-Json -Depth 12 -Compress -InputObject @{jsonrpc='2.0';id=$id;method=$method;params=$p}))
    $task=$wire.StandardOutput.ReadLineAsync()
    if(-not $task.Wait(6000)){throw "stdio timeout $method"}
    if($null -eq $task.Result){throw "stdio closed $method"}
    $task.Result | ConvertFrom-Json
  }
  $listed=Rpc 1 'tools/list' @{}
  if('import_cloud_answer' -notin @($listed.result.tools.name)){throw 'tool unavailable'}
  $msgid=[Guid]::NewGuid().ToString('D')
  $data=@{name='import_cloud_answer';arguments=@{source_id=$s1.id;import_id=$msgid;text='M05 Win synthetic Chat reply'}}
  foreach($i in 2,3){$resp=Rpc $i 'tools/call' $data; if($resp.result.isError -or -not ($resp.result.content[0].text|ConvertFrom-Json).stored){throw 'failed Chat import'}}
  $resp=Rpc 4 'tools/call' @{name='import_cloud_answer';arguments=@{source_id=$s2.id;import_id=[Guid]::NewGuid().ToString('D');text='M05 Win synthetic Work reply'}}
  if($resp.result.isError -or -not ($resp.result.content[0].text|ConvertFrom-Json).stored){throw 'failed Work import'}
  $a=Request GET "/api/m05/sources/$($s1.id)/answers" $null
  $b=Request GET "/api/m05/sources/$($s2.id)/answers" $null
  $hist=Request GET /api/messages $null
  if(@($a.answers).Count -ne 1 -or @($b.answers).Count -ne 1 -or @($hist.messages).Count -ne 0 -or
    $a.answers[0].text -cne 'M05 Win synthetic Chat reply' -or $b.answers[0].text -cne 'M05 Win synthetic Work reply'){
    throw 'history isolation/retry failed'
  }
  'WIN_STDIO_OK: Chat/Work separate, exact retry once, Dot empty'
  if(-not $NoDesktop){
    $desktopStart=[Diagnostics.ProcessStartInfo]::new($de.FullName)
    $desktopStart.UseShellExecute=$false
    $desk=[Diagnostics.Process]::Start($desktopStart)
    Start-Sleep -Seconds 2
    if($desk.HasExited){throw "Trial Desktop exited: $($desk.ExitCode)"}
    Write-Host '試験用マスコットを右クリック →「取り込んだクラウド回答」。'
    Write-Host '「M05 Win Chat alpha」は Chat reply 1件、「M05 Win Work beta」は Work reply 1件。互いの本文が混ざらないことを確認。'
    Write-Host 'この画面で合成URL https://chatgpt.com/c/11111111-2222-4333-8444-555555555555 と会話名を登録し、再読込して名前と登録IDが残ることも確認できます。'
    Write-Host '「取り込んだクラウド回答」メニューがある試験版だけを操作してください。'
    $null=Read-Host '確認が済んだら、ここで Enter（試験版だけ終了）'
  }
} finally {
  foreach($p in @($desk,$wire,$relay)){
    if($null -eq $p){continue}
    try {if(-not $p.HasExited){$p.Kill(); $null=$p.WaitForExit(5000)}} catch {Write-Warning ('Trial process cleanup: ' + $_.Exception.GetType().Name)}
    finally {$p.Dispose()}
  }
  foreach($name in $vars){[Environment]::SetEnvironmentVariable($name,$savedEnv[$name],'Process')}
  $after=Prod
  $http.Dispose()
  if($before -ne 'unavailable') { "PRODUCTION_HEALTH_AND_SESSION_UNCHANGED=$($before -ceq $after)" }
  else { 'Production health was unavailable at preflight; no live app change attempted.' }
}
