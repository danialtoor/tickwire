using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Tickwire.Fix;
using Tickwire.Fix.Dictionary;
using Tickwire.LogAnalyzer;

// tickwire: support-desk tools for FIX.
//   tickwire fixlog analyze <file|-> [--json]   diagnose a FIX log
//   tickwire fix decode "<message>"            decode one message tag by tag
//   tickwire session list [--api <url>]        list sessions on a Tickwire server

var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

return args switch
{
    ["fixlog", "analyze", var path, .. var rest] => Analyze(path, rest.Contains("--json")),
    ["fix", "decode", var message] => Decode(message),
    ["session", "list", .. var rest] => await ListSessions(rest),
    ["--version"] => Print(typeof(FixMessage).Assembly.GetName().Version?.ToString() ?? "dev"),
    _ => Usage(),
};

int Analyze(string path, bool asJson)
{
    var text = path == "-" ? Console.In.ReadToEnd() : File.ReadAllText(path);
    var result = FixLogAnalyzer.Analyze(text);
    Console.Write(asJson ? JsonSerializer.Serialize(result, json) + Environment.NewLine : FixLogAnalyzer.Report(result));
    return result.Errors > 0 ? 2 : 0;
}

int Decode(string message)
{
    if (!FixParser.TryParse(FixDisplay.FromPiped(message), out var msg, out var error))
    {
        Console.Error.WriteLine($"Not a FIX message: {error}");
        return 1;
    }

    var dict = FixDictionary.Fix44;
    Console.WriteLine($"{dict.MessageName(msg.MsgType)} (35={msg.MsgType})  integrity: {(msg.IsIntact ? "OK" : msg.Integrity.ToString())}");
    foreach (var f in dict.Decode(msg))
    {
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  {f.Tag,6}  {f.Name,-22} {f.Value}{(f.Meaning is null ? "" : $"  ({f.Meaning})")}"));
    }

    if (dict.Validate(msg) is { } issue)
    {
        Console.WriteLine($"  validation: {issue.Text} (SessionRejectReason {(int)issue.Reason})");
    }

    return msg.IsIntact ? 0 : 2;
}

async Task<int> ListSessions(string[] rest)
{
    var api = rest.SkipWhile(a => a != "--api").Skip(1).FirstOrDefault()
        ?? Environment.GetEnvironmentVariable("TICKWIRE_API") ?? "http://localhost:8080";
    using var http = new HttpClient { BaseAddress = new Uri(api) };
    var sessions = await http.GetFromJsonAsync<JsonElement>("/api/sessions");
    Console.WriteLine($"{"CLIENT COMPID",-16} {"STATE",-18} {"TRANSPORT",-10} {"OUT",6} {"IN",6}");
    foreach (var s in sessions.EnumerateArray())
    {
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{s.GetProperty("clientCompId").GetString(),-16} {s.GetProperty("state").GetString(),-18} {s.GetProperty("transport").GetString(),-10} {s.GetProperty("nextSenderSeqNum").GetInt32(),6} {s.GetProperty("nextTargetSeqNum").GetInt32(),6}"));
    }

    return 0;
}

static int Print(string s)
{
    Console.WriteLine(s);
    return 0;
}

static int Usage()
{
    Console.Error.WriteLine("""
        tickwire: FIX support tools

          tickwire fixlog analyze <file|-> [--json]   diagnose a FIX log (exit code 2 when errors are found)
          tickwire fix decode "<message>"            decode one message ('|' or SOH delimited)
          tickwire session list [--api <url>]        list FIX sessions on a Tickwire server
        """);
    return 1;
}
