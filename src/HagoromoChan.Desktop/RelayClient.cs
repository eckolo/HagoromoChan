using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using HagoromoChan.Contracts;

namespace HagoromoChan.Desktop;

internal sealed class RelayClient : IDisposable
{
    private readonly string _tokenPath;
    private readonly HttpClient _httpClient = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseProxy = false
    })
    {
        BaseAddress = new Uri("http://127.0.0.1:48732"),
        Timeout = TimeSpan.FromSeconds(10)
    };

    public RelayClient(string dataDirectory)
    {
        _tokenPath = Path.Combine(dataDirectory, "relay.token");
    }

    public Task<RelayStatus> GetStatusAsync(CancellationToken cancellationToken) =>
        SendAsync<RelayStatus>(HttpMethod.Get, "/api/status", null, cancellationToken);

    public Task<MessagePage> GetMessagesAsync(long cursor, CancellationToken cancellationToken) =>
        SendAsync<MessagePage>(HttpMethod.Get, $"/api/messages?after={cursor}&limit=100", null, cancellationToken);

    public Task<ChatMessage> SendMessageAsync(SendMessageRequest message, CancellationToken cancellationToken) =>
        SendAsync<ChatMessage>(HttpMethod.Post, "/api/messages", message, cancellationToken);

    public Task<RelayStatus> RetryDeliveryAsync(CancellationToken cancellationToken) =>
        SendAsync<RelayStatus>(HttpMethod.Post, "/api/retry", null, cancellationToken);

    public Task<CloudSource[]> GetCloudSourcesAsync(CancellationToken cancellationToken) =>
        SendAsync<CloudSource[]>(HttpMethod.Get, "/api/m05/sources", null, cancellationToken);

    public Task<CloudSource> SaveCloudSourceAsync(SaveCloudSourceRequest source, CancellationToken cancellationToken) =>
        SendAsync<CloudSource>(HttpMethod.Post, "/api/m05/sources", source, cancellationToken);

    public Task<CloudAnswerPage> ReadCloudAnswersAsync(string sourceId, long cursor, CancellationToken cancellationToken) =>
        SendAsync<CloudAnswerPage>(HttpMethod.Get,
            $"/api/m05/sources/{Uri.EscapeDataString(sourceId)}/answers?after={cursor}&limit=20", null, cancellationToken);

    private async Task<T> SendAsync<T>(HttpMethod method, string path, object? message,
        CancellationToken cancellationToken)
    {
        var token = (await File.ReadAllTextAsync(_tokenPath, cancellationToken).ConfigureAwait(false)).Trim();
        if (token.Length == 0 || token.Any(char.IsControl))
        {
            throw new InvalidDataException("中継の認証情報を読み取れません。起動スクリプトを再実行してください。");
        }

        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (message is not null)
        {
            request.Content = JsonContent.Create(message);
        }

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var description = response.StatusCode switch
            {
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                    "中継の認証に失敗しました。起動スクリプトを再実行してください。",
                HttpStatusCode.BadRequest => "送信内容を受け付けられませんでした。",
                HttpStatusCode.Conflict => "保存済みの送信内容と一致しません。未確認のメッセージを確認してください。",
                _ => $"中継でエラーが発生しました（HTTP {(int)response.StatusCode}）。"
            };

            if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Conflict)
            {
                try
                {
                    var details = await response.Content.ReadFromJsonAsync<ApiError>(cancellationToken)
                        .ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(details?.Error))
                    {
                        description += Environment.NewLine + details.Error;
                    }
                }
                catch (JsonException)
                {
                    // The HTTP status still gives an actionable error if the body is malformed.
                }
            }

            throw new HttpRequestException(description, null, response.StatusCode);
        }

        try
        {
            return await response.Content.ReadFromJsonAsync<T>(cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("中継から空の応答が返りました。");
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("中継の応答形式を読み取れません。", error);
        }
    }

    public void Dispose() => _httpClient.Dispose();
}
