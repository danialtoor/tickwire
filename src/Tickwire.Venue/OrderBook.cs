namespace Tickwire.Venue;

/// <summary>An execution between an incoming order and a resting one, at the resting order's price.</summary>
public readonly record struct Fill(long AggressorId, long RestingId, decimal Price, decimal Quantity);

public readonly record struct SubmitResult(IReadOnlyList<Fill> Fills, decimal RestingQuantity, decimal CanceledQuantity, string? CancelReason)
{
    public decimal FilledQuantity => Fills.Sum(f => f.Quantity);
}

public readonly record struct BookLevel(decimal Price, decimal Quantity, int Orders);

public sealed class RestingOrder
{
    internal RestingOrder(long id, Side side, decimal price, decimal remaining, long priority)
    {
        Id = id;
        Side = side;
        Price = price;
        Remaining = remaining;
        Priority = priority;
    }

    public long Id { get; }
    public Side Side { get; }
    public decimal Price { get; internal set; }
    public decimal Remaining { get; internal set; }

    /// <summary>Arrival sequence; lower trades first at the same price.</summary>
    public long Priority { get; internal set; }
}

/// <summary>
/// Price-time priority limit order book for one contract. Not thread-safe: each book is owned by one venue shard loop.
/// Incoming orders trade at the resting order's price. Market orders never rest. IOC cancels any remainder;
/// FOK trades in full or not at all.
/// </summary>
public sealed class OrderBook
{
    private readonly SortedDictionary<decimal, LinkedList<RestingOrder>> _bids = new(Comparer<decimal>.Create((a, b) => b.CompareTo(a)));
    private readonly SortedDictionary<decimal, LinkedList<RestingOrder>> _asks = new();
    private readonly Dictionary<long, LinkedListNode<RestingOrder>> _index = [];
    private long _nextPriority;

    public OrderBook(OptionContract contract) => Contract = contract;

    public OptionContract Contract { get; }
    public int OrderCount => _index.Count;
    public decimal? LastPrice { get; private set; }
    public decimal LastQuantity { get; private set; }
    public decimal Volume { get; private set; }

    public BookLevel? BestBid => Top(_bids);
    public BookLevel? BestAsk => Top(_asks);

    public bool Contains(long id) => _index.ContainsKey(id);

    public RestingOrder? Get(long id) => _index.TryGetValue(id, out var node) ? node.Value : null;

    public IReadOnlyList<BookLevel> Depth(Side side, int levels)
    {
        var book = side == Side.Buy ? _bids : _asks;
        var result = new List<BookLevel>(levels);
        foreach (var (price, queue) in book)
        {
            if (result.Count == levels)
            {
                break;
            }

            result.Add(new BookLevel(price, queue.Sum(o => o.Remaining), queue.Count));
        }

        return result;
    }

    public SubmitResult Submit(long id, Side side, OrderType type, TimeInForce tif, decimal? limitPrice, decimal quantity)
    {
        if (quantity <= 0)
        {
            return new SubmitResult([], 0, quantity, "Quantity must be positive");
        }

        if (type == OrderType.Limit && (limitPrice is null or <= 0))
        {
            return new SubmitResult([], 0, quantity, "Limit order needs a positive price");
        }

        if (_index.ContainsKey(id))
        {
            throw new InvalidOperationException($"Order {id} is already in the book");
        }

        var price = type == OrderType.Market ? (decimal?)null : limitPrice;

        if (tif == TimeInForce.FillOrKill && AvailableAgainst(side, price) < quantity)
        {
            return new SubmitResult([], 0, quantity, "Fill-or-kill could not be filled in full");
        }

        var fills = new List<Fill>();
        var remaining = Match(id, side, price, quantity, fills);

        if (remaining == 0)
        {
            return new SubmitResult(fills, 0, 0, null);
        }

        if (type == OrderType.Market)
        {
            return new SubmitResult(fills, 0, remaining, fills.Count == 0 ? "No liquidity for market order" : "Market order remainder canceled");
        }

        if (tif == TimeInForce.ImmediateOrCancel)
        {
            return new SubmitResult(fills, 0, remaining, "Immediate-or-cancel remainder canceled");
        }

        Rest(id, side, price!.Value, remaining);
        return new SubmitResult(fills, remaining, 0, null);
    }

