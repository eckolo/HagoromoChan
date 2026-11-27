using HagoromoChan.Contracts;

namespace HagoromoChan.Relay;

public static class CloudImportRoutes
{
    internal const string LegacyPrefix = "/api/m05";

    public static void MapCloudImportRoutes(this IEndpointRouteBuilder endpoints)
    {
        MapOperations(endpoints.MapGroup(CloudImportHttp.Prefix));
        // Older Desktop builds use this prefix. Both groups have the same
        // handlers and are served by the same Relay authentication pipeline.
        MapOperations(endpoints.MapGroup(LegacyPrefix));
    }

    private static void MapOperations(RouteGroupBuilder routes)
    {
        routes.MapGet("/sources", (ChatStore store) => store.ListCloudSources());
        routes.MapPost("/sources", (SaveCloudSourceRequest request, ChatStore store) =>
            store.SaveCloudSource(request));
        routes.MapGet("/sources/{sourceId}/answers", (string sourceId, long? after, int? limit, ChatStore store) =>
            store.ReadCloudAnswers(sourceId, after ?? 0, limit ?? 50));

        routes.MapPost("/work-discovery", (McpHandler handler) => handler.WorkPull.Discover());
        routes.MapPost("/work-discovery/{operationId}/select/{conversationId}",
            (string operationId, string conversationId, McpHandler handler) => handler.WorkPull.SelectChoice(operationId, conversationId));
        routes.MapPost("/sources/{sourceId}/work-answers", (string sourceId, McpHandler handler) =>
            handler.WorkPull.ListAnswers(sourceId));
        routes.MapPost("/work-answers/{operationId}/select/{messageId}",
            (string operationId, string messageId, McpHandler handler) => handler.WorkPull.SelectAnswer(operationId, messageId));
        routes.MapPost("/sources/{sourceId}/work-retrieval", (string sourceId, McpHandler handler) =>
            handler.WorkPull.Start(sourceId));
        routes.MapGet("/work-retrieval/{operationId}", (string operationId, McpHandler handler) =>
            handler.WorkPull.Status(operationId));
    }
}
