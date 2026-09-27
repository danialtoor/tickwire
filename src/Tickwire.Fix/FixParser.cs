namespace Tickwire.Fix;

/// <summary>Why a byte sequence could not be read as a FIX message at all.</summary>
public enum FixParseErrorKind
{
    None,
    Empty,
    MustStartWithBeginString,
    BodyLengthMustBeSecond,
    MsgTypeMustBeThird,
    InvalidTagNumber,
    MissingEquals,
    TagWithoutValue,
    UnterminatedField,
    InvalidBodyLength,
    ChecksumMustBeLast,
    InvalidDataLength,
}

public readonly record struct FixParseError(FixParseErrorKind Kind, int Offset, int Tag = 0)
{
    public static readonly FixParseError None = new(FixParseErrorKind.None, 0);

    public override string ToString() => Tag == 0 ? $"{Kind} at byte {Offset}" : $"{Kind} (tag {Tag}) at byte {Offset}";

    /// <summary>The SessionRejectReason(373) a counterparty should receive for this error.</summary>
    public SessionRejectReason RejectReason => Kind switch
    {
        FixParseErrorKind.InvalidTagNumber => SessionRejectReason.InvalidTagNumber,
        FixParseErrorKind.TagWithoutValue => SessionRejectReason.TagSpecifiedWithoutValue,
        FixParseErrorKind.BodyLengthMustBeSecond or FixParseErrorKind.MsgTypeMustBeThird
            or FixParseErrorKind.ChecksumMustBeLast => SessionRejectReason.TagSpecifiedOutOfRequiredOrder,
        _ => SessionRejectReason.Other,
    };
}

/// <summary>
/// Parses one complete message. Scans the bytes once and records field offsets; nothing is copied.
/// Header order (8, 9, 35 first and 10 last) is enforced. BodyLength and CheckSum are verified but a mismatch
/// is reported through <see cref="FixMessage.Integrity"/> rather than failing, so callers can log what arrived.
/// </summary>
public static class FixParser
{
    public const byte Soh = 0x01;

