using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HagoromoChan.Launcher;

internal sealed record TunnelConfiguration(string Executable, string ProfileFile, string LogLevel,
    string ExecutableSha256, string Version, string ProtectedRuntimeKey)
{
    internal static TunnelConfiguration? Load(string root)
    {
        var path = Path.Combine(root, "launcher.json");
        if (!File.Exists(path)) return null;
        var result = JsonSerializer.Deserialize<TunnelConfiguration>(File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (result is null || !Path.IsPathFullyQualified(result.Executable) ||
            !Path.IsPathFullyQualified(result.ProfileFile) || !File.Exists(result.Executable) ||
            !File.Exists(result.ProfileFile) || result.LogLevel is not ("info" or "debug" or "warn") ||
            string.IsNullOrWhiteSpace(result.ProtectedRuntimeKey) || string.IsNullOrWhiteSpace(result.ExecutableSha256))
            throw new StartupException("トンネル設定が無効です。Configure.ps1 で再設定してください。");
        using var executable = File.OpenRead(result.Executable);
        if (!Convert.ToHexString(SHA256.HashData(executable)).Equals(result.ExecutableSha256, StringComparison.OrdinalIgnoreCase))
            throw new StartupException("トンネル実行ファイルが変更されました。Configure.ps1 で明示的に再設定してください。");
        return result;
    }

    // Windows PowerShell ConvertFrom-SecureString (without -Key) uses current-user DPAPI.
    // No plaintext key file, command-line value, Git entry, or status/log field is used.
    internal string ReadRuntimeKey()
    {
        var encrypted = Convert.FromHexString(ProtectedRuntimeKey.Trim());
        var input = new DataBlob { Length = encrypted.Length, Data = Marshal.AllocHGlobal(encrypted.Length) };
        DataBlob output = default;
        try
        {
            Marshal.Copy(encrypted, 0, input.Data, encrypted.Length);
            if (!CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            if (output.Length is <= 0 or > 16384 || output.Length % 2 != 0)
                throw new InvalidDataException("Invalid DPAPI value.");
            var bytes = new byte[output.Length];
            try
            {
                Marshal.Copy(output.Data, bytes, 0, bytes.Length);
                var value = Encoding.Unicode.GetString(bytes);
                if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl))
                    throw new InvalidDataException("Invalid runtime key.");
                return value;
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        finally
        {
            if (output.Data != IntPtr.Zero)
            {
                for (var i = 0; i < output.Length; i++) Marshal.WriteByte(output.Data, i, 0);
                _ = LocalFree(output.Data);
            }
            Marshal.FreeHGlobal(input.Data);
            CryptographicOperations.ZeroMemory(encrypted);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob { public int Length; public IntPtr Data; }
    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CryptUnprotectData(ref DataBlob input, IntPtr description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, uint flags, out DataBlob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
}
