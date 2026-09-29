using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Tickwire.Api.Endpoints;
using Tickwire.Api.Services;
using Tickwire.Fix;

namespace Tickwire.Integration.Tests;

/// <summary>The whole application in-process (in-memory persistence), with the FIX acceptor on a free TCP port.</summary>
public class ApiFactory : WebApplicationFactory<Program>
{
    private readonly string _connectionString;

    public ApiFactory()
        : this(string.Empty)
    {
    }

    protected ApiFactory(string connectionString)
    {
        _connectionString = connectionString;
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        FixPort = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
    }

    public int FixPort { get; }

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Default", _connectionString);
        builder.UseSetting("Tickwire:FixPort", FixPort.ToString(System.Globalization.CultureInfo.InvariantCulture));
        builder.UseSetting("Tickwire:AdminKey", "test-admin");
        builder.UseSetting("Tickwire:PublicFixHost", "127.0.0.1");
    }

    /// <summary>Places an order, retrying while the guest's FIX session is still logging on (the API answers 503).</summary>
    public static async Task<HttpResponseMessage> PlaceOrderAsync(HttpClient http, NewOrderBody body, int timeoutMs = 20_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (true)
        {
            var resp = await http.PostAsJsonAsync("/api/orders", body);
            if (resp.StatusCode != HttpStatusCode.ServiceUnavailable || DateTime.UtcNow > deadline)
            {
                return resp;
            }

            await Task.Delay(200);
        }
    }

    public async Task<(HttpClient Http, GuestProvisioned Guest)> NewGuestAsync()
    {
        var http = CreateClient();
        var resp = await http.PostAsync("/api/guest", null);
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var guest = (await resp.Content.ReadFromJsonAsync<GuestProvisioned>(Json))!;
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", guest.Token);
        return (http, guest);
    }
}

[CollectionDefinition("api")]
public sealed class ApiCollection : ICollectionFixture<ApiFactory>;

[Collection("api")]
public sealed class ApiTests(ApiFactory factory)
{
    private static async Task<T> Eventually<T>(Func<Task<T?>> probe, Func<T, bool> done, int timeoutMs = 10_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (true)
        {
            var value = await probe();
            if (value is not null && done(value))
            {
                return value;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Last value: {JsonSerializer.Serialize(value)}");
            }

            await Task.Delay(100);
        }
    }

    private async Task<QuoteDto> AtmCallAsync(HttpClient http)
    {
        var chain = await Eventually(() => http.GetFromJsonAsync<ChainDto>("/api/chain/SPY", ApiFactory.Json),
            c => c.Rows.Count > 0 && c.Rows[c.Rows.Count / 2].Call!.Ask is not null);
        return chain.Rows[chain.Rows.Count / 2].Call!;
    }

