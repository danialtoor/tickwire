using System.IO.Pipelines;
using System.Net.Sockets;

namespace Tickwire.Fix.Session.Transport;

/// <summary>What happened to an outbound message at the transport. Faults can drop or alter a message.</summary>
public readonly record struct SendResult(bool Delivered, byte[] ActualBytes, string? FaultNote = null)
{
    public static SendResult Sent(byte[] bytes) => new(true, bytes);
}

/// <summary>A connection that carries whole FIX messages. TCP, WebSocket and in-memory pipes all implement this.</summary>
public interface IFixTransport : IAsyncDisposable
{
    string Description { get; }

    ValueTask<SendResult> SendAsync(byte[] message, CancellationToken cancellationToken);

    /// <summary>Next complete message, or null when the connection closed.</summary>
    ValueTask<byte[]?> ReceiveAsync(CancellationToken cancellationToken);

    /// <summary>Garbage bytes skipped by the framer since the last call.</summary>
    int TakeDiscardedByteCount();
}

/// <summary>A transport over a pair of pipes. Used directly for in-process sessions and as the base for TCP.</summary>
public class PipeTransport : IFixTransport
{
    private readonly PipeReader _reader;
    private readonly PipeWriter _writer;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly Func<ValueTask>? _onDispose;
    private int _discarded;
    private int _disposed;

    public PipeTransport(PipeReader reader, PipeWriter writer, string description, Func<ValueTask>? onDispose = null)
    {
        _reader = reader;
        _writer = writer;
        Description = description;
        _onDispose = onDispose;
    }

    public string Description { get; }

    /// <summary>Two connected in-memory transports, as if joined by a socket.</summary>
    public static (PipeTransport Left, PipeTransport Right) CreatePair(string leftName = "left", string rightName = "right")
    {
        var aToB = new Pipe();
        var bToA = new Pipe();
        var left = new PipeTransport(bToA.Reader, aToB.Writer, $"memory:{leftName}");
        var right = new PipeTransport(aToB.Reader, bToA.Writer, $"memory:{rightName}");
        return (left, right);
    }

    public virtual async ValueTask<SendResult> SendAsync(byte[] message, CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return new SendResult(false, message, "transport closed");
            }

            var result = await _writer.WriteAsync(message, cancellationToken).ConfigureAwait(false);
            return result.IsCompleted ? new SendResult(false, message, "peer closed") : SendResult.Sent(message);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or SocketException)
        {
            return new SendResult(false, message, ex.Message);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public virtual async ValueTask<byte[]?> ReceiveAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            ReadResult read;
            try
            {
                read = await _reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or SocketException)
            {
                return null;
            }

            var buffer = read.Buffer;
            var found = FixFramer.TryReadMessage(ref buffer, out var message, out var discarded);
            _discarded += discarded;
            if (found)
            {
                _reader.AdvanceTo(buffer.Start);
                return message;
            }

            _reader.AdvanceTo(buffer.Start, buffer.End);
            if (read.IsCompleted || read.IsCanceled)
            {
                return null;
            }
        }
    }

    public int TakeDiscardedByteCount() => Interlocked.Exchange(ref _discarded, 0);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await _writer.CompleteAsync().ConfigureAwait(false);
            _reader.CancelPendingRead();
            await _reader.CompleteAsync().ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }

        if (_onDispose is not null)
        {
            await _onDispose().ConfigureAwait(false);
        }

        GC.SuppressFinalize(this);
    }
}

public static class TcpTransport
{
    public static PipeTransport Create(Socket socket)
    {
        socket.NoDelay = true;
        var stream = new NetworkStream(socket, ownsSocket: true);
        var reader = PipeReader.Create(stream, new StreamPipeReaderOptions(leaveOpen: true));
        var writer = PipeWriter.Create(stream, new StreamPipeWriterOptions(leaveOpen: true));
        return new PipeTransport(reader, writer, $"tcp:{socket.RemoteEndPoint}", async () =>
        {
            try
            {
                socket.Shutdown(SocketShutdown.Both);
            }
            catch (SocketException)
            {
            }
            catch (ObjectDisposedException)
            {
            }

            await stream.DisposeAsync().ConfigureAwait(false);
        });
    }

    public static async Task<PipeTransport> ConnectAsync(string host, int port, CancellationToken cancellationToken)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        try
        {
            await socket.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
            return Create(socket);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
