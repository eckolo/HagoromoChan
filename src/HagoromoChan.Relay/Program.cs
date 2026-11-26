using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HagoromoChan.Contracts;
using HagoromoChan.Relay;

var settings = new RelaySettings();
if (args.Contains("--mcp-stdio", StringComparer.Ordinal))
{
    // The tunnel runtime key is not needed by the stdio adapter.
    Environment.SetEnvironmentVariable("CONTROL_PLANE_API_KEY", null);
    Environment.SetEnvironmentVariable("OPENAI_API_KEY", null);
    await StdioProxy.RunAsync(settings);
    return;
}
if (args.Length != 0)
{
    Console.Error.WriteLine("Usage: HagoromoChan.Relay [--mcp-stdio]");
    Environment.ExitCode = 2;
    return;
}

settings.EnsureDirectory();
FileStream processLock;
try
{
    processLock = new FileStream(Path.Combine(settings.DataDirectory, "relay.lock"),
        FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
}
catch (IOException)
{
    Console.Error.WriteLine("中継は同じデータフォルダーですでに起動しています。");
    Environment.ExitCode = 1;
    return;
}

using var relayLock = processLock;
var tokenBytes = Encoding.ASCII.GetBytes(settings.GetOrCreateToken());
var builder = WebApplication.CreateBuilder(Array.Empty<string>());
builder.WebHost.ConfigureKestrel(server =>
{
    server.Listen(IPAddress.Loopback, RelaySettings.Port);
    server.Limits.MaxRequestBodySize = 256 * 1024;
});
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(options => options.SingleLine = true);
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
using var store = new ChatStore(settings.DatabasePath, RelaySettings.ChannelId);
builder.Services.AddSingleton(store);
builder.Services.AddSingleton<WebhookClient>();
builder.Services.AddSingleton<McpHandler>();
builder.Services.AddHostedService<EventDispatcher>();
builder.Services.AddHostedService<WorkWakeDispatcher>();
var app = builder.Build();

app.Use(async (context, next) =>
{
    if (context.Connection.RemoteIpAddress is null ||
        !IPAddress.IsLoopback(context.Connection.RemoteIpAddress) ||
        context.Request.Headers.ContainsKey("Origin"))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }

    if (context.Request.Path != "/health")
    {
        var authorization = context.Request.Headers.Authorization.ToString();
        var supplied = authorization.StartsWith("Bearer ", StringComparison.Ordinal)
            ? Encoding.ASCII.GetBytes(authorization[7..]) : Array.Empty<byte>();
        if (!CryptographicOperations.FixedTimeEquals(supplied, tokenBytes))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new ApiError("ローカル接続キーが一致しません。"));
            return;
        }
    }

    context.Response.Headers.CacheControl = "no-store";
    try
    {
        await next(context);
    }
    catch (ValidationException exception)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsJsonAsync(new ApiError(exception.Message));
    }
    catch (ConflictException exception)
    {
        context.Response.StatusCode = StatusCodes.Status409Conflict;
        await context.Response.WriteAsJsonAsync(new ApiError(exception.Message));
    }
    catch (KeyNotFoundException)
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        await context.Response.WriteAsJsonAsync(new ApiError("指定した発言が見つかりません。"));
    }
});

var managedSession = Environment.GetEnvironmentVariable(RuntimeControl.SessionVariable);
app.MapGet("/health", () => Results.Json(new { service = "HagoromoChan", status = "ok", managedSession }));
app.MapGet("/api/status", (ChatStore store) => store.GetStatus());
app.MapGet("/api/messages", (long? after, int? limit, ChatStore store) =>
    store.ReadMessages(after ?? 0, limit ?? 100));
app.MapPost("/api/messages", (SendMessageRequest request, ChatStore store) =>
    Results.Json(store.AddUserMessage(request), statusCode: StatusCodes.Status200OK));
app.MapPost("/api/retry", (ChatStore store) =>
{
    store.RetryFailedDeliveries();
    return store.GetStatus();
});

app.MapGet("/api/m05/sources", (ChatStore store) => store.ListCloudSources());
app.MapPost("/api/m05/sources", (SaveCloudSourceRequest request, ChatStore store) =>
    store.SaveCloudSource(request));
app.MapGet("/api/m05/sources/{sourceId}/answers", (string sourceId, long? after, int? limit, ChatStore store) =>
    store.ReadCloudAnswers(sourceId, after ?? 0, limit ?? 50));

app.MapPost("/api/m05/work-discovery", (McpHandler handler) => handler.WorkPull.Discover());
app.MapPost("/api/m05/work-discovery/{operationId}/select/{conversationId}",
    (string operationId, string conversationId, McpHandler handler) => handler.WorkPull.SelectChoice(operationId, conversationId));
app.MapPost("/api/m05/sources/{sourceId}/work-answers", (string sourceId, McpHandler handler) =>
    handler.WorkPull.ListAnswers(sourceId));
app.MapPost("/api/m05/work-answers/{operationId}/select/{messageId}",
    (string operationId, string messageId, McpHandler handler) => handler.WorkPull.SelectAnswer(operationId, messageId));
app.MapPost("/api/m05/sources/{sourceId}/work-retrieval", (string sourceId, McpHandler handler) =>
    handler.WorkPull.Start(sourceId));
app.MapGet("/api/m05/work-retrieval/{operationId}", (string operationId, McpHandler handler) =>
    handler.WorkPull.Status(operationId));

app.MapPost("/mcp", async (HttpRequest request, McpHandler handler, CancellationToken cancellationToken) =>
{
    if (!request.HasJsonContentType())
    {
        return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);
    }

    try
    {
        using var document = await JsonDocument.ParseAsync(request.Body,
            new JsonDocumentOptions { MaxDepth = 32 }, cancellationToken);
        var response = await handler.HandleAsync(document.RootElement, cancellationToken);
        return response is null
            ? Results.StatusCode(StatusCodes.Status202Accepted)
            : Results.Json(response);
    }
    catch (JsonException)
    {
        return Results.Json(new
        {
            jsonrpc = "2.0",
            id = (object?)null,
            error = new { code = -32700, message = "Parse error" }
        });
    }
});

// Standalone Linux/Windows debug runs retain their existing console shutdown.
using var managedStop = OperatingSystem.IsWindows()
    ? RuntimeControl.WatchStop(app.Lifetime.StopApplication) : null;
await app.RunAsync();