    [Fact]
    public async Task Health_and_instruments_are_public()
    {
        var http = factory.CreateClient();

        (await http.GetStringAsync("/health")).Should().Be("Healthy");
        var instruments = await http.GetFromJsonAsync<List<UnderlyingDto>>("/api/instruments", ApiFactory.Json);
        instruments!.Select(i => i.Symbol).Should().BeEquivalentTo(["SPY", "AAPL", "TSLA", "NVDA"]);
        (await http.GetAsync("/openapi/v1.json")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Guest_order_travels_over_FIX_and_fills()
    {
        var (http, guest) = await factory.NewGuestAsync();
        var call = await AtmCallAsync(http);

        var resp = await ApiFactory.PlaceOrderAsync(http, new NewOrderBody(call.Id, "buy", "limit", "day", call.Ask, 3));
        resp.StatusCode.Should().Be(HttpStatusCode.Accepted);

        var rows = await Eventually(() => http.GetFromJsonAsync<List<OrderRowDto>>("/api/orders", ApiFactory.Json),
            r => r.Count == 1 && r[0].Order.Status == "Filled" && r[0].InSync);
        rows[0].ClientView!.CumQty.Should().Be(3);

        var traffic = await http.GetFromJsonAsync<SessionTrafficDto>($"/api/sessions/{guest.ClientCompId}/messages", ApiFactory.Json);
        traffic!.Messages.Should().Contain(m => m.Side == "venue" && m.Direction == "in" && m.MsgType == "D");
        traffic.Messages.Should().Contain(m => m.Side == "client" && m.Direction == "in" && m.MsgType == "8");
    }

    [Fact]
    public async Task Risk_reject_comes_back_as_an_ExecutionReport_with_a_reason()
    {
        var (http, guest) = await factory.NewGuestAsync();
        var call = await AtmCallAsync(http);

        await ApiFactory.PlaceOrderAsync(http, new NewOrderBody(call.Id, "buy", "limit", "day", call.Ask, 500));

        var traffic = await Eventually(() => http.GetFromJsonAsync<SessionTrafficDto>($"/api/sessions/{guest.ClientCompId}/messages",
                ApiFactory.Json),
            t => t.Messages.Any(m => m.Side == "client" && m.MsgType == "8" && m.Raw.Contains("|39=8|", StringComparison.Ordinal)));
        traffic.Messages.Last(m => m.Raw.Contains("|39=8|", StringComparison.Ordinal)).Raw
            .Should().Contain("|103=3|").And.Contain("exceeds max order quantity 100");
    }

    [Fact]
    public async Task Chaos_drop_is_recovered_through_resend()
    {
        var (http, guest) = await factory.NewGuestAsync();
        var call = await AtmCallAsync(http);

        (await http.PostAsJsonAsync($"/api/sessions/{guest.ClientCompId}/chaos", new ChaosBody("drop-venue", 2))).EnsureSuccessStatusCode();
        await ApiFactory.PlaceOrderAsync(http, new NewOrderBody(call.Id, "buy", "limit", "day", call.Ask, 2));

        var traffic = await Eventually(() => http.GetFromJsonAsync<SessionTrafficDto>($"/api/sessions/{guest.ClientCompId}/messages",
                ApiFactory.Json),
            t => t.Logs.Any(l => l.Text.StartsWith("Gap recovered", StringComparison.Ordinal)));
        traffic.Messages.Should().Contain(m => m.Disposition == "DroppedByFault");
        traffic.Messages.Should().Contain(m => m.Side == "client" && m.Direction == "out" && m.MsgType == "2");
        traffic.Messages.Should().Contain(m => m.Side == "venue" && m.Disposition == "Resent" && m.PossDup);
        await Eventually(() => http.GetFromJsonAsync<List<OrderRowDto>>("/api/orders", ApiFactory.Json),
            r => r.Count == 1 && r[0].Order.Status == "Filled" && r[0].InSync);
    }

    [Fact]
    public async Task Guests_cannot_touch_other_guests()
    {
        var (alice, _) = await factory.NewGuestAsync();
        var (_, bob) = await factory.NewGuestAsync();

        (await alice.GetAsync($"/api/sessions/{bob.ClientCompId}/messages")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await alice.PostAsJsonAsync($"/api/clients/{bob.ClientId}/kill", new KillBody(true))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await factory.CreateClient().GetAsync("/api/orders")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Limits_update_applies_to_the_next_order()
    {
        var (http, guest) = await factory.NewGuestAsync();
        var call = await AtmCallAsync(http);

        var limits = new LimitsBody(100, 50_000, 0.5m, 0.25m, 25, ["AAPL"], ["Limit", "Market"], ["Day", "ImmediateOrCancel", "FillOrKill"], 10, true);
        (await http.PutAsJsonAsync($"/api/clients/{guest.ClientId}/limits", limits)).EnsureSuccessStatusCode();
        await ApiFactory.PlaceOrderAsync(http, new NewOrderBody(call.Id, "buy", "limit", "day", call.Ask, 1));

        await Eventually(() => http.GetFromJsonAsync<SessionTrafficDto>($"/api/sessions/{guest.ClientCompId}/messages", ApiFactory.Json),
            t => t.Messages.Any(m => m.Side == "client" && m.Raw.Contains("not enabled for this account", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Own_FIX_engine_connects_over_TCP_with_generated_credentials()
    {
        var (http, _) = await factory.NewGuestAsync();
        var call = await AtmCallAsync(http);
        var connect = (await (await http.PostAsync("/api/connect", null)).Content.ReadFromJsonAsync<ConnectInfo>(ApiFactory.Json))!;
        connect.Port.Should().Be(factory.FixPort);
        connect.Configs["quickfixn.cfg"].Should().Contain($"SenderCompID={connect.SenderCompID}");

        using var client = new QuickFixClient(factory.FixPort, connect.SenderCompID, connect.TargetCompID);
        client.Start();
        await Until(() => client.LoggedOn);

        var chain = await http.GetFromJsonAsync<ChainDto>("/api/chain/SPY", ApiFactory.Json);
        var order = QuickFixClient.Order("BYO-1", 2, call.Ask!.Value);
        order.Set(new QuickFix.Fields.StrikePrice(chain!.Rows[chain.Rows.Count / 2].Strike));
        order.Set(new QuickFix.Fields.MaturityDate(chain.Expiry.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture)));
        client.Send(order);

        await Until(() => client.AppMessages.Count(m => m.Header.GetString(35) == "8") >= 2);
        client.AppMessages.First().GetString(39).Should().Be("A"); // PendingNew first
        var rows = await Eventually(() => http.GetFromJsonAsync<List<OrderRowDto>>("/api/orders", ApiFactory.Json), r => r.Count == 1);
        rows[0].Order.ClOrdID.Should().Be("BYO-1");
    }

    [Fact]
    public async Task FIX_over_WebSocket_accepts_piped_text_frames()
    {
        var (http, _) = await factory.NewGuestAsync();
        var connect = (await (await http.PostAsync("/api/connect", null)).Content.ReadFromJsonAsync<ConnectInfo>(ApiFactory.Json))!;
        var ws = factory.Server.CreateWebSocketClient();
        using var socket = await ws.ConnectAsync(new Uri(factory.Server.BaseAddress, "/fix/ws"), CancellationToken.None);

        using var logon = new FixMessageBuilder(MsgTypes.Logon);
        logon.Set(Tags.EncryptMethod, 0).Set(Tags.HeartBtInt, 30).Set(Tags.ResetSeqNumFlag, true);
        var text = FixDisplay.ToPiped(logon.ToBytes(new FixHeader("FIX.4.4", connect.SenderCompID, connect.TargetCompID, 1, DateTime.UtcNow)));
        await socket.SendAsync(Encoding.ASCII.GetBytes(text), WebSocketMessageType.Text, true, CancellationToken.None);

        var buffer = new byte[4096];
        var result = await socket.ReceiveAsync(buffer, new CancellationTokenSource(5000).Token);
        var reply = Encoding.ASCII.GetString(buffer, 0, result.Count);
        result.MessageType.Should().Be(WebSocketMessageType.Text);
        reply.Should().StartWith("8=FIX.4.4|").And.Contain("|35=A|").And.Contain($"|56={connect.SenderCompID}|");
        FixMessage.Parse(reply).IsIntact.Should().BeTrue();
    }

    [Fact]
    public async Task Admin_endpoints_require_the_admin_key()
    {
        var anon = factory.CreateClient();
        (await anon.PostAsJsonAsync("/api/admin/kill", new KillBody(false))).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var admin = factory.CreateClient();
        admin.DefaultRequestHeaders.Add("X-Admin-Key", "test-admin");
        (await admin.PostAsync("/admin/reset", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await admin.GetAsync("/api/admin/audit")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Decode_endpoint_explains_a_message()
    {
        var http = factory.CreateClient();
        var result = await (await http.PostAsJsonAsync("/api/fix/decode",
            new DecodeBody("8=FIX.4.2|9=178|35=8|49=PHLX|56=PERS|52=20071123-05:30:00.000|11=ATOMNOCCC9990900|20=3|150=E|39=E|55=MSFT|167=CS|54=1|38=15|40=2|44=15|58=PHLX EQUITY TESTING|59=0|47=C|32=0|31=0|151=15|14=0|6=0|10=128|")))
            .Content.ReadFromJsonAsync<DecodeResult>(ApiFactory.Json);

        result!.MsgTypeName.Should().Be("ExecutionReport");
        result.Integrity.Should().Be("OK");
        result.Fields.Should().Contain(f => f.Tag == 39 && f.Meaning == "PendingReplace");
    }

    private static async Task Until(Func<bool> condition, int timeoutMs = 10_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Condition not met in time");
            }

            await Task.Delay(50);
        }
    }
}
