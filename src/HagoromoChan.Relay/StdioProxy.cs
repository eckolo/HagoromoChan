using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace HagoromoChan.Relay;

// The official tunnel client launches this process. Only JSON-RPC is written to stdout.
public static class StdioProxy
{
    public static async Task RunAsync(RelaySettings settings)
    {
        using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false })
        {
            BaseAddress = new Uri(RelaySettings.BaseUrl),
            Timeout = TimeSpan.FromSeconds(25)
        };
        Console.InputEncoding = new UTF8Encoding(false);
        Console.OutputEncoding = new UTF8Encoding(false);

        while (await Console.In.ReadLineAsync() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            JsonElement? id = null;
            var needsReply = true;
            try
            {
                if (Encoding.UTF8.GetByteCount(line) > 256 * 1024)
                {
                    throw new JsonException("Message too large");
                }
                using var document = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 32 });
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    throw new JsonException("Expected object");
                }
                needsReply = document.RootElement.TryGetProperty("id", out var requestId);
                if (needsReply)
                {
                    id = requestId.Clone();
                }

                using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ReadToken());
                request.Content = new StringContent(line, Encoding.UTF8, "application/json");
                using var response = await client.SendAsync(request);
                response.EnsureSuccessStatusCode();
                if (needsReply)
                {
                    var payload = await response.Content.ReadAsStringAsync();
                    using var result = JsonDocument.Parse(payload);
                    await Console.Out.WriteLineAsync(result.RootElement.GetRawText());
                    await Console.Out.FlushAsync();
                }
            }
            catch (JsonException)
            {
                await WriteErrorAsync(id, -32700, "Invalid JSON-RPC message.");
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException or
                                              UnauthorizedAccessException or TaskCanceledException)
            {
                if (needsReply)
                {
                    await WriteErrorAsync(id, -32603,
                        "HagoromoChan relay is unavailable. Start the local relay and check its connection key.");
                }
                Console.Error.WriteLine("HagoromoChan local relay connection failed.");
            }
        }
    }

    private static async Task WriteErrorAsync(JsonElement? id, int code, string message)
    {
        var response = new { jsonrpc = "2.0", id, error = new { code, message } };
        await Console.Out.WriteLineAsync(JsonSerializer.Serialize(response));
        await Console.Out.FlushAsync();
    }
}
