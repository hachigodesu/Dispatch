using System.Text;
using Dispatch.Core.Sii.Binary;
using Dispatch.Core.Sii.Crypto;

namespace Dispatch.Core.Sii;

/// <summary>Result of turning any SII file into text.</summary>
/// <param name="Text">The SII text.</param>
/// <param name="Layers">The encodings that were peeled off, outermost first (e.g. Encrypted, Binary).</param>
/// <param name="Binary">BSII statistics when a binary layer was present.</param>
public sealed record SiiDecodeResult(string Text, IReadOnlyList<SiiFormat> Layers, BsiiDecodeInfo? Binary);

public static class SiiFileCodec
{
    private const int MaxLayers = 4;

    /// <summary>Decodes text, ScsC and BSII files (in any nesting) into SII text.</summary>
    /// <exception cref="InvalidDataException">Unrecognised or corrupt data.</exception>
    /// <exception cref="NotSupportedException">A known but not yet supported encoding (3nK).</exception>
    public static SiiDecodeResult Decode(byte[] data)
    {
        var layers = new List<SiiFormat>();
        BsiiDecodeInfo? binaryInfo = null;
        for (int depth = 0; depth < MaxLayers; depth++)
        {
            var format = SiiFormatDetector.Detect(data);
            layers.Add(format);
            switch (format)
            {
                case SiiFormat.Text:
                    return new SiiDecodeResult(DecodeUtf8(data), layers, binaryInfo);
                case SiiFormat.Encrypted:
                    data = ScsCDecryptor.Decrypt(data);
                    break;
                case SiiFormat.Binary:
                    string text = BsiiDecoder.Decode(data, out binaryInfo);
                    return new SiiDecodeResult(text, layers, binaryInfo);
                case SiiFormat.ThreeNK:
                    throw new NotSupportedException("3nK-obfuscated SII is not supported yet.");
                default:
                    throw new InvalidDataException("Unrecognised SII data.");
            }
        }

        throw new InvalidDataException("Too many nested SII encodings.");
    }

    private static string DecodeUtf8(byte[] data)
    {
        int offset = data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF ? 3 : 0;
        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
            .GetString(data, offset, data.Length - offset);
    }
}
