using System.Security.Cryptography;

namespace HagoromoChan.Relay;

public sealed class RelaySettings
{
    public const int Port = 48732;
    public const string BaseUrl = "http://127.0.0.1:48732";
    public const string ChannelId = "hagoromo-mini";

    public string DataDirectory { get; }
    public string TokenPath => Path.Combine(DataDirectory, "relay.token");
    public string DatabasePath => Path.Combine(DataDirectory, "conversations.db");

    public RelaySettings()
    {
        var configured = Environment.GetEnvironmentVariable("HAGOROMO_DATA_DIR");
        DataDirectory = Path.GetFullPath(string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HagoromoChan")
            : configured);
    }

    public void EnsureDirectory()
    {
        Directory.CreateDirectory(DataDirectory);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(DataDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    public string GetOrCreateToken()
    {
        if (!File.Exists(TokenPath))
        {
            var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
            using var stream = new FileStream(TokenPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(TokenPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            using var writer = new StreamWriter(stream);
            writer.Write(token);
        }

        return ReadToken();
    }

    public string ReadToken()
    {
        var token = File.ReadAllText(TokenPath).Trim();
        if (token.Length != 64 || !token.All(Uri.IsHexDigit))
        {
            throw new InvalidDataException("ローカル接続キーの形式が不正です。relay.token を確認してください。");
        }
        return token;
    }
}
