using System.IO;
using System.Text.Json;
using HagoromoChan.Contracts;

namespace HagoromoChan.Desktop;

internal sealed class PendingMessageStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _path;

    public PendingMessageStore(string dataDirectory)
    {
        _path = Path.Combine(dataDirectory, "pending-message.json");
    }

    public async Task<SendMessageRequest?> LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read,
                4096, FileOptions.Asynchronous);
            var pending = await JsonSerializer.DeserializeAsync<SendMessageRequest>(stream, JsonOptions, cancellationToken);
            if (pending is null || !Guid.TryParseExact(pending.RequestId, "D", out _)
                || string.IsNullOrWhiteSpace(pending.Text) || pending.Text.Length > 32768)
            {
                throw new InvalidDataException("未確認のメッセージの保存内容を読み取れません。");
            }

            return pending;
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }

    public async Task SaveAsync(SendMessageRequest message, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporaryPath = _path + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None,
                             4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, message, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public void Clear() => File.Delete(_path);
}
