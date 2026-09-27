using System.Collections.Concurrent;
using System.Diagnostics;

namespace Tickwire.Engine;

/// <summary>Fixed-size ring of latency samples (microseconds) with percentile queries. Thread-safe.</summary>
public sealed class LatencyRecorder
{
    private readonly double[] _samples;
    private readonly Lock _lock = new();
    private int _next;
    private int _count;

    public LatencyRecorder(int capacity = 8192) => _samples = new double[capacity];

    public long TotalSamples { get; private set; }

    public void RecordSince(long startTimestamp) =>
        Record(Stopwatch.GetElapsedTime(startTimestamp).TotalMicroseconds);

    public void Record(double micros)
    {
        lock (_lock)
        {
            _samples[_next] = micros;
            _next = (_next + 1) % _samples.Length;
            _count = Math.Min(_count + 1, _samples.Length);
            TotalSamples++;
        }
    }

    public LatencySummary Summary()
    {
        double[] copy;
        lock (_lock)
        {
            copy = _samples[.._count];
        }

        if (copy.Length == 0)
        {
            return new LatencySummary(0, 0, 0, 0, 0);
        }

        Array.Sort(copy);
        return new LatencySummary(copy.Length, Percentile(copy, 0.50), Percentile(copy, 0.90), Percentile(copy, 0.99), copy[^1]);
    }

    private static double Percentile(double[] sorted, double p)
    {
        var rank = (int)Math.Ceiling(p * sorted.Length) - 1;
        return sorted[Math.Clamp(rank, 0, sorted.Length - 1)];
    }
}

public readonly record struct LatencySummary(int Samples, double P50, double P90, double P99, double Max);

/// <summary>Counts messages per second over the last minute, using one bucket per second.</summary>
public sealed class RateCounter
{
    private readonly long[] _buckets = new long[60];
    private readonly long[] _bucketSecond = new long[60];
    private readonly TimeProvider _time;

    public RateCounter(TimeProvider time) => _time = time;

    public long Total => Interlocked.Read(ref _total);

    private long _total;

    public void Increment()
    {
        var second = _time.GetUtcNow().ToUnixTimeSeconds();
        var i = (int)(second % 60);
        if (Interlocked.Exchange(ref _bucketSecond[i], second) != second)
        {
            Interlocked.Exchange(ref _buckets[i], 0);
        }

        Interlocked.Increment(ref _buckets[i]);
        Interlocked.Increment(ref _total);
    }

    /// <summary>Average rate over the last <paramref name="seconds"/> complete seconds.</summary>
    public double PerSecond(int seconds = 5)
    {
        var now = _time.GetUtcNow().ToUnixTimeSeconds();
        long sum = 0;
        for (var s = 1; s <= seconds; s++)
        {
            var second = now - s;
            var i = (int)(second % 60);
            if (Volatile.Read(ref _bucketSecond[i]) == second)
            {
                sum += Volatile.Read(ref _buckets[i]);
            }
        }

        return sum / (double)seconds;
    }

    /// <summary>Per-second counts for the last <paramref name="seconds"/> seconds, oldest first.</summary>
    public IReadOnlyList<long> Series(int seconds = 60)
    {
        var now = _time.GetUtcNow().ToUnixTimeSeconds();
        var result = new long[seconds];
        for (var s = 0; s < seconds; s++)
        {
            var second = now - seconds + s;
            var i = (int)(second % 60);
            result[s] = Volatile.Read(ref _bucketSecond[i]) == second ? Volatile.Read(ref _buckets[i]) : 0;
        }

        return result;
    }
}

/// <summary>Engine-wide counters for the ops dashboard.</summary>
public sealed class EngineMetrics : IOmsListener
{
    public EngineMetrics(TimeProvider? time = null)
    {
        var t = time ?? TimeProvider.System;
        MessagesIn = new RateCounter(t);
        MessagesOut = new RateCounter(t);
    }

    public RateCounter MessagesIn { get; }
    public RateCounter MessagesOut { get; }

    /// <summary>Time from a NewOrderSingle arriving at the gateway to its New ExecutionReport being handed to the session.</summary>
    public LatencyRecorder OrderToAck { get; } = new();

    public ConcurrentDictionary<RiskRejectCode, long> RejectsByReason { get; } = new();

    public long OrdersAccepted => Interlocked.Read(ref _ordersAccepted);
    public long Fills => Interlocked.Read(ref _fills);
    public long ContractsTraded => Interlocked.Read(ref _contracts);
    public long CancelRejects => Interlocked.Read(ref _cancelRejects);

    private long _ordersAccepted;
    private long _fills;
    private long _contracts;
    private long _cancelRejects;

    public void OnExecutionReport(ExecutionReportEvent report)
    {
        switch (report.ExecType)
        {
            case ExecType.New:
                Interlocked.Increment(ref _ordersAccepted);
                break;
            case ExecType.Trade:
                Interlocked.Increment(ref _fills);
                Interlocked.Add(ref _contracts, (long)report.LastQty);
                break;
            default:
                break;
        }
    }

    public void OnCancelReject(CancelRejectEvent reject) => Interlocked.Increment(ref _cancelRejects);

    public void OnRiskReject(string clientId, RiskRejectCode code, string text) =>
        RejectsByReason.AddOrUpdate(code, 1, (_, n) => n + 1);
}
