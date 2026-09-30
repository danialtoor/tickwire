using Microsoft.Extensions.Options;
using Tickwire.Api.Services;
using Tickwire.Engine;
using Tickwire.Fix;
using Tickwire.Fix.Dictionary;
using Tickwire.Persistence;
using Tickwire.Venue;

namespace Tickwire.Api.Endpoints;

public sealed record LimitsBody(
    decimal MaxOrderQty,
    decimal MaxNotional,
    decimal PriceBandPct,
    decimal PriceBandMinAbs,
    int MaxOpenOrders,
    string[]? AllowedUnderlyings,
    string[] AllowedOrderTypes,
    string[] AllowedTimeInForce,
    int MaxMessagesPerSecond,
    bool CancelOnDisconnect,
    double? MaxAbsDelta = null,
    double? MaxAbsVega = null);

public sealed record KillBody(bool Engaged);

public sealed record KillResult(bool Engaged, int OrdersCanceled);

public sealed record DecodeBody(string Message);

public sealed record DecodeResult(bool Parsed, string? Error, string? MsgType, string? MsgTypeName, string? Integrity,
    IReadOnlyList<DecodedField> Fields, ValidationIssue? Validation);

public sealed record ConnectInfo(string ClientId, string SenderCompID, string TargetCompID, string Host, int Port, int HeartBtInt,
    IReadOnlyDictionary<string, string> Configs);

public static class AdminEndpoints
{
    public static void MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var clients = app.MapGroup("/api/clients").WithTags("Clients");

        clients.MapGet("/", async (HttpContext http, IClientRepository repo, CancellationToken ct) =>
            {
                var caller = await Auth.CallerAsync(http);
                var all = await repo.ListAsync(ct);
                if (caller.IsAdmin)
                {
                    return Results.Ok(all.Select(Redact));
                }

                return caller.ClientId is null ? Auth.Unauthorized() : Results.Ok(all.Where(c => c.ClientId == caller.ClientId).Select(Redact));
            })
            .WithSummary("Client configurations (admins see all, guests see their own)");

        clients.MapPut("/{clientId}/limits", async (string clientId, LimitsBody body, HttpContext http, IClientRepository repo,
                SessionManager sessions, CancellationToken ct) =>
            {
                var caller = await Auth.CallerAsync(http);
                if (!caller.CanManage(clientId))
                {
                    return caller.ClientId is null && !caller.IsAdmin ? Auth.Unauthorized() : Auth.Forbidden();
                }

                if (!TryMapLimits(body, caller.IsAdmin, out var limits, out var error))
                {
                    return Results.ValidationProblem(new Dictionary<string, string[]> { ["limits"] = [error] });
                }

                await repo.UpdateLimitsAsync(clientId, limits, ct);
                if (await sessions.LoadAccountAsync(clientId, ct) is { } account)
                {
                    account.Limits = limits; // takes effect on the next order, no restart
                }

                await repo.AuditAsync(caller.IsAdmin ? "admin" : clientId, "limits.updated", clientId,
                    System.Text.Json.JsonSerializer.Serialize(body), ct);
                return Results.Ok(limits);
            })
            .WithSummary("Changes a client's pre-trade risk limits; applies immediately");

        clients.MapPost("/{clientId}/kill", async (string clientId, KillBody body, HttpContext http, IClientRepository repo,
                SessionManager sessions, OrderManager oms, CancellationToken ct) =>
            {
                var caller = await Auth.CallerAsync(http);
                if (!caller.CanManage(clientId))
                {
                    return caller.ClientId is null && !caller.IsAdmin ? Auth.Unauthorized() : Auth.Forbidden();
                }

                var account = await sessions.LoadAccountAsync(clientId, ct);
                if (account is null)
                {
                    return Results.NotFound();
                }

                account.KillSwitch = body.Engaged;
                await repo.SetKillSwitchAsync(clientId, body.Engaged, ct);
                var canceled = await oms.KillSwitchAsync(clientId, body.Engaged, "Kill switch engaged");
                await repo.AuditAsync(caller.IsAdmin ? "admin" : clientId, body.Engaged ? "kill.engaged" : "kill.released", clientId,
                    $"{canceled} order(s) canceled", ct);
                return Results.Ok(new KillResult(body.Engaged, canceled));
            })
            .WithSummary("Per-client kill switch: blocks new orders and cancels open ones");

        var admin = app.MapGroup("/api/admin").WithTags("Admin");

        admin.MapPost("/kill", async (KillBody body, HttpContext http, OrderManager oms, IClientRepository repo, CancellationToken ct) =>
            {
                var caller = await Auth.CallerAsync(http);
                if (!caller.IsAdmin)
                {
                    return Auth.Forbidden("Admin key required");
                }

                var canceled = await oms.KillSwitchAsync(null, body.Engaged, "Global kill switch engaged");
                await repo.AuditAsync("admin", body.Engaged ? "global-kill.engaged" : "global-kill.released", null, $"{canceled} canceled", ct);
                return Results.Ok(new KillResult(body.Engaged, canceled));
            })
            .WithSummary("Global kill switch (admin)");

        admin.MapGet("/audit", async (HttpContext http, IClientRepository repo, CancellationToken ct) =>
            (await Auth.CallerAsync(http)).IsAdmin ? Results.Ok(await repo.RecentAuditAsync(200, ct)) : Auth.Forbidden("Admin key required"))
            .WithSummary("Recent admin and onboarding actions (admin)");

