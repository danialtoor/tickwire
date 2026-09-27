using System.Text;
using FluentAssertions;

namespace Tickwire.Fix.Tests;

public class ParserTests
{
    // The well-known example from the FIX Wikipedia article: BodyLength 178, CheckSum 128.
    private const string WikipediaExample =
        "8=FIX.4.2|9=178|35=8|49=PHLX|56=PERS|52=20071123-05:30:00.000|11=ATOMNOCCC9990900|20=3|150=E|39=E|55=MSFT|167=CS|54=1|38=15|40=2|44=15|58=PHLX EQUITY TESTING|59=0|47=C|32=0|31=0|151=15|14=0|6=0|10=128|";

    [Fact]
    public void Parses_reference_message_and_verifies_length_and_checksum()
    {
        var msg = FixMessage.Parse(WikipediaExample);

        msg.IsIntact.Should().BeTrue();
        msg.DeclaredBodyLength.Should().Be(178);
        msg.ActualChecksum.Should().Be(128);
        msg.MsgType.Should().Be("8");
        msg.SenderCompID.Should().Be("PHLX");
        msg.GetString(Tags.Text).Should().Be("PHLX EQUITY TESTING");
        msg.GetDecimal(Tags.OrderQty).Should().Be(15m);
        msg.GetChar(Tags.Side).Should().Be('1');
        msg.GetUtcTimestamp(Tags.SendingTime).Should().Be(new DateTime(2007, 11, 23, 5, 30, 0, DateTimeKind.Utc));
        msg.ToString().Should().Be(WikipediaExample);
    }

    [Fact]
    public void Flags_bad_checksum_without_failing()
    {
        var msg = FixMessage.Parse(WikipediaExample.Replace("10=128", "10=129", StringComparison.Ordinal));

        msg.Integrity.Should().Be(FixIntegrity.ChecksumMismatch);
        msg.DeclaredChecksum.Should().Be(129);
        msg.ActualChecksum.Should().Be(128);
    }

    [Fact]
    public void Flags_bad_body_length()
    {
        var msg = FixMessage.Parse(WikipediaExample.Replace("9=178", "9=170", StringComparison.Ordinal));

        msg.Integrity.Should().HaveFlag(FixIntegrity.BodyLengthMismatch);
        msg.ActualBodyLength.Should().Be(178);
    }

    [Theory]
    [InlineData("9=5|8=FIX.4.4|35=0|10=000|", FixParseErrorKind.MustStartWithBeginString)]
    [InlineData("8=FIX.4.4|35=0|9=5|10=000|", FixParseErrorKind.BodyLengthMustBeSecond)]
    [InlineData("8=FIX.4.4|9=5|34=1|35=0|10=000|", FixParseErrorKind.MsgTypeMustBeThird)]
    [InlineData("8=FIX.4.4|9=5|35=0|34=|10=000|", FixParseErrorKind.TagWithoutValue)]
    [InlineData("8=FIX.4.4|9=5|35=0|3x4=1|10=000|", FixParseErrorKind.InvalidTagNumber)]
    [InlineData("8=FIX.4.4|9=5|35=0|10=000|34=1|", FixParseErrorKind.ChecksumMustBeLast)]
    [InlineData("8=FIX.4.4|9=5|35=0|34=1|", FixParseErrorKind.ChecksumMustBeLast)]
    [InlineData("8=FIX.4.4|9=abc|35=0|10=000|", FixParseErrorKind.InvalidBodyLength)]
    public void Rejects_structurally_invalid_messages(string text, FixParseErrorKind expected)
    {
        var ok = FixParser.TryParse(Encoding.ASCII.GetBytes(text.Replace('|', '\u0001')), out _, out var error);

        ok.Should().BeFalse();
        error.Kind.Should().Be(expected);
    }

    [Fact]
    public void Data_fields_may_contain_SOH()
    {
        using var b = new FixMessageBuilder(MsgTypes.NewOrderSingle);
        var payload = "a\u0001b=c"u8.ToArray();
        b.Set(Tags.RawDataLength, payload.Length).Set(Tags.RawData, payload).Set(Tags.Text, "after");

        var msg = FixMessage.Parse(b.ToBytes(TestMessages.Header()));

        msg.IsIntact.Should().BeTrue();
        msg.GetRaw(Tags.RawData).ToArray().Should().Equal(payload);
        msg.GetString(Tags.Text).Should().Be("after");
    }

    [Fact]
    public void FromPiped_keeps_pipes_that_are_part_of_values()
    {
        var bytes = FixDisplay.FromPiped("8=FIX.4.4|9=5|58=a|b|10=000|");

        Encoding.ASCII.GetString(bytes).Should().Be("8=FIX.4.4\u00019=5\u000158=a|b\u000110=000\u0001");
    }
}
