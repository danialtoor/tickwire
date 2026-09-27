using System.Globalization;
using System.Text;

namespace Tickwire.Fix;

public static class FixTime
{
    /// <summary>Length of yyyyMMdd-HH:mm:ss.fff.</summary>
    public const int UtcTimestampLength = 21;

    private static readonly string[] Formats =
    [
        "yyyyMMdd-HH:mm:ss.fff",
        "yyyyMMdd-HH:mm:ss",
        "yyyyMMdd-HH:mm:ss.ffffff",
        "yyyyMMdd-HH:mm:ss.fffffff",
        "yyyyMMdd-HH:mm:ss.fffffffff",
    ];

    public static int FormatUtcTimestamp(DateTime value, Span<byte> destination)
    {
        var utc = value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : value;
        utc.TryFormat(destination, out var written, "yyyyMMdd-HH:mm:ss.fff", CultureInfo.InvariantCulture);
        return written;
    }

    public static string FormatUtcTimestamp(DateTime value)
    {
        Span<byte> buffer = stackalloc byte[UtcTimestampLength];
        var n = FormatUtcTimestamp(value, buffer);
        return Encoding.ASCII.GetString(buffer[..n]);
    }

    public static bool TryParseUtcTimestamp(string text, out DateTime value)
    {
        // Nanosecond precision (9 digits) is legal in FIX but DateTime holds 7; trim before parsing.
        var s = text.Length > 25 ? text[..25] : text;
        return DateTime.TryParseExact(s, Formats, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out value);
    }
}

public static class FixDisplay
{
    /// <summary>Renders wire bytes with SOH shown as '|'.</summary>
    public static string ToPiped(ReadOnlySpan<byte> raw)
    {
        var chars = new char[raw.Length];
        for (var i = 0; i < raw.Length; i++)
        {
            var b = raw[i];
            chars[i] = b == FixParser.Soh ? '|' : (char)b;
        }

        return new string(chars);
    }

    /// <summary>
    /// Converts a human-written message ('|', '^', or the literal text "&lt;SOH&gt;" as delimiters) back to wire bytes.
    /// A delimiter is only replaced when it ends a field, i.e. when it is followed by a tag number and '=' or ends the text.
    /// </summary>
    public static byte[] FromPiped(string text)
    {
        var s = text.Trim().Replace("<SOH>", "\u0001", StringComparison.OrdinalIgnoreCase)
            .Replace("\\x01", "\u0001", StringComparison.OrdinalIgnoreCase);
        var bytes = new byte[s.Length];
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            bytes[i] = (c is '|' or '^') && IsFieldBoundary(s, i) ? FixParser.Soh : (byte)c;
        }

        return bytes;
    }

    private static bool IsFieldBoundary(string s, int i)
    {
        var j = i + 1;
        if (j >= s.Length)
        {
            return true;
        }

        var start = j;
        while (j < s.Length && char.IsAsciiDigit(s[j]))
        {
            j++;
        }

        return j > start && j < s.Length && s[j] == '=';
    }
}
