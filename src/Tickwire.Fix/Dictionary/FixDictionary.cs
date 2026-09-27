using System.Collections.Frozen;
using System.Globalization;
using System.Text;
using System.Xml.Linq;

namespace Tickwire.Fix.Dictionary;

public sealed record FieldDef(int Number, string Name, string Type, FrozenDictionary<string, string> Values)
{
    public bool IsMultiValue => Type is "MULTIPLEVALUESTRING" or "MULTIPLECHARVALUE" or "MULTIPLESTRINGVALUE";

    /// <summary>Readable name for an enum value, e.g. "1" on OrdStatus → "PartiallyFilled".</summary>
    public string? Describe(string value) => Values.TryGetValue(value, out var d) ? d : null;
}

public sealed record MessageDef(
    string MsgType,
    string Name,
    bool IsAdmin,
    FrozenSet<int> AllowedTags,
    FrozenSet<int> GroupMemberTags,
    IReadOnlyList<int> RequiredTags);

/// <summary>One decoded field for display: tag, dictionary name, raw value, enum meaning.</summary>
public readonly record struct DecodedField(int Tag, string Name, string Value, string? Meaning, bool IsHeader, bool IsTrailer);

public readonly record struct ValidationIssue(SessionRejectReason Reason, int RefTagId, string Text);

/// <summary>
/// FIX 4.4 data dictionary loaded from the standard XML spec (the same format QuickFIX uses).
/// Supplies names for decoding and the rules for session-level validation.
/// </summary>
public sealed class FixDictionary
{
    private static readonly Lazy<FixDictionary> Fix44Lazy = new(LoadEmbeddedFix44);

    private readonly FrozenDictionary<int, FieldDef> _fields;
    private readonly FrozenDictionary<string, FieldDef> _fieldsByName;
    private readonly FrozenDictionary<string, MessageDef> _messages;
    private readonly FrozenSet<int> _headerTags;
    private readonly FrozenSet<int> _trailerTags;
    private readonly int[] _headerRequired;

    private FixDictionary(string beginString, Dictionary<int, FieldDef> fields, Dictionary<string, MessageDef> messages,
        HashSet<int> headerTags, HashSet<int> trailerTags, List<int> headerRequired)
    {
        BeginString = beginString;
        _fields = fields.ToFrozenDictionary();
        _fieldsByName = fields.Values.ToFrozenDictionary(f => f.Name, StringComparer.Ordinal);
        _messages = messages.ToFrozenDictionary(StringComparer.Ordinal);
        _headerTags = headerTags.ToFrozenSet();
        _trailerTags = trailerTags.ToFrozenSet();
        _headerRequired = [.. headerRequired];
    }

    /// <summary>The bundled FIX 4.4 dictionary.</summary>
    public static FixDictionary Fix44 => Fix44Lazy.Value;

    public string BeginString { get; }
    public IEnumerable<FieldDef> AllFields => _fields.Values;
    public IEnumerable<MessageDef> Messages => _messages.Values;

    public FieldDef? Field(int tag) => _fields.GetValueOrDefault(tag);

    public FieldDef? Field(string name) => _fieldsByName.GetValueOrDefault(name);

    public MessageDef? Message(string msgType) => _messages.GetValueOrDefault(msgType);

    public string FieldName(int tag) => _fields.TryGetValue(tag, out var f) ? f.Name : CustomTagName(tag);

    public string MessageName(string msgType) => _messages.TryGetValue(msgType, out var m) ? m.Name : $"Unknown({msgType})";

    public bool IsHeaderTag(int tag) => _headerTags.Contains(tag);

    public bool IsTrailerTag(int tag) => _trailerTags.Contains(tag);

    public IReadOnlyList<DecodedField> Decode(FixMessage message)
    {
        var result = new List<DecodedField>(message.Fields.Length);
        foreach (var f in message.Fields)
        {
            var value = message.ValueString(f);
            var def = Field(f.Tag);
            string? meaning = null;
            if (def is not null && def.Values.Count > 0)
            {
                meaning = def.IsMultiValue
                    ? string.Join(", ", value.Split(' ').Select(v => def.Describe(v) ?? v))
                    : def.Describe(value);
            }

            if (f.Tag == Tags.MsgType)
            {
                meaning = MessageName(value);
            }

            result.Add(new DecodedField(f.Tag, def?.Name ?? CustomTagName(f.Tag), value, meaning, IsHeaderTag(f.Tag),
                IsTrailerTag(f.Tag)));
        }

        return result;
    }

