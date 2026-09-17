using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Diagnostics.Ping;

namespace SpencerDenim.Api.Endpoints;

internal static class PingEndpoints
{
    public static IEndpointRouteBuilder MapPingEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api").WithTags("Diagnostics");

        api.MapGet("/ping", GetPingAsync)
            .WithName("Ping")
            .WithSummary("Checks that the API is reachable.")
            .WithDescription("Returns a small payload without touching the database. Use /health/ready for dependency checks.")
            .Produces<PingResponse>();

        return endpoints;
    }

    private static async Task<IResult> GetPingAsync(
        IQueryHandler<PingQuery, PingResponse> handler,
        CancellationToken cancellationToken)
    {
        var response = await handler.HandleAsync(new PingQuery(), cancellationToken);
        return TypedResults.Ok(response);
    }
}
