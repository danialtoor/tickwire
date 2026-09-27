using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Tickwire.Fix;
using Tickwire.Fix.Dictionary;

namespace Tickwire.LogAnalyzer;

/// <summary>
/// Reads a FIX log in any common shape (raw SOH, '|' or '^' delimited, QuickFIX log lines with timestamps, several
/// messages per line), rebuilds each session's sequence timeline and each order's lifecycle, and runs diagnostic
/// rules. The rule codes are shared with the browser port (web/src/analyzer) and pinned by samples/expected.json.
/// </summary>
public static partial class FixLogAnalyzer
{
    private static readonly FixDictionary Dict = FixDictionary.Fix44;
    private static readonly HashSet<string> Terminal = ["2", "4", "8", "C", "3"];

    public static AnalysisResult Analyze(string text)
    {
        var (messages, parsed, lines) = Extract(text);
        var diags = new DiagnosticSet();

        foreach (var m in messages.Where(m => !m.Parsed))
        {
            diags.Add("UNPARSEABLE", Severity.Warning, "Text that looks like FIX but can't be parsed",
                $"Line {m.Line}: {m.ParseError}. The receiving engine would drop this silently.",
                "Check the log wasn't truncated or re-wrapped, and that each field is tag=value.", m.Index, m.Line);
        }

        foreach (var m in messages.Where(m => m.Parsed && !m.Intact))
        {
            diags.Add("GARBLED", Severity.Error, "Garbled message (BodyLength or CheckSum wrong)",
                $"Message {m.Index} ({m.MsgTypeName}, seq {m.SeqNum}): {m.IntegrityDetail}. FIX engines ignore garbled messages without replying, so the counterparty sees a sequence gap on the next message.",
                "Look for manual edits, character-set conversion (the checksum is over bytes), or a proxy rewriting fields.", m.Index, m.Line);
        }

        var sessions = AnalyzeSessions(messages, parsed, diags);
        ValidateMessages(messages, parsed, diags);
        var orders = AnalyzeOrders(messages, parsed, diags);
        AnalyzeRejectsAndLogouts(messages, parsed, diags);

        return new AnalysisResult(lines, messages.Count, messages.Count(m => !m.Parsed), messages, sessions, orders, diags.ToList());
    }

    // ------------------------------------------------------------------ extraction

    [GeneratedRegex(@"(\d{8}-\d{2}:\d{2}:\d{2}(?:\.\d{1,9})?)|(\d{4}-\d{2}-\d{2}[ T]\d{2}:\d{2}:\d{2}(?:[.,]\d{1,9})?)")]
    private static partial Regex TimestampRegex();

    private static (List<LogMessage> Messages, Dictionary<int, FixMessage> Parsed, int Lines) Extract(string text)
    {
        var messages = new List<LogMessage>();
        var parsed = new Dictionary<int, FixMessage>();
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (var li = 0; li < lines.Length; li++)
        {
            var line = lines[li];
            var start = line.IndexOf("8=FIX", StringComparison.Ordinal);
            if (start < 0)
            {
                continue;
            }

            DateTime? logTs = null;
            var ts = TimestampRegex().Match(line[..start]);
            if (ts.Success)
            {
                logTs = ParseLogTimestamp(ts.Value);
            }

            while (start >= 0)
            {
                var next = line.IndexOf("8=FIX", start + 5, StringComparison.Ordinal);
                // Only split where a new message really starts: at a delimiter.
                while (next > 0 && line[next - 1] is not ('\u0001' or '|' or '^' or ' ' or '\t'))
                {
                    next = line.IndexOf("8=FIX", next + 5, StringComparison.Ordinal);
                }

                var segment = (next < 0 ? line[start..] : line[start..next]).TrimEnd();
                var cs = Regex.Match(segment, @"(?:\u0001|\||\^)10=\d{3}(?:\u0001|\||\^)?");
                if (cs.Success)
                {
                    segment = segment[..(cs.Index + cs.Length)];
                }

                messages.Add(ToLogMessage(messages.Count, li + 1, logTs, segment, parsed));
                start = next;
            }
        }

        return (messages, parsed, lines.Length);
    }

