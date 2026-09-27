using System.Buffers.Text;
using System.Globalization;
using System.Text;

namespace Tickwire.Fix;

/// <summary>Location of one tag=value pair inside a message buffer.</summary>
public readonly record struct FixField(int Tag, int ValueOffset, int ValueLength);

/// <summary>Structural problems found while parsing. The message is still usable when only integrity checks fail.</summary>
[Flags]
public enum FixIntegrity
{
    Ok = 0,
    BodyLengthMismatch = 1,
    ChecksumMismatch = 2,
}

/// <summary>
/// A parsed FIX message: a read-only view over its wire bytes plus an index of field positions.
/// Values are decoded on demand, so parsing itself does not allocate strings.
/// </summary>
public sealed class FixMessage
{
    private readonly ReadOnlyMemory<byte> _raw;
    private readonly FixField[] _fields;
    private string? _msgType;

    private readonly int _fieldCount;

    internal FixMessage(ReadOnlyMemory<byte> raw, FixField[] fields, int fieldCount, FixIntegrity integrity, int declaredBodyLength,
        int actualBodyLength, int declaredChecksum, int actualChecksum)
    {
        _raw = raw;
        _fields = fields;
        _fieldCount = fieldCount;
        Integrity = integrity;
        DeclaredBodyLength = declaredBodyLength;
        ActualBodyLength = actualBodyLength;
        DeclaredChecksum = declaredChecksum;
        ActualChecksum = actualChecksum;
    }

    /// <summary>The exact bytes of the message as received or sent.</summary>
    public ReadOnlyMemory<byte> Raw => _raw;

    public ReadOnlySpan<FixField> Fields => _fields.AsSpan(0, _fieldCount);

    public FixIntegrity Integrity { get; }
    public int DeclaredBodyLength { get; }
    public int ActualBodyLength { get; }
    public int DeclaredChecksum { get; }
    public int ActualChecksum { get; }

    /// <summary>True when BodyLength and CheckSum both match. Garbled messages must be ignored by a session.</summary>
    public bool IsIntact => Integrity == FixIntegrity.Ok;

    public string MsgType => _msgType ??= GetString(Tags.MsgType) ?? string.Empty;
    public string BeginString => GetString(Tags.BeginString) ?? string.Empty;
    public int MsgSeqNum => GetInt(Tags.MsgSeqNum) ?? 0;
    public string SenderCompID => GetString(Tags.SenderCompID) ?? string.Empty;
    public string TargetCompID => GetString(Tags.TargetCompID) ?? string.Empty;
    public bool PossDupFlag => GetBool(Tags.PossDupFlag) ?? false;
    public bool IsAdmin => MsgTypes.IsAdmin(MsgType);

    public bool Has(int tag) => IndexOf(tag) >= 0;

    /// <summary>Raw value bytes of the first occurrence of <paramref name="tag"/>, or empty.</summary>
    public ReadOnlySpan<byte> GetRaw(int tag)
    {
        var i = IndexOf(tag);
        return i < 0 ? default : ValueSpan(_fields[i]);
    }

    public ReadOnlySpan<byte> ValueSpan(FixField field) => _raw.Span.Slice(field.ValueOffset, field.ValueLength);

    public string ValueString(FixField field) => Encoding.ASCII.GetString(ValueSpan(field));

    public string? GetString(int tag)
    {
        var i = IndexOf(tag);
        return i < 0 ? null : ValueString(_fields[i]);
    }

    public bool TryGetString(int tag, out string value)
    {
        value = GetString(tag)!;
        return value is not null;
    }

    public int? GetInt(int tag)
    {
        var i = IndexOf(tag);
        if (i < 0)
        {
            return null;
        }

        var span = ValueSpan(_fields[i]);
        return Utf8Parser.TryParse(span, out int value, out var consumed) && consumed == span.Length ? value : null;
    }

    public long? GetLong(int tag)
    {
        var i = IndexOf(tag);
        if (i < 0)
        {
            return null;
        }

        var span = ValueSpan(_fields[i]);
        return Utf8Parser.TryParse(span, out long value, out var consumed) && consumed == span.Length ? value : null;
    }

    public decimal? GetDecimal(int tag)
    {
        var i = IndexOf(tag);
        if (i < 0)
        {
            return null;
        }

        var span = ValueSpan(_fields[i]);
        return Utf8Parser.TryParse(span, out decimal value, out var consumed) && consumed == span.Length ? value : null;
    }

    public char? GetChar(int tag)
    {
        var span = GetRaw(tag);
        return span.Length == 1 ? (char)span[0] : null;
    }

    public bool? GetBool(int tag) => GetChar(tag) switch
    {
        'Y' => true,
        'N' => false,
        _ => null,
    };

    /// <summary>Parses a UTCTimestamp (yyyyMMdd-HH:mm:ss[.fff[fff]]).</summary>
    public DateTime? GetUtcTimestamp(int tag)
    {
        var s = GetString(tag);
        return s is not null && FixTime.TryParseUtcTimestamp(s, out var value) ? value : null;
    }

    public int IndexOf(int tag)
    {
        var fields = _fields;
        for (var i = 0; i < _fieldCount; i++)
        {
            if (fields[i].Tag == tag)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Human-readable form with SOH shown as '|'.</summary>
    public override string ToString() => FixDisplay.ToPiped(_raw.Span);

    public static FixMessage Parse(ReadOnlyMemory<byte> data) => FixParser.TryParse(data, out var message, out var error)
        ? message
        : throw new FormatException(error.ToString());

    /// <summary>Parses a string that may use '|' or '^' in place of SOH (as logs and docs usually do).</summary>
    public static FixMessage Parse(string text) => Parse(FixDisplay.FromPiped(text));

    public override int GetHashCode() => HashCode.Combine(_raw.Length, MsgSeqNum);

    internal static string FormatDecimal(decimal value) => value.ToString(CultureInfo.InvariantCulture);
}
