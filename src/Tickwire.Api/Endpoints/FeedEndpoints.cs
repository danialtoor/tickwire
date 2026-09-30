using Tickwire.Api.Services;

namespace Tickwire.Api.Endpoints;

public sealed record FeedConnectBody(string Provider, Dictionary<string, string>? Credentials);

public static class FeedEndpoints
{
    public static void MapFeedEndpoints(this IEndpointRouteBuilder app)
    {
        var feeds = app.MapGroup("/api/feeds").WithTags("Market data");

        feeds.MapGet("/providers", (FeedManager manager) => manager.Providers)
            .WithSummary("Supported external options data providers and the credentials each needs");

        feeds.MapGet("/", async (HttpContext http, FeedManager manager) =>
            {
                var caller = await Auth.CallerAsync(http);
                return caller.ClientId is null ? Auth.Unauthorized() : Results.Ok(manager.Snapshot(caller.ClientId));
            })
            .WithSummary("The caller's feed status and latest quotes");

        feeds.MapPost("/connect", async (FeedConnectBody body, HttpContext http, FeedManager manager) =>
            {
                var caller = await Auth.CallerAsync(http);
                if (caller.ClientId is null)
                {
                    return Auth.Unauthorized();
                }

                try
                {
                    return Results.Ok(await manager.ConnectAsync(caller.ClientId, body.Provider, body.Credentials ?? []));
                }
                catch (ArgumentException ex)
                {
                    return Results.ValidationProblem(new Dictionary<string, string[]> { ["feed"] = [ex.Message] });
                }
                catch (InvalidOperationException ex)
                {
                    return Results.Problem(ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
                }
            })
            .RequireRateLimiting("guest")
            .WithSummary("Connects the caller's own provider account. Credentials are kept in memory for this session only.");

        feeds.MapPost("/disconnect", async (HttpContext http, FeedManager manager) =>
            {
                var caller = await Auth.CallerAsync(http);
                if (caller.ClientId is null)
                {
                    return Auth.Unauthorized();
                }

                await manager.DisconnectAsync(caller.ClientId);
                return Results.NoContent();
            })
            .WithSummary("Stops the caller's feed and forgets its credentials");
    }
}
