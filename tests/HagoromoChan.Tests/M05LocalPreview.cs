using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using HagoromoChan.Contracts;
using HagoromoChan.Relay;

internal static class M05LocalPreview
{
    internal static (CloudSource Source, CloudAnswer Answer)[] Seed(ChatStore store)
    {
        return new[] { (Kind: "chat", Label: "Chat alpha"), (Kind: "work", Label: "Work beta") }
            .Select(sample =>
            {
                var source = store.SaveCloudSource(new(sample.Kind, $"https://chatgpt.com/c/{Guid.NewGuid():D}",
                    $"M05 {sample.Label}"));
                var answer = store.ImportCloudAnswer(source.Id, Guid.NewGuid().ToString("D"),
                    $"M05 {sample.Kind} synthetic reply");
                return (source, answer);
            }).ToArray();
    }

    public static async Task<int> RunAsync()
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("M05の画面試験はWindowsで実行してください。");
            return 1;
        }

        Process? relay = null;
        Process? desktop = null;
        try
        {
            CheckPort();
            var root = FindCheckout();
            var relayProject = Path.Combine("src", "HagoromoChan.Relay");
            var desktopProject = Path.Combine("src", "HagoromoChan.Desktop");
            var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
            foreach (var project in new[] { relayProject, desktopProject })
            {
                using var build = Start(dotnet, root, null, "build", Path.Combine(project, Path.GetFileName(project) + ".csproj"),
                    "--configuration", "Debug");
                await build.WaitForExitAsync();
                if (build.ExitCode != 0) return build.ExitCode;
            }
            CheckPort();
            var data = Path.Combine(Path.GetTempPath(), "Hagoromo-M05-preview-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(data);
            Console.WriteLine($"試験データ: {data}");

            (CloudSource Source, CloudAnswer Answer)[] samples;
            using (var store = new ChatStore(Path.Combine(data, "conversations.db"), RelaySettings.ChannelId))
                samples = Seed(store);

            relay = Start(dotnet, root, data, Path.Combine(relayProject, "bin", "Debug", "net10.0", "HagoromoChan.Relay.dll"));
            await VerifyLiveFixtureAsync(relay, data, samples);
            desktop = Start(Path.Combine(root, desktopProject, "bin", "Debug", "net10.0-windows", "HagoromoChan.Desktop.exe"),
                root, data);

            Console.WriteLine("試験用マスコットを右クリック →「取り込んだクラウド回答」。");
            Console.WriteLine("「M05 Chat alpha」と「M05 Work beta」で、それぞれのsynthetic replyが1件ずつ、混ざらずに表示されるかご確認ください。");
            Console.WriteLine("確認が済んだら、この端末でEnterを押すと試験版だけ終了します。");
            Console.ReadLine();
            return 0;
        }
        catch (Exception error) when (error is IOException or SocketException or InvalidOperationException
            or System.ComponentModel.Win32Exception or HttpRequestException or TaskCanceledException or System.Text.Json.JsonException
            or UnauthorizedAccessException)
        {
            Console.Error.WriteLine(error.Message);
            return 1;
        }
        finally
        {
            Stop(desktop);
            Stop(relay);
        }
    }

    private static void CheckPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, RelaySettings.Port) { ExclusiveAddressUse = true };
        try { listener.Start(); }
        catch (SocketException error) when (error.SocketErrorCode is SocketError.AddressAlreadyInUse or SocketError.AccessDenied)
        {
            throw new IOException($"試験用の既定ポート {RelaySettings.Port} を使用できません。通常の羽衣ちゃんSSPが起動中なら、ご自身で終了してから再実行してください。このコマンドは既存アプリを停止しません。", error);
        }
        finally { listener.Stop(); }
    }

    private static string FindCheckout()
    {
        for (DirectoryInfo? dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "HagoromoChan.sln")))
                return dir.FullName;
        throw new InvalidOperationException("HagoromoChanの作業コピーから起動してください。");
    }

    private static Process Start(string file, string root, string? data, params string[] args)
    {
        var start = new ProcessStartInfo(file) { WorkingDirectory = root, UseShellExecute = false };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        if (data is not null)
        {
            start.Environment["HAGOROMO_DATA_DIR"] = data;
            foreach (var key in new[] { RuntimeControl.SessionVariable, RuntimeControl.StopVariable, RuntimeControl.PipeVariable,
                "HAGOROMO_ENABLE_M05_IMPORT", "CONTROL_PLANE_API_KEY", "OPENAI_API_KEY" })
                start.Environment.Remove(key);
        }
        return Process.Start(start) ?? throw new InvalidOperationException($"試験プロセスを起動できません: {Path.GetFileName(file)}");
    }

    private static async Task VerifyLiveFixtureAsync(Process relay, string data, (CloudSource Source, CloudAnswer Answer)[] expected)
    {
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false })
        {
            BaseAddress = new Uri(RelaySettings.BaseUrl),
            Timeout = TimeSpan.FromSeconds(2)
        };
        var tokenPath = Path.Combine(data, "relay.token");
        for (var attempt = 0; attempt < 25; attempt++)
        {
            if (relay.HasExited) throw new InvalidOperationException($"試験Relayは終了しました (exit {relay.ExitCode})。");
            if (File.Exists(tokenPath))
            {
                var token = (await File.ReadAllTextAsync(tokenPath)).Trim();
                if (token.Length == 64 && token.All(Uri.IsHexDigit))
                {
                    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    try
                    {
                        var sources = await http.GetFromJsonAsync<CloudSource[]>("/api/m05/sources");
                        if (sources is null || sources.Length != expected.Length ||
                            !sources.Select(s => (s.Id, s.Kind, s.Title)).ToHashSet().SetEquals(
                                expected.Select(x => (x.Source.Id, x.Source.Kind, x.Source.Title))))
                            throw new InvalidOperationException("試験用の会話名を保存直後に確認できません。");
                        foreach (var sample in expected)
                        {
                            var page = await http.GetFromJsonAsync<CloudAnswerPage>($"/api/m05/sources/{sample.Source.Id}/answers");
                            if (page is null || page.Answers.Length != 1 || page.Answers[0] != sample.Answer)
                                throw new InvalidOperationException("試験用回答を保存直後に確認できません。");
                        }
                        var dot = await http.GetFromJsonAsync<MessagePage>("/api/messages");
                        if (dot?.Messages.Count != 0)
                            throw new InvalidOperationException("試験版の通常会話に意図しない履歴があります。");
                        return;
                    }
                    catch (HttpRequestException error) when (error.StatusCode is null)
                    {
                        // The token can appear before Kestrel begins listening.
                    }
                }
            }
            await Task.Delay(200);
        }
        throw new InvalidOperationException("試験Relayの起動を確認できませんでした。");
    }

    private static void Stop(Process? process)
    {
        if (process is null) return;
        using (process)
        {
            try
            {
                if (process.HasExited) return;
                if (process.CloseMainWindow() && process.WaitForExit(2500)) return;
                process.Kill();
                process.WaitForExit(2500);
            }
            catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                Console.Error.WriteLine($"試験プロセスの終了確認: {error.Message}");
            }
        }
    }
}
