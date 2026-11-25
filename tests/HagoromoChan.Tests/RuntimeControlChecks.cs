using System.IO.Pipes;
using System.Text.Json;
using HagoromoChan.Contracts;

internal static class RuntimeControlChecks
{
    public static void SnapshotIdentity()
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = new RuntimeSnapshot("one", "running", 1, 2, true, 3, false, "not-ready", null, now);
        var restored = JsonSerializer.Deserialize<RuntimeSnapshot>(JsonSerializer.Serialize(snapshot));
        True(restored == snapshot, "Runtime snapshot serialization round trip.");
        True(RuntimeControl.IsCurrentSnapshot(restored, "one", now), "Current snapshot accepted.");
        True(!RuntimeControl.IsCurrentSnapshot(restored, "two", now), "Different runtime session rejected.");
        True(!RuntimeControl.IsCurrentSnapshot(restored, "one", now.AddSeconds(12)), "Stale snapshot rejected.");
        True(!RuntimeControl.IsCurrentSnapshot(restored, "one", now.AddMinutes(-1)), "Future snapshot rejected.");
        True(!RuntimeControl.IsCurrentSnapshot(null, "one", now), "Missing snapshot not treated as ready.");

        var path = Path.Combine(Path.GetTempPath(), "HagoromoRuntimeTest");
        True(RuntimeControl.PipeName(path) == RuntimeControl.PipeName(path + Path.DirectorySeparatorChar),
            "Trailing path separator does not change the local control identity.");
        True(RuntimeControl.PipeName(path) != RuntimeControl.PipeName(path + "-other"),
            "Different data directories use separate control names.");
        if (OperatingSystem.IsWindows())
            True(RuntimeControl.PipeName(path) == RuntimeControl.PipeName(path.ToUpperInvariant()),
                "Windows case-insensitive path identity.");
    }

    public static void WindowsArgumentQuoting()
    {
        True(RuntimeControl.QuoteWindowsArgument("") == "\"\"", "Empty argument remains a distinct argument.");
        True(RuntimeControl.QuoteWindowsArgument(@"C:\開発 環境\app.exe") ==
            "\"C:\\開発 環境\\app.exe\"", "Space and Japanese path are preserved.");
        True(RuntimeControl.QuoteWindowsArgument(@"C:\folder\") ==
            "\"C:\\folder\\\\\"", "Trailing separator is doubled before the closing quote.");
        True(RuntimeControl.QuoteWindowsArgument("a\"b") == "\"a\\\"b\"", "Embedded quote escaped.");
    }

    public static void TunnelStdioCommandQuoting()
    {
        var plain = @"C:\Users\Example\AppData\Local\HagoromoChan\app\Relay\HagoromoChan.Relay.exe";
        True(RuntimeControl.BuildTunnelStdioCommand(plain) ==
            "\"C:\\\\Users\\\\Example\\\\AppData\\\\Local\\\\HagoromoChan\\\\app\\\\Relay\\\\HagoromoChan.Relay.exe\" --mcp-stdio",
            "Tunnel command preserves the Windows path separators.");
        foreach (var path in new[] { plain, @"D:\開発 環境\羽衣ちゃん\Relay.exe",
            @"\\server\share\app\Relay.exe", @"C:\user's folder\Relay.exe" })
        {
            var command = RuntimeControl.BuildTunnelStdioCommand(path);
            // Independent model of the quoted token in v0.0.21 parseCommandArgv:
            // backslash escapes the next character even inside double quotes.
            True(command[0] == '"', "Command starts with a quoted executable.");
            var decoded = new System.Text.StringBuilder();
            var index = 1;
            for (; index < command.Length; index++)
            {
                if (command[index] == '"') break;
                if (command[index] == '\\') index++;
                True(index < command.Length, "Escape is followed by a character.");
                decoded.Append(command[index]);
            }
            True(index < command.Length && command[index..] == "\" --mcp-stdio",
                "The executable is followed by the one fixed stdio argument.");
            True(decoded.ToString() == path, "The tunnel parser recovers the original executable.");
        }
    }

    public static void PipeCommands() => RunPipeCommands().GetAwaiter().GetResult();

    private static async Task RunPipeCommands()
    {
        foreach (var (command, accepted) in new[] {
            (RuntimeCommand.Stop, true), (RuntimeCommand.RestartDesktop, false),
            (RuntimeCommand.Restart, true), (RuntimeCommand.RestartTunnel, true) })
        {
            var pipeName = "HagoromoChan-test-" + Guid.NewGuid().ToString("N");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            async Task Serve()
            {
                await server.WaitForConnectionAsync(timeout.Token);
                var bytes = new byte[1];
                True(await server.ReadAsync(bytes, timeout.Token) == 1 &&
                    bytes[0] == (byte)command, "Expected bounded control command.");
                await server.WriteAsync(new byte[] { accepted ? (byte)1 : (byte)0 }, timeout.Token);
                await server.FlushAsync(timeout.Token);
            }
            var serve = Serve();
            var reply = await RuntimeControl.SendAsync(pipeName, command, timeout.Token);
            await serve;
            True(reply == accepted, "Control acknowledgement preserved.");
        }
    }

    private static void True(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
