using System.Text;

namespace Tickwire.Fix.Session.Transport;

/// <summary>
/// Wraps a transport and breaks outbound traffic on request. Powers the Chaos panel; only sessions that opt in get it.
/// </summary>
public sealed class FaultInjectingTransport : IFixTransport
{
    private readonly IFixTransport _inner;
    private readonly ChaosFaults _faults;

    public FaultInjectingTransport(IFixTransport inner, ChaosFaults faults)
    {
        _inner = inner;
        _faults = faults;
    }

    public string Description => _inner.Description + "+chaos";

    public async ValueTask<SendResult> SendAsync(byte[] message, CancellationToken cancellationToken)
    {
        if (_faults.TryTakeDrop())
        {
            return new SendResult(false, message, "dropped by chaos fault");
        }

        if (_faults.TryTakeCorruptChecksum())
        {
            var corrupted = CorruptChecksum(message);
            var result = await _inner.SendAsync(corrupted, cancellationToken).ConfigureAwait(false);
            return result with { ActualBytes = corrupted, FaultNote = "checksum corrupted by chaos fault" };
        }

        if (_faults.TryTakeCorruptBodyLength())
        {
            var corrupted = CorruptBodyLength(message);
            var result = await _inner.SendAsync(corrupted, cancellationToken).ConfigureAwait(false);
            return result with { ActualBytes = corrupted, FaultNote = "body length corrupted by chaos fault" };
        }

        return await _inner.SendAsync(message, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<byte[]?> ReceiveAsync(CancellationToken cancellationToken) => _inner.ReceiveAsync(cancellationToken);

    public int TakeDiscardedByteCount() => _inner.TakeDiscardedByteCount();

    public ValueTask DisposeAsync() => _inner.DisposeAsync();

    internal static byte[] CorruptChecksum(byte[] message)
    {
        var copy = (byte[])message.Clone();
        // "10=NNN<SOH>" is the last 7 bytes. Change the last digit.
        var i = copy.Length - 2;
        copy[i] = copy[i] == (byte)'9' ? (byte)'0' : (byte)(copy[i] + 1);
        return copy;
    }

    internal static byte[] CorruptBodyLength(byte[] message)
    {
        var text = Encoding.ASCII.GetString(message);
        var start = text.IndexOf("\u00019=", StringComparison.Ordinal) + 3;
        var end = text.IndexOf('\u0001', start);
        var length = int.Parse(text.AsSpan(start, end - start), System.Globalization.CultureInfo.InvariantCulture);
        return Encoding.ASCII.GetBytes(string.Concat(text.AsSpan(0, start),
            (length + 7).ToString(System.Globalization.CultureInfo.InvariantCulture), text.AsSpan(end)));
    }
}

/// <summary>Pending faults for one direction of one session. Thread-safe; set from the API, consumed by the session.</summary>
public sealed class ChaosFaults
{
    private int _dropCount;
    private int _corruptChecksum;
    private int _corruptBodyLength;

    public int PendingDrops => Volatile.Read(ref _dropCount);

    public void DropNext(int count) => Interlocked.Add(ref _dropCount, Math.Max(0, count));

    public void CorruptNextChecksum() => Interlocked.Increment(ref _corruptChecksum);

    public void CorruptNextBodyLength() => Interlocked.Increment(ref _corruptBodyLength);

    public void Clear()
    {
        Interlocked.Exchange(ref _dropCount, 0);
        Interlocked.Exchange(ref _corruptChecksum, 0);
        Interlocked.Exchange(ref _corruptBodyLength, 0);
    }

    internal bool TryTakeDrop() => TryDecrement(ref _dropCount);

    internal bool TryTakeCorruptChecksum() => TryDecrement(ref _corruptChecksum);

    internal bool TryTakeCorruptBodyLength() => TryDecrement(ref _corruptBodyLength);

    private static bool TryDecrement(ref int counter)
    {
        while (true)
        {
            var current = Volatile.Read(ref counter);
            if (current <= 0)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref counter, current - 1, current) == current)
            {
                return true;
            }
        }
    }
}
