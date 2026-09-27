using System.Net.Http.Json;
using FluentAssertions;
using MySqlConnector;
using Testcontainers.MySql;
using Tickwire.Api.Endpoints;
using Tickwire.Api.Services;

namespace Tickwire.Integration.Tests;

public sealed class MySqlApiFactory(string connectionString) : ApiFactory(connectionString);

/// <summary>
/// Real MySQL in a container (skipped when Docker isn't available). Verifies migrations, the Dapper session store,
/// the order journal, and that a restarted server continues sequence numbers instead of starting at 1.
/// </summary>
public sealed class MySqlPersistenceTests : IAsyncLifetime
{
    private MySqlContainer? _mysql;
    private string? _connectionString;

    public async Task InitializeAsync()
    {
        // Without Docker, point TICKWIRE_TEST_MYSQL at any MySQL server; a fresh database is created per run.
        if (Environment.GetEnvironmentVariable("TICKWIRE_TEST_MYSQL") is { Length: > 0 } server)
        {
            var db = $"tickwire_test_{Guid.NewGuid():N}"[..28];
            await using var conn = new MySqlConnection(server);
            await conn.OpenAsync();
            await new MySqlCommand($"CREATE DATABASE `{db}`", conn).ExecuteNonQueryAsync();
            _connectionString = new MySqlConnectionStringBuilder(server) { Database = db }.ConnectionString;
            return;
        }

        try
        {
            _mysql = new MySqlBuilder("mysql:8.4").WithDatabase("tickwire").Build();
            await _mysql.StartAsync();
            _connectionString = _mysql.GetConnectionString() + ";AllowPublicKeyRetrieval=True";
        }
        catch (Exception)
        {
            // No Docker daemon (e.g. a laptop without Docker Desktop): the test reports itself as skipped.
            _mysql = null;
        }
    }

    public async Task DisposeAsync()
    {
        if (_mysql is not null)
        {
            await _mysql.DisposeAsync();
        }
    }

    [SkippableFact]
    public async Task Sessions_orders_and_sequence_numbers_survive_a_restart()
    {
        Skip.If(_connectionString is null, "Docker is not available and TICKWIRE_TEST_MYSQL is not set");
        var cs = _connectionString!;
        GuestProvisioned guest;
        int venueOutSeq, venueInSeq;

        await using (var first = new MySqlApiFactory(cs))
        {
            var (http, g) = await first.NewGuestAsync();
            guest = g;
            var chain = await Until(() => http.GetFromJsonAsync<ChainDto>("/api/chain/SPY", ApiFactory.Json),
                c => c.Rows.Count > 0 && c.Rows[c.Rows.Count / 2].Call!.Ask is not null);
            var call = chain.Rows[chain.Rows.Count / 2].Call!;
            await http.PostAsJsonAsync("/api/orders", new NewOrderBody(call.Id, "buy", "limit", "day", call.Ask, 2));
            await Until(() => http.GetFromJsonAsync<List<OrderRowDto>>("/api/orders", ApiFactory.Json), r => r.Count == 1 && r[0].InSync);
            var session = await Until(() => http.GetFromJsonAsync<List<SessionSummaryDto>>("/api/sessions", ApiFactory.Json),
                s => s.Any(x => x.ClientCompId == g.ClientCompId));
            var mine = session.Single(x => x.ClientCompId == g.ClientCompId);
            (venueOutSeq, venueInSeq) = (mine.NextSenderSeqNum, mine.NextTargetSeqNum);
            await Task.Delay(500); // let the write-behind store flush
        }

        await using (var db = new MySqlConnection(cs))
        {
            await db.OpenAsync();
            await using var cmd = new MySqlCommand("SELECT COUNT(*) FROM executions WHERE ClientId = @c", db);
            cmd.Parameters.AddWithValue("c", guest.ClientId);
            Convert.ToInt32(await cmd.ExecuteScalarAsync()).Should().BeGreaterThan(0, "the order journal writes executions");
        }

        await using var second = new MySqlApiFactory(cs);
        var client = second.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", guest.Token);
        (await client.GetAsync("/api/me")).EnsureSuccessStatusCode(); // restarts the guest's FIX client
        var after = await Until(() => client.GetFromJsonAsync<List<SessionSummaryDto>>("/api/sessions", ApiFactory.Json),
            s => s.Any(x => x.ClientCompId == guest.ClientCompId && x.State == "Active"));
        var resumed = after.Single(x => x.ClientCompId == guest.ClientCompId);
        resumed.NextSenderSeqNum.Should().BeGreaterThan(venueOutSeq, "the venue continues from its stored outbound sequence");
        resumed.NextTargetSeqNum.Should().BeGreaterThan(venueInSeq, "and from the stored inbound sequence, without a reset");
    }

    private static async Task<T> Until<T>(Func<Task<T?>> probe, Func<T, bool> done, int timeoutMs = 20_000)
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
                throw new TimeoutException();
            }

            await Task.Delay(200);
        }
    }
}
