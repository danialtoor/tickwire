using System.Globalization;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Tickwire.MarketData.Providers;

/// <summary>
/// Databento live gateway for OPRA (dataset OPRA.PILLAR). Protocol (public "Raw API" docs), line-based over TCP:
/// the gateway greets with <c>lsg_version=...</c> and <c>cram=CHALLENGE</c>; the client answers
/// <c>auth=SHA256(CHALLENGE|KEY)-BUCKET|dataset=...|encoding=json|pretty_px=1|pretty_ts=1</c>, gets
/// <c>success=1|session_id=...</c>, sends one or more <c>schema=...|stype_in=parent|symbols=SPY.OPT</c> lines and
/// <c>start_session</c>. Records then arrive as JSON lines; symbol-mapping records (rtype 22) map instrument ids to
/// OCC symbols. Each underlying is subscribed as a parent symbol and filtered to Tickwire's listed contracts.
/// </summary>
public sealed class DatabentoProvider : IOptionFeedProvider
{
    private const string Dataset = "OPRA.PILLAR";

    public ProviderInfo Info { get; } = new(
        "databento",
        "Databento",
        "Consolidated OPRA quotes from Databento's live gateway.",
        "Raw TCP (CRAM auth, JSON records)",
        "https://databento.com/docs/api-reference-live",
        "https://databento.com/signup",
        [
            new("apiKey", "API key", true, "db-..."),
            new("schema", "Schema (cbbo-1s, cmbp-1 or tcbbo)", false, "cbbo-1s", "cbbo-1s"),
        ],
        ["Consolidated BBO", "Trades (tcbbo)"],
        "#ff5a36",
        Verified: false);

    public async Task RunAsync(IReadOnlyDictionary<string, string> credentials, FeedSubscription subscription, IFeedSink sink,
        CancellationToken cancellationToken)
    {
        var key = Required.Get(credentials, "apiKey");
        var schema = credentials.GetValueOrDefault("schema")?.Trim() is { Length: > 0 } s ? s : "cbbo-1s";
        var host = Dataset.ToLowerInvariant().Replace('.', '-') + ".lsg.databento.com";

        using var tcp = new TcpClient();
        await tcp.ConnectAsync(host, 13000, cancellationToken).ConfigureAwait(false);
        await using var stream = tcp.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { NewLine = "\n", AutoFlush = true };

        string? challenge = null;
        while (challenge is null && await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } greeting)
        {
            challenge = Field(greeting, "cram");
        }

        if (challenge is null)
        {
            throw new IOException("Databento gateway sent no authentication challenge");
        }

        await writer.WriteLineAsync($"auth={CramResponse(challenge, key)}|dataset={Dataset}|encoding=json|ts_out=0|pretty_px=1|pretty_ts=1")
            .ConfigureAwait(false);
        var reply = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) ?? "";
        if (Field(reply, "success") != "1")
        {
            throw new FeedAuthException($"Databento rejected the key: {Field(reply, "error") ?? reply}");
        }

        var parents = string.Join(',', subscription.Underlyings.Select(u => $"{u}.OPT"));
        await writer.WriteLineAsync($"schema={schema}|stype_in=parent|symbols={parents}").ConfigureAwait(false);
        await writer.WriteLineAsync("start_session").ConfigureAwait(false);
        sink.OnStatus($"Authenticated with Databento; streaming {schema} for {parents}");

        var wanted = subscription.Contracts.Select(c => c.ToString()).ToHashSet(StringComparer.Ordinal);
        var instruments = new Dictionary<ulong, string>();
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (line.Length > 0 && line[0] == '{')
            {
                Handle(line, instruments, wanted, sink);
            }
        }

        throw new IOException("Databento closed the session");
    }

    /// <summary>Challenge-response per the Raw API: hex SHA-256 of "challenge|key", then '-' and the key's last 5 characters.</summary>
    public static string CramResponse(string challenge, string apiKey)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{challenge}|{apiKey}")));
        return $"{hash}-{apiKey[^5..]}";
    }

    internal static string? Field(string line, string name)
    {
        foreach (var part in line.Trim().Split('|'))
        {
            var eq = part.IndexOf('=', StringComparison.Ordinal);
            if (eq > 0 && part[..eq] == name)
            {
                return part[(eq + 1)..];
            }
        }

        return null;
    }

    internal static void Handle(string line, Dictionary<ulong, string> instruments, HashSet<string> wanted, IFeedSink sink)
    {
        using var doc = JsonDocument.Parse(line);
        var r = doc.RootElement;
        var hd = r.Obj("hd");
        if (hd is null || !ulong.TryParse(hd.Value.Str("instrument_id"), CultureInfo.InvariantCulture, out var id))
        {
            return;
        }

        var rtype = hd.Value.Long("rtype");
        if (rtype == 22)
        {
            // Symbol mapping: stype_out_symbol is the OPRA raw symbol (21-char OCC).
            if (r.Str("stype_out_symbol") is { } raw && Symbology.TryParse(raw, out var mapped))
            {
                instruments[id] = mapped.ToString();
            }

            return;
        }

        if (!instruments.TryGetValue(id, out var occ) || !wanted.Contains(occ))
        {
            return;
        }

        var time = DateTime.TryParse(hd.Value.Str("ts_event"), CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var ts)
            ? ts
            : DateTime.UtcNow;
        var level = r.TryGetProperty("levels", out var levels) && levels.ValueKind == JsonValueKind.Array && levels.GetArrayLength() > 0
            ? levels[0]
            : r;
        var tradePx = r.Str("action") == "T" ? r.Dec("price") : null;
        sink.OnQuote(new ExternalQuote(occ, Px(level.Dec("bid_px")), Px(level.Dec("ask_px")), level.Dec("bid_sz"), level.Dec("ask_sz"),
            Px(tradePx), null, null, time, "databento"));
    }

    /// <summary>With pretty_px the gateway sends decimal prices; without it, fixed-point integers scaled by 1e9.</summary>
    private static decimal? Px(decimal? value) => value is { } v && v > 1_000_000 ? v / 1_000_000_000m : value;
}
