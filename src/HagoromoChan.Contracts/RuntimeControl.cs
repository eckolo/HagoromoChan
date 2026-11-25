using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace HagoromoChan.Contracts;

// Local, current-user-only control plane. Never put conversation data or credentials here.
public enum RuntimeCommand : byte { Stop = 1, Restart = 2, RestartDesktop = 3, RestartTunnel = 4 }

public sealed record RuntimeSnapshot(string Session, string Phase, int? DesktopPid,
    int? RelayPid, bool RelayReady, int? TunnelPid, bool TunnelReady, string TunnelState,
    string? Problem, DateTimeOffset ObservedAt);

public static class RuntimeControl
{
    public const string SessionVariable = "HAGOROMO_MANAGED_SESSION";
    public const string StopVariable = "HAGOROMO_STOP_EVENT";
    public const string PipeVariable = "HAGOROMO_CONTROL_PIPE";
    public const string StatusFile = "runtime-status.json";
    public const string LockFile = "launcher.lock";

    public static string DataDirectory()
    {
        var configured = Environment.GetEnvironmentVariable("HAGOROMO_DATA_DIR");
        return Path.GetFullPath(string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HagoromoChan")
            : configured);
    }

    public static string PipeName(string directory)
    {
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (OperatingSystem.IsWindows()) normalized = normalized.ToUpperInvariant();
        return "HagoromoChan-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..32];
    }

    // Standard Windows command-line escaping, including paths with spaces and trailing backslashes.
    public static string QuoteWindowsArgument(string value)
    {
        var result = new StringBuilder("\"");
        var slashes = 0;
        foreach (var ch in value)
        {
            if (ch == '\\') { slashes++; continue; }
            if (ch == '"') { result.Append('\\', slashes * 2 + 1); result.Append('"'); }
            else { result.Append('\\', slashes); result.Append(ch); }
            slashes = 0;
        }
        result.Append('\\', slashes * 2).Append('"');
        return result.ToString();
    }

    public static bool IsCurrentSnapshot(RuntimeSnapshot? snapshot, string session, DateTimeOffset now) =>
        snapshot is not null && snapshot.Session == session &&
        now - snapshot.ObservedAt >= TimeSpan.FromSeconds(-2) &&
        now - snapshot.ObservedAt < TimeSpan.FromSeconds(12);

    public static async Task<bool> SendAsync(string pipe, RuntimeCommand command, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        using var client = new NamedPipeClientStream(".", pipe, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await client.ConnectAsync(timeout.Token).ConfigureAwait(false);
        await client.WriteAsync(new byte[] { (byte)command }, timeout.Token).ConfigureAwait(false);
        await client.FlushAsync(timeout.Token).ConfigureAwait(false);
        var buffer = new byte[1];
        return await client.ReadAsync(buffer, timeout.Token).ConfigureAwait(false) == 1 && buffer[0] == 1;
    }

    [SupportedOSPlatform("windows")]
    public static IDisposable? WatchStop(Action stop)
    {
        var name = Environment.GetEnvironmentVariable(StopVariable);
        if (string.IsNullOrEmpty(name)) return null;
        // If the manager died before the child opened its event, fail closed.
        var handle = EventWaitHandle.OpenExisting(name);
        try
        {
            var registration = ThreadPool.RegisterWaitForSingleObject(handle,
                (_, _) => stop(), null, Timeout.Infinite, executeOnlyOnce: true);
            return new StopRegistration(handle, registration);
        }
        catch { handle.Dispose(); throw; }
    }

    private sealed class StopRegistration(EventWaitHandle handle, RegisteredWaitHandle registration) : IDisposable
    {
        public void Dispose()
        {
            registration.Unregister(null);
            handle.Dispose();
        }
    }
}
