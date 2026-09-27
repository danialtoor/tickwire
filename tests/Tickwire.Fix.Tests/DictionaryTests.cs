using FluentAssertions;
using Tickwire.Fix.Dictionary;

namespace Tickwire.Fix.Tests;

public class DictionaryTests
{
    private static readonly FixDictionary Dict = FixDictionary.Fix44;

    private static FixMessage Nos(Action<FixMessageBuilder>? tweak = null, bool includeSymbol = true)
    {
        using var b = new FixMessageBuilder(MsgTypes.NewOrderSingle);
        b.Set(Tags.ClOrdID, "C1");
        if (includeSymbol)
        {
            b.Set(Tags.Symbol, "SPY");
        }

        b.Set(Tags.Side, '1').SetUtcTimestamp(Tags.TransactTime, TestMessages.SendingTime).Set(Tags.OrdType, '1')
            .Set(Tags.OrderQty, 5m);
        tweak?.Invoke(b);
        return b.ToMessage(TestMessages.Header());
    }

    [Fact]
    public void Loads_the_embedded_FIX44_spec()
    {
        Dict.BeginString.Should().Be("FIX.4.4");
        Dict.FieldName(Tags.OrdStatus).Should().Be("OrdStatus");
        Dict.MessageName("8").Should().Be("ExecutionReport");
        Dict.Message("A")!.IsAdmin.Should().BeTrue();
        Dict.Field(Tags.OrdStatus)!.Describe("1").Should().Be("PartiallyFilled");
    }

    [Fact]
    public void Decodes_fields_with_names_and_enum_meanings()
    {
        var decoded = Dict.Decode(Nos());

        decoded.Should().Contain(f => f.Tag == 35 && f.Meaning == "NewOrderSingle");
        decoded.Should().Contain(f => f.Tag == 54 && f.Name == "Side" && f.Meaning == "Buy");
        decoded.Should().Contain(f => f.Tag == 49 && f.IsHeader);
        decoded.Should().Contain(f => f.Tag == 10 && f.IsTrailer);
    }

    [Fact]
    public void Valid_order_passes()
    {
        Dict.Validate(Nos()).Should().BeNull();
        Dict.Validate(FixMessage.Parse(TestMessages.NewOrderSingle())).Should().BeNull();
    }

    [Fact]
    public void Missing_required_component_field_is_reported()
    {
        var issue = Dict.Validate(Nos(includeSymbol: false));

        issue.Should().Be(new ValidationIssue(SessionRejectReason.RequiredTagMissing, Tags.Symbol,
            "Required tag missing: Symbol(55)"));
    }

    [Theory]
    [InlineData(Tags.Side, "Z", SessionRejectReason.ValueIsIncorrect)]
    [InlineData(Tags.Price, "1.2.3", SessionRejectReason.IncorrectDataFormat)]
    [InlineData(Tags.TransactTime, "yesterday", SessionRejectReason.IncorrectDataFormat)]
    [InlineData(Tags.BeginSeqNo, "1", SessionRejectReason.TagNotDefinedForMessageType)]
    [InlineData(4999, "x", SessionRejectReason.InvalidTagNumber)]
    public void Invalid_field_values_map_to_reject_reasons(int tag, string value, SessionRejectReason expected)
    {
        var msg = Nos(b => b.Set(tag, value));

        Dict.Validate(msg)!.Value.Reason.Should().Be(expected);
        Dict.Validate(msg)!.Value.RefTagId.Should().Be(tag);
    }

    [Fact]
    public void Duplicate_tag_outside_a_group_is_reported()
    {
        var issue = Dict.Validate(Nos(b => b.Set(Tags.ClOrdID, "again")));

        issue!.Value.Reason.Should().Be(SessionRejectReason.TagAppearsMoreThanOnce);
    }

    [Fact]
    public void User_defined_tags_are_accepted()
    {
        Dict.Validate(Nos(b => b.Set(Tags.TheoValue, 1.23m))).Should().BeNull();
    }

    [Fact]
    public void Unknown_msg_type_is_invalid()
    {
        using var b = new FixMessageBuilder("ZZ");

        Dict.Validate(b.ToMessage(TestMessages.Header()))!.Value.Reason.Should().Be(SessionRejectReason.InvalidMsgType);
    }
}
