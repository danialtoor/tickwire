using System.Buffers;
using System.Text;
using FluentAssertions;

namespace Tickwire.Fix.Tests;

public class BuilderTests
{
    [Fact]
    public void Writes_standard_header_in_required_order()
    {
        var bytes = TestMessages.NewOrderSingle(seq: 7);
        var msg = FixMessage.Parse(bytes);

        msg.IsIntact.Should().BeTrue();
        msg.Fields[0].Tag.Should().Be(Tags.BeginString);
        msg.Fields[1].Tag.Should().Be(Tags.BodyLength);
        msg.Fields[2].Tag.Should().Be(Tags.MsgType);
        msg.Fields[^1].Tag.Should().Be(Tags.CheckSum);
        msg.MsgSeqNum.Should().Be(7);
        msg.GetString(Tags.SendingTime).Should().Be("20250314-13:30:00.123");
        msg.GetString(Tags.MaturityDate).Should().Be("20250620");
        msg.GetDecimal(Tags.Price).Should().Be(4.25m);
    }

    [Fact]
    public void Body_length_and_checksum_match_an_independent_calculation()
    {
        var text = Encoding.ASCII.GetString(TestMessages.NewOrderSingle());
        var bodyStart = text.IndexOf("\u000135=", StringComparison.Ordinal) + 1;
        var checksumStart = text.IndexOf("\u000110=", StringComparison.Ordinal) + 1;
        var declaredLength = int.Parse(text.Split('\u0001')[1][2..], System.Globalization.CultureInfo.InvariantCulture);

        declaredLength.Should().Be(checksumStart - bodyStart);
        var sum = text[..checksumStart].Sum(c => c) % 256;
        text[(checksumStart + 3)..(checksumStart + 6)].Should().Be(sum.ToString("000", System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Resend_header_carries_PossDupFlag_and_OrigSendingTime()
    {
        var original = FixMessage.Parse(TestMessages.NewOrderSingle(seq: 3));
        using var b = FixMessageBuilder.FromBody(original);
        var resendTime = TestMessages.SendingTime.AddSeconds(5);

        var resent = b.ToMessage(new FixHeader("FIX.4.4", "CLIENT", "TICKWIRE", 3, resendTime, PossDupFlag: true,
            OrigSendingTime: TestMessages.SendingTime));

        resent.IsIntact.Should().BeTrue();
        resent.PossDupFlag.Should().BeTrue();
        resent.GetUtcTimestamp(Tags.OrigSendingTime).Should().Be(TestMessages.SendingTime);
        resent.GetUtcTimestamp(Tags.SendingTime).Should().Be(resendTime);
        resent.GetString(Tags.ClOrdID).Should().Be(original.GetString(Tags.ClOrdID));
        Encoding.ASCII.GetString(b.Body).Should().NotContain("\u000149=").And.NotStartWith("49=");
    }

    [Fact]
    public void Writes_into_buffer_writer()
    {
        var writer = new ArrayBufferWriter<byte>();
        using var b = new FixMessageBuilder(MsgTypes.Heartbeat);

        var n = b.WriteTo(writer, TestMessages.Header());

        writer.WrittenCount.Should().Be(n);
        FixMessage.Parse(writer.WrittenMemory).IsIntact.Should().BeTrue();
    }

    [Fact]
    public void Grows_past_initial_capacity()
    {
        using var b = new FixMessageBuilder(MsgTypes.NewOrderSingle, capacity: 16);
        var text = new string('x', 5000);

        b.Set(Tags.Text, text);

        FixMessage.Parse(b.ToBytes(TestMessages.Header())).GetString(Tags.Text).Should().Be(text);
    }
}