    /// <summary>
    /// Session-level validation (FIX 4.4 volume 2): message type, tag definitions, formats, enums, duplicates and
    /// required fields. Returns the first problem found, which maps directly onto a Reject(3).
    /// Tags 5000+ are the user-defined range and are accepted without a definition.
    /// </summary>
    public ValidationIssue? Validate(FixMessage message)
    {
        var msgType = message.MsgType;
        if (!_messages.TryGetValue(msgType, out var def))
        {
            return new ValidationIssue(SessionRejectReason.InvalidMsgType, Tags.MsgType, $"Invalid MsgType '{msgType}'");
        }

        Span<byte> seenSmall = stackalloc byte[1024];
        HashSet<int>? seenLarge = null;
        var inBody = false;

        foreach (var f in message.Fields)
        {
            var tag = f.Tag;
            var isHeader = _headerTags.Contains(tag);
            var isTrailer = _trailerTags.Contains(tag);
            if (!isHeader && !isTrailer)
            {
                inBody = true;
            }
            else if (isHeader && inBody)
            {
                return new ValidationIssue(SessionRejectReason.TagSpecifiedOutOfRequiredOrder, tag,
                    $"Header tag {tag} ({FieldName(tag)}) appears after body fields");
            }

            var fieldDef = Field(tag);
            if (fieldDef is null)
            {
                if (tag < 5000)
                {
                    return new ValidationIssue(SessionRejectReason.InvalidTagNumber, tag, $"Invalid tag number {tag}");
                }
            }
            else
            {
                if (!isHeader && !isTrailer && !def.AllowedTags.Contains(tag))
                {
                    return new ValidationIssue(SessionRejectReason.TagNotDefinedForMessageType, tag,
                        $"Tag {tag} ({fieldDef.Name}) is not defined for {def.Name}");
                }

                var value = message.ValueSpan(f);
                if (!IsValidFormat(fieldDef.Type, value))
                {
                    return new ValidationIssue(SessionRejectReason.IncorrectDataFormat, tag,
                        $"Incorrect data format for {fieldDef.Name}({tag}): expected {fieldDef.Type}");
                }

                if (fieldDef.Values.Count > 0 && !IsValidEnum(fieldDef, value))
                {
                    return new ValidationIssue(SessionRejectReason.ValueIsIncorrect, tag,
                        $"Value '{Encoding.ASCII.GetString(value)}' is not valid for {fieldDef.Name}({tag})");
                }
            }

            if (!def.GroupMemberTags.Contains(tag))
            {
                bool duplicate;
                if (tag < seenSmall.Length)
                {
                    duplicate = seenSmall[tag] != 0;
                    seenSmall[tag] = 1;
                }
                else
                {
                    seenLarge ??= [];
                    duplicate = !seenLarge.Add(tag);
                }

                if (duplicate)
                {
                    return new ValidationIssue(SessionRejectReason.TagAppearsMoreThanOnce, tag,
                        $"Tag {tag} ({FieldName(tag)}) appears more than once");
                }
            }
        }

        foreach (var tag in _headerRequired)
        {
            if (!message.Has(tag))
            {
                return new ValidationIssue(SessionRejectReason.RequiredTagMissing, tag,
                    $"Required tag missing: {FieldName(tag)}({tag})");
            }
        }

        foreach (var tag in def.RequiredTags)
        {
            if (!message.Has(tag))
            {
                return new ValidationIssue(SessionRejectReason.RequiredTagMissing, tag,
                    $"Required tag missing: {FieldName(tag)}({tag})");
            }
        }

        return null;
    }

    public static bool IsValidFormat(string type, ReadOnlySpan<byte> value)
    {
        switch (type)
        {
            case "INT" or "LENGTH" or "SEQNUM" or "NUMINGROUP" or "DAYOFMONTH":
                return IsInteger(value);
            case "FLOAT" or "PRICE" or "QTY" or "AMT" or "PERCENTAGE" or "PRICEOFFSET":
                return IsDecimal(value);
            case "CHAR":
                return value.Length == 1;
            case "BOOLEAN":
                return value.Length == 1 && (value[0] == (byte)'Y' || value[0] == (byte)'N');
            case "UTCTIMESTAMP":
                return FixTime.TryParseUtcTimestamp(Encoding.ASCII.GetString(value), out _);
            case "LOCALMKTDATE" or "UTCDATEONLY" or "UTCDATE":
                return value.Length == 8 && IsInteger(value)
                    && DateOnly.TryParseExact(Encoding.ASCII.GetString(value), "yyyyMMdd", CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out _);
            case "MONTHYEAR":
                return value.Length is >= 6 and <= 8 && IsInteger(value[..6]);
            default:
                return true;
        }
    }

    private static bool IsValidEnum(FieldDef def, ReadOnlySpan<byte> value)
    {
        var s = Encoding.ASCII.GetString(value);
        return def.IsMultiValue
            ? s.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(def.Values.ContainsKey)
            : def.Values.ContainsKey(s);
    }

