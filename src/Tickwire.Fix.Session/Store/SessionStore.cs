namespace Tickwire.Fix.Session.Store;

/// <summary>
/// Sequence numbers and sent messages for one session. Called only from the session loop, so implementations don't
/// need to be thread-safe with respect to the session itself.
/// </summary>
public interface ISessionStore
{
    int NextSenderSeqNum { get; set; }
    int NextTargetSeqNum { get; set; }
    DateTime CreationTime { get; }

    void StoreOutbound(int seqNum, byte[] message);

    /// <summary>Stored messages with sequence numbers in [begin, end], in order. Missing numbers are skipped.</summary>
    IReadOnlyList<(int SeqNum, byte[] Message)> GetOutbound(int begin, int end);

    void Reset(DateTime creationTime);
}

public sealed class MemorySessionStore : ISessionStore
{
    private readonly SortedDictionary<int, byte[]> _messages = [];
    private readonly int _maxMessages;

    public MemorySessionStore(DateTime creationTime, int maxMessages = 20_000)
    {
        CreationTime = creationTime;
        _maxMessages = maxMessages;
    }

    public int NextSenderSeqNum { get; set; } = 1;
    public int NextTargetSeqNum { get; set; } = 1;
    public DateTime CreationTime { get; private set; }

    public int Count => _messages.Count;

    public void StoreOutbound(int seqNum, byte[] message)
    {
        _messages[seqNum] = message;
        while (_messages.Count > _maxMessages)
        {
            // Oldest messages fall out; a resend request for them is answered with a gap fill.
            _messages.Remove(_messages.Keys.First());
        }
    }

    public IReadOnlyList<(int SeqNum, byte[] Message)> GetOutbound(int begin, int end)
    {
        var result = new List<(int, byte[])>();
        foreach (var (seq, msg) in _messages)
        {
            if (seq > end)
            {
                break;
            }

            if (seq >= begin)
            {
                result.Add((seq, msg));
            }
        }

        return result;
    }

    public void Reset(DateTime creationTime)
    {
        _messages.Clear();
        NextSenderSeqNum = 1;
        NextTargetSeqNum = 1;
        CreationTime = creationTime;
    }
}
