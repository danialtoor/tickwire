namespace Tickwire.Fix;

/// <summary>FIX 4.4 tag numbers used by Tickwire. The data dictionary knows every tag; these are the ones code refers to by name.</summary>
public static class Tags
{
    public const int Account = 1;
    public const int AvgPx = 6;
    public const int BeginSeqNo = 7;
    public const int BeginString = 8;
    public const int BodyLength = 9;
    public const int CheckSum = 10;
    public const int ClOrdID = 11;
    public const int CumQty = 14;
    public const int Currency = 15;
    public const int EndSeqNo = 16;
    public const int ExecID = 17;
    public const int ExecInst = 18;
    public const int ExecRefID = 19;
    public const int HandlInst = 21;
    public const int SecurityIDSource = 22;
    public const int LastPx = 31;
    public const int LastQty = 32;
    public const int MsgSeqNum = 34;
    public const int MsgType = 35;
    public const int NewSeqNo = 36;
    public const int OrderID = 37;
    public const int OrderQty = 38;
    public const int OrdStatus = 39;
    public const int OrdType = 40;
    public const int OrigClOrdID = 41;
    public const int PossDupFlag = 43;
    public const int Price = 44;
    public const int RefSeqNum = 45;
    public const int SecurityID = 48;
    public const int SenderCompID = 49;
    public const int SenderSubID = 50;
    public const int SendingTime = 52;
    public const int Side = 54;
    public const int Symbol = 55;
    public const int TargetCompID = 56;
    public const int TargetSubID = 57;
    public const int Text = 58;
    public const int TimeInForce = 59;
    public const int TransactTime = 60;
    public const int PositionEffect = 77;
    public const int SignatureLength = 93;
    public const int Signature = 89;
    public const int SecureDataLen = 90;
    public const int SecureData = 91;
    public const int RawDataLength = 95;
    public const int RawData = 96;
    public const int PossResend = 97;
    public const int EncryptMethod = 98;
    public const int CxlRejReason = 102;
    public const int OrdRejReason = 103;
    public const int HeartBtInt = 108;
    public const int TestReqID = 112;
    public const int OnBehalfOfCompID = 115;
    public const int OrigSendingTime = 122;
    public const int GapFillFlag = 123;
    public const int DeliverToCompID = 128;
    public const int ResetSeqNumFlag = 141;
    public const int ExecType = 150;
    public const int LeavesQty = 151;
    public const int SecurityType = 167;
    public const int MaturityMonthYear = 200;
    public const int PutOrCall = 201;
    public const int StrikePrice = 202;
    public const int XmlDataLen = 212;
    public const int XmlData = 213;
    public const int RefTagID = 371;
    public const int RefMsgType = 372;
    public const int SessionRejectReason = 373;
    public const int BusinessRejectRefID = 379;
    public const int BusinessRejectReason = 380;
    public const int CxlRejResponseTo = 434;
    public const int MaturityDate = 541;
    public const int Username = 553;
    public const int Password = 554;
    public const int LastMsgSeqNumProcessed = 369;
    public const int MultiLegReportingType = 442;
    public const int NoLegs = 555;
    public const int LegPositionEffect = 564;
    public const int LegSymbol = 600;
    public const int LegSecurityID = 602;
    public const int LegSecurityIDSource = 603;
    public const int LegCFICode = 608;
    public const int LegSecurityType = 609;
    public const int LegMaturityDate = 611;
    public const int LegStrikePrice = 612;
    public const int LegRatioQty = 623;
    public const int LegSide = 624;
    public const int LegLastPx = 637;
    public const int NextExpectedMsgSeqNum = 789;
    public const int OrdStatusReqID = 790;
    public const int CopyMsgIndicator = 797;

    /// <summary>Tickwire custom tag (user-defined range): theoretical value of the option at the time of the report.</summary>
    public const int TheoValue = 20001;

    /// <summary>Tickwire custom tag: underlying price at the time of the report.</summary>
    public const int UnderlyingLastPx = 20002;

