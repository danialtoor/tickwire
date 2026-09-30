using Tickwire.Api.Services;
using Tickwire.Engine;
using Tickwire.Persistence;
using Tickwire.Venue;

namespace Tickwire.Api.Endpoints;

public sealed record NewOrderBody(int ContractId, string Side, string Type, string? Tif, decimal? Price, decimal Quantity,
    string? Destination = null);

public sealed record ReplaceBody(decimal? Price, decimal Quantity);

public sealed record SpreadLegBody(int ContractId, int Ratio, string Side);

public sealed record SpreadOrderBody(IReadOnlyList<SpreadLegBody> Legs, string Side, string? Tif, decimal Price, decimal Quantity);

public sealed record OrderAccepted(string ClOrdID);

public sealed record MeDto(string ClientId, string DisplayName, bool IsGuest, DateTime? ExpiresAt, bool KillSwitch, RiskLimits Limits,
    IReadOnlyList<SessionConfig> Sessions);

public sealed record OrderRowDto(BlotterRowDto Order, ClientOrderState? ClientView, bool InSync);

public static class TradingEndpoints
{
    public static void MapTradingEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api").WithTags("Trading");

        api.MapGet("/instruments", (MarketView market) => market.Underlyings())
            .WithSummary("Underlyings with spot prices and listed expiries");

        api.MapGet("/chain/{underlying}", (string underlying, DateOnly? expiry, MarketView market) =>
                market.Chain(underlying.ToUpperInvariant(), expiry) is { } chain ? Results.Ok(chain) : Results.NotFound())
            .WithSummary("Option chain with theo, greeks and top of book");

        api.MapGet("/book/{contractId:int}", (int contractId, MarketView market) =>
                market.Book(contractId) is { } book ? Results.Ok(book) : Results.NotFound())
            .WithSummary("Five levels of depth for one contract");

        api.MapGet("/exchanges", () => Exchanges.All.Select(e => new
            {
                e.Code, e.Name, e.TakerFee, e.MakerFee, Makers = e.Makers.Select(m => m.Name), Primary = e == Exchanges.Primary,
            }))
            .WithSummary("The simulated exchanges and their fee schedules (per contract; negative = rebate)");

        api.MapGet("/tape", (string? underlying, MarketView market) => market.Tape(underlying?.ToUpperInvariant()))
            .WithSummary("Recent trades");

        api.MapPost("/guest", async (SessionManager sessions, CancellationToken ct) =>
            {
                var guest = await sessions.CreateGuestAsync(ct);
                return Results.Created($"/api/me", guest);
            })
            .RequireRateLimiting("guest")
            .WithSummary("Creates a guest client with conservative limits, a FIX session and a 24h TTL");

        api.MapGet("/me", async (HttpContext http, SessionManager sessions, IClientRepository repo, CancellationToken ct) =>
            {
                var caller = await Auth.CallerAsync(http);
                if (caller.ClientId is null)
                {
                    return Auth.Unauthorized();
                }

                await sessions.EnsureGuestTraderAsync(caller.ClientId, ct);
                var client = await repo.GetAsync(caller.ClientId, ct);
                var account = await sessions.LoadAccountAsync(caller.ClientId, ct);
                return client is null
                    ? Results.NotFound()
                    : Results.Ok(new MeDto(client.ClientId, client.DisplayName, client.IsGuest, client.ExpiresAt,
                        account?.KillSwitch ?? client.KillSwitch, account?.Limits ?? client.Limits, client.Sessions));
            })
            .WithSummary("The calling guest's account, limits and sessions");

