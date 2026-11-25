using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Net;
using System.Net.NetworkInformation;
using System.Text.Json;
using System.Threading.Channels;
using HagoromoChan.Contracts;

namespace HagoromoChan.Launcher;

internal sealed class Supervisor : IDisposable
{
    private readonly string _root, _app, _logs, _pipe;
    private readonly HttpClient _http = new(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false })
        { Timeout = TimeSpan.FromSeconds(1) };
    private readonly CancellationTokenSource _controlLifetime = new();
    private readonly Channel<RuntimeCommand> _commands = Channel.CreateBounded<RuntimeCommand>(1);
    private int _commandPending;
    private string _session = Guid.NewGuid().ToString("N");
    private ChildProcess? _desktop, _relay, _tunnel;
    private EventWaitHandle? _desktopStop, _relayStop;
    private string? _healthFile;
    private string _phase = "starting", _tunnelState = "stopped";
    private string? _problem;
    private bool _relayReady, _tunnelReady;
    private bool _relayFailureHandled;
    private bool _disposed;

    internal Supervisor(string root, string app)
    {
        _root = root; _app = app; _logs = Path.Combine(root, "logs");
        _pipe = RuntimeControl.PipeName(root);
        Directory.CreateDirectory(_logs);
    }

    internal async Task RunAsync()
    {
        var server = ServeControlAsync(_controlLifetime.Token);
        Microsoft.Win32.SystemEvents.SessionEnding += SessionEnding;
        try
        {
            await StartAllAsync().ConfigureAwait(false);
            while (true)
            {
                if (_commands.Reader.TryRead(out var command))
                {
                    try
                    {
                        switch (command)
                        {
                            case RuntimeCommand.Stop: return;
                            case RuntimeCommand.Restart:
                                await StopAllAsync().ConfigureAwait(false);
                                _session = Guid.NewGuid().ToString("N");
                                await StartAllAsync().ConfigureAwait(false);
                                break;
                            case RuntimeCommand.RestartDesktop:
                                await StopDesktopAsync().ConfigureAwait(false);
                                StartDesktop();
                                break;
                            case RuntimeCommand.RestartTunnel:
                                await StopTunnelAsync().ConfigureAwait(false);
                                if (_relayReady) StartTunnel();
                                else _problem = "中継を再起動してからトンネルを起動してください。";
                                break;
                        }
                    }
                    finally { Interlocked.Exchange(ref _commandPending, 0); }
                }
                else if (_desktop is not { Alive: true })
                {
                    Log("Desktop exited; stopping application.");
                    return;
                }

                await RefreshHealthAsync().ConfigureAwait(false);
                PublishStatus();
                if (server.IsCompleted)
                    await server.ConfigureAwait(false); // pipe failure is not silently ignored
                await Task.Delay(500).ConfigureAwait(false);
            }
        }
        finally
        {
            Microsoft.Win32.SystemEvents.SessionEnding -= SessionEnding;
            try { await StopAllAsync().ConfigureAwait(false); }
            finally
            {
                _controlLifetime.Cancel();
                try { await server.ConfigureAwait(false); }
                catch (OperationCanceledException) when (_controlLifetime.IsCancellationRequested) { }
            }
        }
    }

    private void SessionEnding(object? sender, Microsoft.Win32.SessionEndingEventArgs e)
    {
        // Windows may impose a shutdown deadline; the job handles are the fallback.
        _commands.Writer.TryWrite(RuntimeCommand.Stop);
    }

    private async Task ServeControlAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            using var pipe = new NamedPipeServerStream(_pipe, PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
            using var requestDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            requestDeadline.CancelAfter(TimeSpan.FromSeconds(3));
            try
            {
                var request = new byte[1];
                var count = await pipe.ReadAsync(request, requestDeadline.Token).ConfigureAwait(false);
                var accepted = false;
                if (count == 1 && Enum.IsDefined((RuntimeCommand)request[0]) &&
                    Interlocked.CompareExchange(ref _commandPending, 1, 0) == 0)
                {
                    accepted = _commands.Writer.TryWrite((RuntimeCommand)request[0]);
                    if (!accepted) Interlocked.Exchange(ref _commandPending, 0);
                }
                await pipe.WriteAsync(new byte[] { accepted ? (byte)1 : (byte)0 }, requestDeadline.Token).ConfigureAwait(false);
                await pipe.FlushAsync(requestDeadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
            catch (IOException) { }
        }
    }

    private async Task StartAllAsync()
    {
        _phase = "starting"; _problem = null; _relayFailureHandled = false;
        _relayReady = false; _tunnelReady = false; _tunnelState = "stopped";
        PublishStatus();
        try
        {
            Preflight();
            Log("Starting Relay.");
            _relayStop = CreateStopEvent("relay", out var relayEvent);
            _relay = ChildProcess.Start(Path.Combine(_app, "Relay", "HagoromoChan.Relay.exe"), [],
                Path.Combine(_app, "Relay"), ChildEnvironment(relayEvent), Path.Combine(_logs, "relay.log"));
            var timer = Stopwatch.StartNew();
            do
            {
                if (!_relay.Alive) throw new StartupException("ローカル中継が起動直後に終了しました。");
                if (await CheckRelayAsync().ConfigureAwait(false)) { _relayReady = true; break; }
                await Task.Delay(200).ConfigureAwait(false);
            } while (timer.Elapsed < TimeSpan.FromSeconds(20));
            if (!_relayReady) throw new StartupException("ローカル中継が準備完了になりません。");
            StartTunnel();
            StartDesktop();
            _phase = "running";
            PublishStatus();
        }
        catch (Exception error)
        {
            Log("Startup failed: " + error.GetType().Name);
            throw;
        }
    }

    private void Preflight()
    {
        foreach (var relative in new[] { "Relay/HagoromoChan.Relay.exe", "Desktop/HagoromoChan.Desktop.exe" })
            if (!File.Exists(Path.Combine(_app, relative)))
                throw new StartupException("実行ファイルが不足しています。Update.ps1 で配置してください。");
        foreach (var name in new[] { "desktop.lock", "relay.lock" })
        {
            try { using var probe = new FileStream(Path.Combine(_root, name), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { throw new StartupException("旧版または別の羽衣ちゃんが起動中です。先に正常終了してください。"); }
        }

        // Never attach to or stop an unowned service merely because its health endpoint responds.
        if (IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(endpoint => endpoint.Port == 48732))
            throw new StartupException("ポート48732は使用中です。旧中継等を確認してください。");

        // Official stdio tunnels permit only one active poller. Conservative fail-closed
        // migration check: unrelated official tunnel processes are not killed either.
        if (UnownedTunnelExists(null))
            throw new StartupException("手動起動のtunnel-clientが稼働しています。先に終了してください。");
    }

    private static bool UnownedTunnelExists(string? configuredExe)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "tunnel-client", "tunnel-client-runtime", "tunnel-client-runtime-cloudflared" };
        if (configuredExe is not null) names.Add(Path.GetFileNameWithoutExtension(configuredExe));
        foreach (var name in names)
        {
            var processes = Process.GetProcessesByName(name);
            try { if (processes.Any(p => !p.HasExited)) return true; }
            finally { foreach (var process in processes) process.Dispose(); }
        }
        return false;
    }

    private void StartDesktop()
    {
        Log("Starting Desktop.");
        _desktopStop = CreateStopEvent("desktop", out var stopName);
        _desktop = ChildProcess.Start(Path.Combine(_app, "Desktop", "HagoromoChan.Desktop.exe"), [],
            Path.Combine(_app, "Desktop"), ChildEnvironment(stopName), Path.Combine(_logs, "desktop.log"));
    }

    private void StartTunnel()
    {
        _tunnelReady = false;
        _tunnelState = "unconfigured";
        _problem = null;
        try
        {
            var config = TunnelConfiguration.Load(_root);
            if (config is null) { _problem = "トンネル未設定。Configure.ps1で初期設定してください。"; return; }
            if (UnownedTunnelExists(config.Executable))
            {
                _tunnelState = "blocked";
                _problem = "別のトンネルが稼働中です。重複起動しません。";
                return;
            }
            var key = config.ReadRuntimeKey();
            _healthFile = Path.Combine(_logs, "tunnel-health-" + Guid.NewGuid().ToString("N") + ".url");
            var env = ChildEnvironment(null);
            env["CONTROL_PLANE_API_KEY"] = key;
            foreach (var name in new[] { "TUNNEL_CLIENT_CONFIG", "TUNNEL_CLIENT_PROFILE", "TUNNEL_CLIENT_PROFILE_FILE",
                "CONTROL_PLANE_TUNNEL_ID", "MCP_COMMAND", "MCP_SERVER_URL" })
                env[name] = null;
            var relayExe = Path.Combine(_app, "Relay", "HagoromoChan.Relay.exe");
            var mcp = RuntimeControl.QuoteWindowsArgument(relayExe) + " --mcp-stdio";
            Log("Starting configured tunnel with initialized-notification shim.");
            _tunnel = ChildProcess.Start(config.Executable,
                ["run", "--profile-file", config.ProfileFile, "--mcp.command", mcp,
                "--control-plane.api-key=env:CONTROL_PLANE_API_KEY",
                "--mcp.stdio-send-initialized-notification=true",
                "--health.listen-addr=127.0.0.1:0", "--health.url-file", _healthFile,
                "--log.level=" + config.LogLevel, "--log.format=json",
                "--log.file", Path.Combine(_logs, "tunnel-client.log"), "--log.http-raw-unsafe=false"],
                Path.GetDirectoryName(config.Executable)!, env, Path.Combine(_logs, "tunnel.log"), privateConsole: true);
            env.Remove("CONTROL_PLANE_API_KEY");
            _tunnelState = "starting";
        }
        catch (Exception error)
        {
            _tunnelState = "failed";
            _problem = error is StartupException ? error.Message
                : "トンネルを起動できません。設定とlogs/tunnel.logを確認してください。";
            Log("Tunnel start failed: " + error.GetType().Name);
        }
    }

    private Dictionary<string, string?> ChildEnvironment(string? stopName) => new(StringComparer.OrdinalIgnoreCase)
    {
        ["HAGOROMO_DATA_DIR"] = _root,
        [RuntimeControl.SessionVariable] = _session,
        [RuntimeControl.PipeVariable] = _pipe,
        [RuntimeControl.StopVariable] = stopName,
        ["CONTROL_PLANE_API_KEY"] = null,
        ["OPENAI_API_KEY"] = null,
        ["OPENAI_ADMIN_KEY"] = null
    };

    private EventWaitHandle CreateStopEvent(string role, out string name)
    {
        name = "Local\\HagoromoChan-" + _session + "-" + role + "-" + Guid.NewGuid().ToString("N");
        var result = new EventWaitHandle(false, EventResetMode.ManualReset, name, out var created);
        if (!created) { result.Dispose(); throw new InvalidOperationException("Stop event already exists."); }
        return result;
    }

    private async Task RefreshHealthAsync()
    {
        _relayReady = _relay is { Alive: true } && await CheckRelayAsync().ConfigureAwait(false);
        if (_relay is not { Alive: true } && !_relayFailureHandled)
        {
            _relayFailureHandled = true;
            Log("Relay exited unexpectedly.");
            await StopTunnelAsync().ConfigureAwait(false);
            _problem = "ローカル中継が終了しました。アプリを再起動してください。";
        }
        if (_tunnel is { Alive: true })
        {
            _tunnelReady = await CheckTunnelAsync().ConfigureAwait(false);
            _tunnelState = _tunnelReady ? "ready" : "not-ready";
        }
        else if (_tunnel is not null)
        {
            _tunnelReady = false;
            if (_tunnelState != "exited") Log("Tunnel exited; code=" + _tunnel.ExitCode);
            _tunnelState = "exited";
            _problem = "トンネルが終了しました。logs/tunnel.logを確認してください。";
        }
    }

    private async Task<bool> CheckRelayAsync()
    {
        try
        {
            using var response = await _http.GetAsync("http://127.0.0.1:48732/health").ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return false;
            using var document = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false));
            var json = document.RootElement;
            return json.TryGetProperty("service", out var service) && service.GetString() == "HagoromoChan" &&
                json.TryGetProperty("managedSession", out var session) && session.GetString() == _session;
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException or JsonException or InvalidOperationException)
        { return false; }
    }

    private async Task<bool> CheckTunnelAsync()
    {
        try
        {
            if (_healthFile is null || !File.Exists(_healthFile)) return false;
            var text = File.ReadAllText(_healthFile).Trim();
            if (!Uri.TryCreate(text, UriKind.Absolute, out var url) || url.Scheme != "http" ||
                url.Host != "127.0.0.1" || url.UserInfo.Length != 0 || url.Query.Length != 0 || url.Fragment.Length != 0)
                return false;
            using var response = await _http.GetAsync(new Uri(url, "/readyz")).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or HttpRequestException or OperationCanceledException)
        { return false; }
    }

    private async Task StopAllAsync()
    {
        _phase = "stopping"; PublishStatus();
        Exception? failure = null;
        foreach (var stop in new Func<Task>[] { StopDesktopAsync, StopTunnelAsync, StopRelayAsync })
        {
            try { await stop().ConfigureAwait(false); }
            catch (Exception error) { failure ??= error; Log("Stop failed: " + error.GetType().Name); }
        }
        _phase = failure is null ? "stopped" : "stop-failed";
        if (failure is not null) _problem = "終了確認に失敗しました。logs/launcher.logを確認してください。";
        PublishStatus();
        if (failure is not null) throw new StartupException("関連プロセスの終了を確認できませんでした。");
    }

    private async Task StopDesktopAsync()
    {
        try { await StopChildAsync(_desktop, _desktopStop, "Desktop", 8).ConfigureAwait(false); }
        finally { _desktop = null; _desktopStop?.Dispose(); _desktopStop = null; }
    }

    private async Task StopTunnelAsync()
    {
        _tunnelReady = false;
        try { await StopChildAsync(_tunnel, null, "Tunnel", 12).ConfigureAwait(false); }
        finally
        {
            _tunnel = null;
            _tunnelState = "stopped";
            if (_healthFile is not null)
            {
                try { File.Delete(_healthFile); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Log("Health file cleanup failed."); }
                _healthFile = null;
            }
        }
    }

    private async Task StopRelayAsync()
    {
        _relayReady = false;
        try { await StopChildAsync(_relay, _relayStop, "Relay", 20).ConfigureAwait(false); }
        finally { _relay = null; _relayStop?.Dispose(); _relayStop = null; }
    }

    private async Task StopChildAsync(ChildProcess? child, EventWaitHandle? stop, string role, int seconds)
    {
        if (child is null) return;
        using (child)
        {
            if (child.Alive)
            {
                Log("Stopping " + role + ".");
                if (stop is not null) stop.Set();
                else if (!await child.SignalConsoleAsync().ConfigureAwait(false))
                    Log(role + ": graceful console signal unavailable.");
            }
            if (!await child.WaitEmptyAsync(TimeSpan.FromSeconds(seconds)).ConfigureAwait(false))
            {
                Log(role + ": graceful timeout; terminating owned job only.");
                child.Terminate();
                if (!await child.WaitEmptyAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false))
                    throw new InvalidOperationException("Owned job did not empty.");
            }
            Log(role + " stopped.");
        }
    }

    private void PublishStatus()
    {
        try
        {
            var snapshot = new RuntimeSnapshot(_session, _phase,
                _desktop is { Alive: true } ? _desktop.Pid : null,
                _relay is { Alive: true } ? _relay.Pid : null, _relayReady,
                _tunnel is { Alive: true } ? _tunnel.Pid : null, _tunnelReady,
                _tunnelState, _problem, DateTimeOffset.UtcNow);
            var destination = Path.Combine(_root, RuntimeControl.StatusFile);
            var temporary = destination + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(snapshot));
            File.Move(temporary, destination, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { Log("Runtime status write failed: " + error.GetType().Name); }
    }

    private void Log(string message)
    {
        try { File.AppendAllText(Path.Combine(_logs, "launcher.log"), DateTimeOffset.Now.ToString("O") + " " + message + Environment.NewLine); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _desktop?.Dispose(); _tunnel?.Dispose(); _relay?.Dispose();
        _desktopStop?.Dispose(); _relayStop?.Dispose();
        _http.Dispose(); _controlLifetime.Dispose();
    }
}
