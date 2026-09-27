using System.Buffers;
using System.Text;
using FluentAssertions;

namespace Tickwire.Fix.Tests;

public class FramerTests
{
    private static List<byte[]> ReadAll(ReadOnlySequence<byte> seq, out int discarded)
    {
        var messages = new List<byte[]>();
        discarded = 0;
        while (FixFramer.TryReadMessage(ref seq, out var msg, out var d))
        {
            discarded += d;
            messages.Add(msg);
        }

        return messages;
    }

    [Fact]
    public void Splits_back_to_back_messages()
    {
        var a = TestMessages.NewOrderSingle(1, "A");
        var b = TestMessages.NewOrderSingle(2, "B");

        var messages = ReadAll(new ReadOnlySequence<byte>([.. a, .. b]), out var discarded);

        messages.Should().HaveCount(2);
        messages[0].Should().Equal(a);
        messages[1].Should().Equal(b);
        discarded.Should().Be(0);
    }

    [Fact]
    public void Waits_for_rest_of_partial_message()
    {
        var a = TestMessages.NewOrderSingle();

        for (var cut = 1; cut < a.Length; cut++)
        {
            FixFramer.Scan(a.AsSpan(0, cut)).Status.Should().Be(FrameStatus.NeedMoreData, $"cut at {cut}");
        }

        FixFramer.Scan(a).Should().Be(new FrameScan(FrameStatus.Frame, 0, a.Length, a.Length));
    }

    [Fact]
    public void Skips_garbage_before_a_message()
    {
        var a = TestMessages.NewOrderSingle();
        var input = Encoding.ASCII.GetBytes("junk\r\n").Concat(a).ToArray();

        var messages = ReadAll(new ReadOnlySequence<byte>(input), out var discarded);

        messages.Should().ContainSingle().Which.Should().Equal(a);
        discarded.Should().Be(6);
    }

    [Fact]
    public void Recovers_when_body_length_is_too_long()
    {
        var bad = Encoding.ASCII.GetBytes(Encoding.ASCII.GetString(TestMessages.NewOrderSingle(1, "A"))
            .Replace("\u00019=", "\u00019=9", StringComparison.Ordinal));
        var good = TestMessages.NewOrderSingle(2, "B");

        var messages = ReadAll(new ReadOnlySequence<byte>([.. bad, .. good]), out _);

        messages.Should().HaveCount(2);
        FixMessage.Parse(messages[0]).Integrity.Should().HaveFlag(FixIntegrity.BodyLengthMismatch);
        messages[1].Should().Equal(good);
    }

    [Fact]
    public void Recovers_when_body_length_is_too_short()
    {
        var text = Encoding.ASCII.GetString(TestMessages.NewOrderSingle(1, "A"));
        var declared = text.Split('\u0001')[1];
        var bad = Encoding.ASCII.GetBytes(text.Replace(declared, "9=20", StringComparison.Ordinal));
        var good = TestMessages.NewOrderSingle(2, "B");

        var messages = ReadAll(new ReadOnlySequence<byte>([.. bad, .. good]), out _);

        messages.Should().HaveCount(2);
        messages[1].Should().Equal(good);
    }

    [Fact]
    public void Drops_truncated_message_followed_by_a_new_one()
    {
        var truncated = TestMessages.NewOrderSingle(1, "A")[..40];
        var good = TestMessages.NewOrderSingle(2, "B");

        var messages = ReadAll(new ReadOnlySequence<byte>([.. truncated, .. good]), out var discarded);

        messages.Should().ContainSingle().Which.Should().Equal(good);
        discarded.Should().Be(40);
    }

    [Fact]
    public void Handles_multi_segment_sequences()
    {
        var a = TestMessages.NewOrderSingle(1, "A");
        var b = TestMessages.NewOrderSingle(2, "B");
        var all = a.Concat(b).ToArray();
        var seq = SequenceFromChunks(all, 7);

        ReadAll(seq, out _).Should().HaveCount(2);
    }

    internal static ReadOnlySequence<byte> SequenceFromChunks(byte[] data, int chunk)
    {
        Segment? first = null;
        Segment? last = null;
        for (var i = 0; i < data.Length; i += chunk)
        {
            var mem = data.AsMemory(i, Math.Min(chunk, data.Length - i));
            if (first is null)
            {
                first = last = new Segment(mem, 0);
            }
            else
            {
                last = last!.Append(mem);
            }
        }

        return new ReadOnlySequence<byte>(first!, 0, last!, last!.Memory.Length);
    }

    private sealed class Segment : ReadOnlySequenceSegment<byte>
    {
        public Segment(ReadOnlyMemory<byte> memory, long runningIndex)
        {
            Memory = memory;
            RunningIndex = runningIndex;
        }

        public Segment Append(ReadOnlyMemory<byte> memory)
        {
            var next = new Segment(memory, RunningIndex + Memory.Length);
            Next = next;
            return next;
        }
    }
}
