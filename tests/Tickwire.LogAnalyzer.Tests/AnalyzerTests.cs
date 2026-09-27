using System.Text.Json;
using FluentAssertions;

namespace Tickwire.LogAnalyzer.Tests;

public class AnalyzerTests
{
    private static string SamplesDir => Path.Combine(AppContext.BaseDirectory, "samples");

    public static TheoryData<string> SampleFiles()
    {
        var data = new TheoryData<string>();
        foreach (var f in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "samples"), "*.log").Order())
        {
            data.Add(Path.GetFileName(f));
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(SampleFiles))]
    public void Samples_produce_exactly_the_expected_diagnostics(string file)
    {
        var expected = JsonSerializer.Deserialize<Dictionary<string, string[]>>(File.ReadAllText(Path.Combine(SamplesDir, "expected.json")))!;

        var result = FixLogAnalyzer.Analyze(File.ReadAllText(Path.Combine(SamplesDir, file)));

        result.Diagnostics.Select(d => d.Code).Distinct().Order().Should().Equal(expected[file].Order());
        result.Unparsed.Should().Be(0);
    }

    [Fact]
    public void Gap_sample_rebuilds_the_order_through_the_resend()
    {
        var result = FixLogAnalyzer.Analyze(File.ReadAllText(Path.Combine(SamplesDir, "01-gap-recovered-and-unfilled.log")));

        var order = result.Orders.Single(o => o.RootClOrdID == "ORD-1");
        order.FinalStatus.Should().Be("Filled");
        order.Events.Where(e => e.ExecType == "F").Select(e => e.CumQty).Should().Equal(4m, 6m, 8m, 10m);
        var venueFlow = result.Sessions.Single(s => s.SenderCompID == "TICKWIRE");
        venueFlow.Gaps.Should().HaveCount(2);
        venueFlow.Gaps[0].Recovered.Should().BeTrue();
        venueFlow.Gaps[1].Should().Be(new SeqGap(9, 11, venueFlow.Gaps[1].MessageIndex, false));
    }

    [Fact]
    public void Diagnostics_explain_and_suggest()
    {
        var result = FixLogAnalyzer.Analyze(File.ReadAllText(Path.Combine(SamplesDir, "02-sequence-too-low.log")));

        var d = result.Diagnostics.Single(x => x.Code == "SEQ_TOO_LOW");
        d.Severity.Should().Be(Severity.Error);
        d.Explanation.Should().Contain("MsgSeqNum 1").And.Contain("40 was expected");
        d.Suggestion.Should().Contain("ResetSeqNumFlag(141)=Y");
        d.Line.Should().Be(6);
    }

    [Theory]
    [InlineData("\u0001")]
    [InlineData("|")]
    [InlineData("^")]
    [InlineData("<SOH>")]
    public void Accepts_any_common_delimiter(string delimiter)
    {
        var raw = "8=FIX.4.4|9=45|35=0|49=A|56=B|34=1|52=20250314-13:30:00.000|10=064|".Replace("|", delimiter, StringComparison.Ordinal);

        var result = FixLogAnalyzer.Analyze(raw);

        result.MessageCount.Should().Be(1);
        result.Messages[0].Parsed.Should().BeTrue();
        result.Messages[0].MsgTypeName.Should().Be("Heartbeat");
        result.Messages[0].Intact.Should().BeTrue();
    }

    [Fact]
    public void Finds_several_messages_on_one_line_and_reads_the_log_timestamp()
    {
        const string hb = "8=FIX.4.4|9=45|35=0|49=A|56=B|34=1|52=20250314-13:30:00.000|10=064|";
        var result = FixLogAnalyzer.Analyze($"2025-03-14 13:30:00.250 <incoming> {hb} {hb.Replace("34=1", "34=2", StringComparison.Ordinal)}");

        result.MessageCount.Should().Be(2);
        result.Messages[0].LogTimestamp.Should().Be(new DateTime(2025, 3, 14, 13, 30, 0, 250, DateTimeKind.Utc));
        result.Messages[1].SeqNum.Should().Be(2);
    }

    [Fact]
    public void Garbage_that_starts_like_FIX_is_reported_not_thrown()
    {
        var result = FixLogAnalyzer.Analyze("8=FIX.4.4|9=5|garbage without equals|10=000|");

        result.Unparsed.Should().Be(1);
        result.Diagnostics.Should().Contain(d => d.Code == "UNPARSEABLE");
    }

    [Fact]
    public void Clean_session_has_no_findings()
    {
        var result = FixLogAnalyzer.Analyze(string.Join('\n',
            "8=FIX.4.4|9=45|35=0|49=A|56=B|34=1|52=20250314-13:30:00.000|10=064|",
            "8=FIX.4.4|9=45|35=0|49=A|56=B|34=2|52=20250314-13:30:10.000|10=066|"));

        result.Diagnostics.Should().BeEmpty();
    }
}
