namespace Clipstems.Core.Steam;

/// <summary>One protobuf field: varint value, or the raw bytes of a length-delimited field.</summary>
public readonly record struct ProtoField(int Number, int WireType, ulong Varint, ReadOnlyMemory<byte> Bytes);

/// <summary>
/// Schemaless protobuf reader. Steam ships no .proto for its recording files,
/// so callers name the field numbers they rely on and treat anything else as
/// unknown.
/// </summary>
public static class ProtoReader
{
    public static List<ProtoField> Read(ReadOnlyMemory<byte> message)
    {
        var fields = new List<ProtoField>();
        var span = message.Span;
        int i = 0;
        while (i < span.Length)
        {
            ulong key = ReadVarint(span, ref i);
            int number = (int)(key >> 3), wire = (int)(key & 7);
            switch (wire)
            {
                case 0:
                    fields.Add(new ProtoField(number, wire, ReadVarint(span, ref i), default));
                    break;
                case 1:
                    Need(span, i, 8);
                    fields.Add(new ProtoField(number, wire, BitConverter.ToUInt64(span.Slice(i, 8)), default));
                    i += 8;
                    break;
                case 2:
                    int len = checked((int)ReadVarint(span, ref i));
                    Need(span, i, len);
                    fields.Add(new ProtoField(number, wire, 0, message.Slice(i, len)));
                    i += len;
                    break;
                case 5:
                    Need(span, i, 4);
                    fields.Add(new ProtoField(number, wire, BitConverter.ToUInt32(span.Slice(i, 4)), default));
                    i += 4;
                    break;
                default:
                    throw new FormatException($"Unsupported protobuf wire type {wire} at byte {i}.");
            }
        }
        return fields;
    }

    private static ulong ReadVarint(ReadOnlySpan<byte> span, ref int i)
    {
        ulong result = 0;
        for (int shift = 0; shift < 64; shift += 7)
        {
            Need(span, i, 1);
            byte b = span[i++];
            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return result;
        }
        throw new FormatException("Malformed protobuf varint.");
    }

    private static void Need(ReadOnlySpan<byte> span, int at, int count)
    {
        if (at + count > span.Length) throw new FormatException("Truncated protobuf message.");
    }
}

internal static class ProtoFieldExtensions
{
    public static ulong? Varint(this List<ProtoField> fields, int number) =>
        fields.FirstOrDefault(f => f.Number == number && f.WireType == 0) is { WireType: 0, Number: > 0 } f ? f.Varint : null;

    public static string? String(this List<ProtoField> fields, int number) =>
        fields.FirstOrDefault(f => f.Number == number && f.WireType == 2) is { WireType: 2, Number: > 0 } f
            ? System.Text.Encoding.UTF8.GetString(f.Bytes.Span)
            : null;

    public static IEnumerable<List<ProtoField>> Messages(this List<ProtoField> fields, int number) =>
        fields.Where(f => f.Number == number && f.WireType == 2).Select(f => ProtoReader.Read(f.Bytes));
}
