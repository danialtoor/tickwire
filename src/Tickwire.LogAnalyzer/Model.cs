namespace Tickwire.LogAnalyzer;

public enum Severity
{
    Info,
    Warning,
    Error,
}

/// <summary>One FIX message found in the log.</summary>
public sealed record LogMessage(
    int Index,
    int Line,
    DateTime? LogTimestamp,
    string Raw,
    bool Parsed,
    string? ParseError,
    string SenderCompID,
    string TargetCompID,
    int SeqNum,
    string MsgType,
    string MsgTypeName,
    DateTime? SendingTime,
    bool PossDup,
    bool Intact,
    string? IntegrityDetail,
    string Summary);

/// <summary>A finding, written for a support engineer: what happened, why it matters, what to do.</summary>
public sealed record Diagnostic(
    string Code,
    Severity Severity,
    string Title,
    string Explanation,
    string Suggestion,
    IReadOnlyList<int> MessageIndexes,
    int? Line);

/// <summary>Traffic in one direction of one session (sender → target).</summary>
public sealed record SessionFlow(
    string SenderCompID,
    string TargetCompID,
    int Messages,
    int FirstSeq,
    int LastSeq,
    IReadOnlyList<SeqGap> Gaps,
    int ResendRequests,
    int GapFills,
    int Logons,
    int Logouts,
    int? HeartBtInt);

public sealed record SeqGap(int ExpectedSeq, int ReceivedSeq, int MessageIndex, bool Recovered);

public sealed record OrderEvent(
    int MessageIndex,
    DateTime? Time,
    string MsgType,
    string Description,
    string? ExecType,
    string? OrdStatus,
    decimal? CumQty,
    decimal? LeavesQty,
    decimal? OrderQty,
    decimal? LastQty,
    decimal? LastPx,
    string? Text,
    bool PossDup);

/// <summary>One order, followed through its ClOrdID/OrigClOrdID chain.</summary>
public sealed record OrderLifecycle(
    string RootClOrdID,
    IReadOnlyList<string> ClOrdIDs,
    string? OrderID,
    string? Symbol,
    string? Side,
    decimal? OrderQty,
    decimal? Price,
    string? FinalStatus,
    IReadOnlyList<OrderEvent> Events);

public sealed record AnalysisResult(
    int Lines,
    int MessageCount,
    int Unparsed,
    IReadOnlyList<LogMessage> Messages,
    IReadOnlyList<SessionFlow> Sessions,
    IReadOnlyList<OrderLifecycle> Orders,
    IReadOnlyList<Diagnostic> Diagnostics)
{
    public int Errors => Diagnostics.Count(d => d.Severity == Severity.Error);
    public int Warnings => Diagnostics.Count(d => d.Severity == Severity.Warning);
}
