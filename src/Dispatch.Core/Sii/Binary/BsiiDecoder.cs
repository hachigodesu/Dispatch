using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Dispatch.Core.Sii.Text;

namespace Dispatch.Core.Sii.Binary;

/// <summary>Statistics from a BSII decode, used by the spikes and diagnostics.</summary>
public sealed record BsiiDecodeInfo(uint Version, int StructureCount, int UnitCount, int TrailingBytes,
                                    IReadOnlySet<uint> ValueTypesSeen);

/// <summary>
/// Decodes binary SII ("BSII", versions 1-3) into SII text.
/// Written from the public format description; see docs/file-formats.md.
/// </summary>
/// <remarks>
/// Output conventions (all lossless, all accepted by the game's text parser):
/// floats that are exact small integers print as integers, every other float as "&amp;" + 8 hex digits
/// of its IEEE-754 bits; strings are always quoted, with non-ASCII as UTF-8 byte escapes like the game; tokens are bare; IDs are dotted tokens,
/// "_nameless.x.xxxx.xxxx" for nameless IDs, and "null" for empty IDs.
/// </remarks>
public sealed class BsiiDecoder
{
    private sealed record Field(uint Type, string Name, IReadOnlyDictionary<uint, string>? Ordinals);
    private sealed record Structure(uint Id, string Name, IReadOnlyList<Field> Fields);

    /// <summary>The game writes text SII with CRLF line endings; so do we.</summary>
    public const string Nl = "\r\n";

    private readonly byte[] _data;
    private int _pos;
    private uint _version;
    private readonly Dictionary<uint, Structure> _structures = [];
    private readonly HashSet<uint> _typesSeen = [];
    private readonly StringBuilder _out = new();

    private BsiiDecoder(byte[] data) => _data = data;

    public static string Decode(byte[] data) => Decode(data, out _);

    public static string Decode(byte[] data, out BsiiDecodeInfo info)
    {
        var decoder = new BsiiDecoder(data);
        int units = decoder.Run();
        info = new BsiiDecodeInfo(decoder._version, decoder._structures.Count, units,
                                  data.Length - decoder._pos, decoder._typesSeen);
        return decoder._out.ToString();
    }

    private int Run()
    {
        if (ReadUInt32() != SiiFormatDetector.BinaryMagic)
        {
            throw Error("Missing BSII signature.");
        }

        _version = ReadUInt32();
        if (_version is < 1 or > 3)
        {
            throw Error($"Unsupported BSII version {_version}.");
        }

        _out.Append("SiiNunit").Append(Nl).Append('{').Append(Nl);
        int units = 0;
        while (_pos < _data.Length)
        {
            uint blockType = ReadUInt32();
            if (blockType == 0)
            {
                if (!ReadStructureBlock())
                {
                    break; // the invalid structure block marks the end of the file
                }
            }
            else
            {
                ReadDataBlock(blockType);
                units++;
            }
        }

        _out.Append('}').Append(Nl);
        return units;
    }

    private bool ReadStructureBlock()
    {
        bool valid = ReadByte() != 0;
        if (!valid)
        {
            return false;
        }

        uint id = ReadUInt32();
        string name = ReadString();
        var fields = new List<Field>();
        while (true)
        {
            uint type = ReadUInt32();
            if (type == 0)
            {
                break;
            }

            string fieldName = ReadString();
            Dictionary<uint, string>? ordinals = null;
            if (type == 0x37)
            {
                uint count = ReadUInt32();
                ordinals = new Dictionary<uint, string>((int)Math.Min(count, 4096));
                for (uint i = 0; i < count; i++)
                {
                    uint ordinal = ReadUInt32();
                    ordinals[ordinal] = ReadString();
                }
            }

            fields.Add(new Field(type, fieldName, ordinals));
        }

        if (id == 0 || !_structures.TryAdd(id, new Structure(id, name, fields)))
        {
            throw Error($"Invalid or duplicate structure id {id}.");
        }

        return true;
    }

