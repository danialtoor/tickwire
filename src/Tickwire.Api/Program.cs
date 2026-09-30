using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using MySqlConnector;
using Tickwire.Api.Endpoints;
using Tickwire.Api.Hubs;
using Tickwire.Api.Services;
using Tickwire.Engine;
using Tickwire.Fix.Session.Store;
using Tickwire.MarketData;
using Tickwire.MarketData.Providers;
using Tickwire.Persistence;
using Tickwire.Venue;

var builder = WebApplication.CreateBuilder(args);
var services = builder.Services;

services.Configure<TickwireOptions>(builder.Configuration.GetSection("Tickwire"));
services.AddSingleton(TimeProvider.System);

// ---- persistence: MySQL when a connection string is configured, otherwise in-memory
var connectionString = builder.Configuration.GetConnectionString("Default");
if (!string.IsNullOrWhiteSpace(connectionString))
{
    services.AddDbContextFactory<TickwireDbContext>(o => o.UseMySql(connectionString, MySqlServer.Version,
        my => my.EnableRetryOnFailure(3)));
    services.AddSingleton<IClientRepository, EfClientRepository>();
    services.AddSingleton<ISessionStoreBackend>(_ => new MySqlSessionStoreBackend(connectionString));
    services.AddSingleton(sp => new MySqlJournal(connectionString, sp.GetRequiredService<ILogger<MySqlJournal>>()));
    services.AddSingleton(new PersistenceMode("MySQL"));
    services.AddHealthChecks().AddAsyncCheck("mysql", async ct =>
    {
        try
        {
            await using var conn = new MySqlConnection(connectionString);
            await conn.OpenAsync(ct);
            return HealthCheckResult.Healthy();
        }
        catch (MySqlException ex)
        {
            return HealthCheckResult.Unhealthy("MySQL unreachable", ex);
        }
    });
}
else
{
    services.AddSingleton<IClientRepository, InMemoryClientRepository>();
    services.AddSingleton(new PersistenceMode("InMemory"));
    services.AddHealthChecks();
}

// ---- trading core
services.AddSingleton(sp => InstrumentRegistry.Build(DateOnly.FromDateTime(sp.GetRequiredService<TimeProvider>().GetUtcNow().UtcDateTime)));
services.AddSingleton<MarketDataCache>();
services.AddSingleton(new VenueOptions());
services.AddSingleton(sp => new SimulatedVenue(sp.GetRequiredService<InstrumentRegistry>(), sp.GetRequiredService<VenueOptions>(),
    sp.GetRequiredService<MarketDataCache>(), sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ILoggerFactory>()));
services.AddSingleton(sp =>
{
    // Order numbers and ExecIDs must not repeat across restarts (orders are persisted, ExecIDs must be unique).
    var now = sp.GetRequiredService<TimeProvider>().GetUtcNow();
    var firstOrderId = (now.ToUnixTimeSeconds() - 1_735_689_600) * 1000; // seconds since 2025-01-01, x1000
    var bootTag = (now.ToUnixTimeSeconds() % 1_000_000).ToString("x5", System.Globalization.CultureInfo.InvariantCulture);
    return new OrderManager(new SimulatedVenueAdapter(sp.GetRequiredService<SimulatedVenue>()), sp.GetRequiredService<MarketDataCache>(),
        sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ILogger<OrderManager>>(), firstOrderId, bootTag);
});
services.AddSingleton(sp => new EngineMetrics(sp.GetRequiredService<TimeProvider>()));
services.AddSingleton<FixOrderGateway>();
services.AddSingleton(sp => new PositionKeeper(sp.GetRequiredService<MarketDataCache>(), sp.GetRequiredService<TimeProvider>()));
services.AddSingleton<LiveBus>();
services.AddSingleton(sp => new WireTap(sp.GetRequiredService<LiveBus>(), sp.GetRequiredService<EngineMetrics>(),
    sp.GetService<MySqlJournal>()));
services.AddSingleton(sp => new SessionManager(sp.GetRequiredService<IClientRepository>(), sp.GetRequiredService<FixOrderGateway>(),
    sp.GetRequiredService<OrderManager>(), sp.GetRequiredService<WireTap>(), sp.GetRequiredService<IOptions<TickwireOptions>>(),
    sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ILoggerFactory>(), sp.GetService<ISessionStoreBackend>()));
services.AddSingleton<MarketView>();
services.AddSingleton<BookSubscriptions>();
services.AddSingleton<OpsSnapshotter>();
services.AddSingleton<LiveOrderPublisher>();
services.AddSingleton<Housekeeping>();

