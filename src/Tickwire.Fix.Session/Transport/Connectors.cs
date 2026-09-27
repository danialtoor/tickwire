using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Tickwire.Fix.Session.Transport;

public readonly record struct SessionResolution(FixSession? Session, string? RejectText, bool EnableChaos = false)
{
    public static SessionResolution Reject(string text) => new(null, text);
}

/// <summary>Maps an incoming Logon to a configured session (by CompIDs), or explains why it can't.</summary>
public interface ISessionResolver
{
    ValueTask<SessionResolution> ResolveAsync(FixMessage logon, string remote, CancellationToken cancellationToken);
}

/// <summary>
/// Accepts connections, reads the first message, and hands the connection to the matching session.
/// TCP connections come from <see cref="StartTcpAsync"/>; WebSocket and in-memory connections call <see cref="AcceptAsync"/>.
/// </summary>
public sealed partial class FixAcceptor : IAsyncDisposable
{
    private readonly ISessionResolver _resolver;
    private readonly ILogger _logger;
    private readonly TimeSpan _logonTimeout;
    private readonly CancellationTokenSource _stop = new();
    private Socket? _listener;
    private Task? _acceptLoop;

    public FixAcceptor(ISessionResolver resolver, ILogger? logger = null, TimeSpan? logonTimeout = null)
    {
        _resolver = resolver;
        _logger = logger ?? NullLogger.Instance;
        _logonTimeout = logonTimeout ?? TimeSpan.FromSeconds(10);
    }

    public int Port { get; private set; }

    /// <summary>Raised when a connection is refused before reaching a session (unknown CompIDs, not a Logon, ...).</summary>
    public event Action<string, string, byte[]?>? ConnectionRefused;

    public Task StartTcpAsync(IPEndPoint endpoint)
    {
        var listener = new Socket(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        if (endpoint.AddressFamily == AddressFamily.InterNetworkV6)
        {
            listener.DualMode = true;
        }

        listener.Bind(endpoint);
        listener.Listen(128);
        _listener = listener;
        Port = ((IPEndPoint)listener.LocalEndPoint!).Port;
        Listening(_logger, Port);
        _acceptLoop = Task.Run(AcceptLoopAsync);
        return Task.CompletedTask;
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            Socket socket;
            try
            {
                socket = await _listener!.AcceptAsync(_stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (SocketException ex)
            {
                AcceptFailed(_logger, ex);
                continue;
            }

            _ = AcceptAsync(TcpTransport.Create(socket), _stop.Token);
        }
    }

    public async Task AcceptAsync(IFixTransport transport, CancellationToken cancellationToken)
    {
        byte[]? first;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_logonTimeout);
            first = await transport.ReceiveAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await RefuseAsync(transport, null, "No Logon received within timeout", null).ConfigureAwait(false);
            return;
        }

        if (first is null)
        {
            await transport.DisposeAsync().ConfigureAwait(false);
            return;
        }

        if (!FixParser.TryParse(first, out var msg, out var error) || !msg.IsIntact)
        {
            await RefuseAsync(transport, null, $"First message is garbled ({(msg is null ? error.ToString() : msg.Integrity.ToString())})", first)
                .ConfigureAwait(false);
            return;
        }

        if (msg.MsgType != MsgTypes.Logon)
        {
            await RefuseAsync(transport, msg, $"First message must be Logon (35=A), got 35={msg.MsgType}", first).ConfigureAwait(false);
            return;
        }

        SessionResolution resolution;
        try
        {
            resolution = await _resolver.ResolveAsync(msg, transport.Description, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ResolveFailed(_logger, ex);
            resolution = SessionResolution.Reject("Internal error");
        }

        if (resolution.Session is null)
        {
            await RefuseAsync(transport, msg, resolution.RejectText ?? "Unknown session", first).ConfigureAwait(false);
            return;
        }

        if (resolution.Session.State != SessionState.Disconnected)
        {
            await RefuseAsync(transport, msg, "Session is already logged on from another connection", first).ConfigureAwait(false);
            return;
        }

        resolution.Session.Attach(transport, first, resolution.EnableChaos);
    }

    /// <summary>
    /// Sends a best-effort Logout with the reason before closing, so the client sees why (a silent disconnect is the most
    /// common support ticket for FIX onboarding). It's outside any session, so it uses MsgSeqNum 1.
    /// </summary>
    private async Task RefuseAsync(IFixTransport transport, FixMessage? logon, string reason, byte[]? raw)
    {
        Refused(_logger, transport.Description, reason);
        ConnectionRefused?.Invoke(transport.Description, reason, raw);
        if (logon is not null)
        {
            using var logout = new FixMessageBuilder(MsgTypes.Logout);
            logout.Set(Tags.Text, reason);
            var header = new FixHeader(logon.BeginString, logon.TargetCompID, logon.SenderCompID, 1, DateTime.UtcNow);
            await transport.SendAsync(logout.ToBytes(header), CancellationToken.None).ConfigureAwait(false);
        }

        await transport.DisposeAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        _listener?.Dispose();
        if (_acceptLoop is not null)
        {
            await _acceptLoop.ConfigureAwait(false);
        }

        _stop.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "FIX acceptor listening on port {Port}")]
    private static partial void Listening(ILogger logger, int port);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Accept failed")]
    private static partial void AcceptFailed(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Error, Message = "Session resolver failed")]
    private static partial void ResolveFailed(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Refused connection from {Remote}: {Reason}")]
    private static partial void Refused(ILogger logger, string remote, string reason);
}

/// <summary>Keeps an initiator session connected: connects, attaches, and reconnects after a disconnect.</summary>
public sealed class FixInitiator : IAsyncDisposable
{
    private readonly FixSession _session;
    private readonly Func<CancellationToken, Task<IFixTransport>> _connect;
    private readonly TimeSpan _reconnectDelay;
    private readonly TimeProvider _time;
    private readonly CancellationTokenSource _stop = new();
    private TaskCompletionSource _disconnected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _loop;

    public FixInitiator(FixSession session, Func<CancellationToken, Task<IFixTransport>> connect, TimeSpan reconnectDelay,
        TimeProvider? time = null)
    {
        _session = session;
        _connect = connect;
        _reconnectDelay = reconnectDelay;
        _time = time ?? TimeProvider.System;
        _session.Disconnected += (_, _) => _disconnected.TrySetResult();
    }

    public bool EnableChaos { get; init; }

    /// <summary>When false, the initiator stays disconnected after the next disconnect until re-enabled.</summary>
    public bool AutoReconnect { get; set; } = true;

    public int ConnectAttempts { get; private set; }

    public void Start() => _loop ??= Task.Run(RunAsync);

    private async Task RunAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            if (!AutoReconnect)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), _time, _stop.Token).ConfigureAwait(false);
                continue;
            }

            IFixTransport transport;
            try
            {
                ConnectAttempts++;
                transport = await _connect(_stop.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is SocketException or IOException or TimeoutException)
            {
                await Task.Delay(_reconnectDelay, _time, _stop.Token).ConfigureAwait(false);
                continue;
            }
            catch (OperationCanceledException)
            {
                return;
            }

            _disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _session.Attach(transport, null, EnableChaos);
            try
            {
                await _disconnected.Task.WaitAsync(_stop.Token).ConfigureAwait(false);
                await Task.Delay(_reconnectDelay, _time, _stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        if (_loop is not null)
        {
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _stop.Dispose();
    }
}