    private void ReadDataBlock(uint structureId)
    {
        if (!_structures.TryGetValue(structureId, out var structure))
        {
            throw Error($"Data block refers to unknown structure id {structureId}.");
        }

        string unitId = ReadId();
        _out.Append(structure.Name).Append(" : ").Append(unitId).Append(" {").Append(Nl);
        foreach (var field in structure.Fields)
        {
            _typesSeen.Add(field.Type);
            WriteField(field);
        }

        _out.Append('}').Append(Nl).Append(Nl);
    }

    private void WriteField(Field f)
    {
        switch (f.Type)
        {
            case 0x01: Scalar(f.Name, SiiString.Quote(ReadString())); break;
            case 0x02: Array(f.Name, () => SiiString.Quote(ReadString())); break;
            case 0x03: Scalar(f.Name, Token(ReadUInt64())); break;
            case 0x04: Array(f.Name, () => Token(ReadUInt64())); break;
            case 0x05: Scalar(f.Name, Float(ReadSingle())); break;
            case 0x06: Array(f.Name, () => Float(ReadSingle())); break;
            case 0x07: Scalar(f.Name, Vec(2)); break;
            case 0x08: Array(f.Name, () => Vec(2)); break;
            case 0x09: Scalar(f.Name, Vec(3)); break;
            case 0x0A: Array(f.Name, () => Vec(3)); break;
            case 0x11: Scalar(f.Name, VecInt(3)); break;
            case 0x12: Array(f.Name, () => VecInt(3)); break;
            case 0x17: Scalar(f.Name, Quaternion()); break;
            case 0x18: Array(f.Name, Quaternion); break;
            case 0x19: Scalar(f.Name, Placement()); break;
            case 0x1A: Array(f.Name, Placement); break;
            case 0x25: Scalar(f.Name, ReadInt32().ToString(CultureInfo.InvariantCulture)); break;
            case 0x26: Array(f.Name, () => ReadInt32().ToString(CultureInfo.InvariantCulture)); break;
            case 0x27 or 0x2F: Scalar(f.Name, UInt32Text(ReadUInt32())); break;
            case 0x28: Array(f.Name, () => UInt32Text(ReadUInt32())); break;
            case 0x2B: Scalar(f.Name, UInt16Text(ReadUInt16())); break;
            case 0x2C: Array(f.Name, () => UInt16Text(ReadUInt16())); break;
            case 0x31: Scalar(f.Name, ReadInt64().ToString(CultureInfo.InvariantCulture)); break;
            case 0x32: Array(f.Name, () => ReadInt64().ToString(CultureInfo.InvariantCulture)); break;
            case 0x33: Scalar(f.Name, ReadUInt64().ToString(CultureInfo.InvariantCulture)); break;
            case 0x34: Array(f.Name, () => ReadUInt64().ToString(CultureInfo.InvariantCulture)); break;
            case 0x35: Scalar(f.Name, ReadByte() != 0 ? "true" : "false"); break;
            case 0x36: Array(f.Name, () => ReadByte() != 0 ? "true" : "false"); break;
            case 0x37: Scalar(f.Name, Ordinal(f)); break;
            case 0x39 or 0x3B or 0x3D: Scalar(f.Name, ReadId()); break;
            case 0x3A or 0x3C or 0x3E: Array(f.Name, ReadId); break;
            default:
                throw Error($"Unknown BSII value type 0x{f.Type:X2} for field '{f.Name}'.");
        }
    }

    private void Scalar(string name, string value) =>
        _out.Append(' ').Append(name).Append(": ").Append(value).Append(Nl);

    private void Array(string name, Func<string> readElement)
    {
        uint count = ReadUInt32();
        _out.Append(' ').Append(name).Append(": ").Append(count.ToString(CultureInfo.InvariantCulture)).Append(Nl);
        for (uint i = 0; i < count; i++)
        {
            _out.Append(' ').Append(name).Append('[').Append(i.ToString(CultureInfo.InvariantCulture))
                .Append("]: ").Append(readElement()).Append(Nl);
        }
    }

    // ---- value formatting -------------------------------------------------------------------

    private static string Token(ulong value)
    {
        string s = EncodedString.Decode(value);
        return s.Length == 0 ? "\"\"" : s;
    }

    internal static string Float(float f)
    {
        if (float.IsFinite(f) && f == MathF.Truncate(f) && MathF.Abs(f) < 1e7f && !(f == 0 && float.IsNegative(f)))
        {
            return ((long)f).ToString(CultureInfo.InvariantCulture);
        }

        return "&" + BitConverter.SingleToUInt32Bits(f).ToString("x8", CultureInfo.InvariantCulture);
    }