    private static DateTime? ParseLogTimestamp(string s)
    {
        string[] formats = ["yyyyMMdd-HH:mm:ss.FFFFFFF", "yyyyMMdd-HH:mm:ss", "yyyy-MM-dd HH:mm:ss.FFFFFFF", "yyyy-MM-ddTHH:mm:ss.FFFFFFF",
            "yyyy-MM-dd HH:mm:ss", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-dd HH:mm:ss,FFFFFFF"];
        var trimmed = s.Length > 27 ? s[..27] : s;
        return DateTime.TryParseExact(trimmed, formats, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d) ? d : null;
    }

    private static LogMessage ToLogMessage(int index, int line, DateTime? logTs, string segment, Dictionary<int, FixMessage> parsed)
    {
        var bytes = FixDisplay.FromPiped(segment);
        if (bytes.Length > 0 && bytes[^1] != FixParser.Soh)
        {
            bytes = [.. bytes, FixParser.Soh];
        }

        var display = FixDisplay.ToPiped(bytes);
        if (!FixParser.TryParse(bytes, out var m, out var error))
        {
            return new LogMessage(index, line, logTs, display, false, error.ToString(), "", "", 0, "?", "Unparseable", null, false, false,
                null, "Unparseable");
        }

        parsed[index] = m;
        string? integrity = null;
        if (!m.IsIntact)
        {
            var parts = new List<string>();
            if (m.Integrity.HasFlag(FixIntegrity.BodyLengthMismatch))
            {
                parts.Add($"BodyLength(9) says {m.DeclaredBodyLength} but the body is {m.ActualBodyLength} bytes");
            }

            if (m.Integrity.HasFlag(FixIntegrity.ChecksumMismatch))
            {
                parts.Add($"CheckSum(10) says {m.DeclaredChecksum:000} but the bytes sum to {m.ActualChecksum:000}");
            }

            integrity = string.Join("; ", parts);
        }

        return new LogMessage(index, line, logTs, display, true, null, m.SenderCompID, m.TargetCompID, m.MsgSeqNum, m.MsgType,
            Dict.MessageName(m.MsgType), m.GetUtcTimestamp(Tags.SendingTime), m.PossDupFlag, m.IsIntact, integrity, Summarize(m));
    }

    // ------------------------------------------------------------------ sessions

    private static List<SessionFlow> AnalyzeSessions(List<LogMessage> messages, Dictionary<int, FixMessage> parsed, DiagnosticSet diags)
    {
        var flows = new List<SessionFlow>();
        var heartBtInts = new Dictionary<(string, string), int>();
        var logonIndexes = parsed.Where(kv => kv.Value.MsgType == MsgTypes.Logon).Select(kv => kv.Key).ToList();
        foreach (var (idx, m) in parsed)
        {
            if (m.MsgType == MsgTypes.Logon && m.GetInt(Tags.HeartBtInt) is { } hb)
            {
                heartBtInts[Pair(m.SenderCompID, m.TargetCompID)] = hb;
            }
        }

        foreach (var group in parsed.Where(kv => kv.Value.IsIntact).GroupBy(kv => (kv.Value.SenderCompID, kv.Value.TargetCompID)))
        {
            var list = group.OrderBy(kv => kv.Key).ToList();
            int? expected = null;
            var gaps = new List<(SeqGap Gap, HashSet<int> Missing)>();
            int resends = 0, gapFills = 0, logons = 0, logouts = 0;
            var seen = new HashSet<int>();
            FixMessage? prev = null;
            var prevIndex = -1;
            var hbReports = 0;
            heartBtInts.TryGetValue(Pair(group.Key.SenderCompID, group.Key.TargetCompID), out var hbInt);

            foreach (var (idx, m) in list)
            {
                var seq = m.MsgSeqNum;
                var type = m.MsgType;
                resends += type == MsgTypes.ResendRequest ? 1 : 0;
                logons += type == MsgTypes.Logon ? 1 : 0;
                logouts += type == MsgTypes.Logout ? 1 : 0;
                var isGapFill = type == MsgTypes.SequenceReset && m.GetBool(Tags.GapFillFlag) == true;
                gapFills += isGapFill ? 1 : 0;

                if (hbInt > 0 && prev is not null && hbReports < 3 && type != MsgTypes.Logon && prev.MsgType != MsgTypes.Logout
                    && !logonIndexes.Any(li => li > prevIndex && li < idx)
                    && m.GetUtcTimestamp(Tags.SendingTime) is { } t1 && prev.GetUtcTimestamp(Tags.SendingTime) is { } t0
                    && (t1 - t0).TotalSeconds > (hbInt * 1.2) + 1 && !m.PossDupFlag)
                {
                    hbReports++;
                    diags.Add("HEARTBEAT_EXCEEDED", Severity.Warning, "Silence longer than the heartbeat interval",
                        $"{m.SenderCompID} sent nothing for {(t1 - t0).TotalSeconds:0.#}s (HeartBtInt is {hbInt}s). The counterparty should have sent a TestRequest(1) and, without an answer, logged out.",
                        "Check the sender's heartbeat timer and network path; a stalled event loop or GC pause shows up exactly like this.", idx,
                        messages[idx].Line);
                }

                prev = m;
                prevIndex = idx;

                if (type == MsgTypes.Logon && m.GetBool(Tags.ResetSeqNumFlag) == true)
                {
                    expected = seq + 1;
                    seen.Clear();
                    seen.Add(seq);
                    continue;
                }

                if (type == MsgTypes.SequenceReset && !isGapFill)
                {
                    expected = m.GetInt(Tags.NewSeqNo) ?? expected;
                    continue;
                }

                if (expected is null)
                {
                    expected = isGapFill ? m.GetInt(Tags.NewSeqNo) : seq + 1;
                    seen.Add(seq);
                    continue;
                }

                if (seq == expected)
                {
                    expected = isGapFill ? m.GetInt(Tags.NewSeqNo) ?? seq + 1 : seq + 1;
                    seen.Add(seq);
                    Cover(gaps, seq, isGapFill ? m.GetInt(Tags.NewSeqNo) : null);
                }
                else if (seq > expected)
                {
                    var missing = Enumerable.Range(expected.Value, seq - expected.Value).Where(s => !seen.Contains(s)).ToHashSet();
                    if (missing.Count > 0)
                    {
                        gaps.Add((new SeqGap(expected.Value, seq, idx, false), missing));
                    }

                    expected = isGapFill ? m.GetInt(Tags.NewSeqNo) ?? seq + 1 : seq + 1;
                    seen.Add(seq);
                    Cover(gaps, seq, isGapFill ? m.GetInt(Tags.NewSeqNo) : null);
                }
                else if (m.PossDupFlag)
                {
                    // A resend (or gap fill) answering a ResendRequest.
                    seen.Add(seq);
                    Cover(gaps, seq, isGapFill ? m.GetInt(Tags.NewSeqNo) : null);
                }
                else
                {
                    diags.Add("SEQ_TOO_LOW", Severity.Error, "Sequence number went backwards without PossDupFlag",
                        $"{m.SenderCompID} sent {Dict.MessageName(type)} with MsgSeqNum {seq}, but {expected} was expected and PossDupFlag(43) is not Y. A FIX engine must log out and disconnect on this; it usually means one side lost or reset its sequence numbers.",
                        "Agree a reset: log on with ResetSeqNumFlag(141)=Y, or set the sender's next outbound seq to the expected value. Persist sequence numbers across restarts.",
                        idx, messages[idx].Line);
                }
            }

            foreach (var (gap, missing) in gaps)
            {
                var recovered = missing.Count == 0;
                diags.Add(recovered ? "SEQ_GAP_RECOVERED" : "SEQ_GAP", recovered ? Severity.Info : Severity.Error,
                    recovered ? "Sequence gap, recovered by resend" : "Sequence gap that was never filled",
                    recovered
                        ? $"{group.Key.SenderCompID} jumped from {gap.ExpectedSeq} to {gap.ReceivedSeq}; the missing messages arrived later as PossDup resends or a GapFill. The session recovered as designed."
                        : $"{group.Key.SenderCompID} jumped from {gap.ExpectedSeq} to {gap.ReceivedSeq} and {missing.Count} message(s) ({FormatRange(missing)}) never show up in the log, not even as resends. The receiver should have sent a ResendRequest(2); if it did, the sender didn't answer.",
                    recovered
                        ? "No action needed; this is what ResendRequest/SequenceReset are for."
                        : "Check whether the receiver sent ResendRequest(2) and whether the sender's message store still had those messages. Messages the sender can't resend must be answered with SequenceReset-GapFill.",
                    gap.MessageIndex, messages[gap.MessageIndex].Line);
            }

            flows.Add(new SessionFlow(group.Key.SenderCompID, group.Key.TargetCompID, list.Count, list[0].Value.MsgSeqNum,
                list[^1].Value.MsgSeqNum, [.. gaps.Select(g => g.Gap with { Recovered = g.Missing.Count == 0 })], resends, gapFills, logons,
                logouts, hbInt == 0 ? null : hbInt));
        }

        return flows;
    }

    private static void Cover(List<(SeqGap Gap, HashSet<int> Missing)> gaps, int seq, int? newSeqNo)
    {
        foreach (var (_, missing) in gaps)
        {
            missing.Remove(seq);
            if (newSeqNo is { } upTo)
            {
                missing.RemoveWhere(s => s >= seq && s < upTo);
            }
        }
    }

    private static (string, string) Pair(string a, string b) => string.CompareOrdinal(a, b) < 0 ? (a, b) : (b, a);

    private static string FormatRange(HashSet<int> seqs)
    {
        var sorted = seqs.Order().ToList();
        return sorted.Count switch
        {
            0 => "none",
            1 => sorted[0].ToString(CultureInfo.InvariantCulture),
            _ when sorted[^1] - sorted[0] == sorted.Count - 1 => $"{sorted[0]}-{sorted[^1]}",
            _ => string.Join(", ", sorted.Take(8)) + (sorted.Count > 8 ? ", …" : ""),
        };
    }

    // ------------------------------------------------------------------ validation

    private static void ValidateMessages(List<LogMessage> messages, Dictionary<int, FixMessage> parsed, DiagnosticSet diags)
    {
        foreach (var (idx, m) in parsed.OrderBy(kv => kv.Key))
        {
            if (!m.IsIntact || Dict.Validate(m) is not { } issue)
            {
                continue;
            }

            if (issue.Reason == SessionRejectReason.RequiredTagMissing)
            {
                diags.Add("MISSING_REQUIRED_TAG", Severity.Error, $"{Dict.MessageName(m.MsgType)} is missing {Dict.FieldName(issue.RefTagId)}({issue.RefTagId})",
                    $"Message {idx} (seq {m.MsgSeqNum}) from {m.SenderCompID}: {issue.Text}. The receiver should answer with Reject(3), SessionRejectReason(373)=1, RefTagID(371)={issue.RefTagId}.",
                    "Populate the field; if it's conditionally required, check the counterparty's Rules of Engagement.", idx, messages[idx].Line);
            }
            else
            {
                diags.Add("INVALID_FIELD", Severity.Warning, $"Invalid field in {Dict.MessageName(m.MsgType)}",
                    $"Message {idx} (seq {m.MsgSeqNum}): {issue.Text} (SessionRejectReason {(int)issue.Reason} = {issue.Reason}).",
                    "Compare the field against the FIX 4.4 dictionary and the venue's spec; custom tags belong in the user-defined range (5000+).",
                    idx, messages[idx].Line);
            }
        }
    }

    // ------------------------------------------------------------------ orders

    private sealed class OrderBuilder(string root)
    {
        public string Root { get; } = root;
        public List<string> ClOrdIDs { get; } = [root];
        public string? OrderID;
        public string? Symbol;
        public string? Side;
        public decimal? OrderQty;
        public decimal? Price;
        public string? LastStatus;
        public decimal LastCum;
        public decimal TradeQtySum;
        public bool HasNewOrder;
        public bool HasReport;
        public int NewOrderIndex = -1;
        public List<OrderEvent> Events { get; } = [];
    }

    private static List<OrderLifecycle> AnalyzeOrders(List<LogMessage> messages, Dictionary<int, FixMessage> parsed, DiagnosticSet diags)
    {
        var byClOrdId = new Dictionary<string, OrderBuilder>(StringComparer.Ordinal);
        var byOrderId = new Dictionary<string, OrderBuilder>(StringComparer.Ordinal);
        var ordered = new List<OrderBuilder>();
        var execIds = new HashSet<string>(StringComparer.Ordinal);

        OrderBuilder Get(string clOrdId)
        {
            if (!byClOrdId.TryGetValue(clOrdId, out var o))
            {
                o = new OrderBuilder(clOrdId);
                byClOrdId[clOrdId] = o;
                ordered.Add(o);
            }

            return o;
        }

        // Replay order events in the order the application would have seen them: a resend (PossDup) belongs where its
        // OrigSendingTime puts it, not where it happens to appear in the log.
        foreach (var (idx, m) in parsed.Where(kv => kv.Value.IsIntact).OrderBy(kv => EffectiveTime(kv.Value) ?? DateTime.MinValue)
                     .ThenBy(kv => kv.Key))
        {
            var clOrdId = m.GetString(Tags.ClOrdID);
            var orig = m.GetString(Tags.OrigClOrdID);
            var line = messages[idx].Line;
            switch (m.MsgType)
            {
                case MsgTypes.NewOrderSingle when clOrdId is not null:
                {
                    if (m.PossDupFlag && byClOrdId.ContainsKey(clOrdId))
                    {
                        break;
                    }

                    var o = Get(clOrdId);
                    o.HasNewOrder = true;
                    o.NewOrderIndex = idx;
                    o.Symbol = m.GetString(Tags.Symbol);
                    o.Side = m.GetChar(Tags.Side) switch { '1' => "Buy", '2' => "Sell", var c => c?.ToString() };
                    o.OrderQty = m.GetDecimal(Tags.OrderQty);
                    o.Price = m.GetDecimal(Tags.Price);
                    o.Events.Add(Event(idx, m, $"NewOrderSingle {o.Side} {o.OrderQty} {o.Symbol}{(o.Price is { } p ? $" @ {p}" : " MKT")}"));
                    break;
                }

                case MsgTypes.OrderCancelRequest or MsgTypes.OrderCancelReplaceRequest when clOrdId is not null:
                {
                    var isReplace = m.MsgType == MsgTypes.OrderCancelReplaceRequest;
                    if (orig is null || !byClOrdId.TryGetValue(orig, out var o))
                    {
                        diags.Add("UNKNOWN_ORDER", Severity.Warning, $"{(isReplace ? "Replace" : "Cancel")} for a ClOrdID never seen",
                            $"Message {idx}: {Dict.MessageName(m.MsgType)} refers to OrigClOrdID(41)={orig ?? "(missing)"}, which no NewOrderSingle or replace in this log used. The venue will answer with OrderCancelReject(9), CxlRejReason(102)=1 (unknown order).",
                            "OrigClOrdID must be the most recent ClOrdID of the order (after a replace, the replace's ClOrdID). If the order came from another session or before this log starts, include that log.",
                            idx, line);
                        o = Get(orig ?? clOrdId);
                    }

                    if (!o.ClOrdIDs.Contains(clOrdId))
                    {
                        o.ClOrdIDs.Add(clOrdId);
                    }

                    byClOrdId[clOrdId] = o;
                    o.Events.Add(Event(idx, m, isReplace
                        ? $"Replace → {m.GetDecimal(Tags.OrderQty)}{(m.GetDecimal(Tags.Price) is { } p ? $" @ {p}" : "")}"
                        : "Cancel requested"));
                    break;
                }

                case MsgTypes.ExecutionReport:
                {
                    var orderId = m.GetString(Tags.OrderID);
                    OrderBuilder? o = null;
                    if (clOrdId is not null && byClOrdId.TryGetValue(clOrdId, out var a))
                    {
                        o = a;
                    }
                    else if (orig is not null && byClOrdId.TryGetValue(orig, out var b))
                    {
                        o = b;
                    }
                    else if (orderId is not null && byOrderId.TryGetValue(orderId, out var c))
                    {
                        o = c;
                    }

                    o ??= Get(clOrdId ?? orderId ?? $"#{idx}");
                    if (clOrdId is not null && !o.ClOrdIDs.Contains(clOrdId))
                    {
                        o.ClOrdIDs.Add(clOrdId);
                        byClOrdId[clOrdId] = o;
                    }

                    if (orderId is not null and not "NONE")
                    {
                        o.OrderID ??= orderId;
                        byOrderId[orderId] = o;
                    }

                    var execId = m.GetString(Tags.ExecID);
                    if (execId is not null && !execIds.Add(execId))
                    {
                        if (!m.PossDupFlag)
                        {
                            diags.Add("DUPLICATE_EXECID", Severity.Warning, "ExecID reused",
                                $"Message {idx}: ExecID(17)={execId} was already used and PossDupFlag is not set. Clients de-duplicate fills by ExecID, so this fill may be dropped or double-counted.",
                                "ExecID must be unique per trading day (per FIX 4.4); generate it from a persistent sequence.", idx, line);
                        }

                        break; // resent copy of a report we already applied
                    }

                    ApplyReport(o, idx, m, line, diags);
                    break;
                }

                case MsgTypes.OrderCancelReject:
                {
                    var o = (clOrdId is not null ? byClOrdId.GetValueOrDefault(clOrdId) : null)
                        ?? (orig is not null ? byClOrdId.GetValueOrDefault(orig) : null);
                    o?.Events.Add(Event(idx, m, $"Cancel/replace rejected: {m.GetString(Tags.Text) ?? Dict.Field(Tags.CxlRejReason)?.Describe(m.GetString(Tags.CxlRejReason) ?? "")}"));
                    break;
                }

                default:
                    break;
            }
        }

        foreach (var o in ordered.Where(o => o.HasNewOrder && !o.HasReport))
        {
            var sessionReject = parsed.Values.FirstOrDefault(r => r.MsgType is MsgTypes.Reject or MsgTypes.BusinessMessageReject
                && parsed.TryGetValue(o.NewOrderIndex, out var nos) && r.GetInt(Tags.RefSeqNum) == nos.MsgSeqNum
                && r.SenderCompID == nos.TargetCompID);
            diags.Add("NO_ACK", Severity.Warning, "Order never acknowledged",
                sessionReject is not null
                    ? $"ClOrdID {o.Root} got no ExecutionReport: it was rejected before reaching the order book ({Dict.MessageName(sessionReject.MsgType)}: {sessionReject.GetString(Tags.Text)})."
                    : $"ClOrdID {o.Root} has no ExecutionReport anywhere in the log. Either the venue never received it (check for a sequence gap or a garbled message right before) or its reports went to another session.",
                sessionReject is not null
                    ? "Fix the field named in the reject and resend with a new ClOrdID."
                    : "Match the order's MsgSeqNum against the venue's inbound log; send an OrderStatusRequest(H) to ask for its state.",
                o.NewOrderIndex, messages[o.NewOrderIndex].Line);
        }

        return
        [
            .. ordered.Where(o => o.Events.Count > 0).Select(o => new OrderLifecycle(o.Root, o.ClOrdIDs, o.OrderID, o.Symbol, o.Side, o.OrderQty,
                o.Price, o.LastStatus is null ? null : Dict.Field(Tags.OrdStatus)?.Describe(o.LastStatus) ?? o.LastStatus, o.Events)),
        ];
    }

    private static void ApplyReport(OrderBuilder o, int idx, FixMessage m, int line, DiagnosticSet diags)
    {
        o.HasReport = true;
        var status = m.GetString(Tags.OrdStatus);
        var execType = m.GetString(Tags.ExecType);
        var cum = m.GetDecimal(Tags.CumQty);
        var leaves = m.GetDecimal(Tags.LeavesQty);
        var qty = m.GetDecimal(Tags.OrderQty) ?? o.OrderQty;
        var statusName = status is null ? "?" : Dict.Field(Tags.OrdStatus)?.Describe(status) ?? status;
        var execName = execType is null ? "?" : Dict.Field(Tags.ExecType)?.Describe(execType) ?? execType;
        o.Events.Add(Event(idx, m, $"{execName} → {statusName}"));

        if (execType == "8" || status == "8")
        {
            diags.Add("ORDER_REJECTED", Severity.Warning, "Order rejected by the venue",
                $"ClOrdID {m.GetString(Tags.ClOrdID)}: {m.GetString(Tags.Text) ?? "no Text(58)"}{(m.GetString(Tags.OrdRejReason) is { } r ? $" (OrdRejReason {r} = {Dict.Field(Tags.OrdRejReason)?.Describe(r)})" : "")}.",
                "Business reject, not a session problem: fix the order (limits, price, instrument) and send it with a new ClOrdID.", idx, line);
        }

        if (execType is "I")
        {
            return; // status reports restate, they don't transition
        }

        if (o.LastStatus is not null && Terminal.Contains(o.LastStatus) && status is not null && !Terminal.Contains(status))
        {
            diags.Add("STATE_REGRESSION", Severity.Error, "Order came back to life after a terminal state",
                $"ClOrdID {o.Root}: OrdStatus went from {Dict.Field(Tags.OrdStatus)?.Describe(o.LastStatus)} to {statusName} (message {idx}). Filled, canceled and rejected are final; clients stop tracking the order and will ignore or mis-book this report.",
                "Check the venue's order state machine for a race between a fill and a cancel, or for reports sent out of order.", idx, line);
        }

        if (cum is { } c)
        {
            if (c < o.LastCum)
            {
                diags.Add("CUMQTY_DECREASED", Severity.Error, "CumQty went down",
                    $"ClOrdID {o.Root}: CumQty(14) dropped from {o.LastCum} to {c} at message {idx}. Executed quantity can only grow (a trade bust uses ExecType H).",
                    "Look for reports sent out of order or a report built from stale order state.", idx, line);
            }

            o.LastCum = Math.Max(o.LastCum, c);
        }

        if (cum is { } cq && leaves is { } lq && qty is { } oq && status is not null)
        {
            var terminal = Terminal.Contains(status);
            if (!terminal && cq + lq != oq)
            {
                diags.Add("QTY_INVARIANT", Severity.Error, "CumQty + LeavesQty ≠ OrderQty",
                    $"ClOrdID {o.Root}, message {idx}: CumQty {cq} + LeavesQty {lq} = {cq + lq}, but OrderQty is {oq} and the order is still {statusName}. For a live order the two must add up.",
                    "Recompute LeavesQty as OrderQty − CumQty after every fill and replace.", idx, line);
            }
            else if (terminal && lq != 0)
            {
                diags.Add("QTY_INVARIANT", Severity.Error, "Terminal order still has LeavesQty",
                    $"ClOrdID {o.Root}, message {idx}: the order is {statusName} but LeavesQty is {lq}. A done order has nothing left to execute.",
                    "Set LeavesQty(151)=0 on Filled, Canceled, Rejected and Expired reports.", idx, line);
            }
        }

        if (execType is "F" or "1" or "2" && m.GetDecimal(Tags.LastQty) is { } last)
        {
            o.TradeQtySum += last;
            if (cum is { } c2 && o.TradeQtySum != c2)
            {
                diags.Add("FILL_SUM_MISMATCH", Severity.Warning, "Fills don't add up to CumQty",
                    $"ClOrdID {o.Root}: LastQty(32) of the trades so far sums to {o.TradeQtySum}, but this report says CumQty {c2}. Either a fill report is missing from the log or CumQty is wrong.",
                    "Reconcile by ExecID; request an OrderStatusRequest(H) if a report may have been lost.", idx, line);
                o.TradeQtySum = c2; // report once per divergence
            }
        }

        o.LastStatus = status ?? o.LastStatus;
        o.OrderQty ??= qty;
    }

    private static DateTime? EffectiveTime(FixMessage m) =>
        m.PossDupFlag ? m.GetUtcTimestamp(Tags.OrigSendingTime) ?? m.GetUtcTimestamp(Tags.SendingTime) : m.GetUtcTimestamp(Tags.SendingTime);

    private static OrderEvent Event(int idx, FixMessage m, string description) => new(idx, m.GetUtcTimestamp(Tags.SendingTime), m.MsgType,
        description, m.GetString(Tags.ExecType), m.GetString(Tags.OrdStatus), m.GetDecimal(Tags.CumQty), m.GetDecimal(Tags.LeavesQty),
        m.GetDecimal(Tags.OrderQty), m.GetDecimal(Tags.LastQty), m.GetDecimal(Tags.LastPx), m.GetString(Tags.Text), m.PossDupFlag);

    // ------------------------------------------------------------------ rejects and logouts

    private static void AnalyzeRejectsAndLogouts(List<LogMessage> messages, Dictionary<int, FixMessage> parsed, DiagnosticSet diags)
    {
        foreach (var (idx, m) in parsed.OrderBy(kv => kv.Key))
        {
            var line = messages[idx].Line;
            switch (m.MsgType)
            {
                case MsgTypes.Reject:
                {
                    var reason = m.GetString(Tags.SessionRejectReason);
                    var reasonName = reason is null ? "unspecified" : Dict.Field(Tags.SessionRejectReason)?.Describe(reason) ?? reason;
                    var tag = m.GetInt(Tags.RefTagID);
                    diags.Add("SESSION_REJECT", Severity.Warning, $"Session-level Reject: {reasonName}",
                        $"{m.SenderCompID} rejected seq {m.GetInt(Tags.RefSeqNum)} ({(m.GetString(Tags.RefMsgType) is { } refType ? Dict.MessageName(refType) : "?")}){(tag is { } t2 ? $" on tag {t2} ({Dict.FieldName(t2)})" : "")}: {m.GetString(Tags.Text) ?? reasonName}. The rejected message was not processed, but its sequence number was consumed.",
                        "Fix the field and resend as a new message (new ClOrdID for orders); don't resend with the old MsgSeqNum.", idx, line);
                    break;
                }

                case MsgTypes.BusinessMessageReject:
                    diags.Add("BUSINESS_REJECT", Severity.Warning, "BusinessMessageReject",
                        $"{m.SenderCompID} rejected a {(m.GetString(Tags.RefMsgType) is { } bizType ? Dict.MessageName(bizType) : "message")}: {m.GetString(Tags.Text) ?? Dict.Field(Tags.BusinessRejectReason)?.Describe(m.GetString(Tags.BusinessRejectReason) ?? "")}.",
                        "The message was valid FIX but the application can't handle it; check which message types the counterparty supports.", idx, line);
                    break;

                case MsgTypes.Logout when m.GetString(Tags.Text) is { } text:
                    diags.Add("LOGOUT_REASON", text.Contains("too low", StringComparison.OrdinalIgnoreCase) ? Severity.Error : Severity.Info,
                        "Logout with a reason", $"{m.SenderCompID} logged out: \"{text}\".",
                        text.Contains("too low", StringComparison.OrdinalIgnoreCase)
                            ? "Sequence numbers are out of step; reset with ResetSeqNumFlag(141)=Y on the next Logon or fix the stored seq numbers."
                            : "Read the text; if it names a field or limit, fix that before reconnecting.", idx, line);
                    break;

                default:
                    break;
            }
        }
    }

    // ------------------------------------------------------------------ helpers

    public static string Summarize(FixMessage m) => m.MsgType switch
    {
        MsgTypes.NewOrderSingle => $"{(m.GetChar(Tags.Side) == '1' ? "Buy" : "Sell")} {m.GetString(Tags.OrderQty)} {m.GetString(Tags.Symbol)}{(m.GetString(Tags.Price) is { } p ? $" @ {p}" : "")} ({m.GetString(Tags.ClOrdID)})",
        MsgTypes.ExecutionReport => $"{Dict.Field(Tags.ExecType)?.Describe(m.GetString(Tags.ExecType) ?? "")} {m.GetString(Tags.ClOrdID)} cum {m.GetString(Tags.CumQty)} leaves {m.GetString(Tags.LeavesQty)}",
        MsgTypes.OrderCancelRequest => $"Cancel {m.GetString(Tags.OrigClOrdID)}",
        MsgTypes.OrderCancelReplaceRequest => $"Replace {m.GetString(Tags.OrigClOrdID)} → {m.GetString(Tags.ClOrdID)}",
        MsgTypes.ResendRequest => $"Resend {m.GetString(Tags.BeginSeqNo)}..{m.GetString(Tags.EndSeqNo)}",
        MsgTypes.SequenceReset => $"{(m.GetBool(Tags.GapFillFlag) == true ? "GapFill" : "Reset")} → {m.GetString(Tags.NewSeqNo)}",
        MsgTypes.Logout => $"Logout{(m.GetString(Tags.Text) is { } why ? $": {why}" : "")}",
        MsgTypes.Reject => $"Reject seq {m.GetString(Tags.RefSeqNum)}: {m.GetString(Tags.Text)}",
        _ => Dict.MessageName(m.MsgType),
    };

    /// <summary>Collects findings, merging repeats of the same finding into one entry with several messages.</summary>
    private sealed class DiagnosticSet
    {
        private readonly List<Diagnostic> _items = [];
        private readonly Dictionary<string, int> _index = [];

        public void Add(string code, Severity severity, string title, string explanation, string suggestion, int messageIndex, int? line)
        {
            var key = $"{code}|{title}|{explanation}";
            if (_index.TryGetValue(key, out var i))
            {
                var existing = _items[i];
                _items[i] = existing with { MessageIndexes = [.. existing.MessageIndexes, messageIndex] };
                return;
            }

            _index[key] = _items.Count;
            _items.Add(new Diagnostic(code, severity, title, explanation, suggestion, [messageIndex], line));
        }

        public List<Diagnostic> ToList() =>
            [.. _items.OrderByDescending(d => d.Severity).ThenBy(d => d.MessageIndexes.Min())];
    }

    /// <summary>Plain-text report for the CLI.</summary>
    public static string Report(AnalysisResult r)
    {
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"{r.MessageCount} FIX message(s) on {r.Lines} line(s); {r.Unparsed} unparseable.");
        foreach (var s in r.Sessions)
        {
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"  {s.SenderCompID} -> {s.TargetCompID}: {s.Messages} msgs, seq {s.FirstSeq}..{s.LastSeq}, {s.Gaps.Count} gap(s), {s.ResendRequests} resend request(s), {s.GapFills} gap fill(s)");
        }

        sb.AppendLine(CultureInfo.InvariantCulture, $"{r.Orders.Count} order(s):");
        foreach (var o in r.Orders)
        {
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"  {o.RootClOrdID} {o.Side} {o.OrderQty} {o.Symbol} -> {o.FinalStatus ?? "no report"} ({o.Events.Count} event(s), chain {string.Join(" > ", o.ClOrdIDs)})");
        }

        sb.AppendLine(CultureInfo.InvariantCulture, $"{r.Errors} error(s), {r.Warnings} warning(s):");
        foreach (var d in r.Diagnostics)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"  [{d.Severity.ToString().ToUpperInvariant()}] {d.Code} (line {d.Line}): {d.Title}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"      {d.Explanation}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"      Fix: {d.Suggestion}");
        }

        return sb.ToString();
    }
}
