#requires -Version 5.1
<#
.SYNOPSIS
  Run a pinned M05 Work-selection smoke test on Windows without installing anything.
.DESCRIPTION
  Requires a local HagoromoChan Git checkout already containing the pinned commit,
  Windows .NET 10 SDK, and an interactive Windows session for the visual check.
  Builds a disposable alternate-port copy; neither the installed app, its data,
  its normal port, launcher, tunnel, nor connector is accessed.
  The mock worker uses only synthetic inputs and the isolated Relay's real MCP.
  The scratch folder is retained for inspection. The trial processes stop when
  the trial Desktop exits or after ten minutes.
.PARAMETER NoDesktop
  Drive the same operations through the local API and do not build or launch Desktop.
  Also supports PowerShell 7+ on Linux with a local .NET 10 SDK and Git checkout.
#>
[CmdletBinding()]
param([switch]$NoDesktop)

$ErrorActionPreference='Stop'
$windowsHost=$env:OS -eq 'Windows_NT'
if(-not $windowsHost -and -not $NoDesktop){throw 'The visual smoke requires Windows; use -NoDesktop for protocol only.'}
$vars=@('GIT_TERMINAL_PROMPT','DOTNET_CLI_HOME','NUGET_PACKAGES','DOTNET_CLI_TELEMETRY_OPTOUT','MSBuildEnableWorkloadResolver','HAGOROMO_DATA_DIR','HAGOROMO_MANAGED_SESSION','HAGOROMO_STOP_EVENT','HAGOROMO_CONTROL_PIPE','DOTNET_GENERATE_ASPNET_CERTIFICATE')
$savedEnv=@{}
foreach($name in $vars){$savedEnv[$name]=[Environment]::GetEnvironmentVariable($name,'Process')}
$pin='b47bbc4f221e4aab198f44f919a9f9dd8a9836db'
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
$base=Join-Path ([IO.Path]::GetTempPath()) ('M05WorkSelection-' + [Guid]::NewGuid().ToString('N'))
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
  # Mark only the isolated Work window so it cannot be mistaken for the installed one.
  $window=Join-Path $repo 'src/HagoromoChan.Desktop/CloudImportsWindow.cs'
  $windowSource=[IO.File]::ReadAllText($window)
  $heading='Title = "取り込んだクラウド回答";'
  if(([regex]::Matches($windowSource,[regex]::Escape($heading))).Count -ne 1){throw 'test window title guard'}
  [IO.File]::WriteAllText($window,$windowSource.Replace($heading,'Title = "[M05 TEST] 取り込んだクラウド回答";'),[Text.UTF8Encoding]::new($false))
  Write-Host "Trial loopback port: $port"
  $env:DOTNET_CLI_HOME=Join-Path $base 'dotnet-home'
  $env:NUGET_PACKAGES=Join-Path $base 'packages'
  $env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
  $env:HAGOROMO_DATA_DIR=Join-Path $base 'isolated-data'
  $env:DOTNET_GENERATE_ASPNET_CERTIFICATE='false'
  $version=[string](& $dot --version | Select-Object -Last 1)
  $version=$version.Trim()
  if($LASTEXITCODE -ne 0 -or $version -notmatch '^10\.0\.\d+$'){throw 'Install a stable Windows .NET 10 SDK and retry.'}
  $sdk=Join-Path (Split-Path -Parent $dot) "sdk/$version/MSBuild.dll"
  if(-not (Test-Path -LiteralPath $sdk)){throw 'MSBuild.dll not found in the Windows .NET 10 SDK.'}

  $projects=@('HagoromoChan.Relay')
  if(-not $NoDesktop){$projects+=@('HagoromoChan.Desktop')}
  foreach($project in $projects){
    & $dot $sdk (Join-Path $repo "src/$project/$project.csproj") -restore -p:Configuration=Release -m:1 -nodeReuse:false -verbosity:minimal
    if($LASTEXITCODE -ne 0){throw "compile failed $project"}
  }
  $rd=Get-ChildItem -LiteralPath (Join-Path $repo 'src/HagoromoChan.Relay/bin/Release') -Recurse -Filter HagoromoChan.Relay.dll | Select-Object -First 1
  $relayStart=@{FilePath=$dot; ArgumentList=@('"' + $rd.FullName + '"'); PassThru=$true;
    RedirectStandardOutput=(Join-Path $base 'relay.log'); RedirectStandardError=(Join-Path $base 'relay.err')}
  if($windowsHost){$relayStart['WindowStyle']='Hidden'}
  $relay=Start-Process @relayStart
  $health=$null
  for($n=0;$n -lt 24;$n++){
    if($relay.HasExited){ Get-Content -LiteralPath (Join-Path $base 'relay.err') -ErrorAction SilentlyContinue; throw "test Relay exited $($relay.ExitCode)"}
    try {$health=LocalJson GET "$ep/health" '' $null;break} catch {Start-Sleep -Milliseconds 250}
  }
  if($health.status -ne 'ok' -or $health.managedSession){throw 'isolated health failed'}
  $token=[IO.File]::ReadAllText((Join-Path $env:HAGOROMO_DATA_DIR 'relay.token')).Trim()
  function Request($method,$path,$data){LocalJson $method "$ep$path" $token $data}

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
  foreach($requiredTool in @('get_pending_work_retrieval','complete_work_discovery','complete_work_answer_choices','complete_work_retrieval')){
    if($requiredTool -notin @($listed.result.tools.name)){throw "Missing isolated worker tool: $requiredTool"}
  }

  $fixtureSource='work_smoke_fixture'
  $older='msg_smoke_selected_old'
  $latest='msg_smoke_later_unselected'
  $olderBody='M05 synthetic exact selected original.'
  $script:SelectedCompleted=$false

  function Invoke-WorkerTool($name,$arguments){
    $response=Rpc 2 'tools/call' @{name=$name;arguments=$arguments}
    if($response.error -or $response.result.isError){throw "Mock worker call rejected: $name"}
    $response.result.structuredContent
  }
  function Invoke-WorkerStep {
    $pending=(Invoke-WorkerTool 'get_pending_work_retrieval' @{}).pending
    if($null -eq $pending){return}
    if($pending.action -eq 'discover'){
      $null=Invoke-WorkerTool 'complete_work_discovery' @{
        operation_id=$pending.operation_id; choices=@(@{conversation_id=$fixtureSource;title='M05 Synthetic Work'})
      }
      return
    }
    if($pending.conversation_id -cne $fixtureSource){throw 'Unexpected request scope in isolated worker.'}
    $key=@{operation_id=$pending.operation_id;source_id=$pending.source_id;conversation_id=$pending.conversation_id}
    if($pending.action -eq 'list_answers'){
      $null=Invoke-WorkerTool 'complete_work_answer_choices' ($key + @{
        choices=@(@{message_id=$latest;label='02 Newer (do not select)'},@{message_id=$older;label='01 Older (select this)'})
      })
    } elseif($pending.action -eq 'selected_answer' -and $pending.message_id -ceq $older) {
      $completion=$key + @{state='available';message_id=$older;text=$olderBody}
      $null=Invoke-WorkerTool 'complete_work_retrieval' $completion
      $null=Invoke-WorkerTool 'complete_work_retrieval' $completion
      $script:SelectedCompleted=$true
    } elseif($pending.action -in @('selected_answer','latest_answer')) {
      $null=Invoke-WorkerTool 'complete_work_retrieval' ($key + @{state='unavailable'})
    } else {throw 'Unexpected operation in isolated worker.'}
  }
  function Assert-Selection {
    $sources=@(Request GET /api/m05/sources $null | Where-Object {$_.kind -ceq 'work' -and $_.title -ceq 'M05 Synthetic Work'})
    if(-not $script:SelectedCompleted -or $sources.Count -ne 1){throw 'Select the older synthetic Work answer first.'}
    $saved=Request GET "/api/m05/sources/$($sources[0].id)/answers" $null
    $dotHistory=Request GET /api/messages $null
    if(@($saved.answers).Count -ne 1 -or $saved.answers[0].text -cne $olderBody -or @($dotHistory.messages).Count -ne 0){
      throw 'Expected one exact selected Work answer; the Dot history must stay empty.'
    }
    'M05_WORK_SELECTION_PROTOCOL_OK'
  }
  if($NoDesktop) {
    $d=Request POST /api/m05/work-discovery $null
    Invoke-WorkerStep
    $choices=Request GET "/api/m05/work-retrieval/$($d.operationId)" $null
    if($choices.state -cne 'choices' -or @($choices.choices).Count -ne 1){throw 'Work choice missing.'}
    $id=$choices.choices[0].conversationId
    $source=Request POST "/api/m05/work-discovery/$($d.operationId)/select/$id" $null
    $ls=Request POST "/api/m05/sources/$($source.id)/work-answers" $null
    Invoke-WorkerStep
    $answers=Request GET "/api/m05/work-retrieval/$($ls.operationId)" $null
    if($answers.state -cne 'answer_choices' -or @($answers.answerChoices).Count -ne 2){throw 'Answer choices missing.'}
    $id=$answers.answerChoices[1].messageId
    $selected=Request POST "/api/m05/work-answers/$($ls.operationId)/select/$id" $null
    Invoke-WorkerStep
    $final=Request GET "/api/m05/work-retrieval/$($selected.operationId)" $null
    if($final.state -cne 'available' -or $final.answer.text -cne $olderBody){throw 'Exact selected body missing.'}
    Assert-Selection
  } else {
    $de=Get-ChildItem -LiteralPath (Join-Path $repo 'src/HagoromoChan.Desktop/bin/Release') -Recurse -Filter HagoromoChan.Desktop.exe | Select-Object -First 1
    $desktopStart=[Diagnostics.ProcessStartInfo]::new($de.FullName)
    $desktopStart.UseShellExecute=$false
    $desk=[Diagnostics.Process]::Start($desktopStart)
    Start-Sleep -Seconds 2
    if($desk.HasExited){throw "Trial Desktop exited: $($desk.ExitCode)"}
    Write-Host '新しく表示された試験用マスコットを右クリック →「取り込んだクラウド回答」。'
    Write-Host 'ウィンドウのタイトルに [M05 TEST] がある試験版だけを操作してください。'
    Write-Host '「Work候補を探す」→ M05 Synthetic Work →「選んだWorkを登録」。'
    Write-Host '「Work回答候補を探す」→ 01 Older (select this) →「選んだ回答を取得」。'
    Write-Host "本文が $olderBody の1件だけであることを確認。"
    Write-Host '確認後はこの PowerShell の Ctrl+C で試験を終了できます。10分でも自動終了します。'
    $clock=[Diagnostics.Stopwatch]::StartNew()
    $checked=$false
    while((-not $desk.HasExited) -and $clock.Elapsed.TotalMinutes -lt 10){
      Invoke-WorkerStep
      if($script:SelectedCompleted -and -not $checked){
        Assert-Selection
        $checked=$true
      }
      Start-Sleep -Milliseconds 300
    }
    if(-not $checked){throw 'The visual run ended before the older answer was retrieved.'}
  }
} finally {
  foreach($p in @($desk,$wire,$relay)){
    if($null -eq $p){continue}
    try {if(-not $p.HasExited){$p.Kill(); $null=$p.WaitForExit(5000)}} catch {Write-Warning ('Trial process cleanup: ' + $_.Exception.GetType().Name)}
    finally {$p.Dispose()}
  }
  foreach($name in $vars){[Environment]::SetEnvironmentVariable($name,$savedEnv[$name],'Process')}
  $http.Dispose()
}
