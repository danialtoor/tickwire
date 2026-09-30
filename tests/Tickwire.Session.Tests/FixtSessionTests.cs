using FluentAssertions;
using static Tickwire.Fix.Session.Tests.SessionHarness;

namespace Tickwire.Fix.Session.Tests;

/// <summary>FIXT 1.1: DefaultApplVerID(1137) on Logon, ApplVerID(1128) on application messages.</summary>
public class FixtSessionTests
{
    [Fact]
    public async Task FIXT_sessions_exchange_DefaultApplVerID_and_trade()
    {
        await using var h = new SessionHarness(beginString: "FIXT.1.1", acceptorApplVer: "9", initiatorApplVer: "9");
        await h.ConnectAsync();

        h.AcceptorEvents.Sent(MsgTypes.Logon).Single().GetString(Tags.DefaultApplVerID).Should().Be("9");
        h.InitiatorEvents.Sent(MsgTypes.Logon).Single().BeginString.Should().Be("FIXT.1.1");

        h.Initiator.Send(Order("FX-1"));
        await Eventually(() => h.AcceptorApp.Received.Count == 1);
        h.AcceptorEvents.Sent(MsgTypes.Reject).Should().BeEmpty("a FIX 5.0 SP2 NewOrderSingle validates");
    }

    [Theory]
    [InlineData(null, "is required")]
    [InlineData("7", "DefaultApplVerID(1137)=7 is not supported")]
    public async Task Logon_without_the_right_DefaultApplVerID_is_refused(string? initiatorVer, string expected)
    {
        await using var h = new SessionHarness(beginString: "FIXT.1.1", acceptorApplVer: "9", initiatorApplVer: initiatorVer);
        await h.ConnectAsync(waitForLogon: false);

        await Eventually(() => h.AcceptorEvents.Sent(MsgTypes.Logout).Any());
        h.AcceptorEvents.Sent(MsgTypes.Logout).Single().GetString(Tags.Text).Should().Contain(expected);
        h.AcceptorApp.Logons.Should().Be(0);
    }

    [Fact]
    public async Task Message_with_another_ApplVerID_gets_a_session_Reject()
    {
        await using var h = new SessionHarness(beginString: "FIXT.1.1", acceptorApplVer: "9", initiatorApplVer: "9");
        await h.ConnectAsync();

        h.Initiator.Send(Order("FX-2").Set(Tags.ApplVerID, "6"));
        await Eventually(() => h.AcceptorEvents.Sent(MsgTypes.Reject).Any());

        var reject = h.AcceptorEvents.Sent(MsgTypes.Reject).Single();
        reject.GetInt(Tags.RefTagID).Should().Be(Tags.ApplVerID);
        reject.GetInt(Tags.SessionRejectReason).Should().Be((int)SessionRejectReason.ValueIsIncorrect);
        h.AcceptorApp.Received.Should().BeEmpty();
    }
}
