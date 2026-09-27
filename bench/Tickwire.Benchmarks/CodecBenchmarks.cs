using System.Buffers;
using BenchmarkDotNet.Attributes;
using Tickwire.Fix;
using Tickwire.Fix.Dictionary;

namespace Tickwire.Benchmarks;

[MemoryDiagnoser]
public class CodecBenchmarks
{
    private static readonly DateTime Now = new(2025, 3, 14, 13, 30, 0, DateTimeKind.Utc);
    private readonly ArrayBufferWriter<byte> _writer = new(1024);
    private byte[] _nos = [];
    private byte[] _execReport = [];
    private FixMessage _parsedNos = null!;

    [GlobalSetup]
    public void Setup()
    {
        using (var b = NewOrder())
        {
            _nos = b.ToBytes(Header());
        }

        using (var er = new FixMessageBuilder(MsgTypes.ExecutionReport))
        {
            er.Set(Tags.OrderID, "T-000123").Set(Tags.ClOrdID, "ORD-1").Set(Tags.ExecID, "E-000456")
                .Set(Tags.ExecType, 'F').Set(Tags.OrdStatus, '1').Set(Tags.Symbol, "SPY").Set(Tags.SecurityType, "OPT")
                .Set(Tags.PutOrCall, 1).Set(Tags.StrikePrice, 550m).Set(Tags.Side, '1').Set(Tags.OrderQty, 10m)
                .Set(Tags.LastQty, 4m).Set(Tags.LastPx, 4.25m).Set(Tags.LeavesQty, 6m).Set(Tags.CumQty, 4m)
                .Set(Tags.AvgPx, 4.25m).SetUtcTimestamp(Tags.TransactTime, Now);
            _execReport = er.ToBytes(Header());
        }

        _parsedNos = FixMessage.Parse(_nos);
        _ = FixDictionary.Fix44;
    }

    private static FixHeader Header() => new("FIX.4.4", "CLIENT", "TICKWIRE", 1234, Now);

    private static FixMessageBuilder NewOrder()
    {
        var b = new FixMessageBuilder(MsgTypes.NewOrderSingle);
        b.Set(Tags.ClOrdID, "ORD-1").Set(Tags.HandlInst, '1').Set(Tags.Symbol, "SPY").Set(Tags.SecurityType, "OPT")
            .SetLocalMktDate(Tags.MaturityDate, new DateOnly(2025, 6, 20)).Set(Tags.PutOrCall, 1)
            .Set(Tags.StrikePrice, 550m).Set(Tags.Side, '1').SetUtcTimestamp(Tags.TransactTime, Now)
            .Set(Tags.OrderQty, 10m).Set(Tags.OrdType, '2').Set(Tags.Price, 4.25m).Set(Tags.TimeInForce, '0');
        return b;
    }

    [Benchmark]
    public int ParseNewOrderSingle() => FixMessage.Parse(_nos).MsgSeqNum;

    [Benchmark]
    public int ParseExecutionReport() => FixMessage.Parse(_execReport).Fields.Length;

    [Benchmark]
    public int SerializeNewOrderSingle()
    {
        _writer.ResetWrittenCount();
        using var b = NewOrder();
        return b.WriteTo(_writer, Header());
    }

    [Benchmark]
    public FrameStatus FrameNewOrderSingle() => FixFramer.Scan(_nos).Status;

    [Benchmark]
    public bool ValidateNewOrderSingle() => FixDictionary.Fix44.Validate(_parsedNos) is null;

    [Benchmark]
    public int Checksum() => FixChecksum.Compute(_execReport);
}