    public static bool TryParse(ReadOnlyMemory<byte> data, out FixMessage message, out FixParseError error)
    {
        message = null!;
        var span = data.Span;
        if (span.IsEmpty)
        {
            error = new FixParseError(FixParseErrorKind.Empty, 0);
            return false;
        }

        // One field per SOH (data fields may contain extra SOH bytes, so this can over-count, never under-count).
        var fields = new FixField[span.Count(Soh)];
        var fieldCount = 0;
        var pos = 0;
        var bodyStart = -1;
        var checksumFieldStart = -1;
        var declaredBodyLength = -1;
        var pendingDataTag = 0;
        var pendingDataLength = -1;

        while (pos < span.Length)
        {
            var fieldStart = pos;

            // Tag: ASCII digits up to '='.
            var tag = 0;
            var digits = 0;
            while (pos < span.Length && span[pos] != (byte)'=')
            {
                var b = span[pos];
                if (b < (byte)'0' || b > (byte)'9' || digits >= 9)
                {
                    error = b == Soh
                        ? new FixParseError(FixParseErrorKind.MissingEquals, pos)
                        : new FixParseError(FixParseErrorKind.InvalidTagNumber, fieldStart);
                    return false;
                }

                tag = (tag * 10) + (b - '0');
                digits++;
                pos++;
            }

            if (pos >= span.Length)
            {
                error = new FixParseError(FixParseErrorKind.MissingEquals, fieldStart);
                return false;
            }

            if (digits == 0 || tag == 0 || span[fieldStart] == (byte)'0')
            {
                error = new FixParseError(FixParseErrorKind.InvalidTagNumber, fieldStart, tag);
                return false;
            }

            pos++; // skip '='
            var valueStart = pos;
            int valueLength;

            if (pendingDataTag != 0 && tag == pendingDataTag)
            {
                // Data field: length comes from the preceding length tag and the value may contain SOH.
                if (valueStart + pendingDataLength >= span.Length || span[valueStart + pendingDataLength] != Soh)
                {
                    error = new FixParseError(FixParseErrorKind.InvalidDataLength, valueStart, tag);
                    return false;
                }

                valueLength = pendingDataLength;
                pos = valueStart + valueLength;
            }
            else
            {
                var soh = span[valueStart..].IndexOf(Soh);
                if (soh < 0)
                {
                    error = new FixParseError(FixParseErrorKind.UnterminatedField, fieldStart, tag);
                    return false;
                }

                valueLength = soh;
                pos = valueStart + soh;
            }

            pendingDataTag = 0;

            if (valueLength == 0)
            {
                error = new FixParseError(FixParseErrorKind.TagWithoutValue, fieldStart, tag);
                return false;
            }

            var index = fieldCount;
            switch (index)
            {
                case 0 when tag != Tags.BeginString:
                    error = new FixParseError(FixParseErrorKind.MustStartWithBeginString, fieldStart, tag);
                    return false;
                case 1 when tag != Tags.BodyLength:
                    error = new FixParseError(FixParseErrorKind.BodyLengthMustBeSecond, fieldStart, tag);
                    return false;
                case 2 when tag != Tags.MsgType:
                    error = new FixParseError(FixParseErrorKind.MsgTypeMustBeThird, fieldStart, tag);
                    return false;
                default:
                    break;
            }

            if (checksumFieldStart >= 0)
            {
                error = new FixParseError(FixParseErrorKind.ChecksumMustBeLast, fieldStart, tag);
                return false;
            }

            if (tag == Tags.BodyLength)
            {
                if (!TryParsePositiveInt(span.Slice(valueStart, valueLength), out declaredBodyLength))
                {
                    error = new FixParseError(FixParseErrorKind.InvalidBodyLength, valueStart, tag);
                    return false;
                }

                bodyStart = pos + 1;
            }
            else if (tag == Tags.CheckSum)
            {
                checksumFieldStart = fieldStart;
            }
            else
            {
                var dataTag = Tags.DataTagFor(tag);
                if (dataTag != 0)
                {
                    if (!TryParsePositiveInt(span.Slice(valueStart, valueLength), out pendingDataLength))
                    {
                        error = new FixParseError(FixParseErrorKind.InvalidDataLength, valueStart, tag);
                        return false;
                    }

                    pendingDataTag = dataTag;
                }
            }

            fields[fieldCount++] = new FixField(tag, valueStart, valueLength);
            pos++; // skip SOH
        }

        if (checksumFieldStart < 0)
        {
            error = new FixParseError(FixParseErrorKind.ChecksumMustBeLast, span.Length);
            return false;
        }

        var actualBodyLength = checksumFieldStart - bodyStart;
        var actualChecksum = FixChecksum.Compute(span[..checksumFieldStart]);
        var checksumField = fields[fieldCount - 1];
        var declaredChecksum = TryParsePositiveInt(span.Slice(checksumField.ValueOffset, checksumField.ValueLength),
            out var cs) && checksumField.ValueLength == 3 ? cs : -1;

        var integrity = FixIntegrity.Ok;
        if (actualBodyLength != declaredBodyLength)
        {
            integrity |= FixIntegrity.BodyLengthMismatch;
        }

        if (actualChecksum != declaredChecksum)
        {
            integrity |= FixIntegrity.ChecksumMismatch;
        }

        message = new FixMessage(data, fields, fieldCount, integrity, declaredBodyLength, actualBodyLength, declaredChecksum,
            actualChecksum);
        error = FixParseError.None;
        return true;
    }

    internal static bool TryParsePositiveInt(ReadOnlySpan<byte> span, out int value)
    {
        value = 0;
        if (span.IsEmpty || span.Length > 9)
        {
            return false;
        }

        foreach (var b in span)
        {
            if (b < (byte)'0' || b > (byte)'9')
            {
                return false;
            }

            value = (value * 10) + (b - '0');
        }

        return true;
    }
}

public static class FixChecksum
{
    /// <summary>Sum of all bytes modulo 256, as defined for CheckSum(10).</summary>
    public static int Compute(ReadOnlySpan<byte> bytes)
    {
        // Widen bytes to 16-bit lanes and add in bulk; each lane can take 256 additions of 255 before overflowing.
        uint sum = 0;
        var i = 0;
        if (System.Runtime.Intrinsics.Vector128.IsHardwareAccelerated)
        {
            while (i + 16 <= bytes.Length)
            {
                var acc = System.Runtime.Intrinsics.Vector128<ushort>.Zero;
                var blockEnd = Math.Min(bytes.Length - 15, i + (16 * 128));
                for (; i < blockEnd; i += 16)
                {
                    var v = System.Runtime.Intrinsics.Vector128.Create(bytes.Slice(i, 16));
                    var (lo, hi) = System.Runtime.Intrinsics.Vector128.Widen(v);
                    acc += lo + hi;
                }

                sum += System.Runtime.Intrinsics.Vector128.Sum(System.Runtime.Intrinsics.Vector128.WidenLower(acc))
                    + System.Runtime.Intrinsics.Vector128.Sum(System.Runtime.Intrinsics.Vector128.WidenUpper(acc));
            }
        }

        for (; i < bytes.Length; i++)
        {
            sum += bytes[i];
        }

        return (int)(sum & 0xFF);
    }
}