    private static bool IsInteger(ReadOnlySpan<byte> v)
    {
        if (v.IsEmpty)
        {
            return false;
        }

        var start = v[0] == (byte)'-' ? 1 : 0;
        if (start == v.Length)
        {
            return false;
        }

        foreach (var b in v[start..])
        {
            if (b < (byte)'0' || b > (byte)'9')
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsDecimal(ReadOnlySpan<byte> v)
    {
        if (v.IsEmpty)
        {
            return false;
        }

        var start = v[0] == (byte)'-' ? 1 : 0;
        var digits = 0;
        var dots = 0;
        foreach (var b in v[start..])
        {
            if (b == (byte)'.')
            {
                if (++dots > 1)
                {
                    return false;
                }
            }
            else if (b is >= (byte)'0' and <= (byte)'9')
            {
                digits++;
            }
            else
            {
                return false;
            }
        }

        return digits > 0;
    }

    private static string CustomTagName(int tag) => tag switch
    {
        Tags.TheoValue => "TheoValue",
        Tags.UnderlyingLastPx => "UnderlyingLastPx",
        _ => $"Tag{tag}",
    };

    public static FixDictionary Load(Stream xml) => Load(XDocument.Load(xml));

    public static FixDictionary Load(XDocument doc)
    {
        var root = doc.Root ?? throw new InvalidDataException("Empty dictionary");
        var beginString = $"FIX.{root.Attribute("major")?.Value}.{root.Attribute("minor")?.Value}";

        var fields = new Dictionary<int, FieldDef>();
        var nameToTag = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var f in root.Element("fields")!.Elements("field"))
        {
            var number = int.Parse(f.Attribute("number")!.Value, CultureInfo.InvariantCulture);
            var name = f.Attribute("name")!.Value;
            var values = f.Elements("value").ToDictionary(v => v.Attribute("enum")!.Value,
                v => Pascal(v.Attribute("description")?.Value ?? v.Attribute("enum")!.Value), StringComparer.Ordinal);
            fields[number] = new FieldDef(number, name, f.Attribute("type")!.Value, values.ToFrozenDictionary());
            nameToTag[name] = number;
        }

        var components = root.Element("components")?.Elements("component")
            .ToDictionary(c => c.Attribute("name")!.Value, c => c) ?? [];

        var headerTags = new HashSet<int>();
        var headerRequired = new List<int>();
        var headerGroups = new HashSet<int>();
        Walk(root.Element("header")!, nameToTag, components, headerTags, headerGroups, headerRequired, true, false);

        var trailerTags = new HashSet<int>();
        Walk(root.Element("trailer")!, nameToTag, components, trailerTags, [], [], true, false);

        var messages = new Dictionary<string, MessageDef>(StringComparer.Ordinal);
        foreach (var m in root.Element("messages")!.Elements("message"))
        {
            var allowed = new HashSet<int>();
            var groupMembers = new HashSet<int>();
            var required = new List<int>();
            Walk(m, nameToTag, components, allowed, groupMembers, required, true, false);
            var msgType = m.Attribute("msgtype")!.Value;
            messages[msgType] = new MessageDef(msgType, m.Attribute("name")!.Value,
                m.Attribute("msgcat")?.Value == "admin", allowed.ToFrozenSet(), groupMembers.ToFrozenSet(), required);
        }

        return new FixDictionary(beginString, fields, messages, headerTags, trailerTags,
            [.. headerRequired.Where(t => t is not Tags.BeginString and not Tags.BodyLength and not Tags.MsgType)]);
    }

    private static void Walk(XElement element, Dictionary<string, int> nameToTag, Dictionary<string, XElement> components,
        HashSet<int> allowed, HashSet<int> groupMembers, List<int> required, bool requiredPath, bool inGroup)
    {
        foreach (var child in element.Elements())
        {
            var name = child.Attribute("name")?.Value;
            if (name is null)
            {
                continue;
            }

            var isRequired = requiredPath && child.Attribute("required")?.Value == "Y";
            switch (child.Name.LocalName)
            {
                case "field" when nameToTag.TryGetValue(name, out var tag):
                    allowed.Add(tag);
                    if (inGroup)
                    {
                        groupMembers.Add(tag);
                    }
                    else if (isRequired)
                    {
                        required.Add(tag);
                    }

                    break;
                case "group" when nameToTag.TryGetValue(name, out var countTag):
                    allowed.Add(countTag);
                    if (inGroup)
                    {
                        groupMembers.Add(countTag);
                    }
                    else if (isRequired)
                    {
                        required.Add(countTag);
                    }

                    // Fields inside a group may legitimately repeat, and are only required when the group is present.
                    Walk(child, nameToTag, components, allowed, groupMembers, required, false, true);
                    break;
                case "component" when components.TryGetValue(name, out var component):
                    Walk(component, nameToTag, components, allowed, groupMembers, required, isRequired, inGroup);
                    break;
                default:
                    break;
            }
        }
    }

    /// <summary>"PARTIALLY_FILLED" → "PartiallyFilled".</summary>
    private static string Pascal(string description)
    {
        var sb = new StringBuilder(description.Length);
        foreach (var part in description.Split('_', StringSplitOptions.RemoveEmptyEntries))
        {
            sb.Append(char.ToUpperInvariant(part[0]));
            sb.Append(part.AsSpan(1).ToString().ToLowerInvariant());
        }

        return sb.ToString();
    }

    private static FixDictionary LoadEmbeddedFix44()
    {
        using var stream = typeof(FixDictionary).Assembly.GetManifestResourceStream("Tickwire.Fix.Spec.FIX44.xml")
            ?? throw new InvalidOperationException("FIX44.xml resource missing");
        return Load(stream);
    }
}
