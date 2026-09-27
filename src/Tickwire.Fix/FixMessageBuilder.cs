using System.Buffers;
using System.Buffers.Text;
using System.Text;

namespace Tickwire.Fix;

/// <summary>Standard header values the session layer stamps on every outbound message.</summary>
public readonly record struct FixHeader(
    string BeginString,
    string SenderCompID,
    string TargetCompID,
    int MsgSeqNum,
    DateTime SendingTime,
    bool PossDupFlag = false,
    DateTime? OrigSendingTime = null);

/// <summary>
/// Builds a message body into a pooled buffer. The session layer later wraps the body with the header
/// (8, 9, 35, 49, 56, 34, 52, ...) and trailer (10), computing BodyLength and CheckSum in one pass.
/// Separating body from header lets a stored message be re-sent with a new header (PossDupFlag, OrigSendingTime).
/// </summary>
public sealed class FixMessageBuilder : IDisposable
{
    private const byte Soh = FixParser.Soh;
    private byte[] _body;
    private int _length;

    public FixMessageBuilder(string msgType, int capacity = 256)
    {
        ArgumentException.ThrowIfNullOrEmpty(msgType);
        MsgType = msgType;
        _body = ArrayPool<byte>.Shared.Rent(capacity);
    }

    public string MsgType { get; }

    /// <summary>The encoded body fields (everything after the standard header), SOH terminated.</summary>
    public ReadOnlySpan<byte> Body => _body.AsSpan(0, _length);

    public FixMessageBuilder Set(int tag, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return this;
        }

