namespace Tickwire.Fix.Tests;

internal static class TestMessages
{
    public static readonly DateTime SendingTime = new(2025, 3, 14, 13, 30, 0, 123, DateTimeKind.Utc);

    public static FixHeader Header(int seq = 1, string sender = "CLIENT", string target = "TICKWIRE") =>
        new("FIX.4.4", sender, target, seq, SendingTime);

    public static byte[] NewOrderSingle(int seq = 1, string clOrdId = "ORD-1")
    {
        using var b = new FixMessageBuilder(MsgTypes.NewOrderSingle);
        b.Set(Tags.ClOrdID, clOrdId)
            .Set(Tags.HandlInst, '1')
            .Set(Tags.Symbol, "SPY")
            .Set(Tags.SecurityType, "OPT")
            .SetLocalMktDate(Tags.MaturityDate, new DateOnly(2025, 6, 20))
            .Set(Tags.PutOrCall, 1)
            .Set(Tags.StrikePrice, 550m)
            .Set(Tags.Side, '1')
            .SetUtcTimestamp(Tags.TransactTime, SendingTime)
            .Set(Tags.OrderQty, 10m)
            .Set(Tags.OrdType, '2')
            .Set(Tags.Price, 4.25m)
            .Set(Tags.TimeInForce, '0');
        return b.ToBytes(Header(seq));
    }
}