// ---- external options market data (each visitor connects their own account)
services.AddHttpClient();
services.AddSingleton<IOptionFeedProvider, SpiderRockProvider>();
services.AddSingleton<IOptionFeedProvider, DatabentoProvider>();
services.AddSingleton<IOptionFeedProvider, PolygonProvider>();
services.AddSingleton<IOptionFeedProvider>(sp => new TradierProvider(sp.GetRequiredService<IHttpClientFactory>().CreateClient("tradier")));
services.AddSingleton<IOptionFeedProvider, AlpacaProvider>();
services.AddSingleton<IOptionFeedProvider>(sp =>
{
    var instruments = sp.GetRequiredService<InstrumentRegistry>();
    var cache = sp.GetRequiredService<MarketDataCache>();
    return new DemoProvider(occ => instruments.Find(occ) is { } c && cache.Quote(c.Id) is { } q
        ? ((decimal)q.Theo, q.ImpliedVol, q.Delta)
        : null);
});
services.AddSingleton<FeedManager>();
services.AddHostedService<EngineHost>();
services.AddHostedService<LivePublisher>();
services.AddSingleton(sp => new ExpiryRoller(sp.GetRequiredService<InstrumentRegistry>(), sp.GetRequiredService<SimulatedVenue>(),
    sp.GetRequiredService<MarketDataCache>()));
services.AddHostedService<ChainRollService>();
services.AddHostedService(sp => sp.GetRequiredService<Housekeeping>());

// ---- web
services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.Never;
});
services.AddSignalR().AddJsonProtocol(o => o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
services.AddProblemDetails();
services.AddOpenApi(o => o.AddDocumentTransformer((doc, _, _) =>
{
    doc.Info.Title = "Tickwire API";
    doc.Info.Description = "Options execution gateway on a simulated market. Not affiliated with any trading firm; simulated markets only.";
    return Task.CompletedTask;
}));
services.AddCors(o => o.AddDefaultPolicy(p => p
    .SetIsOriginAllowed(origin => builder.Configuration.GetSection("Tickwire").Get<TickwireOptions>()?.IsOriginAllowed(origin) ?? false)
    .AllowAnyHeader()
    .AllowAnyMethod()
    .AllowCredentials()));
services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    static string ClientIp(HttpContext http) =>
        http.Request.Headers["Fly-Client-IP"].FirstOrDefault() ?? http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    // Operators (valid X-Admin-Key) aren't rate limited, so load tests can open many sessions.
    static bool IsAdmin(HttpContext http)
    {
        var key = http.RequestServices.GetRequiredService<IOptions<TickwireOptions>>().Value.AdminKey;
        return !string.IsNullOrEmpty(key) && http.Request.Headers["X-Admin-Key"] == key;
    }

    o.AddPolicy("guest", http => IsAdmin(http) ? RateLimitPartition.GetNoLimiter("admin") : RateLimitPartition.GetFixedWindowLimiter(ClientIp(http),
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = http.RequestServices.GetRequiredService<IOptions<TickwireOptions>>().Value.GuestsPerMinute,
            Window = TimeSpan.FromMinutes(1),
        }));
    o.AddPolicy("orders", http => IsAdmin(http) ? RateLimitPartition.GetNoLimiter("admin") : RateLimitPartition.GetTokenBucketLimiter(ClientIp(http),
        _ => new TokenBucketRateLimiterOptions
        {
            TokenLimit = 40,
            TokensPerPeriod = 20,
            ReplenishmentPeriod = TimeSpan.FromSeconds(1),
        }));
    o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(http => IsAdmin(http) ? RateLimitPartition.GetNoLimiter("admin") : RateLimitPartition.GetFixedWindowLimiter(ClientIp(http),
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 600, Window = TimeSpan.FromMinutes(1) }));
});
services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.KnownIPNetworks.Clear();
    o.KnownProxies.Clear();
});

var app = builder.Build();

app.UseForwardedHeaders();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseCors();
app.UseRateLimiter();
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });

app.MapOpenApi();
app.UseSwaggerUI(o =>
{
    o.SwaggerEndpoint("/openapi/v1.json", "Tickwire API v1");
    o.RoutePrefix = "swagger";
    o.DocumentTitle = "Tickwire API";
});

app.MapGet("/", () => Results.Redirect("/swagger")).ExcludeFromDescription();
app.MapHealthChecks("/health");
app.MapHealthChecks("/api/health");
app.MapHub<LiveHub>("/hubs/live");
app.MapTradingEndpoints();
app.MapSessionEndpoints();
app.MapAdminEndpoints();
app.MapAnalyzerEndpoints();
app.MapFeedEndpoints();

// FIX over WebSocket: same acceptor, same sessions, same validation as TCP.
app.Map("/fix/ws", async (HttpContext http, SessionManager sessions) =>
{
    if (!http.WebSockets.IsWebSocketRequest)
    {
        return Results.BadRequest("WebSocket upgrade required");
    }

    var socket = await http.WebSockets.AcceptWebSocketAsync();
    var transport = new WebSocketFixTransport(socket, http.Connection.RemoteIpAddress?.ToString() ?? "?");
    await sessions.Acceptor.AcceptAsync(transport, http.RequestAborted);
    await transport.Completion;
    return Results.Empty;
}).ExcludeFromDescription();

app.Run();

public partial class Program;