        api.MapPost("/orders", async (NewOrderBody body, HttpContext http, SessionManager sessions, InstrumentRegistry instruments,
                CancellationToken ct) =>
            {
                var caller = await Auth.CallerAsync(http);
                if (caller.ClientId is null)
                {
                    return Auth.Unauthorized();
                }

                var trader = await sessions.EnsureGuestTraderAsync(caller.ClientId, ct);
                if (trader is null)
                {
                    return Results.Problem("No in-browser session for this client", statusCode: 409);
                }

                var contract = instruments.Get(body.ContractId);
                if (contract is null)
                {
                    return Results.ValidationProblem(new Dictionary<string, string[]> { ["contractId"] = ["Unknown contract"] });
                }

                if (!TryParseOrder(body, out var side, out var type, out var tif, out var error))
                {
                    return Results.ValidationProblem(new Dictionary<string, string[]> { ["order"] = [error] });
                }

                if (!trader.Session.IsLoggedOn)
                {
                    return Results.Problem("The FIX session is reconnecting; try again in a few seconds", statusCode: 503);
                }

                var clOrdId = trader.SendNewOrder(contract, side, type, tif, body.Price, body.Quantity, body.Destination);
                return Results.Accepted(value: new OrderAccepted(clOrdId));
            })
            .RequireRateLimiting("orders")
            .WithSummary("Sends a NewOrderSingle(D) over the caller's in-browser FIX session");

        api.MapPost("/orders/spread", async (SpreadOrderBody body, HttpContext http, SessionManager sessions, InstrumentRegistry instruments,
                CancellationToken ct) =>
            {
                var caller = await Auth.CallerAsync(http);
                if (caller.ClientId is null)
                {
                    return Auth.Unauthorized();
                }

                var trader = await sessions.EnsureGuestTraderAsync(caller.ClientId, ct);
                if (trader is null)
                {
                    return Results.Problem("No in-browser session for this client", statusCode: 409);
                }

                var legs = new List<OrderLeg>();
                foreach (var l in body.Legs ?? [])
                {
                    if (instruments.Get(l.ContractId) is not { } contract)
                    {
                        return Results.ValidationProblem(new Dictionary<string, string[]> { ["legs"] = [$"Unknown contract {l.ContractId}"] });
                    }

                    legs.Add(new OrderLeg(contract, l.Ratio, l.Side.Equals("sell", StringComparison.OrdinalIgnoreCase) ? Side.Sell : Side.Buy));
                }

                if (legs.Count is < 2 or > 4)
                {
                    return Results.ValidationProblem(new Dictionary<string, string[]> { ["legs"] = ["2 to 4 legs"] });
                }

                if (!trader.Session.IsLoggedOn)
                {
                    return Results.Problem("The FIX session is reconnecting; try again in a few seconds", statusCode: 503);
                }

                var side = body.Side.Equals("sell", StringComparison.OrdinalIgnoreCase) ? Side.Sell : Side.Buy;
                var tif = (body.Tif ?? "day").Equals("ioc", StringComparison.OrdinalIgnoreCase) ? TimeInForce.ImmediateOrCancel : TimeInForce.Day;
                return Results.Accepted(value: new OrderAccepted(trader.SendMultileg(legs, side, tif, body.Price, body.Quantity)));
            })
            .RequireRateLimiting("orders")
            .WithSummary("Sends a NewOrderMultileg(AB): a spread with a net limit price, over the caller's FIX session");

        api.MapGet("/positions", async (HttpContext http, PositionKeeper positions) =>
            {
                var caller = await Auth.CallerAsync(http);
                return caller.ClientId is null ? Auth.Unauthorized() : Results.Ok(positions.Snapshot(caller.ClientId));
            })
            .WithSummary("Positions, P&L and portfolio greeks, marked to the current market");

        api.MapGet("/orders", async (HttpContext http, SessionManager sessions, OrderManager oms, CancellationToken ct) =>
            {
                var caller = await Auth.CallerAsync(http);
                if (caller.ClientId is null)
                {
                    return Auth.Unauthorized();
                }

                var trader = sessions.Guest(caller.ClientId);
                var orders = await oms.OrdersAsync(caller.ClientId, max: 200);
                return Results.Ok(orders.Select(o =>
                {
                    var clientView = trader?.ClientView.GetValueOrDefault(o.OrderId);
                    // In sync only once the client has seen reports that bring it to the OMS's state.
                    var inSync = clientView is not null && clientView.OrdStatus == (char)o.Status && clientView.CumQty == o.CumQty;
                    return new OrderRowDto(BlotterRowDto.From(o), clientView, inSync);
                }));
            })
            .WithSummary("The caller's orders as the OMS sees them, next to what the FIX client reconstructed from ExecutionReports");

