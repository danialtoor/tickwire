using System.Buffers;

namespace Tickwire.Fix;

public enum FrameStatus
{
    /// <summary>A complete message was found at [Start, Start + Length).</summary>
    Frame,

    /// <summary>The buffer holds the start of a message but not all of it yet.</summary>
    NeedMoreData,

    /// <summary>The first <c>Consumed</c> bytes are not part of any message and should be discarded.</summary>
    Garbage,
}

public readonly record struct FrameScan(FrameStatus Status, int Start, int Length, int Consumed);

/// <summary>
/// Splits a TCP byte stream into FIX messages. TCP has no message boundaries, so a read can hold half a message,
/// exactly one, or several back to back. The framer uses BodyLength(9) to find the end of each message and falls back
/// to searching for the CheckSum field when BodyLength is wrong, so one bad message doesn't break the rest of the stream.
/// </summary>
public static class FixFramer
{
    public const int MaxMessageSize = 64 * 1024;

    private static ReadOnlySpan<byte> BeginMarker => "8=FIX"u8;
    private static ReadOnlySpan<byte> ChecksumMarker => "\u000110="u8;

    public static FrameScan Scan(ReadOnlySpan<byte> buffer)
    {
        var start = buffer.IndexOf(BeginMarker);
        if (start < 0)
        {
            // Keep a short tail in case it's the first bytes of "8=FIX".
            var discard = Math.Max(0, buffer.Length - (BeginMarker.Length - 1));
            return discard > 0 ? new FrameScan(FrameStatus.Garbage, 0, 0, discard) : NeedMore;
        }

        if (start > 0)
        {
            return new FrameScan(FrameStatus.Garbage, 0, 0, start);
        }

        // 8=FIX.4.4|9=NNN|
        var firstSoh = buffer.IndexOf(FixParser.Soh);
        if (firstSoh < 0)
        {
            return buffer.Length > 32 ? SkipOne : NeedMore;
        }

        var rest = buffer[(firstSoh + 1)..];
        if (rest.Length < 3)
        {
            return NeedMore;
        }

        if (rest[0] != (byte)'9' || rest[1] != (byte)'=')
        {
            return SkipOne;
        }

        var lengthEnd = rest.IndexOf(FixParser.Soh);
        if (lengthEnd < 0)
        {
            return rest.Length > 12 ? SkipOne : NeedMore;
        }

        if (!FixParser.TryParsePositiveInt(rest[2..lengthEnd], out var bodyLength) || bodyLength > MaxMessageSize)
        {
            return SkipOne;
        }

        var bodyStart = firstSoh + 1 + lengthEnd + 1;
        var checksumStart = bodyStart + bodyLength;
        var total = checksumStart + 7; // "10=NNN" + SOH

        if (buffer.Length >= total && IsChecksumField(buffer[checksumStart..]))
        {
            return new FrameScan(FrameStatus.Frame, 0, total, total);
        }

        // BodyLength is wrong, or we don't have the whole message yet. Look for a CheckSum field directly.
        var search = buffer[(bodyStart - 1)..];
        // A new BeginString inside this message means it was cut off (it may not even end in SOH).
        var nextBegin = search.IndexOf(BeginMarker);
        var limit = nextBegin < 0 ? search.Length : nextBegin;
        var offset = 0;
        while (true)
        {
            var hit = search[offset..limit].IndexOf(ChecksumMarker);
            if (hit < 0)
            {
                break;
            }

            var fieldStart = bodyStart - 1 + offset + hit + 1;
            if (buffer.Length - fieldStart < 7)
            {
                return NeedMore;
            }

            if (IsChecksumField(buffer[fieldStart..]))
            {
                var end = fieldStart + 7;
                if (buffer.Length >= total || nextBegin >= 0 || end < checksumStart)
                {
                    return new FrameScan(FrameStatus.Frame, 0, end, end);
                }

                // The declared length reaches further than this checksum. Wait for more data before deciding.
                return NeedMore;
            }

            offset += hit + 1;
        }

        if (nextBegin >= 0)
        {
            // A new message starts before this one was terminated. Drop the truncated fragment.
            return new FrameScan(FrameStatus.Garbage, 0, 0, bodyStart - 1 + nextBegin);
        }

        return buffer.Length > MaxMessageSize ? SkipOne : NeedMore;
    }

    /// <summary>
    /// Reads the next message out of a pipe buffer. Advances <paramref name="buffer"/> past consumed bytes.
    /// Returns false when more data is needed. <paramref name="discarded"/> counts garbage bytes skipped.
    /// </summary>
    public static bool TryReadMessage(ref ReadOnlySequence<byte> buffer, out byte[] message, out int discarded)
    {
        discarded = 0;
        message = [];
        while (!buffer.IsEmpty)
        {
            FrameScan scan;
            byte[]? rented = null;
            ReadOnlySpan<byte> span;
            if (buffer.IsSingleSegment)
            {
                span = buffer.FirstSpan;
            }
            else
            {
                var length = (int)Math.Min(buffer.Length, MaxMessageSize + 64);
                rented = ArrayPool<byte>.Shared.Rent(length);
                buffer.Slice(0, length).CopyTo(rented);
                span = rented.AsSpan(0, length);
            }

            try
            {
                scan = Scan(span);
                if (scan.Status == FrameStatus.Frame)
                {
                    message = span.Slice(scan.Start, scan.Length).ToArray();
                }
            }
            finally
            {
                if (rented is not null)
                {
                    ArrayPool<byte>.Shared.Return(rented);
                }
            }

            switch (scan.Status)
            {
                case FrameStatus.Frame:
                    buffer = buffer.Slice(scan.Consumed);
                    return true;
                case FrameStatus.Garbage:
                    discarded += scan.Consumed;
                    buffer = buffer.Slice(scan.Consumed);
                    continue;
                default:
                    return false;
            }
        }

        return false;
    }

    private static FrameScan NeedMore => new(FrameStatus.NeedMoreData, 0, 0, 0);

    private static FrameScan SkipOne => new(FrameStatus.Garbage, 0, 0, 1);

    private static bool IsChecksumField(ReadOnlySpan<byte> s) =>
        s.Length >= 7 && s[0] == (byte)'1' && s[1] == (byte)'0' && s[2] == (byte)'='
        && char.IsAsciiDigit((char)s[3]) && char.IsAsciiDigit((char)s[4]) && char.IsAsciiDigit((char)s[5])
        && s[6] == FixParser.Soh;
}