    private static string UInt32Text(uint v) => v == uint.MaxValue ? "nil" : v.ToString(CultureInfo.InvariantCulture);

    private static string UInt16Text(ushort v) => v == ushort.MaxValue ? "nil" : v.ToString(CultureInfo.InvariantCulture);

    private string Vec(int n)
    {
        var parts = new string[n];
        for (int i = 0; i < n; i++)
        {
            parts[i] = Float(ReadSingle());
        }

        return "(" + string.Join(", ", parts) + ")";
    }

    private string VecInt(int n)
    {
        var parts = new string[n];
        for (int i = 0; i < n; i++)
        {
            parts[i] = ReadInt32().ToString(CultureInfo.InvariantCulture);
        }

        return "(" + string.Join(", ", parts) + ")";
    }

    private string Quaternion()
    {
        string w = Float(ReadSingle()), x = Float(ReadSingle()), y = Float(ReadSingle()), z = Float(ReadSingle());
        return $"({w}; {x}, {y}, {z})";
    }

    private string Placement()
    {
        float x = ReadSingle(), y = ReadSingle(), z = ReadSingle();
        if (_version >= 2)
        {
            // Hidden 4th component carries a coarse position bias for X and Z.
            int bias = (int)ReadSingle();
            x += ((bias & 0xFFF) - 2048) << 9;
            z += (((bias >> 12) & 0xFFF) - 2048) << 9;
        }

        string q = Quaternion();
        return $"({Float(x)}, {Float(y)}, {Float(z)}) {q}";
    }

    private string Ordinal(Field f)
    {
        uint ordinal = ReadUInt32();
        if (f.Ordinals is null || !f.Ordinals.TryGetValue(ordinal, out var s))
        {
            throw Error($"Ordinal {ordinal} not defined for field '{f.Name}'.");
        }

        return EncodedString.IsToken(s) ? s : SiiString.Quote(s);
    }

    private string ReadId()
    {
        byte length = ReadByte();
        if (length == 0)
        {
            return "null";
        }

        if (length == 0xFF)
        {
            return FormatNameless(ReadUInt64());
        }

        var parts = new string[length];
        for (int i = 0; i < length; i++)
        {
            parts[i] = EncodedString.Decode(ReadUInt64());
        }

        return string.Join('.', parts);
    }

    /// <summary>Nameless IDs print as "_nameless." + hex in dot-separated 16-bit groups, e.g. _nameless.27a.c7a9.1bf8.</summary>
    internal static string FormatNameless(ulong value)
    {
        string hex = value.ToString("x", CultureInfo.InvariantCulture);
        var groups = new List<string>();
        for (int end = hex.Length; end > 0; end -= 4)
        {
            int start = Math.Max(0, end - 4);
            groups.Insert(0, hex[start..end].TrimStart('0') is { Length: > 0 } g ? g : "0");
        }

        return "_nameless." + string.Join('.', groups);
    }

    // ---- primitive readers --------------------------------------------------------------------

    private InvalidDataException Error(string message) => new($"{message} (at byte {_pos})");

    private ReadOnlySpan<byte> Take(int n)
    {
        if (n < 0 || _pos + n > _data.Length)
        {
            throw Error($"Unexpected end of BSII data (need {n} bytes).");
        }

        var span = _data.AsSpan(_pos, n);
        _pos += n;
        return span;
    }

    private byte ReadByte() => Take(1)[0];
    private ushort ReadUInt16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
    private int ReadInt32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));
    private uint ReadUInt32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
    private long ReadInt64() => BinaryPrimitives.ReadInt64LittleEndian(Take(8));
    private ulong ReadUInt64() => BinaryPrimitives.ReadUInt64LittleEndian(Take(8));
    private float ReadSingle() => BinaryPrimitives.ReadSingleLittleEndian(Take(4));

    private string ReadString()
    {
        uint length = ReadUInt32();
        if (length > int.MaxValue)
        {
            throw Error("String length out of range.");
        }

        return Encoding.UTF8.GetString(Take((int)length));
    }
}