        // Nightly demo reset, called by a scheduled GitHub Actions workflow.
        app.MapPost("/admin/reset", async (HttpContext http, Housekeeping housekeeping, OrderManager oms, IClientRepository repo,
                CancellationToken ct) =>
            {
                var caller = await Auth.CallerAsync(http);
                if (!caller.IsAdmin)
                {
                    return Auth.Forbidden("Admin key required");
                }

                var purged = await housekeeping.PurgeAsync(ct);
                await oms.KillSwitchAsync(null, false, "reset");
                await repo.AuditAsync("admin", "demo.reset", null, $"{purged} expired guest(s) purged", ct);
                return Results.Ok(new { purged });
            })
            .WithTags("Admin")
            .WithSummary("Purges expired guests and clears the global kill switch (admin)");

        var connect = app.MapGroup("/api/connect").WithTags("Onboarding");

        connect.MapPost("/", async (HttpContext http, SessionManager sessions, IClientRepository repo, IOptions<TickwireOptions> options,
                CancellationToken ct) =>
            {
                var caller = await Auth.CallerAsync(http);
                if (caller.ClientId is null)
                {
                    return Auth.Unauthorized();
                }

                var config = await sessions.ProvisionExternalSessionAsync(caller.ClientId, ct);
                var client = await repo.GetAsync(caller.ClientId, ct);
                await sessions.GetOrCreateAsync(client!, config, ct);
                return Results.Ok(ClientConfigs.Build(caller.ClientId, config, options.Value));
            })
            .RequireRateLimiting("guest")
            .WithSummary("Provisions SenderCompID/TargetCompID for your own FIX engine and returns ready-to-use configs");

        var fix = app.MapGroup("/api/fix").WithTags("FIX tools");

        fix.MapPost("/decode", (DecodeBody body) =>
            {
                var bytes = FixDisplay.FromPiped(body.Message);
                if (!FixParser.TryParse(bytes, out var msg, out var error))
                {
                    return new DecodeResult(false, error.ToString(), null, null, null, [], null);
                }

                var dict = FixDictionary.Fix44;
                return new DecodeResult(true, null, msg.MsgType, dict.MessageName(msg.MsgType),
                    msg.IsIntact ? "OK" : $"{msg.Integrity} (BodyLength {msg.DeclaredBodyLength}/{msg.ActualBodyLength}, CheckSum {msg.DeclaredChecksum:000}/{msg.ActualChecksum:000})",
                    dict.Decode(msg), dict.Validate(msg));
            })
            .WithSummary("Decodes and validates one FIX message ('|' or SOH delimited)");

        app.MapGet("/api/metrics", (OpsSnapshotter ops) => ops.Snapshot()).WithTags("Ops").WithSummary("Ops dashboard snapshot");
    }

    private static object Redact(ClientRecord c) => new
    {
        c.ClientId, c.DisplayName, c.IsGuest, c.KillSwitch, c.Limits, c.Sessions, c.CreatedAt, c.ExpiresAt,
    };

    private static bool TryMapLimits(LimitsBody b, bool isAdmin, out RiskLimits limits, out string error)
    {
        limits = null!;
        error = string.Empty;
        // Guests can tighten or loosen within the demo's sandbox; admins can set anything sensible.
        var maxQty = isAdmin ? 1_000_000m : 1_000m;
        var maxNotional = isAdmin ? 1_000_000_000m : 1_000_000m;
        if (b.MaxOrderQty is <= 0 || b.MaxOrderQty > maxQty)
        {
            error = $"maxOrderQty must be between 1 and {maxQty}";
            return false;
        }

        if (b.MaxNotional is <= 0 || b.MaxNotional > maxNotional)
        {
            error = $"maxNotional must be between 1 and {maxNotional}";
            return false;
        }

        if (b.PriceBandPct is < 0.01m or > 10m || b.PriceBandMinAbs is < 0 or > 100)
        {
            error = "priceBandPct must be 0.01-10 and priceBandMinAbs 0-100";
            return false;
        }

        if (b.MaxOpenOrders is < 1 or > 1000 || b.MaxMessagesPerSecond < 1 || b.MaxMessagesPerSecond > (isAdmin ? 100_000 : 100))
        {
            error = "maxOpenOrders must be 1-1000 and maxMessagesPerSecond within the allowed range";
            return false;
        }

        try
        {
            limits = new RiskLimits
            {
                MaxOrderQty = b.MaxOrderQty,
                MaxNotional = b.MaxNotional,
                PriceBandPct = b.PriceBandPct,
                PriceBandMinAbs = b.PriceBandMinAbs,
                MaxOpenOrders = b.MaxOpenOrders,
                AllowedUnderlyings = b.AllowedUnderlyings is { Length: > 0 } u ? [.. u.Select(x => x.ToUpperInvariant())] : null,
                AllowedOrderTypes = [.. b.AllowedOrderTypes.Select(Enum.Parse<OrderType>)],
                AllowedTimeInForce = [.. b.AllowedTimeInForce.Select(Enum.Parse<TimeInForce>)],
                MaxMessagesPerSecond = b.MaxMessagesPerSecond,
                CancelOnDisconnect = b.CancelOnDisconnect,
                MaxAbsDelta = Math.Clamp(b.MaxAbsDelta ?? 10_000, 0, isAdmin ? 10_000_000 : 100_000),
                MaxAbsVega = Math.Clamp(b.MaxAbsVega ?? 10_000, 0, isAdmin ? 10_000_000 : 100_000),
            };
            return true;
        }
        catch (ArgumentException ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
