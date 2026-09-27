using Microsoft.AspNetCore.SignalR;
using Tickwire.Api.Services;

namespace Tickwire.Api.Hubs;

/// <summary>
/// One hub, many topics. A browser subscribes to the groups it needs: market chains and books, its own orders,
/// its own FIX sessions and the ops dashboard. One WebSocket instead of four (see docs/DECISIONS.md).
/// </summary>
public sealed class LiveHub(SessionManager sessions, MarketView market, BookSubscriptions books) : Hub
{
    public async Task<ChainDto?> SubscribeChain(string underlying, string expiry)
    {
        var exp = DateOnly.TryParse(expiry, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : (DateOnly?)null;
        var chain = market.Chain(underlying, exp);
        if (chain is null)
        {
            return null;
        }

        await SwitchGroupAsync("chain", $"chain:{chain.Underlying}:{chain.Expiry:yyyy-MM-dd}");
        return chain;
    }

    public async Task<BookDto?> SubscribeBook(int contractId)
    {
        var book = market.Book(contractId);
        if (book is not null)
        {
            books.Touch(contractId);
            await SwitchGroupAsync("book", $"book:{contractId}");
        }

        return book;
    }

    public Task SubscribeOps() => Groups.AddToGroupAsync(Context.ConnectionId, "ops");

    public Task SubscribeMarket() => Groups.AddToGroupAsync(Context.ConnectionId, "market");

    /// <summary>Orders and FIX traffic are private: the caller proves ownership with the guest token.</summary>
    public async Task<bool> SubscribeClient(string token)
    {
        var clientId = await sessions.ClientIdForTokenAsync(token, Context.ConnectionAborted);
        if (clientId is null)
        {
            return false;
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, $"orders:{clientId}");
        var account = await sessions.LoadAccountAsync(clientId, Context.ConnectionAborted);
        foreach (var s in sessions.Sessions.Where(s => s.Account.ClientId == clientId))
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, WireTap.Group(s.Key));
        }

        return account is not null;
    }

    private async Task SwitchGroupAsync(string kind, string group)
    {
        var key = $"group:{kind}";
        if (Context.Items.TryGetValue(key, out var previous) && previous is string prev && prev != group)
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, prev);
        }

        Context.Items[key] = group;
        await Groups.AddToGroupAsync(Context.ConnectionId, group);
    }
}