    /// <summary>Standard header tags. Used to split a stored message into header and body when resending.</summary>
    public static bool IsHeaderTag(int tag) => tag switch
    {
        BeginString or BodyLength or MsgType or SenderCompID or TargetCompID or MsgSeqNum or SendingTime
            or PossDupFlag or PossResend or OrigSendingTime or OnBehalfOfCompID or DeliverToCompID
            or SenderSubID or TargetSubID or SecureDataLen or SecureData or LastMsgSeqNumProcessed
            or 116 or 129 or 142 or 143 or 144 or 145 or 347 or 212 or 213 or 627 or 628 or 629 or 630 => true,
        _ => false,
    };

    public static bool IsTrailerTag(int tag) => tag is CheckSum or SignatureLength or Signature;

    /// <summary>
    /// FIX "data" fields carry arbitrary bytes (including SOH), so their length is given by a preceding length tag.
    /// Returns the data tag that follows <paramref name="lengthTag"/>, or 0 when it is not a length tag.
    /// </summary>
    public static int DataTagFor(int lengthTag) => lengthTag switch
    {
        SecureDataLen => SecureData,
        RawDataLength => RawData,
        SignatureLength => Signature,
        XmlDataLen => XmlData,
        348 => 349, // EncodedIssuerLen
        350 => 351, // EncodedSecurityDescLen
        352 => 353, // EncodedListExecInstLen
        354 => 355, // EncodedTextLen
        356 => 357, // EncodedSubjectLen
        358 => 359, // EncodedHeadlineLen
        360 => 361, // EncodedAllocTextLen
        362 => 363, // EncodedUnderlyingIssuerLen
        364 => 365, // EncodedUnderlyingSecurityDescLen
        445 => 446, // EncodedListStatusTextLen
        618 => 619, // EncodedLegIssuerLen
        621 => 622, // EncodedLegSecurityDescLen
        _ => 0,
    };
}

/// <summary>MsgType(35) values.</summary>
public static class MsgTypes
{
    public const string Heartbeat = "0";
    public const string TestRequest = "1";
    public const string ResendRequest = "2";
    public const string Reject = "3";
    public const string SequenceReset = "4";
    public const string Logout = "5";
    public const string ExecutionReport = "8";
    public const string OrderCancelReject = "9";
    public const string Logon = "A";
    public const string NewOrderSingle = "D";
    public const string OrderCancelRequest = "F";
    public const string OrderCancelReplaceRequest = "G";
    public const string OrderStatusRequest = "H";
    public const string BusinessMessageReject = "j";
    public const string NewOrderMultileg = "AB";

    public static bool IsAdmin(string msgType) => msgType is Heartbeat or TestRequest or ResendRequest or Reject
        or SequenceReset or Logout or Logon;
}

/// <summary>SessionRejectReason(373) values.</summary>
public enum SessionRejectReason
{
    InvalidTagNumber = 0,
    RequiredTagMissing = 1,
    TagNotDefinedForMessageType = 2,
    UndefinedTag = 3,
    TagSpecifiedWithoutValue = 4,
    ValueIsIncorrect = 5,
    IncorrectDataFormat = 6,
    DecryptionProblem = 7,
    SignatureProblem = 8,
    CompIdProblem = 9,
    SendingTimeAccuracyProblem = 10,
    InvalidMsgType = 11,
    XmlValidationError = 12,
    TagAppearsMoreThanOnce = 13,
    TagSpecifiedOutOfRequiredOrder = 14,
    RepeatingGroupFieldsOutOfOrder = 15,
    IncorrectNumInGroupCount = 16,
    NonDataValueIncludesFieldDelimiter = 17,
    Other = 99,
}

/// <summary>BusinessRejectReason(380) values.</summary>
public enum BusinessRejectReason
{
    Other = 0,
    UnknownId = 1,
    UnknownSecurity = 2,
    UnsupportedMessageType = 3,
    ApplicationNotAvailable = 4,
    ConditionallyRequiredFieldMissing = 5,
    NotAuthorized = 6,
    DeliverToFirmNotAvailable = 7,
}