    /// <summary>Removes a resting order. Returns the quantity that was still open, or null if not found.</summary>
    public decimal? Cancel(long id)
    {
        if (!_index.Remove(id, out var node))
        {
            return null;
        }

        var book = node.Value.Side == Side.Buy ? _bids : _asks;
        var queue = node.List!;
        queue.Remove(node);
        if (queue.Count == 0)
        {
            book.Remove(node.Value.Price);
        }

        return node.Value.Remaining;
    }

    /// <summary>
    /// Changes price and/or open quantity. Reducing quantity at the same price keeps time priority; any other change
    /// loses it (the order is re-entered and may trade immediately if it now crosses).
    /// </summary>
    public SubmitResult? Replace(long id, decimal newPrice, decimal newRemaining)
    {
        if (!_index.TryGetValue(id, out var node))
        {
            return null;
        }

        var order = node.Value;
        if (newRemaining <= 0)
        {
            Cancel(id);
            return new SubmitResult([], 0, order.Remaining, "Replaced to zero quantity");
        }

        if (newPrice == order.Price && newRemaining <= order.Remaining)
        {
            order.Remaining = newRemaining;
            return new SubmitResult([], newRemaining, 0, null);
        }

        Cancel(id);
        var fills = new List<Fill>();
        var remaining = Match(id, order.Side, newPrice, newRemaining, fills);
        if (remaining > 0)
        {
            Rest(id, order.Side, newPrice, remaining);
        }

        return new SubmitResult(fills, remaining, 0, null);
    }

    private decimal Match(long aggressorId, Side side, decimal? limit, decimal quantity, List<Fill> fills)
    {
        var opposite = side == Side.Buy ? _asks : _bids;
        var remaining = quantity;
        while (remaining > 0 && opposite.Count > 0)
        {
            var (levelPrice, queue) = First(opposite);
            if (limit is { } px && (side == Side.Buy ? levelPrice > px : levelPrice < px))
            {
                break;
            }

            while (remaining > 0 && queue.First is { } head)
            {
                var resting = head.Value;
                var qty = Math.Min(remaining, resting.Remaining);
                fills.Add(new Fill(aggressorId, resting.Id, levelPrice, qty));
                LastPrice = levelPrice;
                LastQuantity = qty;
                Volume += qty;
                remaining -= qty;
                resting.Remaining -= qty;
                if (resting.Remaining == 0)
                {
                    queue.RemoveFirst();
                    _index.Remove(resting.Id);
                }
            }

            if (queue.Count == 0)
            {
                opposite.Remove(levelPrice);
            }
        }

        return remaining;
    }

    private decimal AvailableAgainst(Side side, decimal? limit)
    {
        var opposite = side == Side.Buy ? _asks : _bids;
        decimal total = 0;
        foreach (var (price, queue) in opposite)
        {
            if (limit is { } px && (side == Side.Buy ? price > px : price < px))
            {
                break;
            }

            foreach (var o in queue)
            {
                total += o.Remaining;
            }
        }

        return total;
    }

    private void Rest(long id, Side side, decimal price, decimal quantity)
    {
        var book = side == Side.Buy ? _bids : _asks;
        if (!book.TryGetValue(price, out var queue))
        {
            queue = new LinkedList<RestingOrder>();
            book[price] = queue;
        }

        _index[id] = queue.AddLast(new RestingOrder(id, side, price, quantity, ++_nextPriority));
    }

    private static (decimal Price, LinkedList<RestingOrder> Queue) First(SortedDictionary<decimal, LinkedList<RestingOrder>> book)
    {
        using var e = book.GetEnumerator();
        e.MoveNext();
        return (e.Current.Key, e.Current.Value);
    }

    private static BookLevel? Top(SortedDictionary<decimal, LinkedList<RestingOrder>> book)
    {
        if (book.Count == 0)
        {
            return null;
        }

        var (price, queue) = First(book);
        return new BookLevel(price, queue.Sum(o => o.Remaining), queue.Count);
    }
}
