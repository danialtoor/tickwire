using System.IO.Pipelines;
using System.Net.WebSockets;
using System.Text;
using Tickwire.Fix;
using Tickwire.Fix.Session.Transport;

namespace Tickwire.Api.Services;

/// <summary>
/// FIX over WebSocket, for clients that can't open raw TCP (browsers, some serverless runtimes).
/// Each frame may hold one or more messages. Text frames may use '|' instead of SOH; replies use the same style
/// the client used, so a human can drive it from a WebSocket console.
/// </summary>
public sealed class WebSocketFixTransport : IFixTransport
{
    private readonly WebSocket _socket;
    private readonly Pipe _inbound = new();
    private readonly PipeTransport _framer;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly Task _pump;
    private volatile bool _piped;

    public WebSocketFixTransport(WebSocket socket, string remote)
    {
        _socket = socket;
        Description = $"websocket:{remote}";
        _framer = new PipeTransport(_inbound.Reader, new Pipe().Writer, Description);
        _pump = Task.Run(PumpAsync);
    }

    public string Description { get; }

    /// <summary>Completes when the socket closes.</summary>
    public Task Completion => _pump;

    public async ValueTask<SendResult> SendAsync(byte[] message, CancellationToken cancellationToken)
    {
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_socket.State != WebSocketState.Open)
            {
                return new SendResult(false, message, "websocket closed");
            }

            var payload = _piped ? Encoding.ASCII.GetBytes(FixDisplay.ToPiped(message)) : message;
            await _socket.SendAsync(payload, _piped ? WebSocketMessageType.Text : WebSocketMessageType.Binary, true, cancellationToken)
                .ConfigureAwait(false);
            return SendResult.Sent(message);
        }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException)
        {
            return new SendResult(false, message, ex.Message);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public ValueTask<byte[]?> ReceiveAsync(CancellationToken cancellationToken) => _framer.ReceiveAsync(cancellationToken);

    public int TakeDiscardedByteCount() => _framer.TakeDiscardedByteCount();

    private async Task PumpAsync()
    {
        var buffer = new byte[16 * 1024];
        try
        {
            while (_socket.State == WebSocketState.Open)
            {
                using var frame = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await _socket.ReceiveAsync(buffer, CancellationToken.None).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        return;
                    }

                    frame.Write(buffer, 0, result.Count);
                    if (frame.Length > FixFramer.MaxMessageSize)
                    {
                        return;
                    }
                }
                while (!result.EndOfMessage);

                var bytes = frame.ToArray();
                if (Array.IndexOf(bytes, FixParser.Soh) < 0)
                {
                    _piped = true;
                    bytes = FixDisplay.FromPiped(Encoding.ASCII.GetString(bytes));
                    if (bytes.Length > 0 && bytes[^1] != FixParser.Soh)
                    {
                        bytes = [.. bytes, FixParser.Soh];
                    }
                }

                await _inbound.Writer.WriteAsync(bytes).ConfigureAwait(false);
            }
        }
        catch (WebSocketException)
        {
        }
        finally
        {
            await _inbound.Writer.CompleteAsync().ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_socket.State == WebSocketState.Open)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", cts.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
        }

        await _framer.DisposeAsync().ConfigureAwait(false);
        _sendLock.Dispose();
    }
}
