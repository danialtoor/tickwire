using System.Buffers;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;

namespace Tickwire.Fix.Tests;

public class RoundTripProperties
{
    private const string ValueChars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789 .-_:/+#@,";

    private static readonly Gen<(int Tag, string Value)> FieldGen =
        from tag in Gen.Choose(100, 4999).Where(t => !Tags.IsHeaderTag(t) && !Tags.IsTrailerTag(t) && Tags.DataTagFor(t) == 0 && !IsDataTag(t))
        from len in Gen.Choose(1, 24)
        from chars in Gen.Elements(ValueChars.ToCharArray()).ArrayOf(len)
        select (tag, new string(chars));

    private static readonly Gen<(int Tag, string Value)[]> BodyGen =
        from n in Gen.Choose(0, 30)
        from fields in FieldGen.ArrayOf(n)
        select fields;

    private static readonly Gen<FixHeader> HeaderGen =
        from seq in Gen.Choose(1, int.MaxValue)
        from sender in Gen.Elements("A", "CLIENT_1", "BIGBANK-PROD", "X")
        from target in Gen.Elements("TICKWIRE", "B")
        from ms in Gen.Choose(0, 1_000_000_000)
        from possDup in Gen.Elements(true, false)
        select new FixHeader("FIX.4.4", sender, target, seq,
            new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(ms), possDup,
            possDup ? new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc) : null);

    private static bool IsDataTag(int tag) => tag is Tags.RawData or Tags.SecureData or Tags.Signature or Tags.XmlData
        or 349 or 351 or 353 or 355 or 357 or 359 or 361 or 363 or 365 or 446 or 619 or 622;

    private static byte[] Build(string msgType, (int Tag, string Value)[] body, FixHeader header)
    {
        using var b = new FixMessageBuilder(msgType);
        foreach (var (tag, value) in body)
        {
            b.Set(tag, value);
        }

        return b.ToBytes(header);
    }

    [Property(MaxTest = 300)]
    public Property Serialize_then_parse_returns_the_same_fields() =>
        Prop.ForAll(BodyGen.ToArbitrary(), HeaderGen.ToArbitrary(), (body, header) =>
        {
            var msg = FixMessage.Parse(Build(MsgTypes.NewOrderSingle, body, header));
            var parsedBody = new List<(int, string)>();
            foreach (var f in msg.Fields)
            {
                if (!Tags.IsHeaderTag(f.Tag) && !Tags.IsTrailerTag(f.Tag))
                {
                    parsedBody.Add((f.Tag, msg.ValueString(f)));
                }
            }

            return msg.IsIntact
                && msg.MsgSeqNum == header.MsgSeqNum
                && msg.SenderCompID == header.SenderCompID
                && msg.PossDupFlag == header.PossDupFlag
                && parsedBody.SequenceEqual(body);
        });

    [Property(MaxTest = 200)]
    public Property Framer_recovers_every_message_regardless_of_chunking() =>
        Prop.ForAll(BodyGen.ArrayOf(5).ToArbitrary(), Gen.Choose(1, 64).ToArbitrary(), (bodies, chunk) =>
        {
            var messages = bodies.Select((b, i) => Build(MsgTypes.ExecutionReport, b, TestMessages.Header(i + 1))).ToList();
            var stream = messages.SelectMany(m => m).ToArray();
            var seq = FramerTests.SequenceFromChunks(stream, chunk);

            var read = new List<byte[]>();
            while (FixFramer.TryReadMessage(ref seq, out var m, out _))
            {
                read.Add(m);
            }

            return read.Count == messages.Count && read.Zip(messages).All(p => p.First.AsSpan().SequenceEqual(p.Second));
        });

    [Property(MaxTest = 200)]
    public Property Any_single_byte_corruption_is_detected() =>
        Prop.ForAll(BodyGen.ToArbitrary(), Gen.Choose(0, int.MaxValue).ToArbitrary(), (body, seed) =>
        {
            var bytes = Build(MsgTypes.NewOrderSingle, body, TestMessages.Header());
            var bodyStart = Array.IndexOf(bytes, (byte)'3', 10); // inside "35="
            var checksumStart = bytes.Length - 7;
            var index = bodyStart + (seed % (checksumStart - bodyStart));
            var original = bytes[index];
            if (original == FixParser.Soh || original == (byte)'=')
            {
                return true; // structural bytes produce parse errors, covered elsewhere
            }

            bytes[index] = (byte)(original == (byte)'A' ? 'B' : 'A');
            return !FixParser.TryParse(bytes, out var msg, out _) || !msg.IsIntact;
        });
}
