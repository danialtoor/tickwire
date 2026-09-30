using Tickwire.Api.Endpoints;
using Tickwire.Persistence;

namespace Tickwire.Api.Services;

/// <summary>Generates ready-to-run configuration for common FIX engines, the way a venue's onboarding team would.</summary>
public static class ClientConfigs
{
    public static ConnectInfo Build(string clientId, SessionConfig s, TickwireOptions options)
    {
        var host = options.PublicFixHost;
        var port = options.FixPort;
        var quickfixn = $"""
            # QuickFIX/n initiator config for Tickwire (simulated venue, no real money)
            [DEFAULT]
            ConnectionType=initiator
            ReconnectInterval=5
            FileStorePath=store
            FileLogPath=log
            StartTime=00:00:00
            EndTime=00:00:00
            UseDataDictionary=Y
            DataDictionary=FIX44.xml
            ValidateUserDefinedFields=N
            ResetOnLogon=Y

            [SESSION]
            BeginString={s.BeginString}
            SenderCompID={s.ClientCompId}
            TargetCompID={s.VenueCompId}
            SocketConnectHost={host}
            SocketConnectPort={port}
            HeartBtInt={s.HeartBtInt}
            """;
        var quickfixj = $"""
            # QuickFIX/J initiator config for Tickwire (simulated venue, no real money)
            [default]
            ConnectionType=initiator
            ReconnectInterval=5
            FileStorePath=target/data/store
            FileLogPath=target/data/log
            StartTime=00:00:00
            EndTime=00:00:00
            UseDataDictionary=Y
            DataDictionary=FIX44.xml
            ValidateUserDefinedFields=N
            ResetOnLogon=Y

            [session]
            BeginString={s.BeginString}
            SenderCompID={s.ClientCompId}
            TargetCompID={s.VenueCompId}
            SocketConnectHost={host}
            SocketConnectPort={port}
            HeartBtInt={s.HeartBtInt}
            """;
        var python = $"""
            # pip install simplefix
            # Full example: clients/python/example.py in the Tickwire repo
            python clients/python/example.py --host {host} --port {port} --sender {s.ClientCompId} --target {s.VenueCompId}
            """;
        var raw = $"""
            # Logon you should send (| = SOH):
            8={s.BeginString}|9=..|35=A|49={s.ClientCompId}|56={s.VenueCompId}|34=1|52=<UTC now>|98=0|108={s.HeartBtInt}|141=Y|10=..|

            # Then a limit order for 1 SPY call (use a strike/expiry from GET /api/chain/SPY):
            35=D|11=my-order-1|55=SPY|167=OPT|201=1|202=<strike>|541=<yyyymmdd>|54=1|60=<UTC now>|38=1|40=2|44=<price>|59=0
            """;
        if (s.IsDropCopy)
        {
            raw = $"""
                # Drop copy session: log on as below, then just listen. Every ExecutionReport for {clientId}'s orders
                # arrives here too, with CopyMsgIndicator(797)=Y. Orders sent on this session are rejected (35=j).
                8={s.BeginString}|9=..|35=A|49={s.ClientCompId}|56={s.VenueCompId}|34=1|52=<UTC now>|98=0|108={s.HeartBtInt}|141=Y|10=..|
                """;
        }

        return new ConnectInfo(clientId, s.ClientCompId, s.VenueCompId, host, port, s.HeartBtInt,
            new Dictionary<string, string>
            {
                ["quickfixn.cfg"] = quickfixn,
                ["quickfixj.cfg"] = quickfixj,
                ["python.sh"] = python,
                ["raw-fix.txt"] = raw,
            })
        {
            Role = s.Role,
            BeginString = s.BeginString,
        };
    }
}