        api.MapPost("/orders/{orderId}/cancel", async (string orderId, HttpContext http, SessionManager sessions, OrderManager oms,
                CancellationToken ct) =>
            {
                var (trader, order, error) = await ResolveOrderAsync(orderId, http, sessions, oms, ct);
                if (error is not null)
                {
                    return error;
                }

                return Results.Accepted(value: new OrderAccepted(trader!.SendCancel(order!)));
            })
            .RequireRateLimiting("orders")
            .WithSummary("Sends an OrderCancelRequest(F)");

        api.MapPost("/orders/{orderId}/replace", async (string orderId, ReplaceBody body, HttpContext http, SessionManager sessions,
                OrderManager oms, CancellationToken ct) =>
            {
                var (trader, order, error) = await ResolveOrderAsync(orderId, http, sessions, oms, ct);
                if (error is not null)
                {
                    return error;
                }

                return Results.Accepted(value: new OrderAccepted(trader!.SendReplace(order!, body.Price, body.Quantity)));
            })
            .RequireRateLimiting("orders")
            .WithSummary("Sends an OrderCancelReplaceRequest(G)");
    }

    private static async Task<(GuestTrader? Trader, OrderView? Order, IResult? Error)> ResolveOrderAsync(string orderId, HttpContext http,
        SessionManager sessions, OrderManager oms, CancellationToken ct)
    {
        var caller = await Auth.CallerAsync(http);
        if (caller.ClientId is null)
        {
            return (null, null, Auth.Unauthorized());
        }

        var trader = await sessions.EnsureGuestTraderAsync(caller.ClientId, ct);
        var orders = await oms.OrdersAsync(caller.ClientId, openOnly: true);
        var order = orders.FirstOrDefault(o => o.OrderId == orderId);
        if (trader is null || order is null)
        {
            return (null, null, Results.NotFound());
        }

        return (trader, order, null);
    }

    private static bool TryParseOrder(NewOrderBody body, out Side side, out OrderType type, out TimeInForce tif, out string error)
    {
        side = default;
        type = default;
        tif = TimeInForce.Day;
        error = string.Empty;
        switch (body.Side.ToUpperInvariant())
        {
            case "BUY":
                side = Side.Buy;
                break;
            case "SELL":
                side = Side.Sell;
                break;
            default:
                error = "side must be buy or sell";
                return false;
        }

        switch (body.Type.ToUpperInvariant())
        {
            case "LIMIT":
                type = OrderType.Limit;
                break;
            case "MARKET":
                type = OrderType.Market;
                break;
            default:
                error = "type must be limit or market";
                return false;
        }

        switch ((body.Tif ?? "day").ToUpperInvariant())
        {
            case "DAY":
                tif = TimeInForce.Day;
                break;
            case "IOC":
                tif = TimeInForce.ImmediateOrCancel;
                break;
            case "FOK":
                tif = TimeInForce.FillOrKill;
                break;
            default:
                error = "tif must be day, ioc or fok";
                return false;
        }

        if (body.Destination is { Length: > 0 } d && !d.Equals("SMART", StringComparison.OrdinalIgnoreCase) && Exchanges.Find(d) is null)
        {
            error = $"destination must be SMART or one of {string.Join(", ", Exchanges.All.Select(e => e.Code))}";
            return false;
        }

        // Quantity and price are deliberately not range-checked here: pre-trade risk in the OMS owns that, and its
        // rejects are part of what the demo shows.
        if (body.Quantity <= 0 || body.Quantity > 1_000_000)
        {
            error = "quantity must be between 1 and 1,000,000";
            return false;
        }

        return true;
    }
}
