using System.Text;

namespace FetchHotfixCS;

public enum WireType
{
    VARINT = 0,
    I64,
    LEN,
    SGROUP, // deprecated, decoding not supported
    EGROUP, // deprecated, decoding not supported
    I32
}

public class WireTypeDefinition
{
    public string Name { get; set; }
    public List<string> Types { get; set; }
}

public class Decoded
{
    public WireType Type { get; set; }
    public int Field { get; set; }
    public bool IsObject { get; set; }
    public object Value { get; set; }
}

public class DecodingResult
{
    public List<Decoded> Fields { get; set; }
    public byte[] Unprocessed { get; set; }
}

public class SimpleDecoded
{
    public string Type { get; set; }
    public int Field { get; set; }
    public bool IsObject { get; set; }
    public object Value { get; set; }
}

public class SimpleDecodingResult
{
    public List<SimpleDecoded> Fields { get; set; }
}

public class ProtobufDecoder
{
    private readonly byte[] _data;
    private int _index;

    public ProtobufDecoder(byte[] data)
    {
        _data = data;
        _index = 0;
    }

    private byte NextByte() => _data[_index++];

    public int GetFieldNumber(long value) => (int)(value >> 3);

    public WireType GetWireType(long value) => (WireType)(value & 7);

    public long NextVarInt()
    {
        long value = 0;
        int shift = 0;

        while (true)
        {
            byte b = NextByte();
            value |= (b & 0x7F) << shift;
            if ((b & 0x80) == 0) break;
            shift += 7;
        }

        return value;
    }

    public byte[] Read(int length)
    {
        if (_index + length > _data.Length)
            throw new InvalidOperationException("Invalid memory access detected");

        var result = new byte[length];
        Array.Copy(_data, _index, result, 0, length);
        _index += length;
        return result;
    }

    public int Remaining => _data.Length - _index;

    public DecodingResult Decode()
    {
        var fields = new List<Decoded>();
        int lastIndex = 0;

        try
        {
            while (Remaining > 0)
            {
                lastIndex = _index;

                long enc = NextVarInt();
                int field = GetFieldNumber(enc);
                WireType type = GetWireType(enc);

                object value = null;
                bool valueDecoded = false;

                switch (type)
                {
                    case WireType.VARINT:
                        value = NextVarInt();
                        break;
                    case WireType.LEN:
                        int length = (int)NextVarInt();
                        value = Read(length);
                        try
                        {
                            var subDecoder = new ProtobufDecoder((byte[])value);
                            var decoded = subDecoder.Decode();

                            if (decoded.Unprocessed.Length == 0)
                            {
                                value = decoded;
                                valueDecoded = true;
                            }
                        }
                        catch
                        {
                            // Ignore decoding errors, treat as raw bytes
                        }
                        break;
                    case WireType.I32:
                        value = Read(4);
                        break;
                    case WireType.I64:
                        value = Read(8);
                        break;
                    default:
                        throw new NotSupportedException($"Not supported type: {type}");
                }

                fields.Add(new Decoded
                {
                    Field = field,
                    Type = type,
                    IsObject = valueDecoded,
                    Value = value
                });
            }
        }
        catch
        {
            _index = lastIndex;
        }

        return new DecodingResult
        {
            Fields = fields,
            Unprocessed = Read(Remaining)
        };
    }
}

public static class ProtobufUtils
{
    public static SimpleDecodingResult Simplify(DecodingResult result)
    {
        var fields = result.Fields.Select(f =>
        {
            var typeDef = TypeDefinition(f.Type);

            object value;
            if (f.IsObject)
            {
                value = Simplify((DecodingResult)f.Value);
            }
            else
            {
                var possible = PossibleValues(f);
                value = possible.FirstOrDefault()?.Value?.ToString() ?? string.Empty;
            }

            return new SimpleDecoded
            {
                Field = f.Field,
                IsObject = f.IsObject,
                Type = typeDef.Name,
                Value = value
            };
        }).ToList();

        return new SimpleDecodingResult { Fields = fields };
    }

    public static WireTypeDefinition TypeDefinition(WireType type) => type switch
    {
        WireType.VARINT => new WireTypeDefinition
        {
            Name = "varint",
            Types = new List<string> { "int32", "int64", "uint32", "uint64", "sint32", "sint64", "bool", "enum" }
        },
        WireType.LEN => new WireTypeDefinition
        {
            Name = "len",
            Types = new List<string> { "string", "bytes" }
        },
        WireType.I32 => new WireTypeDefinition
        {
            Name = "i32",
            Types = new List<string> { "fixed32", "sfixed32", "float" }
        },
        WireType.I64 => new WireTypeDefinition
        {
            Name = "i64",
            Types = new List<string> { "fixed64", "sfixed64", "double" }
        },
        _ => new WireTypeDefinition
        {
            Name = "unknown",
            Types = new List<string>()
        }
    };

    public static List<ValueRepresentation> PossibleValues(Decoded value)
    {
        var typeDef = TypeDefinition(value.Type);
        var result = new List<ValueRepresentation>();

        foreach (var t in typeDef.Types)
        {
            switch (t)
            {
                case "uint64":
                    result.Add(new ValueRepresentation { Type = t, Value = (long)value.Value });
                    break;
                case "fixed32":
                    result.Add(new ValueRepresentation { Type = t, Value = BitConverter.ToUInt32((byte[])value.Value, 0) });
                    break;
                case "sfixed32":
                    result.Add(new ValueRepresentation { Type = t, Value = BitConverter.ToInt32((byte[])value.Value, 0) });
                    break;
                case "float":
                    result.Add(new ValueRepresentation { Type = t, Value = BitConverter.ToSingle((byte[])value.Value, 0) });
                    break;
                case "fixed64":
                    result.Add(new ValueRepresentation { Type = t, Value = BitConverter.ToInt64((byte[])value.Value, 0) });
                    break;
                case "double":
                    result.Add(new ValueRepresentation { Type = t, Value = BitConverter.ToDouble((byte[])value.Value, 0) });
                    break;
                case "string":
                    result.Add(new ValueRepresentation { Type = t, Value = Encoding.UTF8.GetString((byte[])value.Value) });
                    break;
                case "bytes":
                    result.Add(new ValueRepresentation { Type = t, Value = BitConverter.ToString((byte[])value.Value).Replace("-", "").ToLower() });
                    break;
            }
        }

        return result;
    }
}

public class ValueRepresentation
{
    public string Type { get; set; }
    public object Value { get; set; }
}