        var span = BeginField(tag, Encoding.ASCII.GetMaxByteCount(value.Length));
        var written = Encoding.ASCII.GetBytes(value, span);
        return EndField(written);
    }

    public FixMessageBuilder Set(int tag, ReadOnlySpan<byte> value)
    {
        var span = BeginField(tag, value.Length);
        value.CopyTo(span);
        return EndField(value.Length);
    }

    public FixMessageBuilder Set(int tag, int value)
    {
        var span = BeginField(tag, 11);
        Utf8Formatter.TryFormat(value, span, out var written);
        return EndField(written);
    }

    public FixMessageBuilder Set(int tag, long value)
    {
        var span = BeginField(tag, 20);
        Utf8Formatter.TryFormat(value, span, out var written);
        return EndField(written);
    }

    public FixMessageBuilder Set(int tag, decimal value)
    {
        var span = BeginField(tag, 32);
        Utf8Formatter.TryFormat(value, span, out var written);
        return EndField(written);
    }

    public FixMessageBuilder Set(int tag, char value)
    {
        var span = BeginField(tag, 1);
        span[0] = (byte)value;
        return EndField(1);
    }

    public FixMessageBuilder Set(int tag, bool value) => Set(tag, value ? 'Y' : 'N');

    public FixMessageBuilder SetUtcTimestamp(int tag, DateTime value)
    {
        var span = BeginField(tag, FixTime.UtcTimestampLength);
        return EndField(FixTime.FormatUtcTimestamp(value, span));
    }

    public FixMessageBuilder SetLocalMktDate(int tag, DateOnly value)
    {
        var span = BeginField(tag, 8);
        value.TryFormat(span, out var written, "yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture);
        return EndField(written);
    }

    /// <summary>Creates a builder holding the body fields of an existing message, byte for byte.</summary>
    public static FixMessageBuilder FromBody(FixMessage message)
    {
        var builder = new FixMessageBuilder(message.MsgType, message.Raw.Length);
        foreach (var field in message.Fields)
        {
            if (!Tags.IsHeaderTag(field.Tag) && !Tags.IsTrailerTag(field.Tag))
            {
                builder.Set(field.Tag, message.ValueSpan(field));
            }
        }

        return builder;
    }

    /// <summary>Upper bound on the encoded size of this message with <paramref name="header"/>.</summary>
    public int MaxLength(in FixHeader header) => _length + header.BeginString.Length + header.SenderCompID.Length
        + header.TargetCompID.Length + MsgType.Length + 128;

    /// <summary>Encodes the full message (header + body + trailer) and returns the byte count.</summary>
    public int WriteTo(Span<byte> destination, in FixHeader header)
    {
        // Layout: [8=..|9=NNN|][35=..|49=..|56=..|34=..|52=..|(43=Y|122=..|)][body][10=CCC|]
        // BodyLength counts from 35= through the SOH before 10=. Its digit count is not known until the header is
        // written, so the standard header is written first at a fixed offset and the prefix is placed in front of it.
        Span<byte> prefix = stackalloc byte[32];
        var prefixLen = 0;
        prefixLen += WriteAscii(prefix, "8=");
        prefixLen += WriteAscii(prefix[prefixLen..], header.BeginString);
        prefix[prefixLen++] = Soh;

        var headerScratch = destination[40..];
        var h = 0;
        h += WriteTagString(headerScratch[h..], Tags.MsgType, MsgType);
        h += WriteTagString(headerScratch[h..], Tags.SenderCompID, header.SenderCompID);
        h += WriteTagString(headerScratch[h..], Tags.TargetCompID, header.TargetCompID);
        h += WriteTagInt(headerScratch[h..], Tags.MsgSeqNum, header.MsgSeqNum);
        if (header.PossDupFlag)
        {
            h += WriteTagString(headerScratch[h..], Tags.PossDupFlag, "Y");
        }

        h += WriteTagTime(headerScratch[h..], Tags.SendingTime, header.SendingTime);
        if (header.OrigSendingTime is { } orig)
        {
            h += WriteTagTime(headerScratch[h..], Tags.OrigSendingTime, orig);
        }

        var bodyLength = h + _length;
        prefixLen += WriteAscii(prefix[prefixLen..], "9=");
        Utf8Formatter.TryFormat(bodyLength, prefix[prefixLen..], out var digits);
        prefixLen += digits;
        prefix[prefixLen++] = Soh;

        // Move the standard header right behind the prefix, then append body and trailer.
        headerScratch[..h].CopyTo(destination[prefixLen..]);
        prefix[..prefixLen].CopyTo(destination);
        var pos = prefixLen + h;
        Body.CopyTo(destination[pos..]);
        pos += _length;

        var checksum = FixChecksum.Compute(destination[..pos]);
        destination[pos++] = (byte)'1';
        destination[pos++] = (byte)'0';
        destination[pos++] = (byte)'=';
        destination[pos++] = (byte)('0' + (checksum / 100));
        destination[pos++] = (byte)('0' + (checksum / 10 % 10));
        destination[pos++] = (byte)('0' + (checksum % 10));
        destination[pos++] = Soh;
        return pos;
    }

    public int WriteTo(IBufferWriter<byte> writer, in FixHeader header)
    {
        var span = writer.GetSpan(MaxLength(header));
        var written = WriteTo(span, header);
        writer.Advance(written);
        return written;
    }

    /// <summary>Encodes the full message into a new array.</summary>
    public byte[] ToBytes(in FixHeader header)
    {
        var scratch = ArrayPool<byte>.Shared.Rent(MaxLength(header));
        try
        {
            var written = WriteTo(scratch, header);
            return scratch.AsSpan(0, written).ToArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(scratch);
        }
    }

    public FixMessage ToMessage(in FixHeader header) => FixMessage.Parse(ToBytes(header));

    public void Dispose()
    {
        var body = _body;
        _body = [];
        _length = 0;
        if (body.Length > 0)
        {
            ArrayPool<byte>.Shared.Return(body);
        }
    }

    private Span<byte> BeginField(int tag, int maxValueLength)
    {
        EnsureCapacity(12 + maxValueLength + 1);
        Utf8Formatter.TryFormat(tag, _body.AsSpan(_length), out var written);
        _length += written;
        _body[_length++] = (byte)'=';
        return _body.AsSpan(_length, maxValueLength);
    }

    private FixMessageBuilder EndField(int valueLength)
    {
        _length += valueLength;
        _body[_length++] = Soh;
        return this;
    }

    private void EnsureCapacity(int extra)
    {
        if (_length + extra <= _body.Length)
        {
            return;
        }

        var bigger = ArrayPool<byte>.Shared.Rent(Math.Max(_body.Length * 2, _length + extra));
        _body.AsSpan(0, _length).CopyTo(bigger);
        ArrayPool<byte>.Shared.Return(_body);
        _body = bigger;
    }

    private static int WriteAscii(Span<byte> dest, string value) => Encoding.ASCII.GetBytes(value, dest);

    private static int WriteTagString(Span<byte> dest, int tag, string value)
    {
        Utf8Formatter.TryFormat(tag, dest, out var n);
        dest[n++] = (byte)'=';
        n += Encoding.ASCII.GetBytes(value, dest[n..]);
        dest[n++] = Soh;
        return n;
    }

    private static int WriteTagInt(Span<byte> dest, int tag, int value)
    {
        Utf8Formatter.TryFormat(tag, dest, out var n);
        dest[n++] = (byte)'=';
        Utf8Formatter.TryFormat(value, dest[n..], out var v);
        n += v;
        dest[n++] = Soh;
        return n;
    }

    private static int WriteTagTime(Span<byte> dest, int tag, DateTime value)
    {
        Utf8Formatter.TryFormat(tag, dest, out var n);
        dest[n++] = (byte)'=';
        n += FixTime.FormatUtcTimestamp(value, dest[n..]);
        dest[n++] = Soh;
        return n;
    }
}
