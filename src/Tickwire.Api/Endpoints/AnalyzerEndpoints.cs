using Tickwire.LogAnalyzer;

namespace Tickwire.Api.Endpoints;

public sealed record AnalyzeBody(string Text);

public static class AnalyzerEndpoints
{
    private const int MaxChars = 2_000_000;

    public static void MapAnalyzerEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/analyzer", (AnalyzeBody body) => body.Text.Length > MaxChars
                ? Results.Problem($"Logs up to {MaxChars / 1_000_000} MB; split larger files or use the CLI (tickwire fixlog analyze)", statusCode: 413)
                : Results.Ok(FixLogAnalyzer.Analyze(body.Text)))
            .RequireRateLimiting("orders")
            .WithTags("FIX tools")
            .WithSummary("Parses a FIX log, rebuilds order lifecycles and flags sequence, state and quantity problems");
    }
}
