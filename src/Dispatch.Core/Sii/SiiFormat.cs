using System.Buffers.Binary;

namespace Dispatch.Core.Sii;

/// <summary>The on-disk encodings an SII file can have, identified by its first four bytes.</summary>
public enum SiiFormat
{
    Unknown,

    /// <summary>Plain text, starts with "SiiNunit".</summary>
    Text,

    /// <summary>"ScsC": AES-256-CBC encrypted, zlib-compressed payload (which is Text or Binary).</summary>
    Encrypted,

    /// <summary>"BSII": binary SII.</summary>
    Binary,

    /// <summary>"3nK": byte-obfuscated text (mostly locale files).</summary>
    ThreeNK,
}

public static class SiiFormatDetector
{
    // Magic values as little-endian UInt32.
    internal const uint TextMagic = 0x4E696953;      // "SiiN"
    internal const uint EncryptedMagic = 0x43736353; // "ScsC"
    internal const uint BinaryMagic = 0x49495342;    // "BSII"
    internal const uint ThreeNKMagic = 0x014B6E33;   // "3nK\x01"

    public static SiiFormat Detect(ReadOnlySpan<byte> data)
    {
        // A UTF-8 byte order mark is tolerated in front of text files.
        if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF)
        {
            data = data[3..];
        }

        if (data.Length < 4)
        {
            return SiiFormat.Unknown;
        }

        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(data);
        return magic switch
        {
            TextMagic => SiiFormat.Text,
            EncryptedMagic => SiiFormat.Encrypted,
            BinaryMagic => SiiFormat.Binary,
            _ when (magic & 0x00FFFFFF) == (ThreeNKMagic & 0x00FFFFFF) => SiiFormat.ThreeNK,
            _ => SiiFormat.Unknown,
        };
    }
}
