using Tickwire.Api.Services;
using Tickwire.Venue;

namespace Tickwire.Api.Endpoints;

public sealed record ChaosBody(string Action, int? Count);

public sealed record ChaosResult(string Action, string Description);

public sealed record SessionTrafficDto(string Key, IReadOnlyList<WireEventDto> Messages, IReadOnlyList<SessionLogDto> Logs);

public static class SessionEndpoints
{
    public static void MapSessionEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/sessions").WithTags("Sessions");

        api.MapGet("/", (OpsSnapshotter ops) => ops.Snapshot().Sessions)
            .WithSummary("All FIX sessions with state and sequence numbers");

        api.MapGet("/{compId}/messages", async (string compId, int? limit, HttpContext http, SessionManager sessions, WireTap tap) =>
            {
                var (managed, error) = await AuthorizeAsync(compId, http, sessions);
                return error ?? Results.Ok(new SessionTrafficDto(managed!.Key, tap.Recent(managed.Key, Math.Clamp(limit ?? 300, 1, 1000)),
                    tap.RecentLogs(managed.Key)));
            })
            .WithSummary("Recent wire traffic and session events (for the FIX Inspector)");

        api.MapPost("/{compId}/chaos", async (string compId, ChaosBody body, HttpContext http, SessionManager sessions,
                InstrumentRegistry instruments) =>
            {
                var (managed, error) = await AuthorizeAsync(compId, http, sessions);
                if (error is not null)
                {
                    return error;
                }

                if (!managed!.Config.EnableChaos)
                {
                    return Results.Problem("Chaos is only enabled for in-browser guest sessions", statusCode: 409);
                }

                var trader = sessions.Guest(managed.Account.ClientId);
                var n = Math.Clamp(body.Count ?? 3, 1, 20);
                var venue = managed.Session;
                var client = trader?.Session;
                if (client is null)
                {
                    return Results.Problem("Guest trader is not running", statusCode: 409);
                }

                var tag = $"CHAOS-{DateTime.UtcNow:HHmmss}";
                string description;
                switch (body.Action)
                {
                    case "drop-venue":
                        venue.Faults.DropNext(n);
                        description = $"The venue will drop its next {n} outbound message(s). Send an order: the client sees the gap, sends ResendRequest(2), and the venue resends with PossDupFlag=Y and gap-fills admin messages.";
                        break;
                    case "drop-client":
                        client.Faults.DropNext(n);
                        trader!.Poke(tag + "-A");
                        trader.Poke(tag + "-B");
                        description = $"The client dropped {Math.Min(n, 2)} message(s) on the way out. The venue will detect the gap and request a resend.";
                        break;
                    case "corrupt-checksum":
                        client.Faults.CorruptNextChecksum();
                        trader!.Poke(tag + "-A");
                        trader.Poke(tag + "-B");
                        description = "The next client message had its CheckSum(10) corrupted. The venue ignores garbled messages (as the spec requires), so the next message reveals a gap and triggers a ResendRequest.";
                        break;
                    case "corrupt-bodylength":
                        client.Faults.CorruptNextBodyLength();
                        trader!.Poke(tag + "-A");
                        trader.Poke(tag + "-B");
                        description = "The next client message had a wrong BodyLength(9). The venue's framer resynchronizes on the CheckSum field, ignores the garbled message, and recovers through a ResendRequest.";
                        break;
                    case "stop-heartbeats":
                        client.SuppressHeartbeats = true;
                        description = $"The client stops heartbeating and ignores TestRequests. After {venue.HeartBtInt * 1.2:0.#}s of silence the venue sends TestRequest(1); after another {venue.HeartBtInt}s it logs out. The client reconnects and heartbeats resume.";
                        break;
                    case "seq-gap":
                        client.AdjustNextSenderSeqNum(n);
                        trader!.Poke(tag);
                        description = $"The client skipped {n} outbound sequence number(s). The venue sends ResendRequest(2); the client answers with SequenceReset-GapFill(4, 123=Y).";
                        break;
                    case "seq-too-low":
                        client.AdjustNextSenderSeqNum(-2);
                        trader!.Poke(tag);
                        description = "The client reused old sequence numbers without PossDupFlag. The venue logs out with \"MsgSeqNum too low\". The client reconnects with ResetSeqNumFlag(141)=Y, which is the standard recovery.";
                        break;
                    case "invalid-field":
                        trader!.SendInvalidOrder(instruments.Chain("SPY")[0]);
                        description = "The client sent a NewOrderSingle with Side(54)=Z. The venue answers with a session-level Reject(3), SessionRejectReason(373)=5 (value incorrect), RefTagID(371)=54.";
                        break;
                    case "disconnect":
                        venue.Disconnect("Disconnected by chaos panel");
                        description = "The venue dropped the TCP-equivalent connection without a Logout. The client reconnects and both sides continue from their stored sequence numbers.";
                        break;
                    default:
                        return Results.ValidationProblem(new Dictionary<string, string[]>
                        {
                            ["action"] = ["drop-venue, drop-client, corrupt-checksum, corrupt-bodylength, stop-heartbeats, seq-gap, seq-too-low, invalid-field, disconnect"],
                        });
                }

                return Results.Ok(new ChaosResult(body.Action, description));
            })
            .RequireRateLimiting("orders")
            .WithSummary("Injects a session fault on a guest session");

        api.MapPost("/{compId}/logout", async (string compId, HttpContext http, SessionManager sessions) =>
            {
                var (managed, error) = await AuthorizeAsync(compId, http, sessions);
                if (error is not null)
                {
                    return error;
                }

                managed!.Session.Logout("Logout requested by operator");
                return Results.Accepted();
            })
            .WithSummary("Sends Logout(5) from the venue side");

        api.MapPost("/{compId}/reset", async (string compId, HttpContext http, SessionManager sessions) =>
            {
                var (managed, error) = await AuthorizeAsync(compId, http, sessions);
                if (error is not null)
                {
                    return error;
                }

                managed!.Session.ResetSequenceNumbers();
                if (sessions.Guest(managed.Account.ClientId) is { } trader)
                {
                    trader.Session.ResetNextLogon = true;
                }

                return Results.Accepted();
            })
            .WithSummary("Logs out and resets both sequence numbers to 1");
    }

    private static async Task<(ManagedSession? Session, IResult? Error)> AuthorizeAsync(string compId, HttpContext http, SessionManager sessions)
    {
        var caller = await Auth.CallerAsync(http);
        if (caller.ClientId is null && !caller.IsAdmin)
        {
            return (null, Auth.Unauthorized());
        }

        if (caller.ClientId is not null)
        {
            await sessions.EnsureGuestTraderAsync(caller.ClientId, http.RequestAborted);
        }

        var managed = sessions.Sessions.FirstOrDefault(s => string.Equals(s.Config.ClientCompId, compId, StringComparison.OrdinalIgnoreCase));
        if (managed is null)
        {
            return (null, Results.NotFound());
        }

        return caller.CanManage(managed.Account.ClientId) ? (managed, null) : (null, Auth.Forbidden());
    }
}
